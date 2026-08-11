using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.Core.Pipeline;

public sealed class InstallTransactionService
{
    private readonly ManifestStore _manifestStore;
    private readonly IAtomicFileOperations _atomicFileOperations;
    private readonly Action<string> _ensureGameStopped;

    public InstallTransactionService(ManifestStore? manifestStore = null)
        : this(manifestStore, new AtomicFileOperations(), EnsureGameStopped)
    {
    }

    internal InstallTransactionService(
        ManifestStore? manifestStore,
        IAtomicFileOperations atomicFileOperations,
        Action<string>? ensureGameStopped = null)
    {
        _manifestStore = manifestStore ?? new ManifestStore();
        _atomicFileOperations = atomicFileOperations
            ?? throw new ArgumentNullException(nameof(atomicFileOperations));
        _ensureGameStopped = ensureGameStopped ?? EnsureGameStopped;
    }

    public async Task<ApplyResult> ApplyAsync(
        ProjectPaths paths,
        string buildManifestPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        string? applyId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildManifestPath);
        _ensureGameStopped("apply");
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);

        var safeManifestPath = PathGuard.EnsurePathUnderRoot(paths.StagingPath, buildManifestPath);
        var build = await _manifestStore.ReadBuildManifestAsync(safeManifestPath, cancellationToken).ConfigureAwait(false);
        var prepared = await PreflightBuildAsync(
            paths,
            safeManifestPath,
            build,
            progress,
            cancellationToken).ConfigureAwait(false);

        var transactionId = PathGuard.ValidateIdentifier(applyId, "apply");
        var backupDirectory = PathGuard.ResolveUnderRoot(paths.BackupPath, transactionId);
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
        {
            throw new IOException($"Install transaction already exists: {backupDirectory}");
        }

        var backupPayloadDirectory = Path.Combine(backupDirectory, "payload");
        Directory.CreateDirectory(backupPayloadDirectory);
        var installedEntries = new List<InstalledArtifact>(prepared.Count);
        progress?.Report(new ProgressUpdate("Backup", "Creating verified backups before changing the install.", 0, prepared.Count));

        for (var index = 0; index < prepared.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = prepared[index];
            var backupPath = PathGuard.ResolveUnderRoot(backupPayloadDirectory, artifact.Entry.RelativeInstallPath);
            await _atomicFileOperations.CopyAndReplaceAsync(
                artifact.InstallPath,
                backupPath,
                artifact.Entry.SourceLength,
                artifact.Entry.SourceSha256,
                cancellationToken).ConfigureAwait(false);

            installedEntries.Add(new InstalledArtifact(
                artifact.Entry.RelativeInstallPath,
                OriginalExisted: true,
                artifact.Entry.SourceLength,
                artifact.Entry.SourceSha256,
                Path.GetRelativePath(backupDirectory, backupPath),
                artifact.Entry.StagedLength,
                artifact.Entry.StagedSha256));
            progress?.Report(new ProgressUpdate(
                "Backup",
                "Original artifact backed up and verified.",
                index + 1,
                prepared.Count,
                artifact.Entry.RelativeInstallPath));
        }

        var installManifest = new InstallManifest(
            InstallManifest.CurrentSchemaVersion,
            transactionId,
            DateTimeOffset.UtcNow,
            Path.GetFullPath(paths.InstallPath),
            build.BuildId,
            safeManifestPath,
            InstallTransactionState.Preparing,
            installedEntries.AsReadOnly());
        var installManifestPath = Path.Combine(backupDirectory, "install-manifest.json");
        await _manifestStore.WriteInstallManifestAsync(
            installManifestPath,
            installManifest,
            cancellationToken).ConfigureAwait(false);

        var applied = new List<PreparedBuildArtifact>(prepared.Count);
        try
        {
            for (var index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = prepared[index];
                await FileIntegrity.EnsureMatchesAsync(
                    artifact.InstallPath,
                    artifact.Entry.SourceLength,
                    artifact.Entry.SourceSha256,
                    "Live install artifact immediately before apply",
                    cancellationToken).ConfigureAwait(false);
                _ensureGameStopped("apply");
                await _atomicFileOperations.CopyAndReplaceAsync(
                    artifact.StagedPath,
                    artifact.InstallPath,
                    artifact.Entry.StagedLength,
                    artifact.Entry.StagedSha256,
                    cancellationToken,
                    onCommitted: () => applied.Add(artifact)).ConfigureAwait(false);
                installedEntries[index] = installedEntries[index] with
                {
                    InstalledLastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(artifact.InstallPath).Ticks
                };
                progress?.Report(new ProgressUpdate(
                    "Apply",
                    "Enhanced artifact installed atomically.",
                    index + 1,
                    prepared.Count,
                    artifact.Entry.RelativeInstallPath));
            }

            installManifest = installManifest with
            {
                State = InstallTransactionState.Applied,
                Entries = installedEntries.AsReadOnly()
            };
            await _manifestStore.WriteInstallManifestAsync(
                installManifestPath,
                installManifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception transactionFailure)
        {
            var rollbackFailures = await RollBackApplyAsync(
                backupDirectory,
                applied,
                progress).ConfigureAwait(false);
            installManifest = installManifest with
            {
                State = rollbackFailures.Count == 0
                    ? InstallTransactionState.RolledBack
                    : InstallTransactionState.RecoveryRequired
            };
            try
            {
                await _manifestStore.WriteInstallManifestAsync(
                    installManifestPath,
                    installManifest,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception manifestFailure)
            {
                rollbackFailures.Add(manifestFailure);
            }

            if (rollbackFailures.Count != 0)
            {
                throw new InstallTransactionException(
                    "Apply failed and one or more artifacts could not be rolled back automatically.",
                    transactionFailure,
                    rollbackFailures);
            }

            throw;
        }

        return new ApplyResult(transactionId, backupDirectory, installManifestPath, installManifest);
    }

    public async Task<RestoreResult> RestoreAsync(
        ProjectPaths paths,
        string installManifestPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(installManifestPath);
        _ensureGameStopped("restore");
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);
        var safeManifestPath = PathGuard.EnsurePathUnderRoot(paths.BackupPath, installManifestPath);
        var install = await _manifestStore.ReadInstallManifestAsync(safeManifestPath, cancellationToken).ConfigureAwait(false);
        ValidateInstallRoot(paths, install.InstallPath);
        ValidateUniquePaths(install.Entries.Select(entry => entry.RelativeInstallPath));
        if (!Enum.IsDefined(install.State))
        {
            throw new InvalidDataException($"Install manifest contains an unknown transaction state: {install.State}");
        }

        var backupDirectory = Path.GetDirectoryName(safeManifestPath)!;
        var prepared = new List<PreparedRestoreArtifact>(install.Entries.Count);
        progress?.Report(new ProgressUpdate("Restore preflight", "Verifying installed artifacts and backups.", 0, install.Entries.Count));

        for (var index = 0; index < install.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = install.Entries[index];
            ValidateHash(entry.InstalledSha256);
            var targetPath = PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath);

            string? originalBackupPath = null;
            if (entry.OriginalExisted)
            {
                if (entry.BackupRelativePath is null || entry.OriginalSha256 is null)
                {
                    throw new InvalidDataException("Install manifest does not identify the original backup.");
                }

                ValidateHash(entry.OriginalSha256);
                originalBackupPath = PathGuard.ResolveUnderRoot(backupDirectory, entry.BackupRelativePath);
                await FileIntegrity.EnsureMatchesAsync(
                    originalBackupPath,
                    entry.OriginalLength,
                    entry.OriginalSha256,
                    "Original backup artifact",
                    cancellationToken).ConfigureAwait(false);
            }

            var observedState = await ObserveInstallStateAsync(
                entry,
                targetPath,
                cancellationToken).ConfigureAwait(false);
            prepared.Add(new PreparedRestoreArtifact(entry, targetPath, originalBackupPath, observedState));
            progress?.Report(new ProgressUpdate(
                "Restore preflight",
                observedState == ObservedInstallState.Enhanced
                    ? "Enhanced artifact and original backup verified."
                    : "Artifact is already original; restore will safely resume past it.",
                index + 1,
                install.Entries.Count,
                entry.RelativeInstallPath));
        }

        var restoreSafetyDirectory = Path.Combine(
            backupDirectory,
            $"restore-safety-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(restoreSafetyDirectory);
        foreach (var artifact in prepared.Where(item => item.ObservedState == ObservedInstallState.Enhanced))
        {
            cancellationToken.ThrowIfCancellationRequested();
            artifact.SafetyPath = PathGuard.ResolveUnderRoot(
                restoreSafetyDirectory,
                artifact.Entry.RelativeInstallPath);
            await _atomicFileOperations.CopyAndReplaceAsync(
                artifact.TargetPath,
                artifact.SafetyPath,
                artifact.Entry.InstalledLength,
                artifact.Entry.InstalledSha256,
                cancellationToken).ConfigureAwait(false);
        }

        var restored = new List<PreparedRestoreArtifact>(prepared.Count);
        try
        {
            for (var index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = prepared[index];
                if (artifact.ObservedState == ObservedInstallState.Original)
                {
                    progress?.Report(new ProgressUpdate(
                        "Restore",
                        "Artifact was already original; resumed without rewriting it.",
                        index + 1,
                        prepared.Count,
                        artifact.Entry.RelativeInstallPath));
                    continue;
                }

                await FileIntegrity.EnsureMatchesAsync(
                    artifact.TargetPath,
                    artifact.Entry.InstalledLength,
                    artifact.Entry.InstalledSha256,
                    "Enhanced artifact immediately before restore",
                    cancellationToken).ConfigureAwait(false);
                _ensureGameStopped("restore");

                if (artifact.Entry.OriginalExisted)
                {
                    await _atomicFileOperations.CopyAndReplaceAsync(
                        artifact.OriginalBackupPath!,
                        artifact.TargetPath,
                        artifact.Entry.OriginalLength,
                        artifact.Entry.OriginalSha256!,
                        cancellationToken,
                        onCommitted: () => restored.Add(artifact)).ConfigureAwait(false);
                }
                else
                {
                    File.Delete(artifact.TargetPath);
                    if (File.Exists(artifact.TargetPath))
                    {
                        throw new IOException($"Could not remove enhanced artifact during restore: {artifact.TargetPath}");
                    }

                    restored.Add(artifact);
                }

                progress?.Report(new ProgressUpdate(
                    "Restore",
                    "Original artifact restored.",
                    index + 1,
                    prepared.Count,
                    artifact.Entry.RelativeInstallPath));
            }

            install = install with { State = InstallTransactionState.Restored };
            await _manifestStore.WriteInstallManifestAsync(
                safeManifestPath,
                install,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception transactionFailure)
        {
            var rollbackFailures = await RollBackRestoreAsync(restored, progress).ConfigureAwait(false);
            if (rollbackFailures.Count != 0)
            {
                throw new InstallTransactionException(
                    "Restore failed and the enhanced install could not be reinstated completely.",
                    transactionFailure,
                    rollbackFailures);
            }

            throw;
        }

        TryDeleteRestoreSafetyDirectory(backupDirectory, restoreSafetyDirectory);
        return new RestoreResult(install.ApplyId, prepared.Count, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Promotes an active install to a strict superset build without restoring or
    /// rewriting artifacts whose enhanced bytes are already active. Returns null
    /// when the selected build is not an additive superset, allowing the caller to
    /// use the normal full switch transaction instead.
    /// </summary>
    public async Task<ApplyResult?> TryPromoteAdditiveAsync(
        ProjectPaths paths,
        string activeInstallManifestPath,
        string selectedBuildManifestPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeInstallManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedBuildManifestPath);
        _ensureGameStopped("add to the active pack");
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);

        var safeInstallManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.BackupPath,
            activeInstallManifestPath);
        var safeBuildManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.StagingPath,
            selectedBuildManifestPath);
        var active = await _manifestStore.ReadInstallManifestAsync(
                safeInstallManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        var selected = await _manifestStore.ReadBuildManifestAsync(
                safeBuildManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateInstallRoot(paths, active.InstallPath);
        ValidateInstallRoot(paths, selected.InstallPath);
        if (active.State != InstallTransactionState.Applied)
        {
            throw new InvalidOperationException(
                $"Only a verified applied install can be promoted; current state is {active.State}.");
        }

        if (active.Entries is null || active.Entries.Count == 0
            || selected.Entries is null || selected.Entries.Count == 0)
        {
            throw new InvalidDataException("The active or selected manifest contains no install artifacts.");
        }

        ValidateUniquePaths(active.Entries.Select(entry => entry.RelativeInstallPath));
        ValidateUniquePaths(selected.Entries.Select(entry => entry.RelativeInstallPath));
        var activeByPath = active.Entries.ToDictionary(
            entry => entry.RelativeInstallPath,
            StringComparer.OrdinalIgnoreCase);
        var selectedByPath = selected.Entries.ToDictionary(
            entry => entry.RelativeInstallPath,
            StringComparer.OrdinalIgnoreCase);

        // Additive promotion is intentionally strict: every active output must be
        // carried by the selection byte-for-byte and must describe the same
        // original source. A removal or replacement uses the established full
        // restore/apply switch path instead.
        foreach (var installed in active.Entries)
        {
            ValidateInstalledArtifact(installed);
            if (!selectedByPath.TryGetValue(installed.RelativeInstallPath, out var carried))
            {
                return null;
            }

            ValidateBuildEntry(carried);
            if (!installed.OriginalExisted
                || installed.OriginalSha256 is null
                || installed.OriginalLength != carried.SourceLength
                || !installed.OriginalSha256.Equals(
                    carried.SourceSha256,
                    StringComparison.OrdinalIgnoreCase)
                || installed.InstalledLength != carried.StagedLength
                || !installed.InstalledSha256.Equals(
                    carried.StagedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var buildDirectory = Path.GetDirectoryName(safeBuildManifestPath)!;
        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        var backupDirectory = Path.GetDirectoryName(safeInstallManifestPath)!;
        var backupPayloadDirectory = PathGuard.ResolveUnderRoot(backupDirectory, "payload");
        var newArtifacts = new List<PreparedPromotionArtifact>();

        // Revalidate the active live set under the transaction lock. Length plus
        // the timestamp captured after the verified commit is the trusted fast
        // path; any metadata change falls back to an exact SHA-256 read.
        foreach (var installed in active.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var livePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                installed.RelativeInstallPath);
            await EnsureActiveArtifactMatchesAsync(installed, livePath, cancellationToken)
                .ConfigureAwait(false);

            if (installed.BackupRelativePath is null)
            {
                throw new InvalidDataException(
                    $"The active install has no original backup path for {installed.RelativeInstallPath}.");
            }

            var existingBackupPath = PathGuard.ResolveUnderRoot(
                backupDirectory,
                installed.BackupRelativePath);
            var backupInfo = new FileInfo(existingBackupPath);
            backupInfo.Refresh();
            if (!backupInfo.Exists || backupInfo.Length != installed.OriginalLength)
            {
                throw new InvalidDataException(
                    $"The active original backup is missing or has the wrong length: {installed.RelativeInstallPath}");
            }

            // Carried staged bytes are never copied to the live client during
            // promotion. Their declared hash already equals the verified active
            // hash; existence and length keep the selected composition coherent
            // without rereading many gigabytes solely to leave them untouched.
            var carried = selectedByPath[installed.RelativeInstallPath];
            var carriedPayloadPath = PathGuard.ResolveUnderRoot(
                payloadDirectory,
                carried.RelativeInstallPath);
            var carriedInfo = new FileInfo(carriedPayloadPath);
            carriedInfo.Refresh();
            if (!carriedInfo.Exists || carriedInfo.Length != carried.StagedLength)
            {
                throw new InvalidDataException(
                    $"The selected carried payload is missing or has the wrong length: {carried.RelativeInstallPath}");
            }
        }

        // Exact-hash every genuinely new source and staged payload before the
        // first backup or live write. A conflict therefore leaves both the active
        // transaction and the game install byte-for-byte unchanged.
        foreach (var entry in selected.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBuildEntry(entry);
            if (activeByPath.ContainsKey(entry.RelativeInstallPath))
            {
                continue;
            }

            var livePath = PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath);
            var stagedPath = PathGuard.ResolveUnderRoot(payloadDirectory, entry.RelativeInstallPath);
            try
            {
                await FileIntegrity.EnsureMatchesAsync(
                        livePath,
                        entry.SourceLength,
                        entry.SourceSha256,
                        "New live artifact required by additive pack promotion",
                        cancellationToken)
                    .ConfigureAwait(false);
                await FileIntegrity.EnsureMatchesAsync(
                        stagedPath,
                        entry.StagedLength,
                        entry.StagedSha256,
                        "New staged artifact required by additive pack promotion",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                throw new InvalidOperationException(
                    $"The checked additive pack cannot be installed because {entry.RelativeInstallPath} no longer matches its verified source or staged bytes. The active pack was left unchanged.",
                    exception);
            }
            var backupPath = PathGuard.ResolveUnderRoot(
                backupPayloadDirectory,
                entry.RelativeInstallPath);
            if (File.Exists(backupPath) || Directory.Exists(backupPath))
            {
                throw new IOException(
                    $"The additive backup destination already exists: {entry.RelativeInstallPath}");
            }

            newArtifacts.Add(new PreparedPromotionArtifact(
                entry,
                stagedPath,
                livePath,
                backupPath));
        }

        progress?.Report(new ProgressUpdate(
            "Incremental install",
            $"{newArtifacts.Count:N0} new {PluralizeArchive(newArtifacts.Count)}; "
            + $"{active.Entries.Count:N0} already active and untouched.",
            0,
            Math.Max(newArtifacts.Count, 1)));

        if (newArtifacts.Count == 0)
        {
            return new ApplyResult(
                active.ApplyId,
                backupDirectory,
                safeInstallManifestPath,
                active);
        }

        var backedUp = new List<PreparedPromotionArtifact>(newArtifacts.Count);
        try
        {
            foreach (var artifact in newArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ensureGameStopped("back up new additive pack artifacts");
                await _atomicFileOperations.CopyAndReplaceAsync(
                        artifact.LivePath,
                        artifact.BackupPath,
                        artifact.Entry.SourceLength,
                        artifact.Entry.SourceSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
                backedUp.Add(artifact);
            }
        }
        catch
        {
            TryDeletePromotionBackups(backupPayloadDirectory, backedUp);
            throw;
        }

        var addedByPath = new Dictionary<string, InstalledArtifact>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in newArtifacts)
        {
            var liveInfo = new FileInfo(artifact.LivePath);
            liveInfo.Refresh();
            addedByPath.Add(
                artifact.Entry.RelativeInstallPath,
                new InstalledArtifact(
                    artifact.Entry.RelativeInstallPath,
                    OriginalExisted: true,
                    artifact.Entry.SourceLength,
                    artifact.Entry.SourceSha256,
                    Path.GetRelativePath(backupDirectory, artifact.BackupPath),
                    artifact.Entry.StagedLength,
                    artifact.Entry.StagedSha256,
                    liveInfo.LastWriteTimeUtc.Ticks));
        }

        var mergedEntries = selected.Entries
            .Select(entry => activeByPath.TryGetValue(entry.RelativeInstallPath, out var carried)
                ? carried
                : addedByPath[entry.RelativeInstallPath])
            .ToList();
        var promoted = active with
        {
            AppliedUtc = DateTimeOffset.UtcNow,
            BuildId = selected.BuildId,
            BuildManifestPath = safeBuildManifestPath,
            State = InstallTransactionState.Preparing,
            Entries = mergedEntries.AsReadOnly()
        };
        var committed = new List<PreparedPromotionArtifact>(newArtifacts.Count);
        var preparingPersisted = false;
        try
        {
            await _manifestStore.WriteInstallManifestAsync(
                    safeInstallManifestPath,
                    promoted,
                    cancellationToken)
                .ConfigureAwait(false);
            preparingPersisted = true;
            for (var index = 0; index < newArtifacts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = newArtifacts[index];
                await FileIntegrity.EnsureMatchesAsync(
                        artifact.LivePath,
                        artifact.Entry.SourceLength,
                        artifact.Entry.SourceSha256,
                        "New live artifact immediately before additive promotion",
                        cancellationToken)
                    .ConfigureAwait(false);
                await FileIntegrity.EnsureMatchesAsync(
                        artifact.StagedPath,
                        artifact.Entry.StagedLength,
                        artifact.Entry.StagedSha256,
                        "New staged artifact immediately before additive promotion",
                        cancellationToken)
                    .ConfigureAwait(false);
                _ensureGameStopped("install new additive pack artifacts");
                await _atomicFileOperations.CopyAndReplaceAsync(
                        artifact.StagedPath,
                        artifact.LivePath,
                        artifact.Entry.StagedLength,
                        artifact.Entry.StagedSha256,
                        cancellationToken,
                        onCommitted: () => committed.Add(artifact))
                    .ConfigureAwait(false);
                var installedIndex = mergedEntries.FindIndex(entry =>
                    entry.RelativeInstallPath.Equals(
                        artifact.Entry.RelativeInstallPath,
                        StringComparison.OrdinalIgnoreCase));
                mergedEntries[installedIndex] = mergedEntries[installedIndex] with
                {
                    InstalledLastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(artifact.LivePath).Ticks
                };
                progress?.Report(new ProgressUpdate(
                    "Incremental install",
                    $"Installed a new archive; {active.Entries.Count:N0} active archive(s) remain untouched.",
                    index + 1,
                    newArtifacts.Count,
                    artifact.Entry.RelativeInstallPath));
            }

            promoted = promoted with
            {
                State = InstallTransactionState.Applied,
                Entries = mergedEntries.AsReadOnly()
            };
            await _manifestStore.WriteInstallManifestAsync(
                    safeInstallManifestPath,
                    promoted,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ApplyResult(
                promoted.ApplyId,
                backupDirectory,
                safeInstallManifestPath,
                promoted);
        }
        catch (Exception transactionFailure)
        {
            var rollbackFailures = await RollBackPromotionAsync(committed, progress)
                .ConfigureAwait(false);
            if (rollbackFailures.Count == 0)
            {
                try
                {
                    if (preparingPersisted)
                    {
                        await _manifestStore.WriteInstallManifestAsync(
                                safeInstallManifestPath,
                                active,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    TryDeletePromotionBackups(backupPayloadDirectory, backedUp);
                }
                catch (Exception manifestFailure)
                {
                    rollbackFailures.Add(manifestFailure);
                }
            }

            if (rollbackFailures.Count != 0)
            {
                try
                {
                    await _manifestStore.WriteInstallManifestAsync(
                            safeInstallManifestPath,
                            promoted with
                            {
                                State = InstallTransactionState.RecoveryRequired,
                                Entries = mergedEntries.AsReadOnly()
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception manifestFailure)
                {
                    rollbackFailures.Add(manifestFailure);
                }

                throw new InstallTransactionException(
                    "Additive pack promotion failed and could not be rolled back completely.",
                    transactionFailure,
                    rollbackFailures);
            }

            throw;
        }
    }

    private async Task<IReadOnlyList<PreparedBuildArtifact>> PreflightBuildAsync(
        ProjectPaths paths,
        string manifestPath,
        BuildManifest build,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ValidateInstallRoot(paths, build.InstallPath);
        ValidateUniquePaths(build.Entries.Select(entry => entry.RelativeInstallPath));
        var buildDirectory = Path.GetDirectoryName(manifestPath)!;
        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        var prepared = new List<PreparedBuildArtifact>(build.Entries.Count);
        progress?.Report(new ProgressUpdate("Apply preflight", "Verifying staged and live artifacts before any install write.", 0, build.Entries.Count));

        for (var index = 0; index < build.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = build.Entries[index];
            ValidateHash(entry.SourceSha256);
            ValidateHash(entry.StagedSha256);
            var stagedPath = PathGuard.ResolveUnderRoot(payloadDirectory, entry.RelativeInstallPath);
            var installPath = PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath);
            await FileIntegrity.EnsureMatchesAsync(
                stagedPath,
                entry.StagedLength,
                entry.StagedSha256,
                "Staged artifact",
                cancellationToken).ConfigureAwait(false);
            try
            {
                await FileIntegrity.EnsureMatchesAsync(
                    installPath,
                    entry.SourceLength,
                    entry.SourceSha256,
                    "Live install artifact",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                throw new InvalidOperationException(
                    $"The live client archive no longer matches the original bytes recorded by this staged pack: {entry.RelativeInstallPath}. "
                    + "If Pack Library offers Repair Source Mismatch for this pack, use it; otherwise finish any LaunchPad update and rebuild the affected pack. No client files were changed.",
                    exception);
            }
            prepared.Add(new PreparedBuildArtifact(entry, stagedPath, installPath));
            progress?.Report(new ProgressUpdate(
                "Apply preflight",
                "Artifact integrity verified.",
                index + 1,
                build.Entries.Count,
                entry.RelativeInstallPath));
        }

        return prepared;
    }

    private async Task<List<Exception>> RollBackApplyAsync(
        string backupDirectory,
        IReadOnlyList<PreparedBuildArtifact> applied,
        IProgress<ProgressUpdate>? progress)
    {
        var failures = new List<Exception>();
        for (var index = applied.Count - 1; index >= 0; index--)
        {
            var artifact = applied[index];
            try
            {
                var backupPath = PathGuard.ResolveUnderRoot(
                    Path.Combine(backupDirectory, "payload"),
                    artifact.Entry.RelativeInstallPath);
                _ensureGameStopped("apply rollback");
                await _atomicFileOperations.CopyAndReplaceAsync(
                    backupPath,
                    artifact.InstallPath,
                    artifact.Entry.SourceLength,
                    artifact.Entry.SourceSha256,
                    CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new ProgressUpdate(
                    "Rollback",
                    "Original artifact restored after apply failure.",
                    applied.Count - index,
                    applied.Count,
                    artifact.Entry.RelativeInstallPath));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private async Task<List<Exception>> RollBackPromotionAsync(
        IReadOnlyList<PreparedPromotionArtifact> committed,
        IProgress<ProgressUpdate>? progress)
    {
        var failures = new List<Exception>();
        for (var index = committed.Count - 1; index >= 0; index--)
        {
            var artifact = committed[index];
            try
            {
                // Do not overwrite a launcher or user change that raced the
                // failed transaction. Recovery remains explicit in that case.
                await FileIntegrity.EnsureMatchesAsync(
                        artifact.LivePath,
                        artifact.Entry.StagedLength,
                        artifact.Entry.StagedSha256,
                        "New enhanced artifact before additive rollback",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                _ensureGameStopped("roll back additive pack promotion");
                await _atomicFileOperations.CopyAndReplaceAsync(
                        artifact.BackupPath,
                        artifact.LivePath,
                        artifact.Entry.SourceLength,
                        artifact.Entry.SourceSha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                progress?.Report(new ProgressUpdate(
                    "Incremental rollback",
                    "Original new archive restored; carried active archives were never rewritten.",
                    committed.Count - index,
                    committed.Count,
                    artifact.Entry.RelativeInstallPath));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private async Task<List<Exception>> RollBackRestoreAsync(
        IReadOnlyList<PreparedRestoreArtifact> restored,
        IProgress<ProgressUpdate>? progress)
    {
        var failures = new List<Exception>();
        for (var index = restored.Count - 1; index >= 0; index--)
        {
            var artifact = restored[index];
            try
            {
                _ensureGameStopped("restore rollback");
                await _atomicFileOperations.CopyAndReplaceAsync(
                    artifact.SafetyPath!,
                    artifact.TargetPath,
                    artifact.Entry.InstalledLength,
                    artifact.Entry.InstalledSha256,
                    CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new ProgressUpdate(
                    "Restore rollback",
                    "Enhanced artifact reinstated after restore failure.",
                    restored.Count - index,
                    restored.Count,
                    artifact.Entry.RelativeInstallPath));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static async Task<ObservedInstallState> ObserveInstallStateAsync(
        InstalledArtifact entry,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            if (!entry.OriginalExisted)
            {
                return ObservedInstallState.Original;
            }

            throw new FileNotFoundException(
                "Install artifact is missing and cannot be identified as either original or enhanced.",
                targetPath);
        }

        var fingerprint = await FileIntegrity.FingerprintAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (fingerprint.Length == entry.InstalledLength
            && string.Equals(fingerprint.Sha256, entry.InstalledSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ObservedInstallState.Enhanced;
        }

        if (entry.OriginalExisted
            && entry.OriginalSha256 is not null
            && fingerprint.Length == entry.OriginalLength
            && string.Equals(fingerprint.Sha256, entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ObservedInstallState.Original;
        }

        throw new InvalidDataException(
            $"Install artifact matches neither the enhanced nor original SHA-256 recorded in the manifest: {targetPath}");
    }

    private static async Task EnsureActiveArtifactMatchesAsync(
        InstalledArtifact artifact,
        string livePath,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(livePath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "An active enhanced artifact is missing during additive promotion.",
                livePath);
        }

        var trustedMetadata = file.Length == artifact.InstalledLength
            && artifact.InstalledLastWriteTimeUtcTicks is > 0
            && file.LastWriteTimeUtc.Ticks == artifact.InstalledLastWriteTimeUtcTicks.Value
            && (!artifact.OriginalExisted || artifact.OriginalLength != artifact.InstalledLength);
        if (trustedMetadata)
        {
            return;
        }

        await FileIntegrity.EnsureMatchesAsync(
                livePath,
                artifact.InstalledLength,
                artifact.InstalledSha256,
                "Carried active artifact during additive promotion",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureGameStopped(string operation)
    {
        if (EverQuestInstall.IsGameOrLauncherRunning())
        {
            throw new InvalidOperationException(
                $"EverQuest and LaunchPad must be closed before SpinTexture can {operation} install files.");
        }
    }

    private static void ValidateInstallRoot(ProjectPaths paths, string manifestInstallPath)
    {
        if (!PathGuard.SamePath(paths.InstallPath, manifestInstallPath))
        {
            throw new InvalidDataException("Manifest belongs to a different EverQuest installation.");
        }
    }

    private static void ValidateUniquePaths(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!seen.Add(path))
            {
                throw new InvalidDataException($"Manifest contains a duplicate install path: {path}");
            }
        }
    }

    private static void ValidateBuildEntry(BuildManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.RelativeInstallPath)
            || entry.SourceLength < 0
            || entry.StagedLength < 0)
        {
            throw new InvalidDataException("Build manifest contains invalid artifact metadata.");
        }

        ValidateHash(entry.SourceSha256);
        ValidateHash(entry.StagedSha256);
    }

    private static void ValidateInstalledArtifact(InstalledArtifact entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.RelativeInstallPath)
            || entry.OriginalLength < 0
            || entry.InstalledLength < 0
            || entry.InstalledLastWriteTimeUtcTicks is <= 0
            || !entry.OriginalExisted
            || string.IsNullOrWhiteSpace(entry.OriginalSha256)
            || string.IsNullOrWhiteSpace(entry.BackupRelativePath))
        {
            throw new InvalidDataException("Active install manifest contains invalid artifact metadata.");
        }

        ValidateHash(entry.OriginalSha256);
        ValidateHash(entry.InstalledSha256);
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Manifest contains an invalid SHA-256 value.");
        }
    }

    private static string PluralizeArchive(int count) => count == 1 ? "archive" : "archives";

    private static void TryDeletePromotionBackups(
        string backupPayloadDirectory,
        IReadOnlyList<PreparedPromotionArtifact> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            try
            {
                var safePath = PathGuard.EnsurePathUnderRoot(
                    backupPayloadDirectory,
                    artifact.BackupPath);
                AtomicFile.TryDelete(safePath);
                var directory = Path.GetDirectoryName(safePath);
                while (directory is not null
                       && !PathGuard.SamePath(directory, backupPayloadDirectory)
                       && Directory.Exists(directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                        || Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        break;
                    }

                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                // An unreferenced verified backup is harmless. Never broaden
                // cleanup after the live transaction has already been recovered.
            }
        }
    }

    internal static void TryDeleteRestoreSafetyDirectory(
        string backupDirectory,
        string restoreSafetyDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(restoreSafetyDirectory));
            var parent = Path.GetDirectoryName(target);
            var name = Path.GetFileName(target);
            if (parent is null
                || !PathGuard.SamePath(root, parent)
                || !name.StartsWith("restore-safety-", StringComparison.Ordinal)
                || name.Length <= "restore-safety-".Length
                || !Directory.Exists(target))
            {
                return;
            }

            // Never recurse through a directory junction or symbolic link that
            // appeared after the owned safety directory was created.
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            Directory.Delete(target, recursive: true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            // The restore is already verified and durable. Cleanup is best effort;
            // an undeleted safety snapshot is safe and can be pruned later.
        }
    }

    private sealed record PreparedBuildArtifact(
        BuildManifestEntry Entry,
        string StagedPath,
        string InstallPath);

    private sealed record PreparedPromotionArtifact(
        BuildManifestEntry Entry,
        string StagedPath,
        string LivePath,
        string BackupPath);

    private sealed class PreparedRestoreArtifact
    {
        public PreparedRestoreArtifact(
            InstalledArtifact entry,
            string targetPath,
            string? originalBackupPath,
            ObservedInstallState observedState)
        {
            Entry = entry;
            TargetPath = targetPath;
            OriginalBackupPath = originalBackupPath;
            ObservedState = observedState;
        }

        public InstalledArtifact Entry { get; }
        public string TargetPath { get; }
        public string? OriginalBackupPath { get; }
        public ObservedInstallState ObservedState { get; }
        public string? SafetyPath { get; set; }
    }

    private enum ObservedInstallState
    {
        Enhanced,
        Original
    }
}
