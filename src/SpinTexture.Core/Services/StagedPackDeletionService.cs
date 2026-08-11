using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public sealed record StagedPackDeletionDependency(
    string CompositionId,
    string CompositionManifestPath);

public sealed record StagedPackDeletionPlan(
    string ManifestPath,
    string BuildDirectory,
    string BuildId,
    long StagedBytes,
    bool IsComposition,
    bool IsReferencedByCurrentInstall,
    StagedPackFileFingerprint ManifestFingerprint,
    IReadOnlyList<StagedPackDeletionDependency> CompositionDependencies,
    IReadOnlyList<string> SafetyBlockers,
    string Summary)
{
    public bool CanDelete =>
        !IsReferencedByCurrentInstall
        && CompositionDependencies.Count == 0
        && SafetyBlockers.Count == 0;
}

public sealed record StagedPackDeletionResult(
    string BuildId,
    string DeletedBuildDirectory,
    long DeletedStagedBytes,
    DateTimeOffset CompletedUtc);

/// <summary>
/// Permanently removes one completed staged pack only after proving that the
/// target is a direct, non-reparse child of the managed Staging directory and
/// that neither the current install nor another composition references it.
/// </summary>
public sealed class StagedPackDeletionService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly StagedPackCatalogService catalog;
    private readonly InstallHealthService installHealthService;
    private readonly ManifestStore manifestStore;

    public StagedPackDeletionService(
        StagedPackCatalogService? catalog = null,
        InstallHealthService? installHealthService = null,
        ManifestStore? manifestStore = null)
    {
        this.manifestStore = manifestStore ?? new ManifestStore();
        this.catalog = catalog ?? new StagedPackCatalogService(this.manifestStore);
        this.installHealthService = installHealthService
            ?? new InstallHealthService(this.manifestStore);
    }

    public async Task<StagedPackDeletionPlan> PlanAsync(
        ProjectPaths paths,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();

        var location = ResolveTargetLocation(paths, manifestPath);
        var manifestFingerprint = await FileIntegrity
            .FingerprintAsync(location.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        var inspected = await catalog.InspectAsync(
                paths,
                location.ManifestPath,
                StagedPackVerificationMode.Metadata,
                cancellationToken)
            .ConfigureAwait(false);
        var blockers = new List<string>();
        try
        {
            EnsureDeletionTreeSafe(paths, location.BuildDirectory);
        }
        catch (Exception exception) when (IsSafetyFailure(exception))
        {
            blockers.Add(exception.Message);
        }

        string? currentInstallBuildManifest = null;
        try
        {
            currentInstallBuildManifest = await FindCurrentInstallBuildManifestAsync(
                    paths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSafetyFailure(exception))
        {
            blockers.Add(
                $"The current install dependency could not be verified: {exception.Message}");
        }

        var dependencyScan = await FindCompositionDependenciesAsync(
                paths,
                location.ManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        blockers.AddRange(dependencyScan.SafetyBlockers);
        var isCurrentInstall = currentInstallBuildManifest is not null
            && PathGuard.SamePath(currentInstallBuildManifest, location.ManifestPath);
        var buildId = Path.GetFileName(location.BuildDirectory);
        var stagedBytes = inspected.StagedBytes;
        var isComposition = File.Exists(Path.Combine(location.BuildDirectory, "composition.json"));

        string summary;
        if (isCurrentInstall)
        {
            summary = "This pack is referenced by the current install transaction. Restore or switch packs before deleting it.";
        }
        else if (dependencyScan.Dependencies.Count != 0)
        {
            summary = dependencyScan.Dependencies.Count == 1
                ? $"Composition '{dependencyScan.Dependencies[0].CompositionId}' depends on this pack. Delete that composition first."
                : $"{dependencyScan.Dependencies.Count:N0} compositions depend on this pack. Delete those compositions first.";
        }
        else if (blockers.Count != 0)
        {
            summary = $"SpinTexture could not prove this pack is safe to delete. {blockers[0]}";
        }
        else
        {
            summary = "This completed pack is not active, has no dependent compositions, and is contained safely in the managed staging workspace.";
        }

        return new StagedPackDeletionPlan(
            location.ManifestPath,
            location.BuildDirectory,
            buildId,
            stagedBytes,
            isComposition,
            isCurrentInstall,
            new StagedPackFileFingerprint(
                manifestFingerprint.Length,
                manifestFingerprint.Sha256),
            dependencyScan.Dependencies,
            blockers,
            summary);
    }

    public async Task<StagedPackDeletionResult> DeleteAsync(
        ProjectPaths paths,
        string manifestPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);
        using var stagingLock = StagingLibraryLock.Acquire(paths.WorkspacePath);

        // This is intentionally a fresh plan under the same cross-process lock
        // held through deletion. Installs and composition commits cannot make the
        // target active or add a dependency after this check succeeds.
        var plan = await PlanAsync(paths, manifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (!plan.CanDelete)
        {
            throw new InvalidOperationException(plan.Summary);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await FileIntegrity.EnsureMatchesAsync(
                plan.ManifestPath,
                plan.ManifestFingerprint.Length,
                plan.ManifestFingerprint.Sha256,
                "Staged-pack manifest immediately before deletion",
                cancellationToken)
            .ConfigureAwait(false);
        EnsureDeletionTreeSafe(paths, plan.BuildDirectory);
        progress?.Report(new ProgressUpdate(
            "Delete pack",
            "Removing the selected completed pack from the managed staging workspace.",
            0,
            1,
            plan.BuildId));

        // Once deletion starts it is intentionally not cancellable. Stopping in
        // the middle would be less safe than completing this one proven target.
        DeleteTreeWithoutFollowingReparsePoints(paths, plan.BuildDirectory);
        if (Directory.Exists(plan.BuildDirectory) || File.Exists(plan.BuildDirectory))
        {
            throw new IOException(
                $"The staged-pack directory still exists after deletion: {plan.BuildDirectory}");
        }

        var completedUtc = DateTimeOffset.UtcNow;
        progress?.Report(new ProgressUpdate(
            "Delete pack",
            "The selected staged pack was deleted. Other completed packs were not changed.",
            1,
            1,
            plan.BuildId));
        return new StagedPackDeletionResult(
            plan.BuildId,
            plan.BuildDirectory,
            plan.StagedBytes,
            completedUtc);
    }

    private async Task<string?> FindCurrentInstallBuildManifestAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var health = await installHealthService
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        if (health.InstallManifestPath is null)
        {
            return null;
        }

        var install = await manifestStore
            .ReadInstallManifestAsync(health.InstallManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (!PathGuard.SamePath(install.InstallPath, paths.InstallPath))
        {
            throw new InvalidDataException(
                "The current install transaction belongs to another EverQuest installation.");
        }

        return Path.GetFullPath(install.BuildManifestPath);
    }

    private static async Task<DependencyScanResult> FindCompositionDependenciesAsync(
        ProjectPaths paths,
        string targetManifestPath,
        CancellationToken cancellationToken)
    {
        var dependencies = new List<StagedPackDeletionDependency>();
        var blockers = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(
                     paths.StagingPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    blockers.Add(
                        $"Dependency scanning found an unsafe reparse-point directory: {Path.GetFileName(directory)}");
                    continue;
                }

                var compositionPath = Path.Combine(directory, "composition.json");
                if (!File.Exists(compositionPath)
                    || PathGuard.SamePath(directory, Path.GetDirectoryName(targetManifestPath)!))
                {
                    continue;
                }

                var document = await ReadCompositionAsync(
                        compositionPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateCompositionDocument(paths, directory, document);
                foreach (var component in document.Components)
                {
                    if (component is null)
                    {
                        throw new InvalidDataException(
                            "A staged-pack composition contains an empty component entry.");
                    }

                    var componentManifestPath = ResolveComponentManifestPath(
                        paths,
                        component.ManifestRelativePath);
                    if (PathGuard.SamePath(componentManifestPath, targetManifestPath))
                    {
                        dependencies.Add(new StagedPackDeletionDependency(
                            document.CompositionId,
                            Path.Combine(directory, "manifest.json")));
                    }

                    await FileIntegrity.EnsureMatchesAsync(
                            componentManifestPath,
                            component.ManifestLength,
                            component.ManifestSha256,
                            $"Composition component for {document.CompositionId}",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsSafetyFailure(exception))
            {
                blockers.Add(
                    $"Composition dependency metadata in '{Path.GetFileName(directory)}' is unsafe or unreadable: {exception.Message}");
            }
        }

        return new DependencyScanResult(
            dependencies
                .DistinctBy(
                    dependency => dependency.CompositionManifestPath,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(dependency => dependency.CompositionId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            blockers.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<StagedPackCompositionDocument> ReadCompositionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<StagedPackCompositionDocument>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               .ConfigureAwait(false)
            ?? throw new InvalidDataException("The composition document is empty.");
    }

    private static void ValidateCompositionDocument(
        ProjectPaths paths,
        string compositionDirectory,
        StagedPackCompositionDocument document)
    {
        if (document.SchemaVersion != StagedPackCompositionDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported composition schema {document.SchemaVersion}.");
        }

        if (!Path.GetFileName(compositionDirectory).Equals(
                document.CompositionId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The composition ID does not match its staging directory.");
        }

        if (!PathGuard.SamePath(document.InstallPath, paths.InstallPath))
        {
            throw new InvalidDataException(
                "The composition belongs to another EverQuest installation.");
        }

        if (document.Components is null || document.Components.Count == 0)
        {
            throw new InvalidDataException("The composition contains no source packs.");
        }
    }

    private static string ResolveComponentManifestPath(
        ProjectPaths paths,
        string manifestRelativePath)
    {
        var manifestPath = PathGuard.ResolveUnderRoot(
            paths.StagingPath,
            manifestRelativePath);
        if (!Path.GetFileName(manifestPath).Equals(
                "manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A composition component does not reference a manifest.json file.");
        }

        var buildDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException(
                "A composition component manifest has no build directory.");
        if (!PathGuard.SamePath(Path.GetDirectoryName(buildDirectory)!, paths.StagingPath))
        {
            throw new InvalidDataException(
                "A composition component is not a direct staged-pack manifest.");
        }

        return manifestPath;
    }

    private static TargetLocation ResolveTargetLocation(
        ProjectPaths paths,
        string manifestPath)
    {
        if (!Directory.Exists(paths.StagingPath))
        {
            throw new DirectoryNotFoundException(
                "The managed staged-pack workspace does not exist.");
        }

        var safeManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.StagingPath,
            manifestPath);
        if (!Path.GetFileName(safeManifestPath).Equals(
                "manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A staged pack must be identified by its manifest.json file.");
        }

        var buildDirectory = Path.GetDirectoryName(safeManifestPath)
            ?? throw new InvalidDataException(
                "The staged-pack manifest has no build directory.");
        var parent = Path.GetDirectoryName(buildDirectory);
        if (parent is null || !PathGuard.SamePath(parent, paths.StagingPath))
        {
            throw new InvalidDataException(
                "Only a direct Staging/<build-id> pack can be deleted.");
        }

        if (!Directory.Exists(buildDirectory) || !File.Exists(safeManifestPath))
        {
            throw new FileNotFoundException(
                "The selected staged pack no longer exists.",
                safeManifestPath);
        }

        return new TargetLocation(safeManifestPath, buildDirectory);
    }

    private static void EnsureDeletionTreeSafe(
        ProjectPaths paths,
        string buildDirectory)
    {
        EnsureDirectoryIsNotReparsePoint(paths.WorkspacePath, "workspace root");
        EnsureDirectoryIsNotReparsePoint(paths.StagingPath, "staging root");
        var safeBuildDirectory = PathGuard.EnsurePathUnderRoot(
            paths.StagingPath,
            buildDirectory);
        if (!PathGuard.SamePath(
                Path.GetDirectoryName(safeBuildDirectory)!,
                paths.StagingPath))
        {
            throw new InvalidDataException(
                "The selected pack is not a direct child of the staging root.");
        }

        foreach (var entry in EnumerateTree(safeBuildDirectory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Deletion is blocked because the pack contains a reparse point: {entry}");
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                throw new InvalidDataException(
                    $"Deletion is blocked because the pack contains a read-only entry: {entry}");
            }
        }
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(
        ProjectPaths paths,
        string buildDirectory)
    {
        EnsureDeletionTreeSafe(paths, buildDirectory);
        var entries = EnumerateTree(buildDirectory).ToArray();
        foreach (var file in entries
                     .Where(path => !Directory.Exists(path))
                     .OrderByDescending(path => path.Length))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
            {
                throw new InvalidDataException(
                    $"The staged pack changed to an unsafe file before deletion: {file}");
            }

            File.Delete(file);
        }

        foreach (var directory in entries
                     .Where(Directory.Exists)
                     .OrderByDescending(path => path.Count(character =>
                         character == Path.DirectorySeparatorChar)))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The staged pack changed to an unsafe directory before deletion: {directory}");
            }

            Directory.Delete(directory, recursive: false);
        }
    }

    private static IEnumerable<string> EnumerateTree(string root)
    {
        var pending = new Stack<string>();
        var entries = new List<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Deletion is blocked by a reparse-point directory: {directory}");
            }

            entries.Add(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Deletion is blocked by a reparse point: {child}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                }
                else
                {
                    entries.Add(child);
                }
            }
        }

        return entries;
    }

    private static void EnsureDirectoryIsNotReparsePoint(
        string directory,
        string description)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The managed {description} does not exist: {directory}");
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Deletion is blocked because the managed {description} is a reparse point.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool IsSafetyFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        InvalidDataException or
        JsonException or
        ArgumentException or
        NotSupportedException;

    private sealed record TargetLocation(
        string ManifestPath,
        string BuildDirectory);

    private sealed record DependencyScanResult(
        IReadOnlyList<StagedPackDeletionDependency> Dependencies,
        IReadOnlyList<string> SafetyBlockers);
}
