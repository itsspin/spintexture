using SpinTexture.Core.Models;

namespace SpinTexture.Core.Pipeline;

public sealed class StagedBuildService
{
    private readonly ManifestStore _manifestStore;

    public StagedBuildService(ManifestStore? manifestStore = null)
    {
        _manifestStore = manifestStore ?? new ManifestStore();
    }

    public async Task<StagedBuildResult> BuildAsync(
        StagedBuildRequest request,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.Items);

        if (request.Items.Count == 0)
        {
            throw new ArgumentException("A staged build must contain at least one install artifact.", nameof(request));
        }

        using var stagingLock = StagingLibraryLock.Acquire(request.Paths.WorkspacePath);
        request.Paths.EnsureWorkspaceDirectories();
        var buildId = PathGuard.ValidateIdentifier(request.BuildId, "build");
        var buildDirectory = PathGuard.ResolveUnderRoot(request.Paths.StagingPath, buildId);
        if (Directory.Exists(buildDirectory) || File.Exists(buildDirectory))
        {
            throw new IOException($"Build transaction already exists: {buildDirectory}");
        }

        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        var workDirectory = Path.Combine(buildDirectory, "work");
        var previewDirectory = Path.Combine(buildDirectory, "previews");
        Directory.CreateDirectory(payloadDirectory);
        Directory.CreateDirectory(workDirectory);

        try
        {
            var normalizedItems = NormalizeItems(request.Paths, request.Items);
            var entries = new List<BuildManifestEntry>(normalizedItems.Count);
            progress?.Report(new ProgressUpdate("Build", "Building into staging; the EverQuest install remains read-only.", 0, normalizedItems.Count));

            for (var index = 0; index < normalizedItems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = normalizedItems[index];
                progress?.Report(new ProgressUpdate(
                    "Build",
                    "Rebuilding complete install artifact in staging.",
                    index,
                    normalizedItems.Count,
                    item.RelativeInstallPath));

                var sourceFingerprint = await FileIntegrity.FingerprintAsync(
                    item.SourcePath,
                    cancellationToken).ConfigureAwait(false);
                if (item.ExpectedSourceLength is not null
                    && (sourceFingerprint.Length != item.ExpectedSourceLength.Value
                        || !sourceFingerprint.Sha256.Equals(
                            item.ExpectedSourceSha256,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"Verified build source no longer matches its expected SHA-256: {item.SourcePath}");
                }

                var finalStagedPath = PathGuard.ResolveUnderRoot(payloadDirectory, item.RelativeInstallPath);
                Directory.CreateDirectory(Path.GetDirectoryName(finalStagedPath)!);
                var temporaryStagedPath = AtomicFile.CreateTemporarySiblingPath(finalStagedPath);
                var artifactWorkDirectory = Path.Combine(workDirectory, $"{index:D5}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(artifactWorkDirectory);
                var sourceSnapshotDirectory = Path.Combine(artifactWorkDirectory, "source");
                Directory.CreateDirectory(sourceSnapshotDirectory);
                var sourceSnapshotPath = Path.Combine(sourceSnapshotDirectory, Path.GetFileName(item.SourcePath));
                await AtomicFile.CopyAndReplaceAsync(
                    item.SourcePath,
                    sourceSnapshotPath,
                    sourceFingerprint.Length,
                    sourceFingerprint.Sha256,
                    cancellationToken).ConfigureAwait(false);
                await using var sourceSnapshotGuard = new FileStream(
                    sourceSnapshotPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var context = new StagedArtifactBuildContext(
                    item.RelativeInstallPath,
                    sourceSnapshotPath,
                    temporaryStagedPath,
                    artifactWorkDirectory,
                    previewDirectory,
                    request.Options);

                await item.Builder.BuildAsync(context, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(temporaryStagedPath))
                {
                    throw new InvalidDataException(
                        $"Artifact builder did not produce its required staged output: {temporaryStagedPath}");
                }

                var sourceAfterBuild = await FileIntegrity.FingerprintAsync(
                    item.SourcePath,
                    cancellationToken).ConfigureAwait(false);
                if (sourceAfterBuild != sourceFingerprint)
                {
                    throw new IOException(
                        $"Source artifact changed while the staged build was running: {item.SourcePath}");
                }

                await FileIntegrity.EnsureMatchesAsync(
                    sourceSnapshotPath,
                    sourceFingerprint.Length,
                    sourceFingerprint.Sha256,
                    "Read-only source snapshot after artifact build",
                    cancellationToken).ConfigureAwait(false);

                var stagedFingerprint = await FileIntegrity.FingerprintAsync(
                    temporaryStagedPath,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ExpectedStagedLength is not null
                    && (stagedFingerprint.Length != item.ExpectedStagedLength.Value
                        || !stagedFingerprint.Sha256.Equals(
                            item.ExpectedStagedSha256,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"Reused staged output no longer matches its expected SHA-256: {item.RelativeInstallPath}");
                }

                if (stagedFingerprint == sourceFingerprint)
                {
                    AtomicFile.TryDelete(temporaryStagedPath);
                    if (request.RequireAllItems)
                    {
                        throw new InvalidOperationException(
                            $"A required replacement artifact contained no verified changes: {item.RelativeInstallPath}");
                    }

                    progress?.Report(new ProgressUpdate(
                        "Build",
                        "Artifact contained no eligible changes and was omitted from the pack.",
                        index + 1,
                        normalizedItems.Count,
                        item.RelativeInstallPath));
                    continue;
                }

                AtomicFile.CommitTemporaryFile(temporaryStagedPath, finalStagedPath);
                await FileIntegrity.EnsureMatchesAsync(
                    finalStagedPath,
                    stagedFingerprint.Length,
                    stagedFingerprint.Sha256,
                    "Committed staged artifact",
                    CancellationToken.None).ConfigureAwait(false);

                entries.Add(new BuildManifestEntry(
                    item.RelativeInstallPath,
                    sourceFingerprint.Length,
                    sourceFingerprint.Sha256,
                    stagedFingerprint.Length,
                    stagedFingerprint.Sha256));

                progress?.Report(new ProgressUpdate(
                    "Build",
                    "Staged artifact verified with SHA-256.",
                    index + 1,
                    normalizedItems.Count,
                    item.RelativeInstallPath));
            }

            if (entries.Count == 0)
            {
                throw new InvalidOperationException("The staged build produced no changed install artifacts.");
            }

            var manifest = new BuildManifest(
                BuildManifest.CurrentSchemaVersion,
                buildId,
                DateTimeOffset.UtcNow,
                Path.GetFullPath(request.Paths.InstallPath),
                request.Options,
                entries.AsReadOnly());
            var manifestPath = Path.Combine(buildDirectory, "manifest.json");
            await _manifestStore.WriteBuildManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            TryDeleteOwnedDirectory(request.Paths.StagingPath, workDirectory);
            progress?.Report(new ProgressUpdate(
                "Build",
                "Staged build is complete and ready for explicit apply.",
                normalizedItems.Count,
                normalizedItems.Count));

            return new StagedBuildResult(buildId, buildDirectory, manifestPath, manifest);
        }
        catch
        {
            TryDeleteOwnedDirectory(request.Paths.StagingPath, buildDirectory);
            throw;
        }
    }

    private static IReadOnlyList<NormalizedBuildItem> NormalizeItems(
        ProjectPaths paths,
        IReadOnlyList<StagedBuildItem> items)
    {
        var normalized = new List<NormalizedBuildItem>(items.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Builder);
            var liveSourcePath = PathGuard.ResolveUnderRoot(paths.InstallPath, item.RelativeInstallPath);
            var sourcePath = string.IsNullOrWhiteSpace(item.VerifiedSourcePath)
                ? liveSourcePath
                : Path.GetFullPath(item.VerifiedSourcePath);
            if ((item.ExpectedSourceLength is null) != (item.ExpectedSourceSha256 is null)
                || item.ExpectedSourceLength < 0
                || (item.ExpectedSourceSha256 is not null
                    && (item.ExpectedSourceSha256.Length != 64
                        || item.ExpectedSourceSha256.Any(character => !Uri.IsHexDigit(character)))))
            {
                throw new ArgumentException(
                    $"Expected source fingerprint metadata is invalid for {item.RelativeInstallPath}.",
                    nameof(items));
            }
            if ((item.ExpectedStagedLength is null) != (item.ExpectedStagedSha256 is null)
                || item.ExpectedStagedLength < 0
                || (item.ExpectedStagedSha256 is not null
                    && (item.ExpectedStagedSha256.Length != 64
                        || item.ExpectedStagedSha256.Any(character => !Uri.IsHexDigit(character)))))
            {
                throw new ArgumentException(
                    $"Expected staged fingerprint metadata is invalid for {item.RelativeInstallPath}.",
                    nameof(items));
            }
            if (!string.IsNullOrWhiteSpace(item.VerifiedSourcePath)
                && !PathGuard.IsPathUnderRoot(paths.BackupPath, sourcePath)
                && !PathGuard.SamePath(liveSourcePath, sourcePath))
            {
                throw new InvalidDataException(
                    $"A staged build source override must be the live artifact or a managed backup: {sourcePath}");
            }
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Install artifact selected for build was not found.", sourcePath);
            }

            var relativePath = Path.GetRelativePath(paths.InstallPath, liveSourcePath);
            if (!seenPaths.Add(relativePath))
            {
                throw new ArgumentException($"Duplicate staged build artifact: {relativePath}", nameof(items));
            }

            normalized.Add(new NormalizedBuildItem(
                relativePath,
                sourcePath,
                item.Builder,
                item.ExpectedSourceLength,
                item.ExpectedSourceSha256,
                item.ExpectedStagedLength,
                item.ExpectedStagedSha256));
        }

        return normalized;
    }

    private sealed record NormalizedBuildItem(
        string RelativeInstallPath,
        string SourcePath,
        IStagedArtifactBuilder Builder,
        long? ExpectedSourceLength,
        string? ExpectedSourceSha256,
        long? ExpectedStagedLength,
        string? ExpectedStagedSha256);

    private static void TryDeleteOwnedDirectory(string stagingRoot, string directory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var relative = Path.GetRelativePath(root, target);
            if (relative is "." or ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathFullyQualified(relative))
            {
                return;
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort; uniquely named transaction paths cannot be reused accidentally.
        }
    }
}
