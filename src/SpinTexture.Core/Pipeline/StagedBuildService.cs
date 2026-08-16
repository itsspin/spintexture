using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.Core.Pipeline;

public sealed class StagedBuildService
{
    private const string CheckpointFileName = "build-checkpoint.json";
    private static readonly JsonSerializerOptions CheckpointJsonOptions = CreateCheckpointJsonOptions();
    private readonly ManifestStore _manifestStore;

    public StagedBuildService(ManifestStore? manifestStore = null)
    {
        _manifestStore = manifestStore ?? new ManifestStore();
    }

    public Task<RecoverableStagedBuild?> FindRecoverableBuildAsync(
        ProjectPaths paths,
        string resumeOperationKey,
        CancellationToken cancellationToken = default)
    {
        var operationKey = PathGuard.ValidateIdentifier(resumeOperationKey, "operation");
        return FindRecoverableBuildCoreAsync(
            paths,
            _ => operationKey,
            cancellationToken);
    }

    public Task<RecoverableStagedBuild?> FindRecoverableBuildAsync(
        ProjectPaths paths,
        Func<UpscaleOptions, string> resumeOperationKeyResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resumeOperationKeyResolver);
        return FindRecoverableBuildCoreAsync(
            paths,
            options => PathGuard.ValidateIdentifier(
                resumeOperationKeyResolver(options),
                "operation"),
            cancellationToken);
    }

    private async Task<RecoverableStagedBuild?> FindRecoverableBuildCoreAsync(
        ProjectPaths paths,
        Func<UpscaleOptions, string> resumeOperationKeyResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.StagingPath))
        {
            return null;
        }

        using var stagingLock = StagingLibraryLock.Acquire(paths.WorkspacePath);
        foreach (var directory in EnumerateBuildDirectories(paths.StagingPath)
                 .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpointPath = PathGuard.ResolveUnderRoot(
                paths.StagingPath,
                Path.Combine(Path.GetFileName(directory), CheckpointFileName));
            if (!File.Exists(checkpointPath))
            {
                continue;
            }

            try
            {
                var checkpoint = await ReadCheckpointAsync(checkpointPath, cancellationToken)
                    .ConfigureAwait(false);
                var operationKey = resumeOperationKeyResolver(checkpoint.Options);
                if (!checkpoint.ResumeOperationKey.Equals(operationKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var calculatedPlan = ComputePlanSha256(
                    checkpoint.InstallPath,
                    checkpoint.Options,
                    checkpoint.RequireAllItems,
                    operationKey,
                    checkpoint.Items);
                ValidateCheckpoint(
                    checkpoint,
                    paths,
                    checkpoint.Items,
                    calculatedPlan,
                    checkpoint.RequireAllItems,
                    checkpoint.ResumeOperationKey,
                    directory);
                var completedByPath = checkpoint.CompletedArtifacts.ToDictionary(
                    item => item.RelativeInstallPath,
                    StringComparer.OrdinalIgnoreCase);
                var verified = 0;
                var payloadDirectory = Path.Combine(directory, "payload");
                foreach (var planned in checkpoint.Items)
                {
                    if (completedByPath.TryGetValue(planned.RelativeInstallPath, out var completed)
                        && await ValidateCompletedArtifactAsync(
                                planned,
                                completed,
                                PathGuard.ResolveUnderRoot(payloadDirectory, planned.RelativeInstallPath),
                                directory,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        verified++;
                    }
                }

                if (verified > 0)
                {
                    return new RecoverableStagedBuild(
                        checkpoint.BuildId,
                        checkpoint.CreatedUtc,
                        checkpoint.Options,
                        verified,
                        checkpoint.Items.Count);
                }
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or InvalidDataException
                                                   or ArgumentException
                                                   or OverflowException)
            {
                // Fail closed. A corrupt or obsolete checkpoint is not offered
                // as recoverable and remains untouched for manual diagnosis.
            }
        }

        return null;
    }

    public async Task<StagedBuildResult> BuildAsync(
        StagedBuildRequest request,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        Func<StagedBuildResult, CancellationToken, Task>? finalizeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.Items);
        WorldExpansionSelectionPolicy.Validate(request.Options);
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("A staged build must contain at least one install artifact.", nameof(request));
        }

        using var stagingLock = StagingLibraryLock.Acquire(request.Paths.WorkspacePath);
        request.Paths.EnsureWorkspaceDirectories();
        var normalizedItems = NormalizeItems(request.Paths, request.Items);
        var planItems = await CreatePlanItemsAsync(normalizedItems, cancellationToken).ConfigureAwait(false);
        var resumeOperationKey = string.IsNullOrWhiteSpace(request.ResumeOperationKey)
            ? null
            : PathGuard.ValidateIdentifier(request.ResumeOperationKey, "operation");
        var planSha256 = ComputePlanSha256(
            request.Paths.InstallPath,
            request.Options,
            request.RequireAllItems,
            resumeOperationKey,
            planItems);
        var existingCheckpoint = await FindMatchingCheckpointAsync(
                request.Paths,
                request.Options,
                planItems,
                planSha256,
                request.RequireAllItems,
                resumeOperationKey,
                request.BuildId,
                cancellationToken)
            .ConfigureAwait(false);
        var activeRunStartedUtc = DateTimeOffset.UtcNow;

        var buildId = existingCheckpoint?.Checkpoint.BuildId
            ?? PathGuard.ValidateIdentifier(request.BuildId, "build");
        var buildDirectory = PathGuard.ResolveUnderRoot(request.Paths.StagingPath, buildId);
        var checkpointPath = Path.Combine(buildDirectory, CheckpointFileName);
        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        var workDirectory = Path.Combine(buildDirectory, "work");
        var previewDirectory = Path.Combine(buildDirectory, "previews");

        StagedBuildCheckpoint checkpoint;
        if (existingCheckpoint is null)
        {
            if (Directory.Exists(buildDirectory) || File.Exists(buildDirectory))
            {
                throw new IOException($"Build transaction already exists: {buildDirectory}");
            }

            Directory.CreateDirectory(payloadDirectory);
            Directory.CreateDirectory(workDirectory);
            checkpoint = new StagedBuildCheckpoint(
                StagedBuildCheckpoint.CurrentSchemaVersion,
                buildId,
                DateTimeOffset.UtcNow,
                0,
                Path.GetFullPath(request.Paths.InstallPath),
                request.Options,
                request.RequireAllItems,
                resumeOperationKey ?? string.Empty,
                planSha256,
                planItems,
                []);
            await WriteCheckpointAsync(checkpointPath, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            checkpoint = existingCheckpoint.Checkpoint;
            Directory.CreateDirectory(payloadDirectory);
            Directory.CreateDirectory(workDirectory);
            progress?.Report(new ProgressUpdate(
                "Resume",
                $"Resuming {checkpoint.CompletedArtifacts.Count:N0} verified artifact checkpoint(s); {Math.Max(0, normalizedItems.Count - checkpoint.CompletedArtifacts.Count):N0} remaining.",
                checkpoint.CompletedArtifacts.Count,
                normalizedItems.Count));
        }

        var activeSegmentStartedUtc = activeRunStartedUtc;
        double CaptureActiveDuration()
        {
            var now = DateTimeOffset.UtcNow;
            var total = AddActiveDuration(
                checkpoint.ActiveDurationSeconds,
                activeSegmentStartedUtc,
                now);
            activeSegmentStartedUtc = now;
            return total;
        }

        try
        {
        var entries = new List<BuildManifestEntry>(normalizedItems.Count);
        var resumedArtifacts = 0;
        var processedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A sparse or tampered checkpoint can force one earlier artifact to be
        // rebuilt while later verified artifacts remain reusable. Never make
        // the user's overall progress bar move backwards in that case.
        var reportedCompletedFloor = existingCheckpoint?.Checkpoint.CompletedArtifacts.Count ?? 0;
        int ReportedCompleted() => Math.Max(reportedCompletedFloor, processedItems.Count);
        var completedByPath = checkpoint.CompletedArtifacts.ToDictionary(
            item => item.RelativeInstallPath,
            StringComparer.OrdinalIgnoreCase);
        progress?.Report(new ProgressUpdate(
            "Build",
            existingCheckpoint is null
                ? "Building into staging; the EverQuest install remains read-only."
                : "Resuming the exact verified staged build; the EverQuest install remains read-only.",
            existingCheckpoint is null ? 0 : checkpoint.CompletedArtifacts.Count,
            normalizedItems.Count));

        for (var index = 0; index < normalizedItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = normalizedItems[index];
            var planned = planItems[index];
            var finalStagedPath = PathGuard.ResolveUnderRoot(payloadDirectory, item.RelativeInstallPath);

            if (completedByPath.TryGetValue(item.RelativeInstallPath, out var completed))
            {
                if (await ValidateCompletedArtifactAsync(
                        planned,
                        completed,
                        finalStagedPath,
                        buildDirectory,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!completed.Omitted)
                    {
                        entries.Add(CreateManifestEntry(completed));
                    }

                    if (completed.Contribution is not null
                        && item.Builder is IStagedArtifactCheckpointParticipant participant)
                    {
                        participant.RestoreCheckpointContribution(
                            MakeContributionPathsAbsolute(completed.Contribution, buildDirectory));
                    }

                    resumedArtifacts++;
                    processedItems.Add(item.RelativeInstallPath);
                    progress?.Report(new ProgressUpdate(
                        "Build",
                        "Verified completed artifact and skipped duplicate upscaling.",
                        ReportedCompleted(),
                        normalizedItems.Count,
                        item.RelativeInstallPath));
                    continue;
                }

                completedByPath.Remove(item.RelativeInstallPath);
                checkpoint = checkpoint with
                {
                    ActiveDurationSeconds = CaptureActiveDuration(),
                    CompletedArtifacts = checkpoint.CompletedArtifacts
                        .Where(candidate => !candidate.RelativeInstallPath.Equals(
                            item.RelativeInstallPath,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                };
                AtomicFile.TryDelete(finalStagedPath);
                await WriteCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None)
                    .ConfigureAwait(false);
                progress?.Report(new ProgressUpdate(
                    "Resume",
                    "A checkpoint payload was missing or failed SHA-256 verification; rebuilding only this artifact.",
                    ReportedCompleted(),
                    normalizedItems.Count,
                    item.RelativeInstallPath));
            }

            progress?.Report(new ProgressUpdate(
                "Build",
                "Rebuilding complete install artifact in staging.",
                ReportedCompleted(),
                normalizedItems.Count,
                item.RelativeInstallPath));

            var sourceFingerprint = await FileIntegrity.FingerprintAsync(
                item.SourcePath,
                cancellationToken).ConfigureAwait(false);
            EnsureSourceMatchesPlan(item.SourcePath, planned, sourceFingerprint);
            Directory.CreateDirectory(Path.GetDirectoryName(finalStagedPath)!);
            var temporaryStagedPath = AtomicFile.CreateTemporarySiblingPath(finalStagedPath);
            var artifactWorkDirectory = Path.Combine(workDirectory, $"{index:D5}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifactWorkDirectory);

            try
            {
                var participant = item.Builder as IStagedArtifactCheckpointParticipant;
                var participantBefore = participant?.CaptureCheckpointState();
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
                    throw new IOException($"Source artifact changed while the staged build was running: {item.SourcePath}");
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
                        || !stagedFingerprint.Sha256.Equals(item.ExpectedStagedSha256, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"Reused staged output no longer matches its expected SHA-256: {item.RelativeInstallPath}");
                }

                StagedBuildCheckpointArtifact completedArtifact;
                if (stagedFingerprint == sourceFingerprint)
                {
                    AtomicFile.TryDelete(temporaryStagedPath);
                    AtomicFile.TryDelete(finalStagedPath);
                    if (request.RequireAllItems && !item.AllowSourceIdenticalOmission)
                    {
                        throw new InvalidOperationException(
                            $"A required replacement artifact contained no verified changes: {item.RelativeInstallPath}");
                    }

                    var contribution = participant is not null && participantBefore is not null
                        ? participant.CompleteCheckpointCapture(participantBefore)
                        : null;
                    contribution = await FingerprintCheckpointPreviewsAsync(
                            contribution,
                            buildDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                    completedArtifact = new StagedBuildCheckpointArtifact(
                        item.RelativeInstallPath,
                        sourceFingerprint.Length,
                        sourceFingerprint.Sha256,
                        stagedFingerprint.Length,
                        stagedFingerprint.Sha256,
                        Omitted: true,
                        contribution);
                }
                else
                {
                    AtomicFile.CommitTemporaryFile(temporaryStagedPath, finalStagedPath);
                    await FileIntegrity.EnsureMatchesAsync(
                        finalStagedPath,
                        stagedFingerprint.Length,
                        stagedFingerprint.Sha256,
                        "Committed staged artifact",
                        CancellationToken.None).ConfigureAwait(false);
                    var contribution = participant is not null && participantBefore is not null
                        ? participant.CompleteCheckpointCapture(participantBefore)
                        : null;
                    contribution = await FingerprintCheckpointPreviewsAsync(
                            contribution,
                            buildDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                    completedArtifact = new StagedBuildCheckpointArtifact(
                        item.RelativeInstallPath,
                        sourceFingerprint.Length,
                        sourceFingerprint.Sha256,
                        stagedFingerprint.Length,
                        stagedFingerprint.Sha256,
                        Omitted: false,
                        contribution);
                    entries.Add(CreateManifestEntry(completedArtifact));
                }

                checkpoint = checkpoint with
                {
                    ActiveDurationSeconds = CaptureActiveDuration(),
                    CompletedArtifacts = checkpoint.CompletedArtifacts
                        .Where(candidate => !candidate.RelativeInstallPath.Equals(
                            item.RelativeInstallPath,
                            StringComparison.OrdinalIgnoreCase))
                        .Append(completedArtifact)
                        .OrderBy(candidate => candidate.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
                await WriteCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None)
                    .ConfigureAwait(false);
                completedByPath[item.RelativeInstallPath] = completedArtifact;
                processedItems.Add(item.RelativeInstallPath);

                progress?.Report(new ProgressUpdate(
                    "Build",
                    completedArtifact.Omitted
                        ? "Artifact contained no eligible changes and was checkpointed as omitted."
                        : "Staged artifact and crash-recovery checkpoint verified with SHA-256.",
                    ReportedCompleted(),
                    normalizedItems.Count,
                    item.RelativeInstallPath));
            }
            finally
            {
                AtomicFile.TryDelete(temporaryStagedPath);
                TryDeleteOwnedDirectory(request.Paths.StagingPath, artifactWorkDirectory);
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The staged build produced no changed install artifacts.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.BeforeManifestCommitAsync is not null)
        {
            await request.BeforeManifestCommitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var manifest = new BuildManifest(
            BuildManifest.CurrentSchemaVersion,
            buildId,
            DateTimeOffset.UtcNow,
            Path.GetFullPath(request.Paths.InstallPath),
            request.Options,
            entries.AsReadOnly());
        var manifestPath = Path.Combine(buildDirectory, "manifest.json");
        // manifest.json is the sole completed-pack marker and is deliberately
        // committed only after every artifact and checkpoint passed validation.
        await _manifestStore.WriteBuildManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        TryDeleteOwnedDirectory(request.Paths.StagingPath, workDirectory);
        if (resumeOperationKey is null && finalizeAsync is null)
        {
            // Repair/composition callers intentionally do not participate in
            // automatic resume. When they have no metadata finalizer, their
            // manifest is their final commit marker. A finalizing repair keeps
            // the checkpoint until its report/previews are durably written so
            // the catalog cannot expose a manifest-only pack after a crash.
            DeleteCheckpointOrThrow(checkpointPath);
        }
        progress?.Report(new ProgressUpdate(
            "Build",
            resumedArtifacts > 0
                ? $"Staged build is complete; {resumedArtifacts:N0} verified artifact(s) resumed without re-upscaling."
                : "Staged build is complete and ready for explicit apply.",
            normalizedItems.Count,
            normalizedItems.Count));

        var result = new StagedBuildResult(buildId, buildDirectory, manifestPath, manifest)
        {
            ResumedArtifactCount = resumedArtifacts,
            ActiveDurationSeconds = AddActiveDuration(
                checkpoint.ActiveDurationSeconds,
                activeSegmentStartedUtc,
                DateTimeOffset.UtcNow),
            CheckpointCreatedUtc = checkpoint.CreatedUtc
        };
        if (finalizeAsync is not null)
        {
            await finalizeAsync(result, cancellationToken).ConfigureAwait(false);
            await ValidateFinalizedResultAsync(request.Paths, result, cancellationToken).ConfigureAwait(false);
            DeleteCheckpointOrThrow(checkpointPath);
        }

        return result;
        }
        catch
        {
            // Fresh application builds carry an explicit operation key and
            // retain their durable checkpoints after cancellation/failure.
            // Legacy and repair callers without one preserve their historical
            // all-or-nothing cleanup behavior and are never auto-resumed.
            if (resumeOperationKey is null)
            {
                TryDeleteOwnedDirectory(request.Paths.StagingPath, buildDirectory);
            }
            else
            {
                try
                {
                    checkpoint = checkpoint with
                    {
                        ActiveDurationSeconds = CaptureActiveDuration()
                    };
                    WriteCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is IOException
                                                       or UnauthorizedAccessException
                                                       or InvalidDataException)
                {
                    // Preserve the original build/cancellation failure. The
                    // most recent already-durable artifact checkpoint remains
                    // valid even if this final timing-only write cannot land.
                }
            }

            throw;
        }
    }

    private async Task ValidateFinalizedResultAsync(
        ProjectPaths paths,
        StagedBuildResult result,
        CancellationToken cancellationToken)
    {
        var checkpointPath = Path.Combine(result.BuildDirectory, CheckpointFileName);
        if (!File.Exists(checkpointPath))
        {
            throw new InvalidDataException("The staged build has no active finalization checkpoint.");
        }

        var checkpoint = await ReadCheckpointAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        var manifest = await _manifestStore.ReadBuildManifestAsync(result.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (!checkpoint.BuildId.Equals(result.BuildId, StringComparison.Ordinal)
            || !manifest.BuildId.Equals(result.BuildId, StringComparison.Ordinal)
            || !PathGuard.SamePath(checkpoint.InstallPath, paths.InstallPath)
            || !PathGuard.SamePath(manifest.InstallPath, paths.InstallPath)
            || !OptionsEqual(checkpoint.Options, manifest.Options)
            || !OptionsEqual(result.Manifest.Options, manifest.Options))
        {
            throw new InvalidDataException("The staged build changed during metadata finalization.");
        }

        var expected = checkpoint.CompletedArtifacts
            .Where(item => !item.Omitted)
            .Select(CreateManifestEntry)
            .OrderBy(item => item.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actual = manifest.Entries
            .OrderBy(item => item.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expected.Length != actual.Length
            || !expected.SequenceEqual(actual))
        {
            throw new InvalidDataException("The staged manifest no longer matches its durable artifact checkpoint.");
        }

        foreach (var entry in actual)
        {
            var payload = PathGuard.ResolveUnderRoot(
                Path.Combine(result.BuildDirectory, "payload"),
                entry.RelativeInstallPath);
            await FileIntegrity.EnsureMatchesAsync(
                payload,
                entry.StagedLength,
                entry.StagedSha256,
                "Finalized staged payload",
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var completed in checkpoint.CompletedArtifacts)
        {
            if (!await ValidateCheckpointPreviewsAsync(
                    completed.Contribution,
                    result.BuildDirectory,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    $"Finalized preview files no longer match the durable checkpoint for {completed.RelativeInstallPath}.");
            }
        }
    }

    private static void DeleteCheckpointOrThrow(string checkpointPath)
    {
        try
        {
            File.Delete(checkpointPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "The staged build finished, but its crash-recovery marker could not be removed safely.",
                exception);
        }

        if (File.Exists(checkpointPath))
        {
            throw new IOException(
                "The staged build finished, but its crash-recovery marker is still present.");
        }
    }

    private static double AddActiveDuration(
        double accumulatedSeconds,
        DateTimeOffset segmentStartedUtc,
        DateTimeOffset segmentCompletedUtc)
    {
        if (!double.IsFinite(accumulatedSeconds) || accumulatedSeconds < 0)
        {
            throw new InvalidDataException("The staged-build active duration checkpoint is invalid.");
        }

        var elapsedSeconds = Math.Max(0, (segmentCompletedUtc - segmentStartedUtc).TotalSeconds);
        var result = accumulatedSeconds + elapsedSeconds;
        if (!double.IsFinite(result) || result > TimeSpan.FromDays(365).TotalSeconds)
        {
            throw new InvalidDataException("The staged-build active duration exceeds the supported range.");
        }

        return result;
    }

    private static async Task<IReadOnlyList<StagedBuildCheckpointItem>> CreatePlanItemsAsync(
        IReadOnlyList<NormalizedBuildItem> items,
        CancellationToken cancellationToken)
    {
        var results = new List<StagedBuildCheckpointItem>(items.Count);
        foreach (var item in items)
        {
            var source = await FileIntegrity.FingerprintAsync(item.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (item.ExpectedSourceLength is not null
                && (source.Length != item.ExpectedSourceLength.Value
                    || !source.Sha256.Equals(item.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Verified build source no longer matches its expected SHA-256: {item.SourcePath}");
            }

            results.Add(new StagedBuildCheckpointItem(
                item.RelativeInstallPath,
                source.Length,
                source.Sha256,
                item.ExpectedStagedLength,
                item.ExpectedStagedSha256,
                item.AllowSourceIdenticalOmission));
        }

        return results;
    }

    private static string ComputePlanSha256(
        string installPath,
        UpscaleOptions options,
        bool requireAllItems,
        string? resumeOperationKey,
        IReadOnlyList<StagedBuildCheckpointItem> items)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            InstallPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)).ToUpperInvariant(),
            Options = options,
            RequireAllItems = requireAllItems,
            ResumeOperationKey = resumeOperationKey ?? string.Empty,
            Items = items
                .OrderBy(item => item.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
                .Select(item => new
                {
                    RelativeInstallPath = item.RelativeInstallPath.Replace('\\', '/').ToLowerInvariant(),
                    item.SourceLength,
                    SourceSha256 = item.SourceSha256.ToLowerInvariant(),
                    item.ExpectedStagedLength,
                    ExpectedStagedSha256 = item.ExpectedStagedSha256?.ToLowerInvariant(),
                    item.AllowSourceIdenticalOmission
                })
        }, CheckpointJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task<CheckpointCandidate?> FindMatchingCheckpointAsync(
        ProjectPaths paths,
        UpscaleOptions options,
        IReadOnlyList<StagedBuildCheckpointItem> planItems,
        string planSha256,
        bool requireAllItems,
        string? resumeOperationKey,
        string? requestedBuildId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.StagingPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(resumeOperationKey))
        {
            return null;
        }

        IEnumerable<string> directories;
        if (!string.IsNullOrWhiteSpace(requestedBuildId))
        {
            var buildId = PathGuard.ValidateIdentifier(requestedBuildId, "build");
            directories = [PathGuard.ResolveUnderRoot(paths.StagingPath, buildId)];
        }
        else
        {
            directories = EnumerateBuildDirectories(paths.StagingPath)
                .OrderByDescending(Directory.GetLastWriteTimeUtc);
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpointPath = PathGuard.ResolveUnderRoot(
                paths.StagingPath,
                Path.Combine(Path.GetFileName(directory), CheckpointFileName));
            if (!File.Exists(checkpointPath))
            {
                continue;
            }

            StagedBuildCheckpoint candidate;
            try
            {
                candidate = await ReadCheckpointAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
                ValidateCheckpoint(
                    candidate,
                    paths,
                    planItems,
                    planSha256,
                    requireAllItems,
                    resumeOperationKey,
                    directory);
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or InvalidDataException
                                                   or ArgumentException)
            {
                continue;
            }

            return new CheckpointCandidate(candidate);
        }

        return null;
    }

    private static void ValidateCheckpoint(
        StagedBuildCheckpoint checkpoint,
        ProjectPaths paths,
        IReadOnlyList<StagedBuildCheckpointItem> planItems,
        string planSha256,
        bool requireAllItems,
        string resumeOperationKey,
        string directory)
    {
        if (!StagedBuildCheckpoint.IsSupportedSchemaVersion(checkpoint.SchemaVersion)
            || !checkpoint.BuildId.Equals(Path.GetFileName(directory), StringComparison.Ordinal)
            || !PathGuard.SamePath(checkpoint.InstallPath, paths.InstallPath)
            || checkpoint.RequireAllItems != requireAllItems
            || !checkpoint.ResumeOperationKey.Equals(resumeOperationKey, StringComparison.Ordinal)
            || !checkpoint.PlanSha256.Equals(planSha256, StringComparison.OrdinalIgnoreCase)
            || checkpoint.Items.Count != planItems.Count
            || checkpoint.CompletedArtifacts.Count > planItems.Count)
        {
            throw new InvalidDataException("The staged-build checkpoint does not match this exact build plan.");
        }

        if (!Enum.IsDefined(checkpoint.Options.Preset)
            || !Enum.IsDefined(checkpoint.Options.Scope)
            || !Enum.IsDefined(checkpoint.Options.PaintedTheme)
            || (checkpoint.Options.Preset != TexturePreset.Illustrated
                && checkpoint.Options.PaintedTheme != PaintedTheme.ClassicPainted)
            || checkpoint.Options.MaximumDimension is not (1024 or 2048 or 4096)
            || (checkpoint.Options.Scope == AssetScope.SelectedZone
                && string.IsNullOrWhiteSpace(checkpoint.Options.SelectedZone)))
        {
            throw new InvalidDataException("The staged-build checkpoint has invalid upscale options.");
        }
        TextureOverridePolicy.ValidateAll(checkpoint.Options.TextureOverrides);
        WorldExpansionSelectionPolicy.Validate(checkpoint.Options);
        if (checkpoint.SchemaVersion == StagedBuildCheckpoint.MinimumSupportedSchemaVersion
            && checkpoint.Options.WorldExpansions is not null)
        {
            throw new InvalidDataException(
                "A schema-2 staged-build checkpoint cannot carry a World expansion selection.");
        }

        if (checkpoint.SchemaVersion < 4 && checkpoint.Options.PaintedStyle is not null)
        {
            throw new InvalidDataException(
                "Staged-build checkpoint schemas before 4 cannot carry painted style settings.");
        }

        if (!double.IsFinite(checkpoint.ActiveDurationSeconds)
            || checkpoint.ActiveDurationSeconds < 0
            || checkpoint.ActiveDurationSeconds > TimeSpan.FromDays(365).TotalSeconds
            || checkpoint.Items.Count != planItems.Count)
        {
            throw new InvalidDataException("The staged-build checkpoint timing or item plan is invalid.");
        }

        for (var index = 0; index < planItems.Count; index++)
        {
            if (checkpoint.Items[index] != planItems[index])
            {
                throw new InvalidDataException("The staged-build checkpoint item plan was modified.");
            }
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var expectedEntries = checkpoint.CompletedArtifacts
                .Where(item => !item.Omitted)
                .Select(CreateManifestEntry)
                .OrderBy(item => item.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var manifest = new ManifestStore().ReadBuildManifestAsync(manifestPath)
                .GetAwaiter().GetResult();
            if (!manifest.BuildId.Equals(checkpoint.BuildId, StringComparison.Ordinal)
                || !PathGuard.SamePath(manifest.InstallPath, checkpoint.InstallPath)
                || !OptionsEqual(manifest.Options, checkpoint.Options)
                || manifest.Entries.Count != expectedEntries.Length)
            {
                throw new InvalidDataException("The finalizing staged manifest does not match its checkpoint.");
            }

            var actualEntries = manifest.Entries
                .OrderBy(item => item.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (var index = 0; index < expectedEntries.Length; index++)
            {
                if (actualEntries[index] != expectedEntries[index])
                {
                    throw new InvalidDataException("The finalizing staged manifest was modified after checkpointing.");
                }
            }
        }

        var plannedByPath = planItems.ToDictionary(item => item.RelativeInstallPath, StringComparer.OrdinalIgnoreCase);
        var completedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewSlots = new HashSet<int>();
        var previewFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewCountByArchive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalDiscovered = 0;
        long totalEnhanced = 0;
        long totalPreserved = 0;
        long totalReused = 0;
        long totalFallback = 0;
        long totalSourceBytes = 0;
        long totalEnhancedBytes = 0;
        long totalReviews = 0;
        foreach (var completed in checkpoint.CompletedArtifacts)
        {
            if (!completedPaths.Add(completed.RelativeInstallPath)
                || !plannedByPath.TryGetValue(completed.RelativeInstallPath, out var planned)
                || completed.SourceLength != planned.SourceLength
                || !completed.SourceSha256.Equals(planned.SourceSha256, StringComparison.OrdinalIgnoreCase)
                || completed.StagedLength < 0
                || completed.StagedSha256.Length != 64
                || completed.StagedSha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("The staged-build checkpoint contains an invalid artifact record.");
            }

            ValidateCheckpointContribution(completed.Contribution);
            if (completed.Contribution is null)
            {
                continue;
            }

            var statistics = completed.Contribution.Statistics;
            totalDiscovered = checked(totalDiscovered + statistics.DiscoveredTextures);
            totalEnhanced = checked(totalEnhanced + statistics.EnhancedTextures);
            totalPreserved = checked(totalPreserved + statistics.PreservedTextures);
            totalReused = checked(totalReused + statistics.ReusedTextures);
            totalFallback = checked(totalFallback + statistics.FallbackTextures);
            totalSourceBytes = checked(totalSourceBytes + statistics.SourceTextureBytes);
            totalEnhancedBytes = checked(totalEnhancedBytes + statistics.EnhancedTextureBytes);
            totalReviews = checked(totalReviews + completed.Contribution.ReviewEntries.Count);
            foreach (var preview in completed.Contribution.Previews)
            {
                var archiveKey = preview.Entry.ArchivePath.Trim().Replace('\\', '/').Trim('/');
                var family = $"{archiveKey}|{TexturePreviewCollector.CreateFamilyKey(preview.Entry.LogicalName)}";
                previewCountByArchive.TryGetValue(archiveKey, out var archiveCount);
                if (!previewSlots.Add(preview.Slot)
                    || !previewFamilies.Add(family)
                    || archiveCount >= 8)
                {
                    throw new InvalidDataException("The staged-build preview checkpoint conflicts with another artifact.");
                }

                previewCountByArchive[archiveKey] = archiveCount + 1;
            }
        }

        if (previewSlots.Count > 24
            || totalReviews > 100_000
            || totalDiscovered > int.MaxValue
            || totalEnhanced > int.MaxValue
            || totalPreserved > int.MaxValue
            || totalReused > int.MaxValue
            || totalFallback > int.MaxValue
            || totalSourceBytes < 0
            || totalEnhancedBytes < 0)
        {
            throw new InvalidDataException("The staged-build checkpoint aggregate contribution is invalid.");
        }
    }

    private static void ValidateCheckpointContribution(StagedBuildCheckpointContribution? contribution)
    {
        if (contribution is null)
        {
            return;
        }

        if (contribution.Statistics is null
            || contribution.Statistics.DiscoveredTextures < 0
            || contribution.Statistics.EnhancedTextures < 0
            || contribution.Statistics.PreservedTextures < 0
            || contribution.Statistics.ReusedTextures < 0
            || contribution.Statistics.FallbackTextures < 0
            || contribution.Statistics.SourceTextureBytes < 0
            || contribution.Statistics.EnhancedTextureBytes < 0
            || contribution.Statistics.PreservedReasons is null
            || contribution.Statistics.Warnings is null
            || contribution.Statistics.PreservedReasons.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0)
            || contribution.Statistics.Warnings.Any(string.IsNullOrWhiteSpace)
            || contribution.Previews is null
            || contribution.ReviewEntries is null
            || contribution.Previews.Count > 24
            || contribution.ReviewEntries.Count > 100_000)
        {
            throw new InvalidDataException("The staged-build checkpoint statistics are invalid.");
        }

        foreach (var preview in contribution.Previews)
        {
            if (preview?.Entry is null
                || preview.Slot is < 0 or >= 24
                || string.IsNullOrWhiteSpace(preview.Entry.ArchivePath)
                || string.IsNullOrWhiteSpace(preview.Entry.LogicalName)
                || string.IsNullOrWhiteSpace(preview.Entry.OriginalImagePath)
                || string.IsNullOrWhiteSpace(preview.Entry.EnhancedImagePath)
                || preview.OriginalLength < 0
                || preview.EnhancedLength < 0
                || !IsSha256(preview.OriginalSha256)
                || !IsSha256(preview.EnhancedSha256)
                || preview.Entry.OriginalWidth <= 0
                || preview.Entry.OriginalHeight <= 0
                || preview.Entry.EnhancedWidth <= 0
                || preview.Entry.EnhancedHeight <= 0)
            {
                throw new InvalidDataException("The staged-build preview checkpoint is invalid.");
            }
        }

        foreach (var review in contribution.ReviewEntries)
        {
            if (review is null
                || string.IsNullOrWhiteSpace(review.ArchivePath)
                || string.IsNullOrWhiteSpace(review.LogicalName)
                || review.OriginalWidth <= 0
                || review.OriginalHeight <= 0
                || review.EnhancedWidth <= 0
                || review.EnhancedHeight <= 0)
            {
                throw new InvalidDataException("The staged-build review checkpoint is invalid.");
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool OptionsEqual(UpscaleOptions? left, UpscaleOptions? right) =>
        left is not null
        && right is not null
        && JsonSerializer.Serialize(left, CheckpointJsonOptions)
            .Equals(JsonSerializer.Serialize(right, CheckpointJsonOptions), StringComparison.Ordinal);

    private static async Task<bool> ValidateCompletedArtifactAsync(
        StagedBuildCheckpointItem planned,
        StagedBuildCheckpointArtifact completed,
        string payloadPath,
        string buildDirectory,
        CancellationToken cancellationToken)
    {
        if (completed.SourceLength != planned.SourceLength
            || !completed.SourceSha256.Equals(planned.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (completed.Omitted)
        {
            return completed.StagedLength == planned.SourceLength
                && completed.StagedSha256.Equals(planned.SourceSha256, StringComparison.OrdinalIgnoreCase)
                && !File.Exists(payloadPath)
                && await ValidateCheckpointPreviewsAsync(completed.Contribution, buildDirectory, cancellationToken)
                    .ConfigureAwait(false);
        }

        try
        {
            await FileIntegrity.EnsureMatchesAsync(
                    payloadPath,
                    completed.StagedLength,
                    completed.StagedSha256,
                    "Checkpointed staged artifact",
                    cancellationToken)
                .ConfigureAwait(false);
            return await ValidateCheckpointPreviewsAsync(
                    completed.Contribution,
                    buildDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<StagedBuildCheckpointContribution?> FingerprintCheckpointPreviewsAsync(
        StagedBuildCheckpointContribution? contribution,
        string buildDirectory,
        CancellationToken cancellationToken)
    {
        if (contribution is null)
        {
            return null;
        }

        var previews = new List<StagedBuildCheckpointPreview>();
        foreach (var entry in contribution.Previews)
        {
            var originalPath = PathGuard.ResolveUnderRoot(buildDirectory, entry.Entry.OriginalImagePath);
            var enhancedPath = PathGuard.ResolveUnderRoot(buildDirectory, entry.Entry.EnhancedImagePath);
            var original = await FileIntegrity.FingerprintAsync(originalPath, cancellationToken).ConfigureAwait(false);
            var enhanced = await FileIntegrity.FingerprintAsync(enhancedPath, cancellationToken).ConfigureAwait(false);
            previews.Add(entry with
            {
                OriginalLength = original.Length,
                OriginalSha256 = original.Sha256,
                EnhancedLength = enhanced.Length,
                EnhancedSha256 = enhanced.Sha256
            });
        }

        return contribution with { Previews = previews };
    }

    private static StagedBuildCheckpointContribution MakeContributionPathsAbsolute(
        StagedBuildCheckpointContribution contribution,
        string buildDirectory) =>
        contribution with
        {
            Previews = contribution.Previews.Select(preview => preview with
            {
                Entry = preview.Entry with
                {
                    OriginalImagePath = PathGuard.ResolveUnderRoot(
                        buildDirectory,
                        preview.Entry.OriginalImagePath),
                    EnhancedImagePath = PathGuard.ResolveUnderRoot(
                        buildDirectory,
                        preview.Entry.EnhancedImagePath)
                }
            }).ToArray()
        };

    private static async Task<bool> ValidateCheckpointPreviewsAsync(
        StagedBuildCheckpointContribution? contribution,
        string buildDirectory,
        CancellationToken cancellationToken)
    {
        if (contribution is null)
        {
            return true;
        }

        try
        {
            foreach (var preview in contribution.Previews)
            {
                var originalPath = PathGuard.ResolveUnderRoot(buildDirectory, preview.Entry.OriginalImagePath);
                var enhancedPath = PathGuard.ResolveUnderRoot(buildDirectory, preview.Entry.EnhancedImagePath);
                await FileIntegrity.EnsureMatchesAsync(
                    originalPath,
                    preview.OriginalLength,
                    preview.OriginalSha256,
                    "Checkpointed original preview",
                    cancellationToken).ConfigureAwait(false);
                await FileIntegrity.EnsureMatchesAsync(
                    enhancedPath,
                    preview.EnhancedLength,
                    preview.EnhancedSha256,
                    "Checkpointed enhanced preview",
                    cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
        {
            return false;
        }
    }

    private static void EnsureSourceMatchesPlan(
        string sourcePath,
        StagedBuildCheckpointItem planned,
        FileFingerprint actual)
    {
        if (actual.Length != planned.SourceLength
            || !actual.Sha256.Equals(planned.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Build source changed after the crash-recovery plan was created: {sourcePath}");
        }
    }

    private static BuildManifestEntry CreateManifestEntry(StagedBuildCheckpointArtifact completed) =>
        new(
            completed.RelativeInstallPath,
            completed.SourceLength,
            completed.SourceSha256,
            completed.StagedLength,
            completed.StagedSha256);

    private static async Task WriteCheckpointAsync(
        string path,
        StagedBuildCheckpoint checkpoint,
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
                await JsonSerializer.SerializeAsync(stream, checkpoint, CheckpointJsonOptions, cancellationToken)
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

    private static async Task<StagedBuildCheckpoint> ReadCheckpointAsync(
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
        return await JsonSerializer.DeserializeAsync<StagedBuildCheckpoint>(
                   stream,
                   CheckpointJsonOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("The staged-build checkpoint is empty.");
    }

    private static JsonSerializerOptions CreateCheckpointJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
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
            ValidateFingerprintMetadata(item.ExpectedSourceLength, item.ExpectedSourceSha256, item.RelativeInstallPath, "source");
            ValidateFingerprintMetadata(item.ExpectedStagedLength, item.ExpectedStagedSha256, item.RelativeInstallPath, "staged");
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
                item.ExpectedStagedSha256,
                item.AllowSourceIdenticalOmission));
        }

        return normalized;
    }

    private static void ValidateFingerprintMetadata(
        long? length,
        string? sha256,
        string relativePath,
        string description)
    {
        if ((length is null) != (sha256 is null)
            || length < 0
            || (sha256 is not null
                && (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))))
        {
            throw new ArgumentException(
                $"Expected {description} fingerprint metadata is invalid for {relativePath}.");
        }
    }

    private sealed record NormalizedBuildItem(
        string RelativeInstallPath,
        string SourcePath,
        IStagedArtifactBuilder Builder,
        long? ExpectedSourceLength,
        string? ExpectedSourceSha256,
        long? ExpectedStagedLength,
        string? ExpectedStagedSha256,
        bool AllowSourceIdenticalOmission);

    private sealed record CheckpointCandidate(StagedBuildCheckpoint Checkpoint);

    private static void TryDeleteOwnedDirectory(string stagingRoot, string directory)
    {
        try
        {
            var target = PathGuard.EnsurePathUnderRoot(stagingRoot, directory);
            if (!Directory.Exists(target))
            {
                return;
            }

            var directories = new List<string> { target };
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return;
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

            foreach (var ownedDirectory in directories.OrderByDescending(path => path.Length))
            {
                Directory.Delete(ownedDirectory, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
        {
            // Cleanup is best effort. A checkpoint never refers to current
            // per-artifact work and every completed payload is hash-verified.
        }
    }

    private static IReadOnlyList<string> EnumerateBuildDirectories(string stagingRoot)
    {
        var fullRoot = Path.GetFullPath(stagingRoot);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The staged-pack library cannot be a reparse point: {fullRoot}");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        return Directory.EnumerateDirectories(fullRoot, "build-*", options)
            .Select(directory => PathGuard.EnsurePathUnderRoot(fullRoot, directory))
            .ToArray();
    }
}
