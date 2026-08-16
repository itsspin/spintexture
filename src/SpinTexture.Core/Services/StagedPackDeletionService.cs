using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public sealed record StagedPackDeletionDependency(
    string CompositionId,
    string CompositionManifestPath)
{
    public bool IsReferencedByCurrentInstall { get; init; }
}

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
    public bool IsRequiredByCurrentInstall =>
        IsReferencedByCurrentInstall
        || CompositionDependencies.Any(dependency =>
            dependency.IsReferencedByCurrentInstall);

    public bool CanDelete =>
        !IsReferencedByCurrentInstall
        && CompositionDependencies.Count == 0
        && SafetyBlockers.Count == 0;
}

public sealed record StagedPackDeletionBatchBlocker(
    string BuildId,
    string ManifestPath,
    string Summary);

public sealed record StagedPackDeletionBatchPlan(
    IReadOnlyList<StagedPackDeletionPlan> RequestedPlans,
    IReadOnlyList<StagedPackDeletionPlan> OrderedPlans,
    IReadOnlyList<StagedPackDeletionBatchBlocker> Blockers,
    string Summary)
{
    public bool CanDelete => RequestedPlans.Count > 0
        && OrderedPlans.Count == RequestedPlans.Count
        && Blockers.Count == 0;

    public long StagedBytes => RequestedPlans.Sum(plan => plan.StagedBytes);
}

public sealed record StagedPackCleanupCandidate(
    string ManifestPath,
    DateTimeOffset CreatedUtc,
    StagedPackDeletionPlan DeletionPlan);

/// <summary>
/// Conservative retention policy for the pack-library cleanup shortcut. The
/// recommendation keeps recent packs, the current install, and the complete
/// dependency chain of anything kept. The catalog omits unfinished builds,
/// and deletion independently rechecks the checkpoint marker under the
/// staging lock before removing anything.
/// </summary>
public static class StagedPackCleanupPolicy
{
    public static readonly TimeSpan RecentPackAge = TimeSpan.FromDays(3);

    public static bool IsRecent(
        DateTimeOffset createdUtc,
        DateTimeOffset utcNow,
        TimeSpan? recentPackAge = null)
    {
        var age = recentPackAge ?? RecentPackAge;
        if (age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recentPackAge),
                "The recent-pack retention age must be positive.");
        }

        return createdUtc >= utcNow - age;
    }

    public static IReadOnlySet<string> RecommendSafeOldPackDeletions(
        IReadOnlyList<StagedPackCleanupCandidate> candidates,
        DateTimeOffset utcNow,
        TimeSpan? recentPackAge = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var age = recentPackAge ?? RecentPackAge;
        if (age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recentPackAge),
                "The recent-pack retention age must be positive.");
        }

        var byManifest = candidates.ToDictionary(
            candidate => Path.GetFullPath(candidate.ManifestPath),
            StringComparer.OrdinalIgnoreCase);
        var retained = candidates
            .Where(candidate =>
                candidate.DeletionPlan.IsRequiredByCurrentInstall
                || candidate.DeletionPlan.SafetyBlockers.Count != 0
                || IsRecent(candidate.CreatedUtc, utcNow, age))
            .Select(candidate => Path.GetFullPath(candidate.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Dependencies point from a source pack to the compositions that use
        // it. If a composition is retained, its source must be retained too.
        // Repeating the pass also handles nested legacy compositions safely.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in candidates)
            {
                var candidatePath = Path.GetFullPath(candidate.ManifestPath);
                if (retained.Contains(candidatePath))
                {
                    continue;
                }

                var requiredByRetainedOrUnknownComposition = candidate
                    .DeletionPlan
                    .CompositionDependencies
                    .Any(dependency =>
                    {
                        var dependencyPath = Path.GetFullPath(
                            dependency.CompositionManifestPath);
                        return !byManifest.ContainsKey(dependencyPath)
                               || retained.Contains(dependencyPath);
                    });
                if (requiredByRetainedOrUnknownComposition)
                {
                    retained.Add(candidatePath);
                    changed = true;
                }
            }
        }

        return candidates
            .Select(candidate => Path.GetFullPath(candidate.ManifestPath))
            .Where(path => !retained.Contains(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
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
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var plans = await PlanManyAsync(
                paths,
                [manifestPath],
                cancellationToken)
            .ConfigureAwait(false);
        return plans[0];
    }

    /// <summary>
    /// Plans every requested completed pack from one active-install read and
    /// one composition-graph scan. This keeps a large pack library responsive
    /// and gives every row a consistent view of its dependencies.
    /// </summary>
    public async Task<IReadOnlyList<StagedPackDeletionPlan>> PlanManyAsync(
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifestPaths);
        cancellationToken.ThrowIfCancellationRequested();
        if (manifestPaths.Count == 0)
        {
            return [];
        }

        var locations = manifestPaths
            .Select(manifestPath =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
                return ResolveTargetLocation(paths, manifestPath);
            })
            .DistinctBy(
                location => location.ManifestPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? currentInstallBuildManifest = null;
        string? currentInstallBlocker = null;
        try
        {
            currentInstallBuildManifest = await FindCurrentInstallBuildManifestAsync(
                    paths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSafetyFailure(exception))
        {
            currentInstallBlocker =
                $"The current install dependency could not be verified: {exception.Message}";
        }

        var dependencyScan = await FindCompositionDependenciesAsync(
                paths,
                cancellationToken)
            .ConfigureAwait(false);
        var results = new List<StagedPackDeletionPlan>(locations.Length);
        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            if (currentInstallBlocker is not null)
            {
                blockers.Add(currentInstallBlocker);
            }

            blockers.AddRange(dependencyScan.SafetyBlockers);
            var isCurrentInstall = currentInstallBuildManifest is not null
                && PathGuard.SamePath(
                    currentInstallBuildManifest,
                    location.ManifestPath);
            var dependencies = dependencyScan.DependenciesByManifest
                .GetValueOrDefault(location.ManifestPath, [])
                .Select(dependency => dependency with
                {
                    IsReferencedByCurrentInstall =
                        currentInstallBuildManifest is not null
                        && PathGuard.SamePath(
                            dependency.CompositionManifestPath,
                            currentInstallBuildManifest)
                })
                .ToArray();
            var buildId = Path.GetFileName(location.BuildDirectory);
            var isComposition = File.Exists(
                Path.Combine(location.BuildDirectory, "composition.json"));
            var summary = BuildPlanSummary(
                isCurrentInstall,
                dependencies,
                blockers);
            results.Add(new StagedPackDeletionPlan(
                location.ManifestPath,
                location.BuildDirectory,
                buildId,
                inspected.StagedBytes,
                isComposition,
                isCurrentInstall,
                new StagedPackFileFingerprint(
                    manifestFingerprint.Length,
                    manifestFingerprint.Sha256),
                dependencies,
                blockers.Distinct(StringComparer.Ordinal).ToArray(),
                summary));
        }

        return results;
    }

    /// <summary>
    /// Proves a checkbox-selected cleanup set can be removed together and
    /// orders generated compositions before the source packs they reference.
    /// </summary>
    public async Task<StagedPackDeletionBatchPlan> PlanBatchAsync(
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        CancellationToken cancellationToken = default)
    {
        var plans = await PlanManyAsync(paths, manifestPaths, cancellationToken)
            .ConfigureAwait(false);
        if (plans.Count == 0)
        {
            return new StagedPackDeletionBatchPlan(
                [],
                [],
                [],
                "No completed packs were selected for deletion.");
        }

        var selectedPaths = plans
            .Select(plan => plan.ManifestPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockers = new List<StagedPackDeletionBatchBlocker>();
        foreach (var plan in plans)
        {
            if (plan.IsReferencedByCurrentInstall)
            {
                blockers.Add(new StagedPackDeletionBatchBlocker(
                    plan.BuildId,
                    plan.ManifestPath,
                    "This pack is installed now and cannot be deleted."));
            }

            if (plan.SafetyBlockers.Count != 0)
            {
                blockers.Add(new StagedPackDeletionBatchBlocker(
                    plan.BuildId,
                    plan.ManifestPath,
                    $"Its safety preflight could not be completed: {plan.SafetyBlockers[0]}"));
            }

            var retainedDependencies = plan.CompositionDependencies
                .Where(dependency =>
                    !selectedPaths.Contains(dependency.CompositionManifestPath))
                .ToArray();
            if (retainedDependencies.Length != 0)
            {
                var detail = retainedDependencies.Length == 1
                    ? $"Keep it, or also check composition '{retainedDependencies[0].CompositionId}' for deletion."
                    : $"Keep it, or also check its {retainedDependencies.Length:N0} dependent compositions for deletion.";
                blockers.Add(new StagedPackDeletionBatchBlocker(
                    plan.BuildId,
                    plan.ManifestPath,
                    detail));
            }
        }

        var ordered = blockers.Count == 0
            ? OrderPlansForDeletion(plans)
            : [];
        if (blockers.Count == 0 && ordered.Count != plans.Count)
        {
            blockers.Add(new StagedPackDeletionBatchBlocker(
                "pack selection",
                paths.StagingPath,
                "The selected composition dependency graph contains a cycle."));
        }

        var distinctBlockers = blockers
            .DistinctBy(
                blocker => $"{blocker.ManifestPath}\n{blocker.Summary}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var summary = distinctBlockers.Length == 0
            ? $"{plans.Count:N0} completed pack(s) passed cleanup preflight. Generated compositions will be removed before source packs."
            : $"Nothing can be deleted until {distinctBlockers.Length:N0} cleanup blocker(s) are resolved.";
        return new StagedPackDeletionBatchPlan(
            plans,
            ordered,
            distinctBlockers,
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
            if (health.State == InstallHealthState.None)
            {
                return null;
            }

            throw new InvalidDataException(
                $"SpinTexture could not prove that no staged pack is active. {health.Summary}");
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

    private static string BuildPlanSummary(
        bool isCurrentInstall,
        IReadOnlyList<StagedPackDeletionDependency> dependencies,
        IReadOnlyList<string> blockers)
    {
        if (isCurrentInstall)
        {
            return "This pack is referenced by the current install transaction. Restore or switch packs before deleting it.";
        }

        var installedDependencies = dependencies
            .Where(dependency => dependency.IsReferencedByCurrentInstall)
            .ToArray();
        if (installedDependencies.Length != 0)
        {
            return installedDependencies.Length == 1
                ? $"The installed composition '{installedDependencies[0].CompositionId}' needs this source pack. Switch installs before deleting it."
                : $"{installedDependencies.Length:N0} installed compositions need this source pack. Switch installs before deleting it.";
        }

        if (blockers.Count != 0)
        {
            return $"SpinTexture could not prove this pack is safe to delete. {blockers[0]}";
        }

        if (dependencies.Count != 0)
        {
            return dependencies.Count == 1
                ? $"Composition '{dependencies[0].CompositionId}' depends on this pack. Check both for deletion so the composition is removed first."
                : $"{dependencies.Count:N0} compositions depend on this pack. Check them together so compositions are removed first.";
        }

        return "This completed pack is not active, has no retained dependent compositions, and is contained safely in the managed staging workspace.";
    }

    private static IReadOnlyList<StagedPackDeletionPlan> OrderPlansForDeletion(
        IReadOnlyList<StagedPackDeletionPlan> plans)
    {
        var remaining = plans.ToDictionary(
            plan => plan.ManifestPath,
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<StagedPackDeletionPlan>(plans.Count);
        while (remaining.Count != 0)
        {
            var next = remaining.Values
                .Where(plan => plan.CompositionDependencies.All(dependency =>
                    !remaining.ContainsKey(dependency.CompositionManifestPath)))
                .OrderByDescending(plan => plan.IsComposition)
                .ThenBy(plan => plan.BuildId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            ordered.Add(next);
            remaining.Remove(next.ManifestPath);
        }

        return ordered;
    }

    private static async Task<DependencyScanResult> FindCompositionDependenciesAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var dependencies = new Dictionary<
            string,
            List<StagedPackDeletionDependency>>(StringComparer.OrdinalIgnoreCase);
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
                if (!File.Exists(compositionPath))
                {
                    continue;
                }

                var compositionManifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(compositionManifestPath))
                {
                    throw new InvalidDataException(
                        "A composition document has no completed manifest.json commit marker.");
                }

                var document = await ReadCompositionAsync(
                        compositionPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateCompositionDocument(paths, directory, document);
                var componentPaths = new List<string>(document.Components.Count);
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
                    await FileIntegrity.EnsureMatchesAsync(
                            componentManifestPath,
                            component.ManifestLength,
                            component.ManifestSha256,
                            $"Composition component for {document.CompositionId}",
                            cancellationToken)
                        .ConfigureAwait(false);
                    componentPaths.Add(componentManifestPath);
                }

                foreach (var componentManifestPath in componentPaths.Distinct(
                             StringComparer.OrdinalIgnoreCase))
                {
                    if (!dependencies.TryGetValue(
                            componentManifestPath,
                            out var componentDependencies))
                    {
                        componentDependencies = [];
                        dependencies.Add(
                            componentManifestPath,
                            componentDependencies);
                    }

                    componentDependencies.Add(new StagedPackDeletionDependency(
                        document.CompositionId,
                        compositionManifestPath));
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
            dependencies.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<StagedPackDeletionDependency>)pair.Value
                    .DistinctBy(
                        dependency => dependency.CompositionManifestPath,
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(
                        dependency => dependency.CompositionId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase),
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

        if (File.Exists(Path.Combine(safeBuildDirectory, "build-checkpoint.json")))
        {
            throw new InvalidDataException(
                "Deletion is blocked because this pack is still finalizing or can be resumed.");
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
            EnsureEntryAncestorsRemainSafe(buildDirectory, file);
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
            EnsureEntryAncestorsRemainSafe(buildDirectory, directory);
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The staged pack changed to an unsafe directory before deletion: {directory}");
            }

            Directory.Delete(directory, recursive: false);
        }
    }

    private static void EnsureEntryAncestorsRemainSafe(
        string buildDirectory,
        string entry)
    {
        var safeRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(buildDirectory));
        var safeEntry = Path.GetFullPath(entry);
        if (!PathGuard.SamePath(safeRoot, safeEntry)
            && !safeEntry.StartsWith(
                safeRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The staged pack changed to an out-of-root entry before deletion: {entry}");
        }

        if (PathGuard.SamePath(safeRoot, safeEntry))
        {
            return;
        }

        var ancestor = Path.GetDirectoryName(safeEntry);
        while (ancestor is not null && !PathGuard.SamePath(ancestor, safeRoot))
        {
            if (!Directory.Exists(ancestor))
            {
                throw new InvalidDataException(
                    $"The staged pack hierarchy changed before deletion: {ancestor}");
            }

            var attributes = File.GetAttributes(ancestor);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The staged pack gained an unsafe ancestor before deletion: {ancestor}");
            }

            ancestor = Path.GetDirectoryName(ancestor);
        }

        if (ancestor is null)
        {
            throw new InvalidDataException(
                $"The staged pack hierarchy escaped its managed root before deletion: {entry}");
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
        IReadOnlyDictionary<
            string,
            IReadOnlyList<StagedPackDeletionDependency>> DependenciesByManifest,
        IReadOnlyList<string> SafetyBlockers);
}
