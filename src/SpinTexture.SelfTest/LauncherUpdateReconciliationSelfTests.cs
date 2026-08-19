using System.Security.Cryptography;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.SelfTest;

internal static class LauncherUpdateReconciliationSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestLargeLauncherReplacementRetiresWithoutLiveWritesAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestMixedReconciliationAndPatchedBaselineRestoreAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestPostCommitFailureRollsBackWithoutTouchingPatchAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestRecoveryRequiredResumeFailureConvergesForLaterUpdateAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestPreparingReceiptResumeAndMetadataFinalizationAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestMissingOriginalDeletionRollsBackAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestRedundantAbsentOriginalAuthorizationIsRejectedAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TestLargeLauncherReplacementRetiresWithoutLiveWritesAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("large");
        try
        {
            var paths = CreatePaths(root);
            var entries = new List<InstalledArtifact>(1535);
            byte[]? patchedBytes = null;
            string? patchedPath = null;
            for (var index = 0; index < 1535; index++)
            {
                var relativePath = index == 1534
                    ? "soldungb_obj.s3d"
                    : $"archive-{index:D4}.s3d";
                var original = Bytes($"original-{index:D4}");
                var enhanced = Bytes($"enhanced-{index:D4}-texture-pack");
                var live = original;
                if (index == 1534)
                {
                    patchedBytes = Bytes("launcher-patched-soldungb-archive");
                    live = patchedBytes;
                    patchedPath = Path.Combine(paths.InstallPath, relativePath);
                }

                await File.WriteAllBytesAsync(
                        Path.Combine(paths.InstallPath, relativePath),
                        live,
                        cancellationToken)
                    .ConfigureAwait(false);
                entries.Add(CreateArtifact(relativePath, original, enhanced));
            }

            var store = new ManifestStore();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-large-launcher-update",
                    entries,
                    store,
                    cancellationToken)
                .ConfigureAwait(false);
            var operations = new RecordingAtomicFileOperations();
            var transactions = new InstallTransactionService(
                store,
                operations,
                ensureGameStopped: _ => { });
            var patchedFingerprint = Fingerprint(patchedBytes!);
            var result = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [
                        new AdoptedOriginalArtifact(
                            "soldungb_obj.s3d",
                            Exists: true,
                            patchedFingerprint.Length,
                            patchedFingerprint.Sha256)
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            AssertEqual(1535, result.ReconciledArtifacts, "large reconciliation artifact count");
            AssertEqual(0, result.RestoredEnhancedArtifacts, "large reconciliation restore count");
            AssertEqual(1, result.AdoptedArtifacts, "large reconciliation adoption count");
            AssertEqual(0, operations.CopyCount, "no-write launcher replacement copy count");
            AssertSequenceEqual(
                patchedBytes!,
                await File.ReadAllBytesAsync(patchedPath!, cancellationToken).ConfigureAwait(false),
                "authorized launcher replacement remains byte-exact");

            var retired = await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.Restored,
                retired.State,
                "large launcher replacement retires old install");
            var receipt = await store.ReadLauncherUpdateReconciliationReceiptAsync(
                    result.ReceiptPath,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateReconciliationState.Completed,
                receipt.State,
                "large launcher replacement receipt state");
            AssertEqual(1535, receipt.Entries.Count, "complete receipt artifact count");
            AssertEqual(
                1534,
                receipt.Entries.Count(entry => entry.Disposition
                    == LauncherUpdateOriginalDisposition.AlreadyManagedOriginal),
                "complete receipt old-original count");
            AssertEqual(
                patchedFingerprint.Sha256,
                receipt.Entries.Single(entry => entry.RelativeInstallPath
                        .Equals("soldungb_obj.s3d", StringComparison.OrdinalIgnoreCase))
                    .Sha256!,
                "complete receipt adopted hash");
            Assert(
                !Directory.EnumerateDirectories(
                        Path.GetDirectoryName(manifestPath)!,
                        "restore-safety-launcher-update-*",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
                "no-write reconciliation cleans its owned empty safety directory");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestMixedReconciliationAndPatchedBaselineRestoreAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("mixed");
        try
        {
            var paths = CreatePaths(root);
            var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["already.s3d"] = Bytes("already-original"),
                ["still-enhanced.s3d"] = Bytes("enhanced-source-original"),
                ["patched.s3d"] = Bytes("pre-patch-original"),
                ["removed.s3d"] = Bytes("pre-patch-removed-original")
            };
            var enhanced = originals.ToDictionary(
                pair => pair.Key,
                pair => Bytes($"enhanced::{pair.Key}::{System.Text.Encoding.UTF8.GetString(pair.Value)}"),
                StringComparer.OrdinalIgnoreCase);
            var patched = Bytes("new-launcher-patched-client-archive");
            await File.WriteAllBytesAsync(
                    Path.Combine(paths.InstallPath, "already.s3d"),
                    originals["already.s3d"],
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(paths.InstallPath, "still-enhanced.s3d"),
                    enhanced["still-enhanced.s3d"],
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(paths.InstallPath, "patched.s3d"),
                    patched,
                    cancellationToken)
                .ConfigureAwait(false);

            var store = new ManifestStore();
            var entries = originals.Select(pair => CreateArtifact(
                    pair.Key,
                    pair.Value,
                    enhanced[pair.Key]))
                .ToArray();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-mixed-launcher-update",
                    entries,
                    store,
                    cancellationToken,
                    writeBackups: true)
                .ConfigureAwait(false);
            var operations = new RecordingAtomicFileOperations();
            var transactions = new InstallTransactionService(
                store,
                operations,
                ensureGameStopped: _ => { });

            var beforeFailure = await FingerprintLiveTreeAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            await AssertThrowsAsync<InvalidDataException>(
                    () => transactions.RestoreAsync(
                        paths,
                        manifestPath,
                        cancellationToken: cancellationToken),
                    "strict restore rejects launcher-patched and removed files")
                .ConfigureAwait(false);
            AssertEqual(0, operations.CopyCount, "strict restore preflight performs no copies");
            await AssertLiveTreeAsync(paths, beforeFailure, cancellationToken).ConfigureAwait(false);

            var patchedFingerprint = Fingerprint(patched);
            var result = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [
                        new AdoptedOriginalArtifact(
                            "patched.s3d",
                            Exists: true,
                            patchedFingerprint.Length,
                            patchedFingerprint.Sha256),
                        new AdoptedOriginalArtifact(
                            "removed.s3d",
                            Exists: false,
                            Length: 0,
                            Sha256: null)
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(1, result.RestoredEnhancedArtifacts, "mixed enhanced restore count");
            AssertEqual(2, result.AdoptedArtifacts, "mixed adopted update count");
            AssertSequenceEqual(
                originals["still-enhanced.s3d"],
                await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, "still-enhanced.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "still-enhanced file restored from old verified backup");
            AssertSequenceEqual(
                patched,
                await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, "patched.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "patched file remains untouched");
            Assert(
                !File.Exists(Path.Combine(paths.InstallPath, "removed.s3d")),
                "explicit launcher removal remains absent");

            var copyCountAfterFirst = operations.CopyCount;
            var repeated = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [
                        new AdoptedOriginalArtifact(
                            "patched.s3d",
                            Exists: true,
                            patchedFingerprint.Length,
                            patchedFingerprint.Sha256),
                        new AdoptedOriginalArtifact(
                            "removed.s3d",
                            Exists: false,
                            Length: 0,
                            Sha256: null)
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(result.ReceiptPath, repeated.ReceiptPath, "completed receipt idempotent path");
            AssertEqual(
                copyCountAfterFirst,
                operations.CopyCount,
                "completed receipt retry performs no live or safety copies");

            var newBuildId = "patched-baseline-build";
            var buildDirectory = Path.Combine(paths.StagingPath, newBuildId);
            var payloadDirectory = Path.Combine(buildDirectory, "payload");
            Directory.CreateDirectory(payloadDirectory);
            var replacementPaths = new[] { "still-enhanced.s3d", "patched.s3d" };
            var buildEntries = new List<BuildManifestEntry>();
            foreach (var relativePath in replacementPaths)
            {
                var source = await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, relativePath),
                        cancellationToken)
                    .ConfigureAwait(false);
                var replacement = Bytes($"refreshed::{relativePath}");
                await File.WriteAllBytesAsync(
                        Path.Combine(payloadDirectory, relativePath),
                        replacement,
                        cancellationToken)
                    .ConfigureAwait(false);
                var sourceFingerprint = Fingerprint(source);
                var replacementFingerprint = Fingerprint(replacement);
                buildEntries.Add(new BuildManifestEntry(
                    relativePath,
                    sourceFingerprint.Length,
                    sourceFingerprint.Sha256,
                    replacementFingerprint.Length,
                    replacementFingerprint.Sha256));
            }

            var buildManifestPath = Path.Combine(buildDirectory, "manifest.json");
            await store.WriteBuildManifestAsync(
                    buildManifestPath,
                    new BuildManifest(
                        BuildManifest.CurrentSchemaVersion,
                        newBuildId,
                        DateTimeOffset.UtcNow,
                        paths.InstallPath,
                        UpscaleOptions.Recommended with { InstallAfterBuild = false },
                        buildEntries),
                    cancellationToken)
                .ConfigureAwait(false);
            var apply = await transactions.ApplyAsync(
                    paths,
                    buildManifestPath,
                    cancellationToken: cancellationToken,
                    applyId: "apply-patched-baseline")
                .ConfigureAwait(false);
            await transactions.RestoreAsync(
                    paths,
                    apply.InstallManifestPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                patched,
                await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, "patched.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "future restore returns the launcher-patched baseline, never the pre-patch backup");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestPostCommitFailureRollsBackWithoutTouchingPatchAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("rollback");
        try
        {
            var paths = CreatePaths(root);
            var original = Bytes("rollback-original");
            var enhanced = Bytes("rollback-enhanced");
            var patched = Bytes("rollback-patched-neighbor");
            var enhancedPath = Path.Combine(paths.InstallPath, "enhanced.s3d");
            var patchedPath = Path.Combine(paths.InstallPath, "patched.s3d");
            await File.WriteAllBytesAsync(enhancedPath, enhanced, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(patchedPath, patched, cancellationToken)
                .ConfigureAwait(false);
            var store = new ManifestStore();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-reconciliation-rollback",
                    [
                        CreateArtifact("enhanced.s3d", original, enhanced),
                        CreateArtifact("patched.s3d", Bytes("old-patched"), Bytes("old-enhanced-patched"))
                    ],
                    store,
                    cancellationToken,
                    writeBackups: true)
                .ConfigureAwait(false);
            var failing = new PostCommitFailingAtomicFileOperations(enhancedPath);
            var transactions = new InstallTransactionService(
                store,
                failing,
                ensureGameStopped: _ => { });
            var patchedFingerprint = Fingerprint(patched);
            await AssertThrowsAsync<IOException>(
                    () => transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                        paths,
                        manifestPath,
                        [
                            new AdoptedOriginalArtifact(
                                "patched.s3d",
                                Exists: true,
                                patchedFingerprint.Length,
                                patchedFingerprint.Sha256)
                        ],
                        cancellationToken: cancellationToken),
                    "post-commit reconciliation failure")
                .ConfigureAwait(false);
            Assert(failing.FailureInjected, "post-commit failure was injected");
            AssertSequenceEqual(
                enhanced,
                await File.ReadAllBytesAsync(enhancedPath, cancellationToken).ConfigureAwait(false),
                "failed reconciliation rolls managed enhanced file back");
            AssertSequenceEqual(
                patched,
                await File.ReadAllBytesAsync(patchedPath, cancellationToken).ConfigureAwait(false),
                "failed reconciliation never touches authorized patched neighbor");
            var active = await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.Applied,
                active.State,
                "successful rollback keeps source install applied");
            var receipt = await store.ReadLauncherUpdateReconciliationReceiptAsync(
                    Path.Combine(
                        Path.GetDirectoryName(manifestPath)!,
                        InstallTransactionService.LauncherUpdateReconciliationReceiptFileName),
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateReconciliationState.RolledBack,
                receipt.State,
                "failed reconciliation durable rolled-back receipt");

            // A later completed LaunchPad session legitimately replaces the
            // same path again. The closed RolledBack journal describes update A
            // and must not pin update B to A's stale authorization.
            var laterPatch = Bytes("later-launcher-patch-b");
            await File.WriteAllBytesAsync(patchedPath, laterPatch, cancellationToken)
                .ConfigureAwait(false);
            var laterPatchFingerprint = Fingerprint(laterPatch);
            var retried = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [
                        new AdoptedOriginalArtifact(
                            "patched.s3d",
                            Exists: true,
                            laterPatchFingerprint.Length,
                            laterPatchFingerprint.Sha256)
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                laterPatch,
                await File.ReadAllBytesAsync(patchedPath, cancellationToken).ConfigureAwait(false),
                "retry after closed rollback preserves later launcher update B");
            AssertSequenceEqual(
                original,
                await File.ReadAllBytesAsync(enhancedPath, cancellationToken).ConfigureAwait(false),
                "retry after closed rollback restores the managed enhanced neighbor");
            var completedReceipt = await store.ReadLauncherUpdateReconciliationReceiptAsync(
                    retried.ReceiptPath,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateReconciliationState.Completed,
                completedReceipt.State,
                "retry after closed rollback completes a fresh receipt");
            AssertEqual(
                laterPatchFingerprint.Sha256,
                completedReceipt.Entries.Single(entry => entry.RelativeInstallPath
                        .Equals("patched.s3d", StringComparison.OrdinalIgnoreCase))
                    .Sha256!,
                "fresh receipt records later launcher update B");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestPreparingReceiptResumeAndMetadataFinalizationAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("resume");
        try
        {
            var paths = CreatePaths(root);
            var firstOriginal = Bytes("resume-first-original");
            var firstEnhanced = Bytes("resume-first-enhanced");
            var secondOriginal = Bytes("resume-second-original");
            var secondEnhanced = Bytes("resume-second-enhanced");
            var patched = Bytes("resume-authorized-patch");
            await File.WriteAllBytesAsync(
                    Path.Combine(paths.InstallPath, "resume-one.s3d"),
                    firstOriginal,
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(paths.InstallPath, "resume-two.s3d"),
                    secondEnhanced,
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(paths.InstallPath, "resume-patch.s3d"),
                    patched,
                    cancellationToken)
                .ConfigureAwait(false);

            var entries = new[]
            {
                CreateArtifact("resume-one.s3d", firstOriginal, firstEnhanced),
                CreateArtifact("resume-two.s3d", secondOriginal, secondEnhanced),
                CreateArtifact("resume-patch.s3d", Bytes("resume-old-patch"), Bytes("resume-enhanced-patch"))
            };
            var store = new ManifestStore();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-resume-reconciliation",
                    entries,
                    store,
                    cancellationToken,
                    writeBackups: true)
                .ConfigureAwait(false);
            var transactionDirectory = Path.GetDirectoryName(manifestPath)!;
            var safetyName = "restore-safety-launcher-update-resume-fixture";
            var safetyDirectory = Path.Combine(transactionDirectory, safetyName);
            Directory.CreateDirectory(safetyDirectory);
            await File.WriteAllBytesAsync(
                    Path.Combine(safetyDirectory, "resume-one.s3d"),
                    firstEnhanced,
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    Path.Combine(safetyDirectory, "resume-two.s3d"),
                    secondEnhanced,
                    cancellationToken)
                .ConfigureAwait(false);
            var manifest = await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            var patchedFingerprint = Fingerprint(patched);
            var receiptPath = Path.Combine(
                transactionDirectory,
                InstallTransactionService.LauncherUpdateReconciliationReceiptFileName);
            await store.WriteLauncherUpdateReconciliationReceiptAsync(
                    receiptPath,
                    new LauncherUpdateReconciliationReceipt(
                        LauncherUpdateReconciliationReceipt.CurrentSchemaVersion,
                        manifest.ApplyId,
                        manifest.AppliedUtc,
                        DateTimeOffset.UtcNow,
                        ReconciledUtc: null,
                        paths.InstallPath,
                        LauncherUpdateReconciliationState.Preparing,
                        safetyName,
                        [
                            ManagedSnapshot(entries[0], "resume-one.s3d"),
                            ManagedSnapshot(entries[1], "resume-two.s3d"),
                            new LauncherUpdateOriginalArtifact(
                                "resume-patch.s3d",
                                Exists: true,
                                patchedFingerprint.Length,
                                patchedFingerprint.Sha256,
                                LauncherUpdateOriginalDisposition.AdoptedUpdatedFile)
                        ]),
                    cancellationToken)
                .ConfigureAwait(false);

            var operations = new RecordingAtomicFileOperations();
            var transactions = new InstallTransactionService(
                store,
                operations,
                ensureGameStopped: _ => { });
            var authorization = new AdoptedOriginalArtifact(
                "resume-patch.s3d",
                Exists: true,
                patchedFingerprint.Length,
                patchedFingerprint.Sha256);
            await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [authorization],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                firstOriginal,
                await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, "resume-one.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "resume retains previously committed original");
            AssertSequenceEqual(
                secondOriginal,
                await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, "resume-two.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "resume finishes remaining enhanced restore");
            AssertSequenceEqual(
                patched,
                await File.ReadAllBytesAsync(
                        Path.Combine(paths.InstallPath, "resume-patch.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "resume never overwrites adopted patch");

            // Recreate the precise crash window after the Completed receipt but
            // before install-manifest retirement. Retry must be metadata-only.
            var completed = await store.ReadLauncherUpdateReconciliationReceiptAsync(
                    receiptPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await store.WriteInstallManifestAsync(
                    manifestPath,
                    manifest with { State = InstallTransactionState.Applied },
                    cancellationToken)
                .ConfigureAwait(false);
            var copiesBeforeMetadataRetry = operations.CopyCount;
            var finalized = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [authorization],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(completed.ReconciledUtc!.Value, finalized.ReconciledUtc,
                "completed-receipt metadata retry retains commit time");
            AssertEqual(copiesBeforeMetadataRetry, operations.CopyCount,
                "completed-receipt metadata retry performs no copies");
            AssertEqual(
                InstallTransactionState.Restored,
                (await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false)).State,
                "completed receipt retry retires source manifest");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestRecoveryRequiredResumeFailureConvergesForLaterUpdateAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("recovery-required-resume-rollback");
        try
        {
            var paths = CreatePaths(root);
            var original = Bytes("rollback-original");
            var enhanced = Bytes("rollback-enhanced");
            var firstPatch = Bytes("recovery-launcher-patch-a");
            var laterPatch = Bytes("recovery-launcher-patch-b");
            var enhancedPath = Path.Combine(paths.InstallPath, "enhanced.s3d");
            var patchedPath = Path.Combine(paths.InstallPath, "patched.s3d");
            await File.WriteAllBytesAsync(enhancedPath, enhanced, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(patchedPath, firstPatch, cancellationToken)
                .ConfigureAwait(false);

            var entries = new[]
            {
                CreateArtifact("enhanced.s3d", original, enhanced),
                CreateArtifact("patched.s3d", Bytes("old-patched"), Bytes("old-enhanced-patched"))
            };
            var store = new ManifestStore();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-recovery-required-resume",
                    entries,
                    store,
                    cancellationToken,
                    writeBackups: true)
                .ConfigureAwait(false);
            var manifest = await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            await store.WriteInstallManifestAsync(
                    manifestPath,
                    manifest with { State = InstallTransactionState.RecoveryRequired },
                    cancellationToken)
                .ConfigureAwait(false);

            var transactionDirectory = Path.GetDirectoryName(manifestPath)!;
            var safetyName = "restore-safety-launcher-update-recovery-resume-fixture";
            var safetyDirectory = Path.Combine(transactionDirectory, safetyName);
            Directory.CreateDirectory(safetyDirectory);
            await File.WriteAllBytesAsync(
                    Path.Combine(safetyDirectory, "enhanced.s3d"),
                    enhanced,
                    cancellationToken)
                .ConfigureAwait(false);
            var firstPatchFingerprint = Fingerprint(firstPatch);
            var receiptPath = Path.Combine(
                transactionDirectory,
                InstallTransactionService.LauncherUpdateReconciliationReceiptFileName);
            await store.WriteLauncherUpdateReconciliationReceiptAsync(
                    receiptPath,
                    new LauncherUpdateReconciliationReceipt(
                        LauncherUpdateReconciliationReceipt.CurrentSchemaVersion,
                        manifest.ApplyId,
                        manifest.AppliedUtc,
                        DateTimeOffset.UtcNow,
                        ReconciledUtc: null,
                        paths.InstallPath,
                        LauncherUpdateReconciliationState.Preparing,
                        safetyName,
                        [
                            ManagedSnapshot(entries[0], "enhanced.s3d"),
                            new LauncherUpdateOriginalArtifact(
                                "patched.s3d",
                                Exists: true,
                                firstPatchFingerprint.Length,
                                firstPatchFingerprint.Sha256,
                                LauncherUpdateOriginalDisposition.AdoptedUpdatedFile)
                        ]),
                    cancellationToken)
                .ConfigureAwait(false);

            var failing = new PostCommitFailingAtomicFileOperations(enhancedPath);
            var transactions = new InstallTransactionService(
                store,
                failing,
                ensureGameStopped: _ => { });
            await AssertThrowsAsync<IOException>(
                    () => transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                        paths,
                        manifestPath,
                        [
                            new AdoptedOriginalArtifact(
                                "patched.s3d",
                                Exists: true,
                                firstPatchFingerprint.Length,
                                firstPatchFingerprint.Sha256)
                        ],
                        cancellationToken: cancellationToken),
                    "RecoveryRequired Preparing resume forward failure")
                .ConfigureAwait(false);
            Assert(failing.FailureInjected,
                "RecoveryRequired resume injects a failure after the forward copy commits");
            AssertSequenceEqual(
                enhanced,
                await File.ReadAllBytesAsync(enhancedPath, cancellationToken).ConfigureAwait(false),
                "successful resume rollback reinstates exact enhanced bytes");
            AssertSequenceEqual(
                firstPatch,
                await File.ReadAllBytesAsync(patchedPath, cancellationToken).ConfigureAwait(false),
                "successful resume rollback never overwrites launcher patch A");
            AssertEqual(
                InstallTransactionState.Applied,
                (await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false)).State,
                "successful resume rollback durably converges RecoveryRequired to Applied");
            AssertEqual(
                LauncherUpdateReconciliationState.RolledBack,
                (await store.ReadLauncherUpdateReconciliationReceiptAsync(
                        receiptPath,
                        cancellationToken)
                    .ConfigureAwait(false)).State,
                "successful resume rollback closes its durable journal");
            Assert(!Directory.Exists(safetyDirectory),
                "closed successful resume rollback cleans its owned safety directory");

            // Recreate the legacy metadata-only window where the live rollback
            // and RolledBack receipt were durable but the install marker was
            // left RecoveryRequired. A later version must converge this closed
            // journal before judging update B's new exact snapshot.
            var appliedAfterRollback = await store.ReadInstallManifestAsync(
                    manifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await store.WriteInstallManifestAsync(
                    manifestPath,
                    appliedAfterRollback with { State = InstallTransactionState.RecoveryRequired },
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(patchedPath, laterPatch, cancellationToken)
                .ConfigureAwait(false);
            var laterPatchFingerprint = Fingerprint(laterPatch);
            var result = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    [
                        new AdoptedOriginalArtifact(
                            "patched.s3d",
                            Exists: true,
                            laterPatchFingerprint.Length,
                            laterPatchFingerprint.Sha256)
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                original,
                await File.ReadAllBytesAsync(enhancedPath, cancellationToken).ConfigureAwait(false),
                "later reconciliation restores the managed enhanced artifact");
            AssertSequenceEqual(
                laterPatch,
                await File.ReadAllBytesAsync(patchedPath, cancellationToken).ConfigureAwait(false),
                "later reconciliation preserves exact launcher patch B");
            AssertEqual(
                InstallTransactionState.Restored,
                (await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false)).State,
                "later reconciliation retires the recovered install");
            var completed = await store.ReadLauncherUpdateReconciliationReceiptAsync(
                    result.ReceiptPath,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                laterPatchFingerprint.Sha256,
                completed.Entries.Single(entry => entry.RelativeInstallPath
                        .Equals("patched.s3d", StringComparison.OrdinalIgnoreCase))
                    .Sha256!,
                "later reconciliation receipt records launcher patch B");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestMissingOriginalDeletionRollsBackAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("missing-original");
        try
        {
            var paths = CreatePaths(root);
            var enhanced = Bytes("generated-enhanced-artifact");
            var enhancedFingerprint = Fingerprint(enhanced);
            var livePath = Path.Combine(paths.InstallPath, "generated.s3d");
            await File.WriteAllBytesAsync(livePath, enhanced, cancellationToken)
                .ConfigureAwait(false);
            var entry = new InstalledArtifact(
                "generated.s3d",
                OriginalExisted: false,
                OriginalLength: 0,
                OriginalSha256: null,
                BackupRelativePath: null,
                enhancedFingerprint.Length,
                enhancedFingerprint.Sha256,
                InstalledLastWriteTimeUtcTicks: null);
            var store = new ManifestStore();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-missing-original",
                    [entry],
                    store,
                    cancellationToken)
                .ConfigureAwait(false);
            var gateCount = 0;
            var transactions = new InstallTransactionService(
                store,
                new RecordingAtomicFileOperations(),
                ensureGameStopped: _ =>
                {
                    gateCount++;
                    if (gateCount == 3)
                    {
                        throw new InvalidOperationException(
                            "Deliberate retirement-gate failure after deletion.");
                    }
                });
            await AssertThrowsAsync<InvalidOperationException>(
                    () => transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                        paths,
                        manifestPath,
                        Array.Empty<AdoptedOriginalArtifact>(),
                        cancellationToken: cancellationToken),
                    "missing-original deletion rollback")
                .ConfigureAwait(false);
            AssertSequenceEqual(
                enhanced,
                await File.ReadAllBytesAsync(livePath, cancellationToken).ConfigureAwait(false),
                "failed reconciliation reinstates deleted enhanced artifact");
            AssertEqual(
                InstallTransactionState.Applied,
                (await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false)).State,
                "missing-original deletion rollback keeps install applied");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestRedundantAbsentOriginalAuthorizationIsRejectedAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("redundant-absent-original");
        try
        {
            var paths = CreatePaths(root);
            var enhancedFingerprint = Fingerprint(Bytes("generated-enhanced-artifact"));
            var relativePath = "already-absent.s3d";
            var entry = new InstalledArtifact(
                relativePath,
                OriginalExisted: false,
                OriginalLength: 0,
                OriginalSha256: null,
                BackupRelativePath: null,
                enhancedFingerprint.Length,
                enhancedFingerprint.Sha256,
                InstalledLastWriteTimeUtcTicks: null);
            var store = new ManifestStore();
            var manifestPath = await WriteAppliedManifestAsync(
                    paths,
                    "apply-redundant-absent-original",
                    [entry],
                    store,
                    cancellationToken)
                .ConfigureAwait(false);
            var operations = new RecordingAtomicFileOperations();
            var transactions = new InstallTransactionService(
                store,
                operations,
                ensureGameStopped: _ => { });
            var receiptPath = Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                InstallTransactionService.LauncherUpdateReconciliationReceiptFileName);

            // The missing path is already the exact managed original because
            // the pack created it. Treating that known state as an adopted
            // launcher removal would make a stale authorization look useful.
            await AssertThrowsAsync<InvalidOperationException>(
                    () => transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                        paths,
                        manifestPath,
                        [new AdoptedOriginalArtifact(relativePath, false, 0, null)],
                        cancellationToken: cancellationToken),
                    "redundant authorization for an already-absent managed original")
                .ConfigureAwait(false);
            Assert(!File.Exists(receiptPath),
                "redundant absent-original authorization fails before journaling");
            AssertEqual(0, operations.CopyCount,
                "redundant absent-original authorization performs no copies");
            AssertEqual(
                InstallTransactionState.Applied,
                (await store.ReadInstallManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false)).State,
                "rejected redundant absent-original authorization leaves install applied");

            var result = await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    manifestPath,
                    Array.Empty<AdoptedOriginalArtifact>(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(0, operations.CopyCount,
                "known absent original retires without live or safety copies");
            var receipt = await store.ReadLauncherUpdateReconciliationReceiptAsync(
                    result.ReceiptPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var reconciled = receipt.Entries.Single();
            AssertEqual(
                LauncherUpdateOriginalDisposition.AlreadyManagedOriginal,
                reconciled.Disposition,
                "known absent original retains managed-original disposition");
            Assert(!reconciled.Exists,
                "known absent original remains explicitly absent in the complete receipt");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static LauncherUpdateOriginalArtifact ManagedSnapshot(
        InstalledArtifact entry,
        string relativePath) => new(
        relativePath,
        Exists: true,
        entry.OriginalLength,
        entry.OriginalSha256,
        LauncherUpdateOriginalDisposition.RestoredManagedOriginal);

    private static ProjectPaths CreatePaths(string root)
    {
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);
        var paths = new ProjectPaths(installPath, workspacePath);
        paths.EnsureWorkspaceDirectories();
        return paths;
    }

    private static async Task<string> WriteAppliedManifestAsync(
        ProjectPaths paths,
        string applyId,
        IReadOnlyList<InstalledArtifact> entries,
        ManifestStore store,
        CancellationToken cancellationToken,
        bool writeBackups = false)
    {
        var transactionDirectory = Path.Combine(paths.BackupPath, applyId);
        if (writeBackups)
        {
            foreach (var entry in entries.Where(entry => entry.OriginalExisted))
            {
                var source = entry.RelativeInstallPath switch
                {
                    "already.s3d" => Bytes("already-original"),
                    "still-enhanced.s3d" => Bytes("enhanced-source-original"),
                    "patched.s3d" when applyId == "apply-mixed-launcher-update" =>
                        Bytes("pre-patch-original"),
                    "removed.s3d" => Bytes("pre-patch-removed-original"),
                    "enhanced.s3d" => Bytes("rollback-original"),
                    "patched.s3d" => Bytes("old-patched"),
                    "resume-one.s3d" => Bytes("resume-first-original"),
                    "resume-two.s3d" => Bytes("resume-second-original"),
                    "resume-patch.s3d" => Bytes("resume-old-patch"),
                    _ => throw new InvalidOperationException(
                        $"Self-test has no original fixture for {entry.RelativeInstallPath}.")
                };
                var backupPath = PathGuard.ResolveUnderRoot(
                    transactionDirectory,
                    entry.BackupRelativePath!);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                await File.WriteAllBytesAsync(backupPath, source, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var manifestPath = Path.Combine(transactionDirectory, "install-manifest.json");
        await store.WriteInstallManifestAsync(
                manifestPath,
                new InstallManifest(
                    InstallManifest.CurrentSchemaVersion,
                    applyId,
                    DateTimeOffset.UtcNow,
                    paths.InstallPath,
                    "source-build",
                    Path.Combine(paths.StagingPath, "source-build", "manifest.json"),
                    InstallTransactionState.Applied,
                    entries),
                cancellationToken)
            .ConfigureAwait(false);
        return manifestPath;
    }

    private static InstalledArtifact CreateArtifact(
        string relativePath,
        byte[] original,
        byte[] enhanced)
    {
        var originalFingerprint = Fingerprint(original);
        var enhancedFingerprint = Fingerprint(enhanced);
        return new InstalledArtifact(
            relativePath,
            OriginalExisted: true,
            originalFingerprint.Length,
            originalFingerprint.Sha256,
            Path.Combine("payload", relativePath),
            enhancedFingerprint.Length,
            enhancedFingerprint.Sha256,
            InstalledLastWriteTimeUtcTicks: null);
    }

    private static async Task<IReadOnlyDictionary<string, FileFingerprint>> FingerprintLiveTreeAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     paths.InstallPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            result.Add(
                Path.GetRelativePath(paths.InstallPath, path),
                await FileIntegrity.FingerprintAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private static async Task AssertLiveTreeAsync(
        ProjectPaths paths,
        IReadOnlyDictionary<string, FileFingerprint> expected,
        CancellationToken cancellationToken)
    {
        var observed = await FingerprintLiveTreeAsync(paths, cancellationToken).ConfigureAwait(false);
        AssertEqual(expected.Count, observed.Count, "live tree file count after rejected restore");
        foreach (var pair in expected)
        {
            Assert(observed.TryGetValue(pair.Key, out var fingerprint), $"live tree retains {pair.Key}");
            AssertEqual(pair.Value, fingerprint!, $"live tree fingerprint for {pair.Key}");
        }
    }

    private static byte[] Bytes(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private static FileFingerprint Fingerprint(byte[] bytes) => new(
        bytes.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static string CreateRoot(string name) => Path.Combine(
        Path.GetTempPath(),
        $"spintexture-launcher-reconcile-{name}-{Guid.NewGuid():N}");

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(root, recursive: true);
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Self-test failed: {description}; expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description}; expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequenceEqual(
        byte[] expected,
        byte[] actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }

    private sealed class RecordingAtomicFileOperations : IAtomicFileOperations
    {
        public int CopyCount { get; private set; }

        public async Task CopyAndReplaceAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default,
            Action? onCommitted = null)
        {
            CopyCount++;
            await AtomicFile.CopyAndReplaceAsync(
                    sourcePath,
                    destinationPath,
                    expectedLength,
                    expectedSha256,
                    cancellationToken,
                    onCommitted)
                .ConfigureAwait(false);
        }
    }

    private sealed class PostCommitFailingAtomicFileOperations : IAtomicFileOperations
    {
        private readonly string failDestination;

        public PostCommitFailingAtomicFileOperations(string failDestination)
        {
            this.failDestination = Path.GetFullPath(failDestination);
        }

        public bool FailureInjected { get; private set; }

        public async Task CopyAndReplaceAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default,
            Action? onCommitted = null)
        {
            await AtomicFile.CopyAndReplaceAsync(
                    sourcePath,
                    destinationPath,
                    expectedLength,
                    expectedSha256,
                    cancellationToken,
                    onCommitted)
                .ConfigureAwait(false);
            if (!FailureInjected
                && PathGuard.SamePath(destinationPath, failDestination))
            {
                FailureInjected = true;
                throw new IOException("Deliberate post-commit reconciliation failure.");
            }
        }
    }
}
