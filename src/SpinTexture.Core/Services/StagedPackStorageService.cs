using System.Security;
using System.Text.Json;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

internal sealed record StagedPackStorageSettings(
    int SchemaVersion,
    string InstallPath,
    string StagingPath)
{
    public const int CurrentSchemaVersion = 1;
}

internal static class StagedPackStorageSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ResolveStagingPath(
        string installPath,
        string workspacePath,
        string defaultStagingPath)
    {
        var settingsPath = Path.Combine(workspacePath, "pack-storage.json");
        if (!File.Exists(settingsPath))
        {
            return Path.GetFullPath(defaultStagingPath);
        }

        StagedPackStorageSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<StagedPackStorageSettings>(
                    File.ReadAllText(settingsPath),
                    JsonOptions)
                ?? throw new InvalidDataException("The pack-storage setting is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The saved staged-pack location is unreadable. Restore or remove pack-storage.json from the SpinTexture profile.",
                exception);
        }

        Validate(settings, installPath, workspacePath);
        return Path.GetFullPath(settings.StagingPath);
    }

    public static async Task WriteAsync(
        ProjectPaths paths,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.WorkspacePath);
        var settings = new StagedPackStorageSettings(
            StagedPackStorageSettings.CurrentSchemaVersion,
            paths.InstallPath,
            Path.GetFullPath(stagingPath));
        Validate(settings, paths.InstallPath, paths.WorkspacePath);
        var temporaryPath = AtomicFile.CreateTemporarySiblingPath(paths.StorageSettingsPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.CommitTemporaryFile(temporaryPath, paths.StorageSettingsPath);
        }
        catch
        {
            AtomicFile.TryDelete(temporaryPath);
            throw;
        }
    }

    private static void Validate(
        StagedPackStorageSettings settings,
        string installPath,
        string workspacePath)
    {
        if (settings.SchemaVersion != StagedPackStorageSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported pack-storage settings schema {settings.SchemaVersion}.");
        }

        if (!PathGuard.SamePath(settings.InstallPath, installPath))
        {
            throw new InvalidDataException(
                "The saved staged-pack location belongs to another EverQuest installation.");
        }

        if (!Path.IsPathFullyQualified(settings.StagingPath))
        {
            throw new InvalidDataException("The saved staged-pack location is not an absolute path.");
        }

        var fullStagingPath = Path.GetFullPath(settings.StagingPath);
        if (PathGuard.SamePath(fullStagingPath, workspacePath)
            || PathGuard.SamePath(fullStagingPath, installPath)
            || PathGuard.IsPathUnderRoot(installPath, fullStagingPath))
        {
            throw new InvalidDataException(
                "The staged-pack library cannot be the game directory or a folder inside it.");
        }

        var defaultStagingPath = Path.Combine(workspacePath, "Staging");
        if (!PathGuard.SamePath(fullStagingPath, defaultStagingPath))
        {
            var profileDirectory = Path.GetFileName(fullStagingPath);
            var managedParent = Path.GetDirectoryName(fullStagingPath);
            if (!string.Equals(profileDirectory, Path.GetFileName(workspacePath), StringComparison.OrdinalIgnoreCase)
                || managedParent is null
                || !string.Equals(Path.GetFileName(managedParent), "SpinTexture Packs", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The saved custom pack location is not a SpinTexture-owned install-specific library.");
            }

            if ((Directory.Exists(managedParent)
                 && (File.GetAttributes(managedParent) & FileAttributes.ReparsePoint) != 0)
                || (Directory.Exists(fullStagingPath)
                    && (File.GetAttributes(fullStagingPath) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException(
                    "The saved custom pack location cannot traverse a shortcut, junction, or reparse point.");
            }
        }
    }
}

public sealed class StagedPackStorageService
{
    private const long FreeSpaceReserve = 256L * 1024 * 1024;
    private readonly ManifestStore manifestStore = new();

    public Task<StagedPackStorageStatus> InspectAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotTree(paths.StagingPath, allowMissing: true, cancellationToken);
        var packCount = Directory.Exists(paths.StagingPath)
            ? Directory.EnumerateDirectories(paths.StagingPath, "*", SearchOption.TopDirectoryOnly)
                .Count(directory => File.Exists(Path.Combine(directory, "manifest.json")))
            : 0;
        return Task.FromResult(new StagedPackStorageStatus(
            paths.StagingPath,
            paths.DefaultStagingPath,
            snapshot.Sum(file => file.Length),
            snapshot.Count,
            packCount,
            PathGuard.SamePath(paths.StagingPath, paths.DefaultStagingPath)));
    }

    public async Task<StagedPackStoragePlan> PlanAsync(
        ProjectPaths paths,
        string destinationBasePath,
        bool useDefaultLocation = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBasePath);
        cancellationToken.ThrowIfCancellationRequested();
        var destinationPath = useDefaultLocation
            ? paths.DefaultStagingPath
            : BuildManagedDestination(paths, destinationBasePath);
        ValidateDestination(paths, destinationBasePath, destinationPath, useDefaultLocation);

        if (PathGuard.SamePath(paths.StagingPath, destinationPath))
        {
            var current = SnapshotTree(paths.StagingPath, allowMissing: true, cancellationToken);
            return new StagedPackStoragePlan(
                paths.StagingPath,
                destinationPath,
                current.Sum(file => file.Length),
                current.Count,
                useDefaultLocation,
                true,
                true,
                "The staged-pack library is already stored in this location.");
        }

        EnsurePathsDoNotOverlap(paths.StagingPath, destinationPath);
        var source = SnapshotTree(paths.StagingPath, allowMissing: true, cancellationToken);
        var target = SnapshotTree(destinationPath, allowMissing: true, cancellationToken);
        var mirror = target.Count != 0 && await TreesMatchAsync(
                paths.StagingPath,
                destinationPath,
                source,
                target,
                cancellationToken)
            .ConfigureAwait(false);
        if (target.Count != 0 && !mirror)
        {
            throw new InvalidOperationException(
                "The destination already contains different files. Choose an empty folder so SpinTexture cannot overwrite unrelated data.");
        }

        if (!mirror)
        {
            EnsureFreeSpace(destinationPath, source.Sum(file => file.Length));
        }

        var bytes = source.Sum(file => file.Length);
        return new StagedPackStoragePlan(
            paths.StagingPath,
            destinationPath,
            bytes,
            source.Count,
            useDefaultLocation,
            mirror,
            false,
            mirror
                ? "A complete verified copy already exists there. SpinTexture can safely switch to it."
                : source.Count == 0
                    ? "The new managed pack library will be created in the selected location."
                    : $"SpinTexture will copy and SHA-256 verify {source.Count:N0} staged file(s) before switching locations.");
    }

    public async Task<StagedPackStorageMoveResult> MoveAsync(
        ProjectPaths paths,
        StagedPackStoragePlan requestedPlan,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(requestedPlan);
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);
        using var stagingLock = StagingLibraryLock.Acquire(paths.WorkspacePath);
        var basePath = requestedPlan.UsesDefaultDestination
            ? paths.DefaultStagingPath
            : GetSelectedBaseFromManagedDestination(requestedPlan.DestinationPath);
        var plan = await PlanAsync(
                paths,
                basePath,
                requestedPlan.UsesDefaultDestination,
                cancellationToken)
            .ConfigureAwait(false);
        if (plan.IsNoOp)
        {
            return new StagedPackStorageMoveResult(
                await InspectAsync(paths, cancellationToken).ConfigureAwait(false),
                true,
                null);
        }

        var sourceSnapshot = SnapshotTree(plan.SourcePath, allowMissing: true, cancellationToken);
        string? transferPath = null;
        var destinationCommitted = plan.DestinationIsVerifiedMirror;
        var relocatedInstallManifests = new List<RelocatedInstallManifest>();
        try
        {
            if (!destinationCommitted)
            {
                var parent = Path.GetDirectoryName(plan.DestinationPath)
                    ?? throw new InvalidOperationException("The pack-library destination has no parent folder.");
                Directory.CreateDirectory(parent);
                transferPath = Path.Combine(
                    parent,
                    $".{Path.GetFileName(plan.DestinationPath)}.moving-{Guid.NewGuid():N}");
                Directory.CreateDirectory(transferPath);

                for (var index = 0; index < sourceSnapshot.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = sourceSnapshot[index];
                    var sourcePath = Path.Combine(plan.SourcePath, file.RelativePath);
                    var destinationPath = Path.Combine(transferPath, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    progress?.Report(new ProgressUpdate(
                        "Move pack library",
                        $"Copying and verifying {index + 1:N0} of {sourceSnapshot.Count:N0} files",
                        index,
                        Math.Max(1, sourceSnapshot.Count),
                        file.RelativePath));
                    var sourceFingerprint = await FileIntegrity.FingerprintAsync(sourcePath, cancellationToken)
                        .ConfigureAwait(false);
                    await AtomicFile.CopyDurablyAsync(sourcePath, destinationPath, cancellationToken)
                        .ConfigureAwait(false);
                    await FileIntegrity.EnsureMatchesAsync(
                            destinationPath,
                            sourceFingerprint.Length,
                            sourceFingerprint.Sha256,
                            "Copied staged-pack file",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                EnsureSnapshotUnchanged(
                    plan.SourcePath,
                    sourceSnapshot,
                    cancellationToken);
                if (Directory.Exists(plan.DestinationPath))
                {
                    if (Directory.EnumerateFileSystemEntries(plan.DestinationPath).Any())
                    {
                        throw new IOException("The destination changed during transfer and is no longer empty.");
                    }

                    Directory.Delete(plan.DestinationPath);
                }

                Directory.Move(transferPath, plan.DestinationPath);
                transferPath = null;
                destinationCommitted = true;
            }

            var destinationSnapshot = SnapshotTree(plan.DestinationPath, allowMissing: false, cancellationToken);
            if (!await TreesMatchAsync(
                    plan.SourcePath,
                    plan.DestinationPath,
                    sourceSnapshot,
                    destinationSnapshot,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The transferred staged-pack library did not match its source exactly.");
            }

            relocatedInstallManifests = await RelocateInstallManifestReferencesAsync(
                    paths,
                    plan.SourcePath,
                    plan.DestinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await StagedPackStorageSettingsStore.WriteAsync(
                    paths,
                    plan.DestinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await RestoreInstallManifestReferencesAsync(relocatedInstallManifests)
                .ConfigureAwait(false);
            if (transferPath is not null)
            {
                TryDeleteTree(transferPath);
            }

            if (destinationCommitted && !plan.DestinationIsVerifiedMirror)
            {
                TryDeleteTree(plan.DestinationPath);
            }

            throw;
        }

        string? cleanupWarning = null;
        var removed = false;
        try
        {
            DeleteTree(plan.SourcePath);
            removed = !Directory.Exists(plan.SourcePath);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            InvalidDataException)
        {
            cleanupWarning =
                $"The new location is active and verified, but the old copy could not be removed: {exception.Message}";
        }

        var currentPaths = new ProjectPaths(paths.InstallPath, paths.WorkspacePath, plan.DestinationPath);
        var status = await InspectAsync(currentPaths, CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate(
            "Move pack library",
            "The verified staged-pack library is now active in its new location.",
            Math.Max(1, plan.FileCount),
            Math.Max(1, plan.FileCount),
            plan.DestinationPath));
        return new StagedPackStorageMoveResult(status, removed, cleanupWarning);
    }

    private async Task<List<RelocatedInstallManifest>> RelocateInstallManifestReferencesAsync(
        ProjectPaths paths,
        string sourceStagingPath,
        string destinationStagingPath,
        CancellationToken cancellationToken)
    {
        var relocated = new List<RelocatedInstallManifest>();
        if (!Directory.Exists(paths.BackupPath))
        {
            return relocated;
        }

        try
        {
            foreach (var transactionDirectory in Directory.EnumerateDirectories(
                         paths.BackupPath,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(transactionDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The backup transaction workspace contains an unsafe reparse point.");
                }

                var manifestPath = Path.Combine(transactionDirectory, "install-manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var original = await manifestStore.ReadInstallManifestAsync(
                        manifestPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!PathGuard.SamePath(original.InstallPath, paths.InstallPath)
                    || !PathGuard.IsPathUnderRoot(sourceStagingPath, original.BuildManifestPath))
                {
                    continue;
                }

                var relativeManifestPath = Path.GetRelativePath(
                    sourceStagingPath,
                    Path.GetFullPath(original.BuildManifestPath));
                var relocatedManifestPath = PathGuard.ResolveUnderRoot(
                    destinationStagingPath,
                    relativeManifestPath);
                var buildDirectory = Path.GetDirectoryName(relocatedManifestPath)
                    ?? throw new InvalidDataException("An installed pack reference has no build directory.");
                if (!File.Exists(relocatedManifestPath)
                    && original.State is InstallTransactionState.Restored or InstallTransactionState.RolledBack)
                {
                    continue;
                }

                if (!Path.GetFileName(relocatedManifestPath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    || !PathGuard.SamePath(Path.GetDirectoryName(buildDirectory)!, destinationStagingPath)
                    || !Path.GetFileName(buildDirectory).Equals(original.BuildId, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(relocatedManifestPath))
                {
                    throw new InvalidDataException(
                        "An install transaction references a staged manifest that could not be relocated safely.");
                }

                relocated.Add(new RelocatedInstallManifest(manifestPath, original));
                await manifestStore.WriteInstallManifestAsync(
                        manifestPath,
                        original with { BuildManifestPath = relocatedManifestPath },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return relocated;
        }
        catch
        {
            await RestoreInstallManifestReferencesAsync(relocated).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RestoreInstallManifestReferencesAsync(
        IReadOnlyList<RelocatedInstallManifest> relocated)
    {
        List<Exception>? failures = null;
        foreach (var item in relocated.Reverse())
        {
            try
            {
                await manifestStore.WriteInstallManifestAsync(
                        item.Path,
                        item.Original,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures ??= [];
                failures.Add(exception);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "The pack-location switch failed and one or more install references could not be rolled back.",
                failures);
        }
    }

    private static string BuildManagedDestination(ProjectPaths paths, string basePath)
    {
        var fullBase = Path.GetFullPath(basePath);
        return Path.Combine(
            fullBase,
            "SpinTexture Packs",
            Path.GetFileName(paths.WorkspacePath));
    }

    private static string GetSelectedBaseFromManagedDestination(string destinationPath)
    {
        var profileParent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
            ?? throw new InvalidDataException("The managed pack location is invalid.");
        return Path.GetDirectoryName(profileParent)
            ?? throw new InvalidDataException("The managed pack location has no storage root.");
    }

    private static void ValidateDestination(
        ProjectPaths paths,
        string selectedBase,
        string destinationPath,
        bool useDefault)
    {
        var fullBase = Path.GetFullPath(selectedBase);
        var fullDestination = Path.GetFullPath(destinationPath);
        if (!useDefault && !Directory.Exists(fullBase))
        {
            throw new DirectoryNotFoundException("Choose an existing destination folder.");
        }

        if ((Directory.Exists(fullBase)
             && (File.GetAttributes(fullBase) & FileAttributes.ReparsePoint) != 0)
            || (Directory.Exists(fullDestination)
                && (File.GetAttributes(fullDestination) & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("The pack-library location cannot be a shortcut, junction, or reparse point.");
        }

        PathGuard.EnsurePathUnderRoot(
            useDefault ? paths.WorkspacePath : fullBase,
            fullDestination);

        if (PathGuard.SamePath(fullDestination, paths.InstallPath)
            || PathGuard.IsPathUnderRoot(paths.InstallPath, fullDestination))
        {
            throw new InvalidDataException(
                "Choose a storage folder outside the EverQuest installation.");
        }
    }

    private static void EnsurePathsDoNotOverlap(string sourcePath, string destinationPath)
    {
        if (PathGuard.IsPathUnderRoot(sourcePath, destinationPath)
            || PathGuard.IsPathUnderRoot(destinationPath, sourcePath))
        {
            throw new InvalidDataException(
                "The old and new pack-library locations cannot contain one another.");
        }
    }

    private static void EnsureFreeSpace(string destinationPath, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationPath))
            ?? throw new InvalidOperationException("Windows could not determine the destination drive.");
        var drive = new DriveInfo(root);
        var required = checked(requiredBytes + FreeSpaceReserve);
        if (drive.AvailableFreeSpace < required)
        {
            throw new IOException(
                $"The destination needs at least {required / (1024d * 1024 * 1024):0.##} GB free for a verified transfer.");
        }
    }

    private static List<StorageFile> SnapshotTree(
        string root,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        var files = new List<StorageFile>();
        if (!Directory.Exists(root))
        {
            if (allowMissing)
            {
                return files;
            }

            throw new DirectoryNotFoundException($"The staged-pack library is missing: {root}");
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The staged-pack library root cannot be a reparse point.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"The staged-pack library contains an unsafe reparse point: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(new StorageFile(
                        Path.GetRelativePath(root, entry),
                        new FileInfo(entry).Length));
                }
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<bool> TreesMatchAsync(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<StorageFile> source,
        IReadOnlyList<StorageFile> destination,
        CancellationToken cancellationToken)
    {
        if (source.Count != destination.Count)
        {
            return false;
        }

        for (var index = 0; index < source.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source[index].RelativePath, destination[index].RelativePath, StringComparison.OrdinalIgnoreCase)
                || source[index].Length != destination[index].Length)
            {
                return false;
            }

            var left = await FileIntegrity.FingerprintAsync(
                    Path.Combine(sourceRoot, source[index].RelativePath),
                    cancellationToken)
                .ConfigureAwait(false);
            var right = await FileIntegrity.FingerprintAsync(
                    Path.Combine(destinationRoot, destination[index].RelativePath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (left.Length != right.Length
                || !string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureSnapshotUnchanged(
        string root,
        IReadOnlyList<StorageFile> expected,
        CancellationToken cancellationToken)
    {
        var current = SnapshotTree(root, allowMissing: false, cancellationToken);
        if (current.Count != expected.Count)
        {
            throw new IOException("The staged-pack library changed during transfer. No location setting was changed.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(expected[index].RelativePath, current[index].RelativePath, StringComparison.OrdinalIgnoreCase)
                || expected[index].Length != current[index].Length)
            {
                throw new IOException("The staged-pack library changed during transfer. No location setting was changed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The managed staging root became a reparse point before cleanup.");
        }

        var directories = new List<string> { root };
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("A reparse point appeared while cleaning the old pack library.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        foreach (var file in files.OrderByDescending(path => path.Length))
        {
            File.Delete(file);
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            DeleteTree(root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (SecurityException)
        {
        }
    }

    private sealed record StorageFile(string RelativePath, long Length);
    private sealed record RelocatedInstallManifest(string Path, InstallManifest Original);
}
