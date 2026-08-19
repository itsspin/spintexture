using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.Core.Pipeline;

public sealed class InstallTransactionService
{
    public const string LauncherUpdateReconciliationReceiptFileName =
        "launcher-update-reconciliation.json";

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
    /// Retires an applied pack after a caller-verified launcher update without
    /// ever treating unknown bytes as an old managed original. Still-enhanced
    /// files are restored from their verified backups; old originals and exact
    /// caller-authorized update snapshots are left untouched. The ordinary
    /// restore path intentionally remains strict.
    /// </summary>
    public async Task<LauncherUpdateReconciliationResult>
        RestoreAfterVerifiedLauncherUpdateAsync(
            ProjectPaths paths,
            string installManifestPath,
            IReadOnlyList<AdoptedOriginalArtifact> authorizedChanges,
            IProgress<ProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(installManifestPath);
        ArgumentNullException.ThrowIfNull(authorizedChanges);
        _ensureGameStopped("reconcile a completed launcher update");
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);

        var safeManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.BackupPath,
            installManifestPath);
        var install = await _manifestStore
            .ReadInstallManifestAsync(safeManifestPath, cancellationToken)
            .ConfigureAwait(false);
        ValidateInstallRoot(paths, install.InstallPath);
        ValidateUniquePaths(install.Entries.Select(entry => entry.RelativeInstallPath));
        var authorizations = ValidateLauncherUpdateAuthorizations(
            paths,
            install,
            authorizedChanges);
        var backupDirectory = Path.GetDirectoryName(safeManifestPath)!;
        var receiptPath = Path.Combine(
            backupDirectory,
            LauncherUpdateReconciliationReceiptFileName);

        if (File.Exists(receiptPath))
        {
            var existingReceipt = await _manifestStore
                .ReadLauncherUpdateReconciliationReceiptAsync(
                    receiptPath,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateLauncherUpdateReceipt(
                paths,
                install,
                existingReceipt,
                authorizations,
                requireAuthorizationMatch:
                    existingReceipt.State != LauncherUpdateReconciliationState.RolledBack);
            if (existingReceipt.State == LauncherUpdateReconciliationState.Completed)
            {
                await EnsureReconciledStateMatchesAsync(
                        paths,
                        existingReceipt.Entries,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (install.State != InstallTransactionState.Restored)
                {
                    _ensureGameStopped(
                        "retire the completed launcher-update reconciliation");
                    install = install with { State = InstallTransactionState.Restored };
                    await _manifestStore.WriteInstallManifestAsync(
                            safeManifestPath,
                            install,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                TryDeleteReceiptSafetyDirectory(backupDirectory, existingReceipt);
                return CreateLauncherUpdateResult(receiptPath, existingReceipt);
            }

            if (existingReceipt.State == LauncherUpdateReconciliationState.Preparing)
            {
                if (install.State is not (InstallTransactionState.Applied
                    or InstallTransactionState.RecoveryRequired))
                {
                    throw new InvalidOperationException(
                        $"An interrupted launcher-update reconciliation cannot resume from install state {install.State}.");
                }

                return await ResumeLauncherUpdateReconciliationAsync(
                        paths,
                        safeManifestPath,
                        install,
                        receiptPath,
                        existingReceipt,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // RolledBack is a closed journal, not a pending authorization. A
            // later legitimate LaunchPad session may produce different exact
            // bytes for the same path. Its new caller snapshots must be judged
            // by a fresh preflight rather than compared with stale adopted
            // snapshots from the completed rollback attempt.
            if (install.State == InstallTransactionState.RecoveryRequired)
            {
                // Older/interrupted reconciliation code could durably close the
                // rollback journal before restoring the install-state marker.
                // A structurally valid RolledBack receipt proves that its live
                // rollback completed. Restore only metadata here; the fresh
                // preflight below still exact-verifies every live artifact and
                // the caller's current launcher snapshots before any write.
                _ensureGameStopped(
                    "finalize a successfully rolled-back launcher-update reconciliation");
                install = install with { State = InstallTransactionState.Applied };
                await _manifestStore.WriteInstallManifestAsync(
                        safeManifestPath,
                        install,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            TryDeleteReceiptSafetyDirectory(backupDirectory, existingReceipt);
        }

        if (install.State != InstallTransactionState.Applied)
        {
            throw new InvalidOperationException(
                $"Only an applied pack can be reconciled after a launcher update; current state is {install.State}.");
        }

        var authorizationPaths = new HashSet<string>(
            authorizations.Keys,
            StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedLauncherUpdateArtifact>(install.Entries.Count);
        progress?.Report(new ProgressUpdate(
            "Update reconciliation preflight",
            "Exact-verifying the launcher-updated client and managed originals before any write.",
            0,
            install.Entries.Count));

        for (var index = 0; index < install.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = install.Entries[index];
            ValidateInstalledArtifactForReconciliation(entry);
            var targetPath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                entry.RelativeInstallPath);
            var canonicalRelativePath = Path.GetRelativePath(
                paths.InstallPath,
                targetPath);
            var observed = await FingerprintOptionalAsync(targetPath, cancellationToken)
                .ConfigureAwait(false);
            var matchesEnhanced = SnapshotMatches(
                observed,
                exists: true,
                entry.InstalledLength,
                entry.InstalledSha256);
            var matchesOriginal = entry.OriginalExisted
                ? SnapshotMatches(
                    observed,
                    exists: true,
                    entry.OriginalLength,
                    entry.OriginalSha256)
                : observed is null;

            LauncherUpdateOriginalArtifact reconciled;
            string? originalBackupPath = null;
            var needsRestore = false;
            if (matchesOriginal)
            {
                reconciled = CreateManagedOriginalSnapshot(
                    entry,
                    LauncherUpdateOriginalDisposition.AlreadyManagedOriginal);
            }
            else if (matchesEnhanced)
            {
                needsRestore = true;
                if (entry.OriginalExisted)
                {
                    originalBackupPath = ResolveAndValidateOriginalBackupPath(
                        backupDirectory,
                        entry);
                    await FileIntegrity.EnsureMatchesAsync(
                            originalBackupPath,
                            entry.OriginalLength,
                            entry.OriginalSha256!,
                            "Original backup used for launcher-update reconciliation",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                reconciled = CreateManagedOriginalSnapshot(
                    entry,
                    LauncherUpdateOriginalDisposition.RestoredManagedOriginal);
            }
            else if (authorizations.TryGetValue(canonicalRelativePath, out var authorization)
                     && AuthorizationMatches(authorization, observed))
            {
                authorizationPaths.Remove(canonicalRelativePath);
                reconciled = new LauncherUpdateOriginalArtifact(
                    canonicalRelativePath,
                    authorization.Exists,
                    authorization.Length,
                    authorization.Sha256,
                    authorization.Exists
                        ? LauncherUpdateOriginalDisposition.AdoptedUpdatedFile
                        : LauncherUpdateOriginalDisposition.AdoptedRemovedFile);
            }
            else
            {
                throw new InvalidOperationException(
                    $"The current client state for {canonicalRelativePath} is neither the managed enhanced file, the managed original, nor an exact caller-authorized launcher-update snapshot. No client files were changed.");
            }

            prepared.Add(new PreparedLauncherUpdateArtifact(
                entry,
                targetPath,
                originalBackupPath,
                reconciled,
                needsRestore));
            progress?.Report(new ProgressUpdate(
                "Update reconciliation preflight",
                needsRestore
                    ? "Managed enhanced artifact will be restored from its verified original."
                    : reconciled.Disposition is LauncherUpdateOriginalDisposition.AdoptedUpdatedFile
                        or LauncherUpdateOriginalDisposition.AdoptedRemovedFile
                            ? "Exact launcher-update snapshot authorized and preserved."
                            : "Artifact is already the managed original and will remain untouched.",
                index + 1,
                install.Entries.Count,
                canonicalRelativePath));
        }

        if (authorizationPaths.Count != 0)
        {
            throw new InvalidOperationException(
                $"One or more launcher-update authorizations were stale or unnecessary: {string.Join(", ", authorizationPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}. No client files were changed.");
        }

        var safetyDirectory = Path.Combine(
            backupDirectory,
            $"restore-safety-launcher-update-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(safetyDirectory);
        LauncherUpdateReconciliationReceipt receipt;
        var receiptWritten = false;
        try
        {
            foreach (var artifact in prepared.Where(item => item.NeedsRestore))
            {
                cancellationToken.ThrowIfCancellationRequested();
                artifact.SafetyPath = PathGuard.ResolveUnderRoot(
                    safetyDirectory,
                    artifact.Entry.RelativeInstallPath);
                await _atomicFileOperations.CopyAndReplaceAsync(
                        artifact.TargetPath,
                        artifact.SafetyPath,
                        artifact.Entry.InstalledLength,
                        artifact.Entry.InstalledSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Safety copies may be large. Recheck every changed or writable
            // preflight snapshot as one barrier immediately before the first
            // live write so a launcher or user race cannot be silently adopted.
            await EnsureInitialReconciliationStateMatchesAsync(prepared, cancellationToken)
                .ConfigureAwait(false);

            receipt = new LauncherUpdateReconciliationReceipt(
                LauncherUpdateReconciliationReceipt.CurrentSchemaVersion,
                install.ApplyId,
                install.AppliedUtc,
                DateTimeOffset.UtcNow,
                ReconciledUtc: null,
                Path.GetFullPath(paths.InstallPath),
                LauncherUpdateReconciliationState.Preparing,
                Path.GetFileName(safetyDirectory),
                prepared.Select(item => item.Reconciled).ToArray());
            await _manifestStore.WriteLauncherUpdateReconciliationReceiptAsync(
                    receiptPath,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
            receiptWritten = true;
        }
        catch
        {
            if (!receiptWritten)
            {
                TryDeleteRestoreSafetyDirectory(backupDirectory, safetyDirectory);
            }

            throw;
        }

        return await ExecuteLauncherUpdateReconciliationAsync(
                paths,
                safeManifestPath,
                install,
                receiptPath,
                receipt,
                prepared,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
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
                    + "If Pack Library offers Repair Pack for this build, use it; otherwise finish any LaunchPad update and rebuild the affected pack. No client files were changed.",
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

    private async Task<LauncherUpdateReconciliationResult>
        ResumeLauncherUpdateReconciliationAsync(
            ProjectPaths paths,
            string installManifestPath,
            InstallManifest install,
            string receiptPath,
            LauncherUpdateReconciliationReceipt receipt,
            IProgress<ProgressUpdate>? progress,
            CancellationToken cancellationToken)
    {
        var backupDirectory = Path.GetDirectoryName(installManifestPath)!;
        var safetyDirectory = ResolveReceiptSafetyDirectory(backupDirectory, receipt);
        var installedByPath = install.Entries.ToDictionary(
            entry => Path.GetRelativePath(
                paths.InstallPath,
                PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath)),
            StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedLauncherUpdateArtifact>(receipt.Entries.Count);

        for (var index = 0; index < receipt.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reconciled = receipt.Entries[index];
            var entry = installedByPath[reconciled.RelativeInstallPath];
            var targetPath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                reconciled.RelativeInstallPath);
            var observed = await FingerprintOptionalAsync(targetPath, cancellationToken)
                .ConfigureAwait(false);
            var isFinal = SnapshotMatches(
                observed,
                reconciled.Exists,
                reconciled.Length,
                reconciled.Sha256);
            var isEnhanced = SnapshotMatches(
                observed,
                exists: true,
                entry.InstalledLength,
                entry.InstalledSha256);
            var restoredDisposition = reconciled.Disposition
                == LauncherUpdateOriginalDisposition.RestoredManagedOriginal;
            if (!isFinal && (!restoredDisposition || !isEnhanced))
            {
                throw new InvalidOperationException(
                    $"Interrupted launcher-update reconciliation cannot resume because {reconciled.RelativeInstallPath} changed after its durable receipt was written. No unverified bytes were overwritten.");
            }

            string? backupPath = null;
            string? safetyPath = null;
            if (restoredDisposition)
            {
                safetyPath = PathGuard.ResolveUnderRoot(
                    safetyDirectory,
                    reconciled.RelativeInstallPath);
                await FileIntegrity.EnsureMatchesAsync(
                        safetyPath,
                        entry.InstalledLength,
                        entry.InstalledSha256,
                        "Launcher-update reconciliation safety artifact",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (entry.OriginalExisted)
                {
                    backupPath = ResolveAndValidateOriginalBackupPath(
                        backupDirectory,
                        entry);
                    await FileIntegrity.EnsureMatchesAsync(
                            backupPath,
                            entry.OriginalLength,
                            entry.OriginalSha256!,
                            "Original backup used to resume launcher-update reconciliation",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            prepared.Add(new PreparedLauncherUpdateArtifact(
                entry,
                targetPath,
                backupPath,
                reconciled,
                needsRestore: restoredDisposition && !isFinal)
            {
                SafetyPath = safetyPath,
                ReconciledBeforeResume = restoredDisposition && isFinal
            });
            progress?.Report(new ProgressUpdate(
                "Resume update reconciliation",
                isFinal
                    ? "Previously reconciled artifact verified."
                    : "Managed enhanced artifact still requires its verified original.",
                index + 1,
                receipt.Entries.Count,
                reconciled.RelativeInstallPath));
        }

        await EnsureInitialReconciliationStateMatchesAsync(prepared, cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteLauncherUpdateReconciliationAsync(
                paths,
                installManifestPath,
                install,
                receiptPath,
                receipt,
                prepared,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LauncherUpdateReconciliationResult>
        ExecuteLauncherUpdateReconciliationAsync(
            ProjectPaths paths,
            string installManifestPath,
            InstallManifest install,
            string receiptPath,
            LauncherUpdateReconciliationReceipt receipt,
            IReadOnlyList<PreparedLauncherUpdateArtifact> prepared,
            IProgress<ProgressUpdate>? progress,
            CancellationToken cancellationToken)
    {
        var reconciledRestores = prepared
            .Where(artifact => artifact.ReconciledBeforeResume)
            .ToList();
        try
        {
            var restoreItems = prepared.Where(artifact => artifact.NeedsRestore).ToArray();
            for (var index = 0; index < restoreItems.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = restoreItems[index];
                await FileIntegrity.EnsureMatchesAsync(
                        artifact.TargetPath,
                        artifact.Entry.InstalledLength,
                        artifact.Entry.InstalledSha256,
                        "Enhanced artifact immediately before launcher-update reconciliation",
                        cancellationToken)
                    .ConfigureAwait(false);
                _ensureGameStopped("restore enhanced files after a launcher update");
                if (artifact.Entry.OriginalExisted)
                {
                    await _atomicFileOperations.CopyAndReplaceAsync(
                            artifact.OriginalBackupPath!,
                            artifact.TargetPath,
                            artifact.Entry.OriginalLength,
                            artifact.Entry.OriginalSha256!,
                            cancellationToken,
                            onCommitted: () => reconciledRestores.Add(artifact))
                        .ConfigureAwait(false);
                }
                else
                {
                    File.Delete(artifact.TargetPath);
                    if (File.Exists(artifact.TargetPath))
                    {
                        throw new IOException(
                            $"Could not remove an enhanced artifact while reconciling the launcher update: {artifact.TargetPath}");
                    }

                    reconciledRestores.Add(artifact);
                }

                progress?.Report(new ProgressUpdate(
                    "Update reconciliation",
                    "Managed enhanced artifact restored; launcher-updated files remain untouched.",
                    index + 1,
                    restoreItems.Length,
                    artifact.Reconciled.RelativeInstallPath));
            }

            await EnsureCriticalReconciledStateMatchesAsync(
                    prepared,
                    cancellationToken)
                .ConfigureAwait(false);
            _ensureGameStopped("retire the reconciled launcher-update transaction");
        }
        catch (Exception transactionFailure)
        {
            var rollbackFailures = await RollBackLauncherUpdateReconciliationAsync(
                    reconciledRestores,
                    progress)
                .ConfigureAwait(false);
            if (rollbackFailures.Count == 0)
            {
                try
                {
                    // Keep the Preparing receipt and safety files intact until
                    // the successfully rolled-back live state is durably marked
                    // Applied. Either side of a crash is then resumable:
                    // RecoveryRequired/Preparing resumes with safety, while
                    // Applied/Preparing may safely retry the reconciliation.
                    if (install.State != InstallTransactionState.Applied)
                    {
                        _ensureGameStopped(
                            "finalize a successfully rolled-back launcher-update reconciliation");
                        install = install with { State = InstallTransactionState.Applied };
                        await _manifestStore.WriteInstallManifestAsync(
                                installManifestPath,
                                install,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    receipt = receipt with
                    {
                        State = LauncherUpdateReconciliationState.RolledBack,
                        ReconciledUtc = null
                    };
                    await _manifestStore.WriteLauncherUpdateReconciliationReceiptAsync(
                            receiptPath,
                            receipt,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    TryDeleteReceiptSafetyDirectory(
                        Path.GetDirectoryName(installManifestPath)!,
                        receipt);
                }
                catch (Exception journalFailure)
                {
                    // Live bytes were reinstated successfully. Do not overwrite
                    // a possibly committed Applied marker with RecoveryRequired:
                    // the still-Preparing (or atomically committed RolledBack)
                    // receipt remains a safe retry point with its safety data.
                    throw new InstallTransactionException(
                        "Launcher-update reconciliation failed, and its live files were safely reinstated, but the rollback journal could not be finalized. Retry the reconciliation to resume safely.",
                        transactionFailure,
                        [journalFailure]);
                }

                throw;
            }

            try
            {
                await _manifestStore.WriteInstallManifestAsync(
                        installManifestPath,
                        install with { State = InstallTransactionState.RecoveryRequired },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception manifestFailure)
            {
                rollbackFailures.Add(manifestFailure);
            }

            throw new InstallTransactionException(
                "Launcher-update reconciliation failed and one or more managed enhanced files could not be reinstated safely. Authorized updated files were not overwritten.",
                transactionFailure,
                rollbackFailures);
        }

        // The completed receipt is the durable commit point. It accounts for
        // every reconciled original and is written before retiring the source
        // install manifest. If the following manifest write fails, a retry can
        // exact-verify this receipt and finish metadata retirement without
        // rewriting live client files.
        var reconciledUtc = DateTimeOffset.UtcNow;
        receipt = receipt with
        {
            State = LauncherUpdateReconciliationState.Completed,
            ReconciledUtc = reconciledUtc
        };
        await _manifestStore.WriteLauncherUpdateReconciliationReceiptAsync(
                receiptPath,
                receipt,
                cancellationToken)
            .ConfigureAwait(false);
        install = install with { State = InstallTransactionState.Restored };
        await _manifestStore.WriteInstallManifestAsync(
                installManifestPath,
                install,
                cancellationToken)
            .ConfigureAwait(false);
        TryDeleteReceiptSafetyDirectory(
            Path.GetDirectoryName(installManifestPath)!,
            receipt);
        return CreateLauncherUpdateResult(receiptPath, receipt);
    }

    private async Task<List<Exception>> RollBackLauncherUpdateReconciliationAsync(
        IReadOnlyList<PreparedLauncherUpdateArtifact> reconciled,
        IProgress<ProgressUpdate>? progress)
    {
        var failures = new List<Exception>();
        for (var index = reconciled.Count - 1; index >= 0; index--)
        {
            var artifact = reconciled[index];
            try
            {
                // Never overwrite a launcher or user change that raced rollback.
                // The target must still be the exact managed original committed
                // by this reconciliation (or still absent) before enhanced bytes
                // may be reinstated from the safety copy.
                var observed = await FingerprintOptionalAsync(
                        artifact.TargetPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!SnapshotMatches(
                        observed,
                        artifact.Reconciled.Exists,
                        artifact.Reconciled.Length,
                        artifact.Reconciled.Sha256))
                {
                    throw new InvalidDataException(
                        $"Reconciliation rollback refused to overwrite externally changed bytes: {artifact.TargetPath}");
                }

                _ensureGameStopped("roll back launcher-update reconciliation");
                await _atomicFileOperations.CopyAndReplaceAsync(
                        artifact.SafetyPath!,
                        artifact.TargetPath,
                        artifact.Entry.InstalledLength,
                        artifact.Entry.InstalledSha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                progress?.Report(new ProgressUpdate(
                    "Update reconciliation rollback",
                    "Managed enhanced artifact reinstated after reconciliation failure.",
                    reconciled.Count - index,
                    reconciled.Count,
                    artifact.Reconciled.RelativeInstallPath));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static async Task EnsureInitialReconciliationStateMatchesAsync(
        IReadOnlyList<PreparedLauncherUpdateArtifact> prepared,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in prepared)
        {
            if (!artifact.NeedsRestore
                && artifact.Reconciled.Disposition
                    == LauncherUpdateOriginalDisposition.AlreadyManagedOriginal)
            {
                // Exact preflight already established this untouched old
                // original. Rehashing thousands of no-write entries at every
                // barrier would multiply full-pack reconciliation time; the
                // subsequent refreshed build/apply exact-verifies its sources.
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observed = await FingerprintOptionalAsync(
                    artifact.TargetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var matches = artifact.NeedsRestore
                ? SnapshotMatches(
                    observed,
                    exists: true,
                    artifact.Entry.InstalledLength,
                    artifact.Entry.InstalledSha256)
                : SnapshotMatches(
                    observed,
                    artifact.Reconciled.Exists,
                    artifact.Reconciled.Length,
                    artifact.Reconciled.Sha256);
            if (!matches)
            {
                throw new InvalidOperationException(
                    $"The live client changed during launcher-update reconciliation preflight: {artifact.Reconciled.RelativeInstallPath}. No client files were changed.");
            }
        }
    }

    private static async Task EnsureCriticalReconciledStateMatchesAsync(
        IReadOnlyList<PreparedLauncherUpdateArtifact> prepared,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in prepared.Where(item =>
                     item.Reconciled.Disposition
                         != LauncherUpdateOriginalDisposition.AlreadyManagedOriginal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = await FingerprintOptionalAsync(
                    artifact.TargetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!SnapshotMatches(
                    observed,
                    artifact.Reconciled.Exists,
                    artifact.Reconciled.Length,
                    artifact.Reconciled.Sha256))
            {
                throw new InvalidOperationException(
                    $"A restored or adopted client artifact changed before transaction retirement: {artifact.Reconciled.RelativeInstallPath}.");
            }
        }
    }

    private static async Task EnsureReconciledStateMatchesAsync(
        ProjectPaths paths,
        IReadOnlyList<LauncherUpdateOriginalArtifact> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var livePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                entry.RelativeInstallPath);
            var observed = await FingerprintOptionalAsync(livePath, cancellationToken)
                .ConfigureAwait(false);
            if (!SnapshotMatches(observed, entry.Exists, entry.Length, entry.Sha256))
            {
                throw new InvalidOperationException(
                    $"The reconciled client artifact changed before transaction retirement: {entry.RelativeInstallPath}.");
            }
        }
    }

    private static async Task<FileFingerprint?> FingerprintOptionalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(path))
            {
                throw new InvalidDataException(
                    $"An install artifact path resolves to a directory instead of a file: {path}");
            }

            if (!File.Exists(path))
            {
                return null;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"An install artifact path is a reparse point and cannot be reconciled safely: {path}");
            }

            return await FileIntegrity.FingerprintAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool SnapshotMatches(
        FileFingerprint? observed,
        bool exists,
        long length,
        string? sha256) =>
        exists
            ? observed is not null
              && observed.Length == length
              && sha256 is not null
              && observed.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)
            : observed is null;

    private static bool AuthorizationMatches(
        AdoptedOriginalArtifact authorization,
        FileFingerprint? observed) =>
        SnapshotMatches(
            observed,
            authorization.Exists,
            authorization.Length,
            authorization.Sha256);

    private static LauncherUpdateOriginalArtifact CreateManagedOriginalSnapshot(
        InstalledArtifact entry,
        LauncherUpdateOriginalDisposition disposition) =>
        entry.OriginalExisted
            ? new LauncherUpdateOriginalArtifact(
                entry.RelativeInstallPath,
                Exists: true,
                entry.OriginalLength,
                entry.OriginalSha256,
                disposition)
            : new LauncherUpdateOriginalArtifact(
                entry.RelativeInstallPath,
                Exists: false,
                Length: 0,
                Sha256: null,
                disposition);

    private static string ResolveAndValidateOriginalBackupPath(
        string backupDirectory,
        InstalledArtifact entry)
    {
        if (!entry.OriginalExisted
            || string.IsNullOrWhiteSpace(entry.BackupRelativePath)
            || string.IsNullOrWhiteSpace(entry.OriginalSha256))
        {
            throw new InvalidDataException(
                $"The install manifest does not identify an original backup for {entry.RelativeInstallPath}.");
        }

        return PathGuard.ResolveUnderRoot(backupDirectory, entry.BackupRelativePath);
    }

    private static IReadOnlyDictionary<string, AdoptedOriginalArtifact>
        ValidateLauncherUpdateAuthorizations(
            ProjectPaths paths,
            InstallManifest install,
            IReadOnlyList<AdoptedOriginalArtifact> authorizations)
    {
        var installedPaths = install.Entries
            .Select(entry => Path.GetRelativePath(
                paths.InstallPath,
                PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, AdoptedOriginalArtifact>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var authorization in authorizations)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            if (string.IsNullOrWhiteSpace(authorization.RelativeInstallPath))
            {
                throw new InvalidDataException(
                    "A launcher-update authorization has no relative install path.");
            }

            var livePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                authorization.RelativeInstallPath);
            var canonicalPath = Path.GetRelativePath(paths.InstallPath, livePath);
            if (!installedPaths.Contains(canonicalPath))
            {
                throw new InvalidDataException(
                    $"A launcher-update authorization does not belong to the active install: {canonicalPath}");
            }

            if (authorization.Exists)
            {
                if (authorization.Length < 0 || authorization.Sha256 is null)
                {
                    throw new InvalidDataException(
                        $"The launcher-update authorization for {canonicalPath} has an invalid existing-file snapshot.");
                }

                ValidateHash(authorization.Sha256);
            }
            else if (authorization.Length != 0 || authorization.Sha256 is not null)
            {
                throw new InvalidDataException(
                    $"An authorized removal must use Exists=false, Length=0, and no SHA-256: {canonicalPath}");
            }

            if (!result.TryAdd(
                    canonicalPath,
                    authorization with { RelativeInstallPath = canonicalPath }))
            {
                throw new InvalidDataException(
                    $"Duplicate launcher-update authorization: {canonicalPath}");
            }
        }

        return result;
    }

    private static void ValidateLauncherUpdateReceipt(
        ProjectPaths paths,
        InstallManifest install,
        LauncherUpdateReconciliationReceipt receipt,
        IReadOnlyDictionary<string, AdoptedOriginalArtifact> authorizations,
        bool requireAuthorizationMatch)
    {
        if (receipt.SchemaVersion != LauncherUpdateReconciliationReceipt.CurrentSchemaVersion
            || !receipt.ApplyId.Equals(install.ApplyId, StringComparison.OrdinalIgnoreCase)
            || receipt.AppliedUtc != install.AppliedUtc
            || !PathGuard.SamePath(receipt.InstallPath, paths.InstallPath)
            || receipt.Entries is null
            || receipt.Entries.Count != install.Entries.Count
            || !Enum.IsDefined(receipt.State))
        {
            throw new InvalidDataException(
                "The launcher-update reconciliation receipt does not match the source install transaction.");
        }

        if (receipt.State == LauncherUpdateReconciliationState.Completed
            && receipt.ReconciledUtc is null)
        {
            throw new InvalidDataException(
                "A completed launcher-update reconciliation receipt has no completion time.");
        }

        if (receipt.State == LauncherUpdateReconciliationState.RolledBack
            && receipt.ReconciledUtc is not null)
        {
            throw new InvalidDataException(
                "A rolled-back launcher-update reconciliation receipt cannot have a completion time.");
        }

        if (receipt.State == LauncherUpdateReconciliationState.Preparing
            && string.IsNullOrWhiteSpace(receipt.SafetyDirectoryName))
        {
            throw new InvalidDataException(
                "An in-progress launcher-update reconciliation has no safety directory.");
        }

        var installedByPath = install.Entries.ToDictionary(
            entry => Path.GetRelativePath(
                paths.InstallPath,
                PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath)),
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedAuthorizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reconciled in receipt.Entries)
        {
            var livePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                reconciled.RelativeInstallPath);
            var canonicalPath = Path.GetRelativePath(paths.InstallPath, livePath);
            if (!seen.Add(canonicalPath)
                || !installedByPath.TryGetValue(canonicalPath, out var installed)
                || !Enum.IsDefined(reconciled.Disposition))
            {
                throw new InvalidDataException(
                    "The launcher-update reconciliation receipt has an unsafe or duplicate artifact.");
            }

            if (reconciled.Exists)
            {
                if (reconciled.Length < 0 || reconciled.Sha256 is null)
                {
                    throw new InvalidDataException(
                        $"The reconciliation receipt has an invalid existing-file snapshot: {canonicalPath}");
                }

                ValidateHash(reconciled.Sha256);
            }
            else if (reconciled.Length != 0 || reconciled.Sha256 is not null)
            {
                throw new InvalidDataException(
                    $"The reconciliation receipt has an invalid missing-file snapshot: {canonicalPath}");
            }

            if (reconciled.Disposition is LauncherUpdateOriginalDisposition.AdoptedUpdatedFile
                or LauncherUpdateOriginalDisposition.AdoptedRemovedFile)
            {
                if ((reconciled.Disposition
                        == LauncherUpdateOriginalDisposition.AdoptedUpdatedFile)
                    != reconciled.Exists)
                {
                    throw new InvalidDataException(
                        $"The adopted disposition does not match the recorded file state for {canonicalPath}.");
                }

                if (requireAuthorizationMatch
                    && (!authorizations.TryGetValue(canonicalPath, out var authorization)
                        || authorization.Exists != reconciled.Exists
                        || authorization.Length != reconciled.Length
                        || !string.Equals(
                            authorization.Sha256,
                            reconciled.Sha256,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"The caller authorization does not match the durable reconciliation receipt for {canonicalPath}.");
                }

                if (requireAuthorizationMatch)
                {
                    usedAuthorizations.Add(canonicalPath);
                }
            }
            else
            {
                var expected = CreateManagedOriginalSnapshot(
                    installed,
                    reconciled.Disposition);
                if (expected.Exists != reconciled.Exists
                    || expected.Length != reconciled.Length
                    || !string.Equals(
                        expected.Sha256,
                        reconciled.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The managed-original snapshot in the reconciliation receipt is invalid: {canonicalPath}");
                }
            }
        }

        if (requireAuthorizationMatch
            && usedAuthorizations.Count != authorizations.Count)
        {
            throw new InvalidDataException(
                "The caller's launcher-update authorizations do not exactly match the durable reconciliation receipt.");
        }
    }

    private static string ResolveReceiptSafetyDirectory(
        string backupDirectory,
        LauncherUpdateReconciliationReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.SafetyDirectoryName)
            || Path.GetFileName(receipt.SafetyDirectoryName) != receipt.SafetyDirectoryName
            || !receipt.SafetyDirectoryName.StartsWith(
                "restore-safety-launcher-update-",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The launcher-update reconciliation receipt has an unsafe safety directory.");
        }

        var directory = PathGuard.ResolveUnderRoot(
            backupDirectory,
            receipt.SafetyDirectoryName);
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The launcher-update reconciliation safety directory is missing or unsafe.");
        }

        return directory;
    }

    private static void ValidateInstalledArtifactForReconciliation(InstalledArtifact entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.RelativeInstallPath)
            || entry.OriginalLength < 0
            || entry.InstalledLength < 0)
        {
            throw new InvalidDataException(
                "The active install manifest contains invalid artifact metadata.");
        }

        ValidateHash(entry.InstalledSha256);
        if (entry.OriginalExisted)
        {
            if (entry.OriginalSha256 is null
                || string.IsNullOrWhiteSpace(entry.BackupRelativePath))
            {
                throw new InvalidDataException(
                    $"The active install has no original metadata for {entry.RelativeInstallPath}.");
            }

            ValidateHash(entry.OriginalSha256);
        }
        else if (entry.OriginalSha256 is not null || entry.BackupRelativePath is not null)
        {
            throw new InvalidDataException(
                $"The active install has inconsistent absent-original metadata for {entry.RelativeInstallPath}.");
        }
    }

    private static LauncherUpdateReconciliationResult CreateLauncherUpdateResult(
        string receiptPath,
        LauncherUpdateReconciliationReceipt receipt)
    {
        if (receipt.State != LauncherUpdateReconciliationState.Completed
            || receipt.ReconciledUtc is null)
        {
            throw new InvalidOperationException(
                "A launcher-update reconciliation result requires a completed receipt.");
        }

        return new LauncherUpdateReconciliationResult(
            receipt.ApplyId,
            receiptPath,
            receipt.Entries.Count,
            receipt.Entries.Count(entry => entry.Disposition
                == LauncherUpdateOriginalDisposition.RestoredManagedOriginal),
            receipt.Entries.Count(entry => entry.Disposition is
                LauncherUpdateOriginalDisposition.AdoptedUpdatedFile or
                LauncherUpdateOriginalDisposition.AdoptedRemovedFile),
            receipt.ReconciledUtc.Value);
    }

    private static void TryDeleteReceiptSafetyDirectory(
        string backupDirectory,
        LauncherUpdateReconciliationReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.SafetyDirectoryName))
        {
            return;
        }

        try
        {
            var safetyDirectory = PathGuard.ResolveUnderRoot(
                backupDirectory,
                receipt.SafetyDirectoryName);
            TryDeleteRestoreSafetyDirectory(backupDirectory, safetyDirectory);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            // Reconciliation is already committed or rolled back. A retained
            // verified safety copy is harmless and remains visibly owned.
        }
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

            // Directory.Delete(recursive: true) can follow a junction or
            // symbolic-link directory nested below the owned safety root. Walk
            // one level at a time and inspect attributes before descending so a
            // reparse point that appeared after snapshot creation makes cleanup
            // fail closed without touching its external target.
            var pending = new Queue<string>();
            pending.Enqueue(target);
            while (pending.Count != 0)
            {
                var directory = pending.Dequeue();
                foreach (var child in Directory.EnumerateFileSystemEntries(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Enqueue(child);
                    }
                }
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

    private sealed class PreparedLauncherUpdateArtifact
    {
        public PreparedLauncherUpdateArtifact(
            InstalledArtifact entry,
            string targetPath,
            string? originalBackupPath,
            LauncherUpdateOriginalArtifact reconciled,
            bool needsRestore)
        {
            Entry = entry;
            TargetPath = targetPath;
            OriginalBackupPath = originalBackupPath;
            Reconciled = reconciled;
            NeedsRestore = needsRestore;
        }

        public InstalledArtifact Entry { get; }
        public string TargetPath { get; }
        public string? OriginalBackupPath { get; }
        public LauncherUpdateOriginalArtifact Reconciled { get; }
        public bool NeedsRestore { get; }
        public string? SafetyPath { get; set; }
        public bool ReconciledBeforeResume { get; set; }
    }

    private enum ObservedInstallState
    {
        Enhanced,
        Original
    }
}
