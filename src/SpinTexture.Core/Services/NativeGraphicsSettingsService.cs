using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

/// <summary>
/// Applies only graphics options that are already implemented and exposed by
/// the EverQuest Legends client. No renderer DLL, shader, UI file, or archive
/// is installed or modified.
/// </summary>
public sealed class NativeGraphicsSettingsService
{
    private const string SettingsRelativePath = "eqclient.ini";
    private const string ManifestFileName = "native-graphics-manifest.json";
    private const string OriginalBackupFileName = "eqclient.original.ini";
    private const string AdvancedOptionsRelativePath =
        "uifiles\\default\\EQUI_AdvancedDisplayOptionsWindow.xml";
    private const string GraphicsDllRelativePath = "EQGraphics.dll";
    private const string GameExecutableRelativePath = "eqgame.exe";
    private const string MaximumShadowClipPlane = "100";

    private static readonly ManagedSetting[] LegacyManagedSettings =
    [
        new("Defaults", "Shadows"),
        new("Defaults", "MultiPassLighting"),
        new("Defaults", "PostEffects")
    ];

    private static readonly ManagedSetting[] LightingOnlyManagedSettings =
    [
        .. LegacyManagedSettings,
        new("Defaults", "Bloom")
    ];

    private static readonly ManagedSetting[] ManagedSettings =
    [
        .. LightingOnlyManagedSettings,
        new("Options", "ShadowClipPlane")
    ];

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IAtomicFileOperations atomicFiles;
    private readonly Action<string> ensureClientsStopped;

    public NativeGraphicsSettingsService()
        : this(new AtomicFileOperations(), EnsureClientsStopped)
    {
    }

    internal NativeGraphicsSettingsService(
        IAtomicFileOperations atomicFiles,
        Action<string> ensureClientsStopped)
    {
        this.atomicFiles = atomicFiles ?? throw new ArgumentNullException(nameof(atomicFiles));
        this.ensureClientsStopped = ensureClientsStopped
            ?? throw new ArgumentNullException(nameof(ensureClientsStopped));
    }

    public async Task<NativeGraphicsSettingsStatus> InspectAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var settingsPath = ResolveSettingsPath(paths);
        if (!File.Exists(settingsPath))
        {
            return new NativeGraphicsSettingsStatus(
                NativeGraphicsSettingsState.Unavailable,
                settingsPath,
                null,
                null,
                [],
                "eqclient.ini was not found in the selected client directory.");
        }

        NativeGraphicsIniDocument document;
        try
        {
            document = await ReadDocumentAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or DecoderFallbackException
                                             or InvalidDataException)
        {
            return new NativeGraphicsSettingsStatus(
                NativeGraphicsSettingsState.RecoveryRequired,
                settingsPath,
                null,
                null,
                [],
                $"SpinTexture could not safely read the native graphics settings: {exception.Message}");
        }

        var active = await FindActiveTransactionAsync(paths, cancellationToken).ConfigureAwait(false);
        var inspectedSettings = active.Manifest is null
            ? ManagedSettings
            : GetManagedSettingsForManifest(active.Manifest);
        IReadOnlyList<NativeGraphicsSettingValue> currentValues;
        try
        {
            currentValues = CaptureValues(document, inspectedSettings);
        }
        catch (InvalidDataException exception)
        {
            return new NativeGraphicsSettingsStatus(
                NativeGraphicsSettingsState.RecoveryRequired,
                settingsPath,
                null,
                null,
                [],
                exception.Message);
        }

        if (active.Error is not null)
        {
            return new NativeGraphicsSettingsStatus(
                NativeGraphicsSettingsState.RecoveryRequired,
                settingsPath,
                active.Manifest?.Preset,
                active.ManifestPath,
                currentValues,
                active.Error);
        }

        if (active.Manifest is not null)
        {
            var manifest = active.Manifest;
            var validationError = await ValidateActiveManifestAsync(
                    paths,
                    active.ManifestPath!,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validationError is not null)
            {
                return new NativeGraphicsSettingsStatus(
                    NativeGraphicsSettingsState.RecoveryRequired,
                    settingsPath,
                    manifest.Preset,
                    active.ManifestPath,
                    currentValues,
                    validationError);
            }

            if (manifest.State is NativeGraphicsTransactionState.Preparing
                or NativeGraphicsTransactionState.RecoveryRequired)
            {
                return new NativeGraphicsSettingsStatus(
                    NativeGraphicsSettingsState.RecoveryRequired,
                    settingsPath,
                    manifest.Preset,
                    active.ManifestPath,
                    currentValues,
                    "A native graphics transaction was interrupted. Restore the verified original settings before applying another preset.",
                    HasVerifiedRestorePoint: true);
            }

            var changedManagedSetting = manifest.AppliedSettings.FirstOrDefault(applied =>
            {
                var current = document.Get(applied.Section, applied.Key);
                return !SettingEquals(current, applied);
            });
            if (changedManagedSetting is not null)
            {
                return new NativeGraphicsSettingsStatus(
                    NativeGraphicsSettingsState.Modified,
                    settingsPath,
                    manifest.Preset,
                    active.ManifestPath,
                    currentValues,
                    $"[{changedManagedSetting.Section}] {changedManagedSetting.Key} changed after SpinTexture applied the preset. Restore will not overwrite that conflict silently.",
                    HasVerifiedRestorePoint: true);
            }

            var currentFingerprint = await FileIntegrity.FingerprintAsync(settingsPath, cancellationToken)
                .ConfigureAwait(false);
            var summary = currentFingerprint.Length == manifest.AppliedLength
                          && string.Equals(
                              currentFingerprint.Sha256,
                              manifest.AppliedSha256,
                              StringComparison.OrdinalIgnoreCase)
                ? $"The {manifest.Preset} native preset is active and verified."
                : $"The {manifest.Preset} native preset is active. EverQuest changed unrelated preferences; SpinTexture will preserve them during restore.";
            return new NativeGraphicsSettingsStatus(
                NativeGraphicsSettingsState.Applied,
                settingsPath,
                manifest.Preset,
                active.ManifestPath,
                currentValues,
                summary,
                HasVerifiedRestorePoint: true);
        }

        var capabilityError = await CheckCapabilityAsync(paths, cancellationToken).ConfigureAwait(false);
        if (capabilityError is not null)
        {
            return new NativeGraphicsSettingsStatus(
                NativeGraphicsSettingsState.Unavailable,
                settingsPath,
                null,
                null,
                currentValues,
                capabilityError);
        }

        return new NativeGraphicsSettingsStatus(
            NativeGraphicsSettingsState.Original,
            settingsPath,
            null,
            null,
            currentValues,
            "No SpinTexture native graphics preset is active. The current EQL settings remain unmanaged and unchanged by SpinTexture.");
    }

    public async Task<NativeGraphicsSettingsPlan> PlanAsync(
        ProjectPaths paths,
        NativeGraphicsPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        var status = await InspectAsync(paths, cancellationToken).ConfigureAwait(false);
        var capabilityError = await CheckCapabilityAsync(paths, cancellationToken).ConfigureAwait(false);
        if (capabilityError is not null)
        {
            status = status with
            {
                State = NativeGraphicsSettingsState.Unavailable,
                Summary = capabilityError
            };
        }

        if (!status.CanApply)
        {
            return new NativeGraphicsSettingsPlan(
                preset,
                status,
                [],
                status.Summary);
        }

        var document = await ReadDocumentAsync(status.SettingsPath, cancellationToken).ConfigureAwait(false);
        var active = await FindActiveTransactionAsync(paths, cancellationToken).ConfigureAwait(false);
        var baseline = active.Manifest is null
            ? CaptureStoredValues(document)
            : BuildCurrentBaseline(active.Manifest, document);
        EnsureCompleteBaseline(baseline, ManagedSettings);

        var desired = BuildDesiredSettings(preset, baseline, ManagedSettings);
        var changes = new List<NativeGraphicsSettingChange>();
        foreach (var setting in ManagedSettings)
        {
            var current = document.Get(setting.Section, setting.Key);
            var target = FindStored(desired, setting);
            if (SettingEquals(current, target))
            {
                continue;
            }

            changes.Add(new NativeGraphicsSettingChange(
                setting.Section,
                setting.Key,
                current.Exists,
                current.Value,
                target.Exists
                    ? target.Value ?? string.Empty
                    : NativeGraphicsSettingsPlan.RemovedValue,
                target.Exists && !current.Exists));
        }

        var distanceSummary = preset == NativeGraphicsPreset.Cinematic
            ? "Cinematic uses EQL's verified maximum native shadow clip distance."
            : "Balanced preserves the pre-preset shadow clip distance.";
        var summary = changes.Count == 0
            ? $"The {preset} native preset already matches eqclient.ini. {distanceSummary}"
            : $"{changes.Count:N0} native EQL setting change(s) are planned. {distanceSummary}";
        return new NativeGraphicsSettingsPlan(preset, status, changes, summary);
    }

    public async Task<NativeGraphicsApplyResult> ApplyAsync(
        ProjectPaths paths,
        NativeGraphicsPreset preset,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ensureClientsStopped("apply native graphics settings");
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);

        progress?.Report(new ProgressUpdate(
            "Native graphics",
            "Verifying client capability and current settings.",
            0,
            3));
        var plan = await PlanAsync(paths, preset, cancellationToken).ConfigureAwait(false);
        if (!plan.Status.CanApply)
        {
            throw new InvalidOperationException(plan.Status.Summary);
        }

        if (!plan.HasChanges)
        {
            throw new InvalidOperationException($"The {preset} native preset is already active.");
        }

        var settingsPath = plan.Status.SettingsPath;
        var currentBytes = await File.ReadAllBytesAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        var currentFingerprint = NativeGraphicsIniDocument.Fingerprint(currentBytes);
        var currentDocument = NativeGraphicsIniDocument.Parse(currentBytes);
        _ = CaptureValues(currentDocument);

        var active = await FindActiveTransactionAsync(paths, cancellationToken).ConfigureAwait(false);
        if (active.Error is not null)
        {
            throw new InvalidDataException(active.Error);
        }

        NativeGraphicsSettingsManifest? previousManifest = active.Manifest;
        if (previousManifest is not null)
        {
            var validationError = await ValidateActiveManifestAsync(
                    paths,
                    active.ManifestPath!,
                    previousManifest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validationError is not null)
            {
                throw new InvalidDataException(validationError);
            }

            if (previousManifest.State != NativeGraphicsTransactionState.Applied)
            {
                throw new InvalidOperationException(
                    "The existing native graphics transaction requires recovery before another preset can be applied.");
            }

            var changedManagedSetting = previousManifest.AppliedSettings.FirstOrDefault(applied =>
            {
                var current = currentDocument.Get(applied.Section, applied.Key);
                return !SettingEquals(current, applied);
            });
            if (changedManagedSetting is not null)
            {
                throw new InvalidOperationException(
                    $"[{changedManagedSetting.Section}] {changedManagedSetting.Key} changed after SpinTexture applied the preset. SpinTexture did not overwrite that conflict.");
            }
        }

        var capabilityError = await CheckCapabilityAsync(paths, cancellationToken).ConfigureAwait(false);
        if (capabilityError is not null)
        {
            throw new InvalidOperationException(capabilityError);
        }

        string transactionId;
        string transactionDirectory;
        string manifestPath;
        string originalBackupPath;
        IReadOnlyList<NativeGraphicsStoredSetting> baseline;

        if (previousManifest is null)
        {
            transactionId = PathGuard.ValidateIdentifier(null, "native-graphics");
            transactionDirectory = PathGuard.ResolveUnderRoot(
                paths.ClientSettingsTransactionsPath,
                transactionId);
            Directory.CreateDirectory(transactionDirectory);
            manifestPath = PathGuard.ResolveUnderRoot(transactionDirectory, ManifestFileName);
            originalBackupPath = PathGuard.ResolveUnderRoot(
                transactionDirectory,
                OriginalBackupFileName);
            baseline = CaptureStoredValues(currentDocument);

            await atomicFiles.CopyAndReplaceAsync(
                    settingsPath,
                    originalBackupPath,
                    currentFingerprint.Length,
                    currentFingerprint.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            transactionId = previousManifest.TransactionId;
            manifestPath = active.ManifestPath!;
            transactionDirectory = Path.GetDirectoryName(manifestPath)!;
            originalBackupPath = PathGuard.ResolveUnderRoot(
                transactionDirectory,
                previousManifest.OriginalBackupRelativePath);
            baseline = BuildCurrentBaseline(previousManifest, currentDocument);
            await FileIntegrity.EnsureMatchesAsync(
                    originalBackupPath,
                    previousManifest.OriginalLength,
                    previousManifest.OriginalSha256,
                    "Native graphics original backup",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureCompleteBaseline(baseline, ManagedSettings);
        var desired = BuildDesiredSettings(preset, baseline, ManagedSettings);
        var enhancedDocument = currentDocument.Clone();
        foreach (var setting in ManagedSettings)
        {
            var target = FindStored(desired, setting);
            enhancedDocument.Set(setting.Section, setting.Key, target.Exists, target.Value);
        }

        var enhancedBytes = enhancedDocument.ToBytes();
        var appliedSettings = BuildAppliedSettings(baseline, desired, ManagedSettings);
        var resultingState = appliedSettings.Count == 0
            ? NativeGraphicsTransactionState.Restored
            : NativeGraphicsTransactionState.Applied;
        if (resultingState == NativeGraphicsTransactionState.Restored
            && previousManifest is not null)
        {
            var originalBytes = await File.ReadAllBytesAsync(
                    originalBackupPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var originalDocument = NativeGraphicsIniDocument.Parse(originalBytes);
            var backupMatchesBaseline = ManagedSettings.All(setting =>
                SettingEquals(
                    originalDocument.Get(setting.Section, setting.Key),
                    FindStored(baseline, setting)));
            var previouslyAppliedDesired = BuildDesiredSettings(
                previousManifest.Preset,
                baseline,
                ManagedSettings);
            foreach (var setting in ManagedSettings)
            {
                var target = FindStored(previouslyAppliedDesired, setting);
                originalDocument.Set(setting.Section, setting.Key, target.Exists, target.Value);
            }

            if (backupMatchesBaseline
                && currentBytes.AsSpan().SequenceEqual(originalDocument.ToBytes()))
            {
                // A switch that returns every managed value to baseline should
                // also retire any section/key that SpinTexture had to create.
                // Use the verified exact backup only when no unrelated live
                // preference changed after the previous apply.
                enhancedBytes = originalBytes;
            }
        }

        var enhancedFingerprint = NativeGraphicsIniDocument.Fingerprint(enhancedBytes);
        var createdUtc = previousManifest?.CreatedUtc ?? DateTimeOffset.UtcNow;
        var preparingManifest = new NativeGraphicsSettingsManifest(
            NativeGraphicsSettingsManifest.CurrentSchemaVersion,
            transactionId,
            createdUtc,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.InstallPath)),
            SettingsRelativePath,
            NativeGraphicsTransactionState.Preparing,
            preset,
            previousManifest?.OriginalLength ?? currentFingerprint.Length,
            previousManifest?.OriginalSha256 ?? currentFingerprint.Sha256,
            OriginalBackupFileName,
            enhancedFingerprint.Length,
            enhancedFingerprint.Sha256,
            baseline,
            appliedSettings,
            CaptureStoredValues(currentDocument));

        var pendingPath = PathGuard.ResolveUnderRoot(
            transactionDirectory,
            $"pending-{Guid.NewGuid():N}.ini");
        var rollbackPath = PathGuard.ResolveUnderRoot(
            transactionDirectory,
            $"rollback-{Guid.NewGuid():N}.ini");
        var committed = false;
        try
        {
            await WriteDurableBytesAsync(pendingPath, enhancedBytes, cancellationToken).ConfigureAwait(false);
            await WriteDurableBytesAsync(rollbackPath, currentBytes, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(manifestPath, preparingManifest, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ProgressUpdate(
                "Native graphics",
                "Applying the verified native EQL settings.",
                1,
                3,
                "eqclient.ini"));
            ensureClientsStopped("apply native graphics settings");
            await FileIntegrity.EnsureMatchesAsync(
                    settingsPath,
                    currentFingerprint.Length,
                    currentFingerprint.Sha256,
                    "Live native graphics settings before apply",
                    cancellationToken)
                .ConfigureAwait(false);
            ensureClientsStopped("apply native graphics settings");
            await atomicFiles.CopyAndReplaceAsync(
                    pendingPath,
                    settingsPath,
                    enhancedFingerprint.Length,
                    enhancedFingerprint.Sha256,
                    cancellationToken,
                    () => committed = true)
                .ConfigureAwait(false);

            var finalManifest = preparingManifest with
            {
                State = resultingState,
                PreApplySettings = null
            };
            await WriteManifestAsync(manifestPath, finalManifest, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(
                "Native graphics",
                resultingState == NativeGraphicsTransactionState.Restored
                    ? "The selected preset matches the original managed values."
                    : "Native EQL graphics settings applied and verified.",
                3,
                3));
        }
        catch (Exception applyFailure)
        {
            var rollbackFailure = await TryRollbackApplyAsync(
                    settingsPath,
                    rollbackPath,
                    currentFingerprint,
                    enhancedFingerprint,
                    committed,
                    manifestPath,
                    preparingManifest,
                    previousManifest)
                .ConfigureAwait(false);
            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Native graphics apply failed and rollback also failed. Restore is required before launching EverQuest.",
                    applyFailure,
                    rollbackFailure);
            }

            throw;
        }
        finally
        {
            AtomicFile.TryDelete(pendingPath);
            AtomicFile.TryDelete(rollbackPath);
        }

        var status = await InspectAsync(paths, cancellationToken).ConfigureAwait(false);
        return new NativeGraphicsApplyResult(transactionId, manifestPath, plan, status);
    }

    public async Task<NativeGraphicsRestoreResult> RestoreAsync(
        ProjectPaths paths,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ensureClientsStopped("restore native graphics settings");
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);
        var active = await FindActiveTransactionAsync(paths, cancellationToken).ConfigureAwait(false);
        if (active.Manifest is null || active.ManifestPath is null)
        {
            throw new InvalidOperationException(active.Error ?? "No managed native graphics preset is active.");
        }

        var manifest = active.Manifest;
        var settingsPath = ResolveSettingsPath(paths);
        var validationError = await ValidateActiveManifestAsync(
                paths,
                active.ManifestPath,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        var manifestManagedSettings = GetManagedSettingsForManifest(manifest);

        progress?.Report(new ProgressUpdate(
            "Native graphics restore",
            "Verifying managed values and the original backup.",
            0,
            3));
        var currentBytes = await File.ReadAllBytesAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        var currentFingerprint = NativeGraphicsIniDocument.Fingerprint(currentBytes);
        var currentDocument = NativeGraphicsIniDocument.Parse(currentBytes);
        var conflicts = new List<string>();
        var restoredSettings = 0;
        var isInterruptedTransaction = manifest.State is NativeGraphicsTransactionState.Preparing
            or NativeGraphicsTransactionState.RecoveryRequired;
        if (isInterruptedTransaction)
        {
            var intendedSettings = BuildDesiredSettings(
                manifest.Preset,
                manifest.BaselineSettings,
                manifestManagedSettings);
            foreach (var setting in manifestManagedSettings)
            {
                var baseline = FindStored(manifest.BaselineSettings, setting);
                var current = currentDocument.Get(setting.Section, setting.Key);
                if (SettingEquals(current, baseline))
                {
                    continue;
                }

                var intended = FindStored(intendedSettings, setting);
                var matchesPreApply = manifest.PreApplySettings is not null
                    && SettingEquals(current, FindStored(manifest.PreApplySettings, setting));
                if (!SettingEquals(current, intended) && !matchesPreApply)
                {
                    conflicts.Add($"[{setting.Section}] {setting.Key}");
                    continue;
                }

                // A Preparing manifest can be durable on either side of the
                // atomic live-file replacement. Accept only the exact target
                // value or the captured pre-apply value, then return it to the
                // single verified baseline. Older manifests have no snapshot;
                // they therefore fail closed on an ambiguous preset switch.
                currentDocument.Set(setting.Section, setting.Key, baseline.Exists, baseline.Value);
                restoredSettings++;
            }
        }
        else
        {
            foreach (var applied in manifest.AppliedSettings)
            {
                var baseline = manifest.BaselineSettings.Single(value =>
                    SameSetting(value.Section, value.Key, applied.Section, applied.Key));
                var current = currentDocument.Get(applied.Section, applied.Key);
                if (SettingEquals(current, baseline))
                {
                    continue;
                }

                if (!SettingEquals(current, applied))
                {
                    conflicts.Add($"[{applied.Section}] {applied.Key}");
                    continue;
                }

                currentDocument.Set(applied.Section, applied.Key, baseline.Exists, baseline.Value);
                restoredSettings++;
            }
        }

        if (conflicts.Count != 0)
        {
            throw new InvalidOperationException(
                (isInterruptedTransaction
                    ? "The interrupted transaction contains ambiguous managed setting(s): "
                    : "EverQuest or another tool changed managed setting(s) after apply: ")
                + string.Join(", ", conflicts)
                + ". SpinTexture did not overwrite them.");
        }

        var restoredBytes = currentDocument.ToBytes();
        var manifestDirectory = Path.GetDirectoryName(active.ManifestPath)!;
        var originalBackupPath = PathGuard.ResolveUnderRoot(
            manifestDirectory,
            manifest.OriginalBackupRelativePath);
        var originalBytes = await File.ReadAllBytesAsync(originalBackupPath, cancellationToken)
            .ConfigureAwait(false);
        var originalDocument = NativeGraphicsIniDocument.Parse(originalBytes);
        var backupMatchesBaseline = manifestManagedSettings.All(setting =>
        {
            var baseline = FindStored(manifest.BaselineSettings, setting);
            return SettingEquals(originalDocument.Get(setting.Section, setting.Key), baseline);
        });
        var originalDesired = BuildDesiredSettings(
            manifest.Preset,
            manifest.BaselineSettings,
            manifestManagedSettings);
        foreach (var setting in manifestManagedSettings)
        {
            var target = FindStored(originalDesired, setting);
            originalDocument.Set(setting.Section, setting.Key, target.Exists, target.Value);
        }

        if (backupMatchesBaseline
            && currentBytes.AsSpan().SequenceEqual(originalDocument.ToBytes()))
        {
            // No unrelated preference changed after apply, so the exact byte-for-byte
            // original is the safest restore (including encoding and file layout).
            restoredBytes = originalBytes;
        }

        var restoredFingerprint = NativeGraphicsIniDocument.Fingerprint(restoredBytes);
        var transactionDirectory = manifestDirectory;
        var pendingPath = PathGuard.ResolveUnderRoot(
            transactionDirectory,
            $"restore-{Guid.NewGuid():N}.ini");
        var rollbackPath = PathGuard.ResolveUnderRoot(
            transactionDirectory,
            $"restore-rollback-{Guid.NewGuid():N}.ini");
        var committed = false;
        var preparingManifest = manifest with { State = NativeGraphicsTransactionState.Preparing };
        try
        {
            await WriteDurableBytesAsync(pendingPath, restoredBytes, cancellationToken).ConfigureAwait(false);
            await WriteDurableBytesAsync(rollbackPath, currentBytes, cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(active.ManifestPath, preparingManifest, cancellationToken).ConfigureAwait(false);
            ensureClientsStopped("restore native graphics settings");
            await FileIntegrity.EnsureMatchesAsync(
                    settingsPath,
                    currentFingerprint.Length,
                    currentFingerprint.Sha256,
                    "Live native graphics settings before restore",
                    cancellationToken)
                .ConfigureAwait(false);
            ensureClientsStopped("restore native graphics settings");
            await atomicFiles.CopyAndReplaceAsync(
                    pendingPath,
                    settingsPath,
                    restoredFingerprint.Length,
                    restoredFingerprint.Sha256,
                    cancellationToken,
                    () => committed = true)
                .ConfigureAwait(false);
            await WriteManifestAsync(
                    active.ManifestPath,
                    manifest with
                    {
                        State = NativeGraphicsTransactionState.Restored,
                        PreApplySettings = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception restoreFailure)
        {
            var rollbackFailure = await TryRollbackRestoreAsync(
                    settingsPath,
                    rollbackPath,
                    currentFingerprint,
                    restoredFingerprint,
                    committed,
                    active.ManifestPath,
                    manifest)
                .ConfigureAwait(false);
            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Native graphics restore failed and rollback also failed. Recovery is required.",
                    restoreFailure,
                    rollbackFailure);
            }

            throw;
        }
        finally
        {
            AtomicFile.TryDelete(pendingPath);
            AtomicFile.TryDelete(rollbackPath);
        }

        progress?.Report(new ProgressUpdate(
            "Native graphics restore",
            "Original managed settings restored; unrelated preferences were preserved.",
            3,
            3));
        var status = await InspectAsync(paths, cancellationToken).ConfigureAwait(false);
        return new NativeGraphicsRestoreResult(
            manifest.TransactionId,
            restoredSettings,
            DateTimeOffset.UtcNow,
            status);
    }

    private async Task<Exception?> TryRollbackApplyAsync(
        string settingsPath,
        string rollbackPath,
        (long Length, string Sha256) rollbackFingerprint,
        (long Length, string Sha256) committedFingerprint,
        bool committed,
        string manifestPath,
        NativeGraphicsSettingsManifest preparingManifest,
        NativeGraphicsSettingsManifest? previousManifest)
    {
        try
        {
            if (committed)
            {
                ensureClientsStopped("roll back native graphics settings");
                await FileIntegrity.EnsureMatchesAsync(
                        settingsPath,
                        committedFingerprint.Length,
                        committedFingerprint.Sha256,
                        "Committed native graphics settings before rollback",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ensureClientsStopped("roll back native graphics settings");
                await atomicFiles.CopyAndReplaceAsync(
                        rollbackPath,
                        settingsPath,
                        rollbackFingerprint.Length,
                        rollbackFingerprint.Sha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await WriteManifestAsync(
                    manifestPath,
                    previousManifest
                    ?? preparingManifest with
                    {
                        State = NativeGraphicsTransactionState.Restored,
                        PreApplySettings = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            try
            {
                await WriteManifestAsync(
                        manifestPath,
                        preparingManifest with { State = NativeGraphicsTransactionState.RecoveryRequired },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the rollback failure, which is the actionable error.
            }

            return exception;
        }
    }

    private async Task<Exception?> TryRollbackRestoreAsync(
        string settingsPath,
        string rollbackPath,
        (long Length, string Sha256) rollbackFingerprint,
        (long Length, string Sha256) committedFingerprint,
        bool committed,
        string manifestPath,
        NativeGraphicsSettingsManifest previousManifest)
    {
        try
        {
            if (committed)
            {
                ensureClientsStopped("roll back native graphics settings");
                await FileIntegrity.EnsureMatchesAsync(
                        settingsPath,
                        committedFingerprint.Length,
                        committedFingerprint.Sha256,
                        "Restored native graphics settings before rollback",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ensureClientsStopped("roll back native graphics settings");
                await atomicFiles.CopyAndReplaceAsync(
                        rollbackPath,
                        settingsPath,
                        rollbackFingerprint.Length,
                        rollbackFingerprint.Sha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await WriteManifestAsync(manifestPath, previousManifest, CancellationToken.None)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            try
            {
                await WriteManifestAsync(
                        manifestPath,
                        previousManifest with { State = NativeGraphicsTransactionState.RecoveryRequired },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the rollback failure.
            }

            return exception;
        }
    }

    private static async Task<string?> CheckCapabilityAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        string advancedOptionsPath;
        string graphicsDllPath;
        string gameExecutablePath;
        try
        {
            advancedOptionsPath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                AdvancedOptionsRelativePath);
            graphicsDllPath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                GraphicsDllRelativePath);
            gameExecutablePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                GameExecutableRelativePath);
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }

        if (!File.Exists(advancedOptionsPath)
            || !File.Exists(graphicsDllPath)
            || !File.Exists(gameExecutablePath))
        {
            return "This client does not include the native EQL shadow controls, game executable, and renderer markers required by SpinTexture.";
        }

        string xml;
        try
        {
            xml = await File.ReadAllTextAsync(advancedOptionsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The native EQL graphics controls could not be verified: {exception.Message}";
        }

        if (!xml.Contains("ADOW_ShadowsCheckbox", StringComparison.Ordinal)
            || !xml.Contains("ADOW_AdvancedLightingCheckbox", StringComparison.Ordinal)
            || !xml.Contains("ADOW_EnablePostEffectsCheckbox", StringComparison.Ordinal)
            || !xml.Contains("ADOW_EnableBloomCheckbox", StringComparison.Ordinal)
            || !xml.Contains("ADOW_ShadowClipPlaneSlider", StringComparison.Ordinal))
        {
            return "This client build does not expose all native shadow-distance and cinematic-lighting controls expected by SpinTexture.";
        }

        try
        {
            var graphicsBytes = await File.ReadAllBytesAsync(graphicsDllPath, cancellationToken)
                .ConfigureAwait(false);
            if (!ContainsAscii(graphicsBytes, "Shadows: Stencil Available"))
            {
                return "The selected renderer does not advertise the native stencil-shadow path expected by SpinTexture.";
            }

            if (!ContainsAscii(graphicsBytes, "MPL: Available")
                || !ContainsAscii(graphicsBytes, "Render: Post Effects Available"))
            {
                return "The selected renderer does not advertise all native advanced-lighting and post-effects paths required by the Cinematic preset.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The native EQL renderer could not be verified: {exception.Message}";
        }

        try
        {
            var gameBytes = await File.ReadAllBytesAsync(gameExecutablePath, cancellationToken)
                .ConfigureAwait(false);
            if (!ContainsAscii(gameBytes, "ShadowClipPlane"))
            {
                return "This EQL executable does not advertise the native ShadowClipPlane setting expected by SpinTexture.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The native EQL shadow-distance runtime could not be verified: {exception.Message}";
        }

        return null;
    }

    private static IReadOnlyList<NativeGraphicsStoredSetting> BuildDesiredSettings(
        NativeGraphicsPreset preset,
        IReadOnlyList<NativeGraphicsStoredSetting> baseline,
        IReadOnlyList<ManagedSetting> managedSettings)
    {
        var desired = baseline.Select(value => value with { }).ToList();
        SetStored(desired, "Defaults", "Shadows", true, "TRUE");
        if (preset == NativeGraphicsPreset.Cinematic)
        {
            SetStored(desired, "Defaults", "MultiPassLighting", true, "TRUE");
            SetStored(desired, "Defaults", "PostEffects", true, "TRUE");
            if (managedSettings.Any(setting => SameSetting(
                    setting.Section,
                    setting.Key,
                    "Defaults",
                    "Bloom")))
            {
                SetStored(desired, "Defaults", "Bloom", true, "TRUE");
            }

            if (managedSettings.Any(setting => SameSetting(
                    setting.Section,
                    setting.Key,
                    "Options",
                    "ShadowClipPlane")))
            {
                SetStored(
                    desired,
                    "Options",
                    "ShadowClipPlane",
                    true,
                    MaximumShadowClipPlane);
            }
        }

        return desired;
    }

    private static IReadOnlyList<NativeGraphicsStoredSetting> BuildAppliedSettings(
        IReadOnlyList<NativeGraphicsStoredSetting> baseline,
        IReadOnlyList<NativeGraphicsStoredSetting> desired,
        IReadOnlyList<ManagedSetting> managedSettings)
    {
        var result = new List<NativeGraphicsStoredSetting>();
        foreach (var setting in managedSettings)
        {
            var before = FindStored(baseline, setting);
            var after = FindStored(desired, setting);
            if (!StoredEquals(before, after))
            {
                result.Add(after);
            }
        }

        return result;
    }

    private static IReadOnlyList<NativeGraphicsSettingValue> CaptureValues(
        NativeGraphicsIniDocument document,
        IReadOnlyList<ManagedSetting>? managedSettings = null) =>
        (managedSettings ?? ManagedSettings)
        .Select(setting =>
        {
            var value = document.Get(setting.Section, setting.Key);
            return new NativeGraphicsSettingValue(
                setting.Section,
                setting.Key,
                value.Exists,
                value.Value);
        })
        .ToArray();

    private static IReadOnlyList<NativeGraphicsStoredSetting> CaptureStoredValues(
        NativeGraphicsIniDocument document) => ManagedSettings
        .Select(setting =>
        {
            var value = document.Get(setting.Section, setting.Key);
            return new NativeGraphicsStoredSetting(
                setting.Section,
                setting.Key,
                value.Exists,
                value.Value);
        })
        .ToArray();

    private static IReadOnlyList<NativeGraphicsStoredSetting> BuildCurrentBaseline(
        NativeGraphicsSettingsManifest manifest,
        NativeGraphicsIniDocument currentDocument)
    {
        var manifestSettings = GetManagedSettingsForManifest(manifest);
        EnsureCompleteBaseline(manifest.BaselineSettings, manifestSettings);
        if (manifestSettings.Count == ManagedSettings.Length)
        {
            return manifest.BaselineSettings;
        }

        // Older schemas predate one or more managed settings. Every newly owned
        // value remains entirely unmanaged until a real preset switch, at which
        // point its current live value becomes the baseline. Never source a newly
        // managed value from the older whole-file backup: EverQuest or the user
        // may have legitimately changed it since the original transaction.
        var migrated = manifest.BaselineSettings.Select(value => value with { }).ToList();
        foreach (var setting in ManagedSettings.Where(setting =>
                     !manifestSettings.Any(existing => SameSetting(
                         existing.Section,
                         existing.Key,
                         setting.Section,
                         setting.Key))))
        {
            var current = currentDocument.Get(setting.Section, setting.Key);
            migrated.Add(new NativeGraphicsStoredSetting(
                setting.Section,
                setting.Key,
                current.Exists,
                current.Value));
        }

        return migrated;
    }

    private static IReadOnlyList<ManagedSetting> GetManagedSettingsForManifest(
        NativeGraphicsSettingsManifest manifest)
    {
        if (manifest.SchemaVersion == NativeGraphicsSettingsManifest.CurrentSchemaVersion)
        {
            return ManagedSettings;
        }

        if (manifest.SchemaVersion == NativeGraphicsSettingsManifest.LightingOnlySchemaVersion)
        {
            return LightingOnlyManagedSettings;
        }

        if (manifest.SchemaVersion == NativeGraphicsSettingsManifest.LegacySchemaVersion)
        {
            return manifest.BaselineSettings?.Count switch
            {
                3 => LegacyManagedSettings,
                4 => LightingOnlyManagedSettings,
                _ => throw new InvalidDataException(
                    "The legacy native graphics transaction has an invalid managed-setting baseline.")
            };
        }

        throw new InvalidDataException(
            $"Unsupported native graphics transaction schema {manifest.SchemaVersion}.");
    }

    private static void EnsureCompleteBaseline(
        IReadOnlyList<NativeGraphicsStoredSetting> baseline,
        IReadOnlyList<ManagedSetting> managedSettings)
    {
        if (baseline is null || baseline.Count != managedSettings.Count)
        {
            throw new InvalidDataException(
                "The native graphics transaction does not contain exactly the managed settings baseline.");
        }

        EnsureValidStoredValues(baseline, "baseline");
        foreach (var setting in managedSettings)
        {
            if (baseline.Count(value => SameSetting(
                    value.Section,
                    value.Key,
                    setting.Section,
                    setting.Key)) != 1)
            {
                throw new InvalidDataException(
                    $"The native graphics transaction has an invalid baseline for [{setting.Section}] {setting.Key}.");
            }
        }

    }

    private static void EnsureValidAppliedSettings(
        IReadOnlyList<NativeGraphicsStoredSetting> appliedSettings,
        IReadOnlyList<ManagedSetting> managedSettings)
    {
        if (appliedSettings is null || appliedSettings.Count > managedSettings.Count)
        {
            throw new InvalidDataException("The native graphics transaction has an invalid applied-settings list.");
        }

        EnsureValidStoredValues(appliedSettings, "applied settings");
        foreach (var setting in appliedSettings)
        {
            if (!managedSettings.Any(managed => SameSetting(
                    managed.Section,
                    managed.Key,
                    setting.Section,
                    setting.Key))
                || appliedSettings.Count(candidate => SameSetting(
                    candidate.Section,
                    candidate.Key,
                    setting.Section,
                    setting.Key)) != 1)
            {
                throw new InvalidDataException(
                    $"The native graphics transaction contains an unmanaged or duplicate applied setting [{setting.Section}] {setting.Key}.");
            }
        }
    }

    private static void EnsureValidPreApplySettings(NativeGraphicsSettingsManifest manifest)
    {
        if (manifest.PreApplySettings is null)
        {
            return;
        }

        if (manifest.SchemaVersion != NativeGraphicsSettingsManifest.CurrentSchemaVersion
            || manifest.State is not (
                NativeGraphicsTransactionState.Preparing
                or NativeGraphicsTransactionState.RecoveryRequired))
        {
            throw new InvalidDataException(
                "The native graphics transaction contains unexpected pre-apply recovery metadata.");
        }

        EnsureCompleteBaseline(manifest.PreApplySettings, ManagedSettings);
    }

    private static void EnsureValidStoredValues(
        IReadOnlyList<NativeGraphicsStoredSetting> values,
        string description)
    {
        foreach (var value in values)
        {
            if (value is null
                || string.IsNullOrWhiteSpace(value.Section)
                || string.IsNullOrWhiteSpace(value.Key)
                || (value.Exists ? value.Value is null : value.Value is not null))
            {
                throw new InvalidDataException(
                    $"The native graphics transaction has an invalid {description} value.");
            }
        }
    }

    private static NativeGraphicsStoredSetting FindStored(
        IReadOnlyList<NativeGraphicsStoredSetting> values,
        ManagedSetting setting) => values.Single(value =>
        SameSetting(value.Section, value.Key, setting.Section, setting.Key));

    private static void SetStored(
        List<NativeGraphicsStoredSetting> values,
        string section,
        string key,
        bool exists,
        string? value)
    {
        var index = values.FindIndex(current =>
            SameSetting(current.Section, current.Key, section, key));
        if (index < 0)
        {
            throw new InvalidDataException($"Missing native graphics baseline [{section}] {key}.");
        }

        values[index] = new NativeGraphicsStoredSetting(section, key, exists, value);
    }

    private static bool SettingEquals(
        NativeGraphicsIniValue current,
        NativeGraphicsStoredSetting expected) =>
        current.Exists == expected.Exists
        && (!current.Exists || ValueEquals(current.Value, expected.Value));

    private static bool StoredEquals(
        NativeGraphicsStoredSetting left,
        NativeGraphicsStoredSetting right) =>
        left.Exists == right.Exists
        && (!left.Exists || ValueEquals(left.Value, right.Value));

    private static bool ValueEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizeBoolean(left);
        var normalizedRight = NormalizeBoolean(right);
        if (normalizedLeft is not null && normalizedRight is not null)
        {
            return normalizedLeft == normalizedRight;
        }

        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool? NormalizeBoolean(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" or "ON" => true,
        "FALSE" or "0" or "OFF" => false,
        _ => null
    };

    private static bool SameSetting(
        string leftSection,
        string leftKey,
        string rightSection,
        string rightKey) =>
        leftSection.Equals(rightSection, StringComparison.OrdinalIgnoreCase)
        && leftKey.Equals(rightKey, StringComparison.OrdinalIgnoreCase);

    private static string ResolveSettingsPath(ProjectPaths paths) =>
        PathGuard.ResolveUnderRoot(paths.InstallPath, SettingsRelativePath);

    private static async Task<NativeGraphicsIniDocument> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return NativeGraphicsIniDocument.Parse(bytes);
    }

    private static async Task<ActiveTransaction> FindActiveTransactionAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.ClientSettingsTransactionsPath))
        {
            return new ActiveTransaction(null, null, null);
        }

        var active = new List<(string Path, NativeGraphicsSettingsManifest Manifest)>();
        foreach (var directory in Directory.EnumerateDirectories(
                     paths.ClientSettingsTransactionsPath,
                     "native-graphics-*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath;
            try
            {
                manifestPath = PathGuard.ResolveUnderRoot(directory, ManifestFileName);
            }
            catch (InvalidDataException exception)
            {
                return new ActiveTransaction(null, null, exception.Message);
            }

            if (!File.Exists(manifestPath))
            {
                continue;
            }

            NativeGraphicsSettingsManifest manifest;
            try
            {
                manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                                 or UnauthorizedAccessException
                                                 or JsonException
                                                 or InvalidDataException)
            {
                return new ActiveTransaction(
                    manifestPath,
                    null,
                    $"Native graphics transaction metadata is invalid: {exception.Message}");
            }

            if (manifest.State != NativeGraphicsTransactionState.Restored)
            {
                active.Add((manifestPath, manifest));
            }
        }

        if (active.Count > 1)
        {
            return new ActiveTransaction(
                null,
                null,
                "More than one native graphics transaction is active. Recovery is required.");
        }

        return active.Count == 0
            ? new ActiveTransaction(null, null, null)
            : new ActiveTransaction(active[0].Path, active[0].Manifest, null);
    }

    private static async Task<string?> ValidateActiveManifestAsync(
        ProjectPaths paths,
        string manifestPath,
        NativeGraphicsSettingsManifest manifest,
        CancellationToken cancellationToken)
    {
        var structureError = ValidateManifestStructure(manifestPath, manifest);
        if (structureError is not null)
        {
            return structureError;
        }

        if (!PathGuard.SamePath(manifest.InstallPath, paths.InstallPath)
            || !string.Equals(
                manifest.SettingsRelativePath,
                SettingsRelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The native graphics transaction belongs to a different client installation.";
        }

        try
        {
            var managedSettings = GetManagedSettingsForManifest(manifest);
            EnsureCompleteBaseline(manifest.BaselineSettings, managedSettings);
            EnsureValidAppliedSettings(manifest.AppliedSettings, managedSettings);
            EnsureValidPreApplySettings(manifest);
            var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
            var backupPath = PathGuard.ResolveUnderRoot(
                manifestDirectory,
                manifest.OriginalBackupRelativePath);
            await FileIntegrity.EnsureMatchesAsync(
                    backupPath,
                    manifest.OriginalLength,
                    manifest.OriginalSha256,
                    "Native graphics original backup",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or InvalidDataException)
        {
            return exception.Message;
        }

        return null;
    }

    private static string? ValidateManifestStructure(
        string manifestPath,
        NativeGraphicsSettingsManifest manifest)
    {
        if (manifest.SchemaVersion is not (
            NativeGraphicsSettingsManifest.LegacySchemaVersion
            or NativeGraphicsSettingsManifest.LightingOnlySchemaVersion
            or NativeGraphicsSettingsManifest.CurrentSchemaVersion))
        {
            return $"Unsupported native graphics transaction schema {manifest.SchemaVersion}.";
        }

        if (!Enum.IsDefined(manifest.State) || !Enum.IsDefined(manifest.Preset))
        {
            return "The native graphics transaction contains an invalid state or preset.";
        }

        if (string.IsNullOrWhiteSpace(manifest.TransactionId)
            || string.IsNullOrWhiteSpace(manifest.InstallPath)
            || string.IsNullOrWhiteSpace(manifest.SettingsRelativePath)
            || string.IsNullOrWhiteSpace(manifest.OriginalBackupRelativePath))
        {
            return "The native graphics transaction is missing required path or identity metadata.";
        }

        try
        {
            _ = PathGuard.ValidateIdentifier(manifest.TransactionId, "native-graphics");
            var transactionDirectory = Path.GetDirectoryName(manifestPath);
            if (transactionDirectory is null
                || !string.Equals(
                    Path.GetFileName(transactionDirectory),
                    manifest.TransactionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "The native graphics transaction identity does not match its protected directory.";
            }

            _ = Path.GetFullPath(manifest.InstallPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                             or NotSupportedException
                                             or PathTooLongException)
        {
            return $"The native graphics transaction identity or install path is invalid: {exception.Message}";
        }

        if (!string.Equals(
                manifest.SettingsRelativePath,
                SettingsRelativePath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                manifest.OriginalBackupRelativePath,
                OriginalBackupFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The native graphics transaction contains an unexpected settings or backup path.";
        }

        if (manifest.OriginalLength < 0
            || manifest.AppliedLength < 0
            || !IsSha256(manifest.OriginalSha256)
            || !IsSha256(manifest.AppliedSha256))
        {
            return "The native graphics transaction contains an invalid file length or SHA-256 fingerprint.";
        }

        if (manifest.BaselineSettings is null || manifest.AppliedSettings is null)
        {
            return "The native graphics transaction is missing managed-setting metadata.";
        }

        try
        {
            var managedSettings = GetManagedSettingsForManifest(manifest);
            if (manifest.BaselineSettings.Count != managedSettings.Count
                || manifest.BaselineSettings.Any(value => !IsValidStoredSetting(value)))
            {
                return "The native graphics transaction contains an invalid managed-setting baseline.";
            }

            EnsureCompleteBaseline(manifest.BaselineSettings, managedSettings);
            var expectedApplied = BuildAppliedSettings(
                manifest.BaselineSettings,
                BuildDesiredSettings(
                    manifest.Preset,
                    manifest.BaselineSettings,
                    managedSettings),
                managedSettings);
            if (manifest.AppliedSettings.Count != expectedApplied.Count
                || manifest.AppliedSettings.Any(value => !IsValidStoredSetting(value))
                || expectedApplied.Any(expected =>
                    manifest.AppliedSettings.Count(actual =>
                        SameSetting(
                            actual.Section,
                            actual.Key,
                            expected.Section,
                            expected.Key)
                        && StoredEquals(actual, expected)) != 1))
            {
                return "The native graphics transaction contains invalid applied managed-setting metadata.";
            }

            EnsureValidPreApplySettings(manifest);
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }

        return null;
    }

    private static bool IsValidStoredSetting(NativeGraphicsStoredSetting? value)
    {
        if (value is null
            || string.IsNullOrWhiteSpace(value.Section)
            || string.IsNullOrWhiteSpace(value.Key)
            || (value.Exists ? value.Value is null : value.Value is not null))
        {
            return false;
        }

        return ManagedSettings.Any(setting => SameSetting(
            value.Section,
            value.Key,
            setting.Section,
            setting.Key));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task WriteDurableBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteManifestAsync(
        string path,
        NativeGraphicsSettingsManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = AtomicFile.CreateTemporarySiblingPath(path);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        manifest,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            AtomicFile.CommitTemporaryFile(temporaryPath, path);
        }
        catch
        {
            AtomicFile.TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task<NativeGraphicsSettingsManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<NativeGraphicsSettingsManifest>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Native graphics manifest is empty.");
        if (manifest.SchemaVersion is not (
            NativeGraphicsSettingsManifest.LegacySchemaVersion
            or NativeGraphicsSettingsManifest.LightingOnlySchemaVersion
            or NativeGraphicsSettingsManifest.CurrentSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported native graphics manifest schema {manifest.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TransactionId)
            || string.IsNullOrWhiteSpace(manifest.InstallPath)
            || string.IsNullOrWhiteSpace(manifest.SettingsRelativePath)
            || string.IsNullOrWhiteSpace(manifest.OriginalBackupRelativePath))
        {
            throw new InvalidDataException("Native graphics manifest identity or path metadata is incomplete.");
        }

        var managedSettings = GetManagedSettingsForManifest(manifest);
        EnsureCompleteBaseline(manifest.BaselineSettings, managedSettings);
        EnsureValidAppliedSettings(manifest.AppliedSettings, managedSettings);
        EnsureValidPreApplySettings(manifest);

        return manifest;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> bytes, string value) =>
        bytes.IndexOf(Encoding.ASCII.GetBytes(value)) >= 0;

    private static void EnsureClientsStopped(string operation)
    {
        if (EverQuestInstall.IsGameOrLauncherRunning())
        {
            throw new InvalidOperationException(
                $"EverQuest and LaunchPad must be closed before SpinTexture can {operation}.");
        }
    }

    private sealed record ManagedSetting(string Section, string Key);

    private sealed record ActiveTransaction(
        string? ManifestPath,
        NativeGraphicsSettingsManifest? Manifest,
        string? Error);
}
