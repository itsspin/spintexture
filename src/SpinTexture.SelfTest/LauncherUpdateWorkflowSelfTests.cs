using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class LauncherUpdateWorkflowSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-refresh-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var originalUnchanged = await CreateArchiveAsync(
                "unchanged-old",
                cancellationToken).ConfigureAwait(false);
            var originalChanged = await CreateArchiveAsync(
                "changed-old",
                cancellationToken).ConfigureAwait(false);
            var launcherUpdatedChanged = await CreateArchiveAsync(
                "changed-new-official",
                cancellationToken).ConfigureAwait(false);
            var enhancedUnchanged = "enhanced-unchanged-staged"u8.ToArray();
            var enhancedChangedOld = "enhanced-changed-old-staged"u8.ToArray();
            var enhancedChangedNew = "enhanced-changed-new-staged"u8.ToArray();
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "unchanged.s3d"),
                originalUnchanged,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "changed.s3d"),
                originalChanged,
                cancellationToken).ConfigureAwait(false);

            var initialBuilder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    context.RelativeInstallPath.Equals(
                        "unchanged.s3d",
                        StringComparison.OrdinalIgnoreCase)
                        ? enhancedUnchanged
                        : enhancedChangedOld,
                    token));
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Scope = AssetScope.WorldOnly,
                        InstallAfterBuild = false
                    },
                    [
                        new StagedBuildItem("unchanged.s3d", initialBuilder),
                        new StagedBuildItem("changed.s3d", initialBuilder)
                    ],
                    BuildId: "launcher-refresh-baseline"),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var firstApply = await transactions.ApplyAsync(
                paths,
                staged.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // LaunchPad restores one archive to the old original and replaces
            // the other with a new official version.
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "unchanged.s3d"),
                originalUnchanged,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "changed.s3d"),
                launcherUpdatedChanged,
                cancellationToken).ConfigureAwait(false);
            var launchSession = firstApply.Manifest.AppliedUtc.AddMinutes(2);
            await File.WriteAllLinesAsync(
                Path.Combine(
                    installPath,
                    LaunchPadUpdateEvidenceService.DownloadLogFileName),
                [
                    $"**** Starting at {launchSession.ToLocalTime():ddd MMM d HH:mm:ss yyyy} with plug-in 1.0.3.204 ****",
                    "5f54-0:00:00:Found 1 file(s) to update.",
                    "5f54-0:00:01:Patching changed.s3d",
                    "Finished downloading 1,024 bytes in 0.100 seconds (10,240 bytes per second)"
                ],
                cancellationToken).ConfigureAwait(false);

            var rebuiltPaths = new List<string>();
            var refreshBuilder = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    rebuiltPaths.Add(context.RelativeInstallPath);
                    await File.WriteAllBytesAsync(
                        context.DestinationPath,
                        enhancedChangedNew,
                        token).ConfigureAwait(false);
                });
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));

            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(assessment.CanRefresh, "completed official update is refreshable");
            AssertEqual(1, assessment.UpdatedArtifactCount, "updated artifact count");
            AssertEqual("changed.s3d", assessment.UpdatedRelativePaths.Single(), "updated path");

            var result = await workflow
                .RefreshAndApplyActivePackAfterLauncherUpdateAsync(
                    paths,
                    refreshBuilder,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(1, rebuiltPaths.Count, "focused rebuild invocation count");
            AssertEqual("changed.s3d", rebuiltPaths.Single(), "only official changed archive rebuilt");
            AssertEqual(1, result.RebuiltPacks.Count, "immutable replacement count");
            AssertEqual(
                1,
                result.RebuiltPacks[0].Report.ReusedArtifacts,
                "unaffected staged archive whole-reused");
            var installedUnchanged = await File.ReadAllBytesAsync(
                Path.Combine(installPath, "unchanged.s3d"),
                cancellationToken).ConfigureAwait(false);
            var installedChanged = await File.ReadAllBytesAsync(
                Path.Combine(installPath, "changed.s3d"),
                cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                enhancedUnchanged,
                installedUnchanged,
                "unaffected enhanced output reinstalled");
            AssertSequenceEqual(
                enhancedChangedNew,
                installedChanged,
                "updated archive rebuilt and installed");
            Assert(
                File.Exists(Path.Combine(
                    firstApply.BackupDirectory,
                    InstallTransactionService.LauncherUpdateReconciliationReceiptFileName)),
                "durable launcher reconciliation receipt");
            Assert(
                File.Exists(Path.Combine(firstApply.BackupDirectory, "restore-complete.json")),
                "retired pre-update transaction marker");

            await workflow.RestoreLatestAsync(paths, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var restoredUnchanged = await File.ReadAllBytesAsync(
                Path.Combine(installPath, "unchanged.s3d"),
                cancellationToken).ConfigureAwait(false);
            var restoredChanged = await File.ReadAllBytesAsync(
                Path.Combine(installPath, "changed.s3d"),
                cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                originalUnchanged,
                restoredUnchanged,
                "final restore retains old unchanged original");
            AssertSequenceEqual(
                launcherUpdatedChanged,
                restoredChanged,
                "final restore retains new official patched original");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }

        await TestLooseFileRequiresReconcileThenFreshBuildAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestInterruptedReconciliationResumesRefreshAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestCompletedReconciliationFinalizesAndRefreshesAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestCompositeRefreshReusesUnaffectedCharacterLeafAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestChangedCharacterLeafRequiresFreshBuildAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestMissingCompositionComponentRequiresFreshBuildAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestChangedPaintedRendererCompatibilityPreflightAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TestLooseFileRequiresReconcileThenFreshBuildAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-fresh-build-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var original = "official-loose-old"u8.ToArray();
            var enhanced = "enhanced-loose-old"u8.ToArray();
            var updated = "official-loose-new"u8.ToArray();
            var livePath = Path.Combine(paths.InstallPath, "global-load.txt");
            await File.WriteAllBytesAsync(livePath, original, cancellationToken)
                .ConfigureAwait(false);
            var builder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    enhanced,
                    token));
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended,
                    [new StagedBuildItem("global-load.txt", builder)],
                    BuildId: "launcher-loose-baseline"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                staged.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(livePath, updated, cancellationToken)
                .ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Replacing",
                    "global-load.txt",
                    cancellationToken)
                .ConfigureAwait(false);

            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(
                assessment.CanReconcileForFreshBuild,
                "changed loose source requires bounded reconcile before fresh build");

            await workflow.ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                updated,
                await File.ReadAllBytesAsync(livePath, cancellationToken)
                    .ConfigureAwait(false),
                "reconcile-only preserves updated official loose file");
            AssertEqual(
                InstallHealthState.None,
                (await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                    .ConfigureAwait(false)).State,
                "reconcile-only retires stale active transaction");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestInterruptedReconciliationResumesRefreshAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-resume-workflow-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var unchangedOriginal = await CreateArchiveAsync(
                "resume-unchanged-original",
                cancellationToken).ConfigureAwait(false);
            var changedOriginal = await CreateArchiveAsync(
                "resume-changed-original",
                cancellationToken).ConfigureAwait(false);
            var changedOfficial = await CreateArchiveAsync(
                "resume-changed-official",
                cancellationToken).ConfigureAwait(false);
            var unchangedEnhanced = "resume-unchanged-enhanced"u8.ToArray();
            var changedEnhancedOld = "resume-changed-enhanced-old"u8.ToArray();
            var changedEnhancedNew = "resume-changed-enhanced-new"u8.ToArray();
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "unchanged.s3d"),
                unchangedOriginal,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "changed.s3d"),
                changedOriginal,
                cancellationToken).ConfigureAwait(false);
            var baselineBuilder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    context.RelativeInstallPath.Equals(
                        "unchanged.s3d",
                        StringComparison.OrdinalIgnoreCase)
                        ? unchangedEnhanced
                        : changedEnhancedOld,
                    token));
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended,
                    [
                        new StagedBuildItem("unchanged.s3d", baselineBuilder),
                        new StagedBuildItem("changed.s3d", baselineBuilder)
                    ],
                    BuildId: "launcher-resume-baseline"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                staged.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "changed.s3d"),
                changedOfficial,
                cancellationToken).ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Patching",
                    "changed.s3d",
                    cancellationToken)
                .ConfigureAwait(false);

            var install = await store.ReadInstallManifestAsync(
                    applied.InstallManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var backupDirectory = Path.GetDirectoryName(applied.InstallManifestPath)!;
            var safetyName = "restore-safety-launcher-update-workflow-resume";
            var safetyDirectory = Path.Combine(backupDirectory, safetyName);
            Directory.CreateDirectory(safetyDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(safetyDirectory, "unchanged.s3d"),
                unchangedEnhanced,
                cancellationToken).ConfigureAwait(false);
            var unchangedFingerprint = Fingerprint(unchangedOriginal);
            var changedFingerprint = Fingerprint(changedOfficial);
            await store.WriteLauncherUpdateReconciliationReceiptAsync(
                Path.Combine(
                    backupDirectory,
                    InstallTransactionService.LauncherUpdateReconciliationReceiptFileName),
                new LauncherUpdateReconciliationReceipt(
                    LauncherUpdateReconciliationReceipt.CurrentSchemaVersion,
                    install.ApplyId,
                    install.AppliedUtc,
                    DateTimeOffset.UtcNow,
                    ReconciledUtc: null,
                    paths.InstallPath,
                    LauncherUpdateReconciliationState.Preparing,
                    safetyName,
                    [
                        new LauncherUpdateOriginalArtifact(
                            "unchanged.s3d",
                            Exists: true,
                            unchangedFingerprint.Length,
                            unchangedFingerprint.Sha256,
                            LauncherUpdateOriginalDisposition.RestoredManagedOriginal),
                        new LauncherUpdateOriginalArtifact(
                            "changed.s3d",
                            Exists: true,
                            changedFingerprint.Length,
                            changedFingerprint.Sha256,
                            LauncherUpdateOriginalDisposition.AdoptedUpdatedFile)
                    ]),
                cancellationToken).ConfigureAwait(false);
            await store.WriteInstallManifestAsync(
                applied.InstallManifestPath,
                install with { State = InstallTransactionState.RecoveryRequired },
                cancellationToken).ConfigureAwait(false);

            var rebuilt = new List<string>();
            var refreshBuilder = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    rebuilt.Add(context.RelativeInstallPath);
                    await File.WriteAllBytesAsync(
                        context.DestinationPath,
                        changedEnhancedNew,
                        token).ConfigureAwait(false);
                });
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateRefreshState.ResumeRequired,
                assessment.State,
                "preparing receipt exposes resumable update refresh");

            await workflow.RefreshAndApplyActivePackAfterLauncherUpdateAsync(
                    paths,
                    refreshBuilder,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(1, rebuilt.Count, "resume rebuilds only adopted changed archive");
            AssertSequenceEqual(
                unchangedEnhanced,
                await File.ReadAllBytesAsync(
                    Path.Combine(paths.InstallPath, "unchanged.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "resume reapplies whole-reused unaffected enhanced archive");
            AssertSequenceEqual(
                changedEnhancedNew,
                await File.ReadAllBytesAsync(
                    Path.Combine(paths.InstallPath, "changed.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "resume installs newly rebuilt changed archive");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestCompletedReconciliationFinalizesAndRefreshesAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-completed-receipt-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var original = await CreateArchiveAsync(
                "completed-receipt-original",
                cancellationToken).ConfigureAwait(false);
            var official = await CreateArchiveAsync(
                "completed-receipt-official-update",
                cancellationToken).ConfigureAwait(false);
            var enhancedOld = "completed-receipt-enhanced-old"u8.ToArray();
            var enhancedNew = "completed-receipt-enhanced-new"u8.ToArray();
            var livePath = Path.Combine(paths.InstallPath, "completed.s3d");
            await File.WriteAllBytesAsync(livePath, original, cancellationToken)
                .ConfigureAwait(false);
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Scope = AssetScope.WorldOnly,
                        InstallAfterBuild = false
                    },
                    [new StagedBuildItem(
                        "completed.s3d",
                        new DelegateStagedArtifactBuilder((context, token) =>
                            File.WriteAllBytesAsync(
                                context.DestinationPath,
                                enhancedOld,
                                token)))],
                    BuildId: "launcher-completed-receipt-baseline"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                staged.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(livePath, official, cancellationToken)
                .ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Patching",
                    "completed.s3d",
                    cancellationToken)
                .ConfigureAwait(false);
            var officialFingerprint = Fingerprint(official);
            await transactions.RestoreAfterVerifiedLauncherUpdateAsync(
                    paths,
                    applied.InstallManifestPath,
                    [new AdoptedOriginalArtifact(
                        "completed.s3d",
                        Exists: true,
                        officialFingerprint.Length,
                        officialFingerprint.Sha256)],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var completedInstall = await store.ReadInstallManifestAsync(
                    applied.InstallManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.Restored,
                completedInstall.State,
                "fixture reconciliation reaches Completed before metadata crash");

            // Recreate the narrow crash window: the durable receipt committed,
            // but the source install manifest did not retain its Restored state.
            await store.WriteInstallManifestAsync(
                applied.InstallManifestPath,
                completedInstall with
                {
                    State = InstallTransactionState.RecoveryRequired
                },
                cancellationToken).ConfigureAwait(false);

            var rebuildCount = 0;
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateRefreshState.ResumeRequired,
                assessment.State,
                "Completed receipt plus RecoveryRequired is metadata-finalizable");
            await workflow.RefreshAndApplyActivePackAfterLauncherUpdateAsync(
                    paths,
                    new DelegateStagedArtifactBuilder(async (context, token) =>
                    {
                        rebuildCount++;
                        await File.WriteAllBytesAsync(
                            context.DestinationPath,
                            enhancedNew,
                            token).ConfigureAwait(false);
                    }),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(1, rebuildCount,
                "Completed receipt route stages only its changed replacement");
            AssertSequenceEqual(
                enhancedNew,
                await File.ReadAllBytesAsync(livePath, cancellationToken)
                    .ConfigureAwait(false),
                "Completed receipt route finalizes metadata then applies refresh");
            await workflow.RestoreLatestAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                official,
                await File.ReadAllBytesAsync(livePath, cancellationToken)
                    .ConfigureAwait(false),
                "post-refresh restore preserves the completed official update");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestCompositeRefreshReusesUnaffectedCharacterLeafAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-composite-refresh-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var worldOriginal = await CreateArchiveAsync(
                "composite-world-original",
                cancellationToken).ConfigureAwait(false);
            var worldOfficial = await CreateArchiveAsync(
                "composite-world-official-update",
                cancellationToken).ConfigureAwait(false);
            var characterOriginal = await CreateArchiveAsync(
                "composite-character-original",
                cancellationToken).ConfigureAwait(false);
            var worldEnhancedOld = "composite-world-enhanced-old"u8.ToArray();
            var worldEnhancedNew = "composite-world-enhanced-new"u8.ToArray();
            var characterEnhanced = "composite-character-enhanced"u8.ToArray();
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "world.s3d"),
                worldOriginal,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "global_chr.s3d"),
                characterOriginal,
                cancellationToken).ConfigureAwait(false);

            var worldBuilder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    worldEnhancedOld,
                    token));
            var characterBuilder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    characterEnhanced,
                    token));
            var stagedBuilds = new StagedBuildService();
            var worldPack = await stagedBuilds.BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Scope = AssetScope.WorldOnly,
                        InstallAfterBuild = false
                    },
                    [new StagedBuildItem("world.s3d", worldBuilder)],
                    BuildId: "launcher-composite-world"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var characterPack = await stagedBuilds.BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Scope = AssetScope.CharactersAndEquipmentOnly,
                        InstallAfterBuild = false
                    },
                    [new StagedBuildItem("global_chr.s3d", characterBuilder)],
                    BuildId: "launcher-composite-characters"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var store = new ManifestStore();
            var catalog = new StagedPackCatalogService(store);
            var composer = new StagedPackComposer(catalog, store);
            var composition = await composer.ComposeAsync(
                paths,
                [worldPack.ManifestPath, characterPack.ManifestPath],
                cancellationToken: cancellationToken,
                compositionId: "launcher-composite-active").ConfigureAwait(false);
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                composition.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "world.s3d"),
                worldOfficial,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "global_chr.s3d"),
                characterOriginal,
                cancellationToken).ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Patching",
                    "world.s3d",
                    cancellationToken)
                .ConfigureAwait(false);

            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store),
                stagedPackCatalogService: catalog,
                stagedPackComposer: composer);
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateRefreshState.Ready,
                assessment.State,
                "composite with unaffected character leaf is focused-refresh ready");

            var stagingDirectoriesBefore = Directory
                .EnumerateDirectories(paths.StagingPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            LauncherUpdateActionRequiredException? blockedSelection = null;
            try
            {
                await workflow.ApplySelectedStagedPacksAsync(
                        paths,
                        [worldPack.ManifestPath, characterPack.ManifestPath],
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LauncherUpdateActionRequiredException exception)
            {
                blockedSelection = exception;
            }

            Assert(blockedSelection is not null,
                "Pack selection is redirected before launcher-update composition");
            AssertEqual(
                LauncherUpdateRefreshState.Ready,
                blockedSelection!.Assessment.State,
                "typed Pack selection gate carries actionable assessment");
            Assert(
                blockedSelection.Message.Contains(
                    "Refresh + Reinstall After Update",
                    StringComparison.Ordinal),
                "typed Pack selection gate names the exact main-screen action");
            var stagingDirectoriesAfter = Directory
                .EnumerateDirectories(paths.StagingPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert(
                stagingDirectoriesBefore.SequenceEqual(
                    stagingDirectoriesAfter,
                    StringComparer.OrdinalIgnoreCase),
                "blocked Pack selection creates no composition directory");

            var rebuiltPaths = new List<string>();
            var refreshBuilder = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    rebuiltPaths.Add(context.RelativeInstallPath);
                    await File.WriteAllBytesAsync(
                        context.DestinationPath,
                        worldEnhancedNew,
                        token).ConfigureAwait(false);
                });
            var refreshed = await workflow
                .RefreshAndApplyActivePackAfterLauncherUpdateAsync(
                    paths,
                    refreshBuilder,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(1, refreshed.RebuiltPacks.Count,
                "only changed World leaf gets immutable replacement");
            AssertEqual(1, refreshed.ReusedPackCount,
                "unaffected character leaf is reused despite ineligible repair scope");
            AssertEqual("world.s3d", rebuiltPaths.Single(),
                "only changed World archive is rebuilt");
            AssertSequenceEqual(
                worldEnhancedNew,
                await File.ReadAllBytesAsync(
                    Path.Combine(paths.InstallPath, "world.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "refreshed composition installs rebuilt World output");
            AssertSequenceEqual(
                characterEnhanced,
                await File.ReadAllBytesAsync(
                    Path.Combine(paths.InstallPath, "global_chr.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "refreshed composition reinstalls unaffected character output");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestChangedCharacterLeafRequiresFreshBuildAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-character-fresh-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var original = await CreateArchiveAsync(
                "character-original",
                cancellationToken).ConfigureAwait(false);
            var official = await CreateArchiveAsync(
                "character-official-update",
                cancellationToken).ConfigureAwait(false);
            var enhanced = "character-enhanced"u8.ToArray();
            var livePath = Path.Combine(paths.InstallPath, "global_chr.s3d");
            await File.WriteAllBytesAsync(livePath, original, cancellationToken)
                .ConfigureAwait(false);
            var builder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    enhanced,
                    token));
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Scope = AssetScope.CharactersAndEquipmentOnly,
                        InstallAfterBuild = false
                    },
                    [new StagedBuildItem("global_chr.s3d", builder)],
                    BuildId: "launcher-character-ineligible"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                staged.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(livePath, official, cancellationToken)
                .ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Patching",
                    "global_chr.s3d",
                    cancellationToken)
                .ConfigureAwait(false);

            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateRefreshState.FreshBuildRequired,
                assessment.State,
                "changed character-only leaf is never incorrectly Ready");
            Assert(assessment.CanReconcileForFreshBuild,
                "changed ineligible leaf keeps reconcile-only action available");
            await workflow.ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                official,
                await File.ReadAllBytesAsync(livePath, cancellationToken)
                    .ConfigureAwait(false),
                "fresh-build reconcile preserves changed official character archive");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestMissingCompositionComponentRequiresFreshBuildAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-missing-component-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var worldOriginal = await CreateArchiveAsync(
                "missing-component-world-original",
                cancellationToken).ConfigureAwait(false);
            var worldOfficial = await CreateArchiveAsync(
                "missing-component-world-official",
                cancellationToken).ConfigureAwait(false);
            var characterOriginal = await CreateArchiveAsync(
                "missing-component-character-original",
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "world.s3d"),
                worldOriginal,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "global_chr.s3d"),
                characterOriginal,
                cancellationToken).ConfigureAwait(false);
            var stagedBuilds = new StagedBuildService();
            var worldPack = await stagedBuilds.BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with { Scope = AssetScope.WorldOnly },
                    [new StagedBuildItem(
                        "world.s3d",
                        new DelegateStagedArtifactBuilder((context, token) =>
                            File.WriteAllBytesAsync(
                                context.DestinationPath,
                                "missing-world-enhanced"u8.ToArray(),
                                token)))],
                    BuildId: "launcher-missing-world"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var characterPack = await stagedBuilds.BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Scope = AssetScope.CharactersAndEquipmentOnly
                    },
                    [new StagedBuildItem(
                        "global_chr.s3d",
                        new DelegateStagedArtifactBuilder((context, token) =>
                            File.WriteAllBytesAsync(
                                context.DestinationPath,
                                "missing-character-enhanced"u8.ToArray(),
                                token)))],
                    BuildId: "launcher-missing-characters"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var store = new ManifestStore();
            var composer = new StagedPackComposer(
                new StagedPackCatalogService(store),
                store);
            var composition = await composer.ComposeAsync(
                paths,
                [worldPack.ManifestPath, characterPack.ManifestPath],
                cancellationToken: cancellationToken,
                compositionId: "launcher-missing-active").ConfigureAwait(false);
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                composition.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "world.s3d"),
                worldOfficial,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "global_chr.s3d"),
                characterOriginal,
                cancellationToken).ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Patching",
                    "world.s3d",
                    cancellationToken)
                .ConfigureAwait(false);

            File.Delete(characterPack.ManifestPath);
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                LauncherUpdateRefreshState.FreshBuildRequired,
                assessment.State,
                "missing active component manifest fails to FreshBuildRequired");
            Assert(assessment.CanReconcileForFreshBuild,
                "missing active component keeps official-update acceptance available");
            await workflow.ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                worldOfficial,
                await File.ReadAllBytesAsync(
                    Path.Combine(paths.InstallPath, "world.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "missing-component reconcile preserves official changed archive");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestChangedPaintedRendererCompatibilityPreflightAsync(
        CancellationToken cancellationToken)
    {
        const string workerEnvironmentVariable = "SPINTEXTURE_ARTISTIC_WORKER";
        var originalWorker = Environment.GetEnvironmentVariable(
            workerEnvironmentVariable);
        var workerRoot = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-worker-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(workerEnvironmentVariable, null);
            await AssertChangedPaintedAssessmentAsync(
                    "painted-unknown",
                    PaintedRendererOutcome.Unknown,
                    TextureBuildReport.CurrentIllustratedProfileRevision,
                    artisticWorkerFingerprint: null,
                    artisticWorkerPreset: null,
                    LauncherUpdateRefreshState.FreshBuildRequired,
                    "unknown painted renderer provenance",
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertChangedPaintedAssessmentAsync(
                    "painted-mixed",
                    PaintedRendererOutcome.Mixed,
                    TextureBuildReport.CurrentIllustratedProfileRevision,
                    artisticWorkerFingerprint: null,
                    artisticWorkerPreset: null,
                    LauncherUpdateRefreshState.FreshBuildRequired,
                    "mixed painted renderer provenance",
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertChangedPaintedAssessmentAsync(
                    "painted-stale-profile",
                    PaintedRendererOutcome.BuiltInOnly,
                    TextureBuildReport.CurrentIllustratedProfileRevision - 1,
                    artisticWorkerFingerprint: null,
                    artisticWorkerPreset: null,
                    LauncherUpdateRefreshState.FreshBuildRequired,
                    "stale painted profile revision",
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertChangedPaintedAssessmentAsync(
                    "painted-compatible-built-in",
                    PaintedRendererOutcome.BuiltInOnly,
                    TextureBuildReport.CurrentIllustratedProfileRevision,
                    artisticWorkerFingerprint: null,
                    artisticWorkerPreset: null,
                    LauncherUpdateRefreshState.Ready,
                    "compatible built-in painted renderer",
                    cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(workerRoot);
            var workerPath = Path.Combine(workerRoot, "worker.exe");
            await File.WriteAllBytesAsync(
                workerPath,
                "different-current-painted-worker"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(workerRoot, "worker-config.json"),
                "{\"preset\":\"current-worker-style\"}",
                cancellationToken).ConfigureAwait(false);
            Environment.SetEnvironmentVariable(
                workerEnvironmentVariable,
                workerPath);
            await AssertChangedPaintedAssessmentAsync(
                    "painted-external-identity-mismatch",
                    PaintedRendererOutcome.ExternalOnly,
                    TextureBuildReport.CurrentIllustratedProfileRevision,
                    artisticWorkerFingerprint: new string('a', 64),
                    artisticWorkerPreset: "recorded-worker-style",
                    LauncherUpdateRefreshState.FreshBuildRequired,
                    "different external painted worker identity",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                workerEnvironmentVariable,
                originalWorker);
            if (Directory.Exists(workerRoot))
            {
                DeleteTree(workerRoot);
            }
        }
    }

    private static async Task AssertChangedPaintedAssessmentAsync(
        string caseName,
        PaintedRendererOutcome rendererOutcome,
        int paintedProfileRevision,
        string? artisticWorkerFingerprint,
        string? artisticWorkerPreset,
        LauncherUpdateRefreshState expectedState,
        string description,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launcher-{caseName}-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "EverQuest"),
            Path.Combine(root, "Workspace"));
        Directory.CreateDirectory(paths.InstallPath);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(paths.InstallPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var original = await CreateArchiveAsync(
                $"{caseName}-original",
                cancellationToken).ConfigureAwait(false);
            var official = await CreateArchiveAsync(
                $"{caseName}-official-update",
                cancellationToken).ConfigureAwait(false);
            var livePath = Path.Combine(paths.InstallPath, "paintzone.s3d");
            await File.WriteAllBytesAsync(livePath, original, cancellationToken)
                .ConfigureAwait(false);
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with
                    {
                        Preset = TexturePreset.Illustrated,
                        Scope = AssetScope.WorldOnly,
                        InstallAfterBuild = false,
                        ArtisticWorkerFingerprint = artisticWorkerFingerprint,
                        ArtisticWorkerPreset = artisticWorkerPreset
                    },
                    [new StagedBuildItem(
                        "paintzone.s3d",
                        new DelegateStagedArtifactBuilder((context, token) =>
                            File.WriteAllBytesAsync(
                                context.DestinationPath,
                                System.Text.Encoding.UTF8.GetBytes(
                                    $"{caseName}-enhanced"),
                                token)))],
                    BuildId: caseName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var report = new TextureBuildReport(
                TextureBuildReport.CurrentSchemaVersion,
                staged.BuildId,
                DateTimeOffset.UtcNow,
                paths.InstallPath,
                staged.BuildDirectory,
                SelectedArchives: 1,
                new TextureBuildStatistics(
                    DiscoveredTextures: 1,
                    EnhancedTextures: 1,
                    PreservedTextures: 0,
                    SourceTextureBytes: 1,
                    EnhancedTextureBytes: 1,
                    new Dictionary<string, int>(),
                    []))
            {
                TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision,
                PaintedProfileRevision = paintedProfileRevision,
                UsedExternalArtisticWorker = rendererOutcome switch
                {
                    PaintedRendererOutcome.ExternalOnly => true,
                    PaintedRendererOutcome.BuiltInOnly => false,
                    PaintedRendererOutcome.Mixed => true,
                    _ => null
                },
                ArtisticWorkerFingerprint = artisticWorkerFingerprint,
                ArtisticWorkerPreset = artisticWorkerPreset,
                PaintedRendererOutcome = rendererOutcome,
                AppliedRepairRuleIds = TextureProcessingPipeline
                    .GetCurrentRepairRuleIds(
                        AssetScope.WorldOnly,
                        ["paintzone.s3d"])
            };
            await using (var stream = new FileStream(
                             Path.Combine(staged.BuildDirectory, "texture-report.json"),
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        report,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var applied = await transactions.ApplyAsync(
                paths,
                staged.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(livePath, official, cancellationToken)
                .ConfigureAwait(false);
            await WriteCompletedLaunchPadLogAsync(
                    paths,
                    applied.Manifest.AppliedUtc.AddMinutes(1),
                    "Patching",
                    "paintzone.s3d",
                    cancellationToken)
                .ConfigureAwait(false);

            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store));
            var assessment = await workflow.AssessLauncherUpdateRefreshAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(expectedState, assessment.State, description);
            if (expectedState == LauncherUpdateRefreshState.FreshBuildRequired)
            {
                Assert(assessment.CanReconcileForFreshBuild,
                    $"{description} retains reconcile-only action");
                var expectedReason = paintedProfileRevision
                        != TextureBuildReport.CurrentIllustratedProfileRevision
                    ? "art-profile revision"
                    : rendererOutcome switch
                    {
                        PaintedRendererOutcome.Unknown =>
                            "predates reliable painted-renderer provenance",
                        PaintedRendererOutcome.Mixed =>
                            "contains both external-diffusion and built-in painted outputs",
                        PaintedRendererOutcome.ExternalOnly =>
                            "different diffusion worker",
                        _ => "painted"
                    };
                Assert(
                    assessment.Summary.Contains(
                        expectedReason,
                        StringComparison.OrdinalIgnoreCase),
                    $"{description} surfaces its deterministic incompatibility reason");
            }
            else
            {
                Assert(assessment.CanRefresh,
                    $"{description} remains focused-refresh compatible");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task WriteCompletedLaunchPadLogAsync(
        ProjectPaths paths,
        DateTimeOffset sessionUtc,
        string action,
        string relativePath,
        CancellationToken cancellationToken) =>
        await File.WriteAllLinesAsync(
            Path.Combine(
                paths.InstallPath,
                LaunchPadUpdateEvidenceService.DownloadLogFileName),
            [
                $"**** Starting at {sessionUtc.ToLocalTime():ddd MMM d HH:mm:ss yyyy} with plug-in 1.0.3.204 ****",
                "5f54-0:00:00:Found 1 file(s) to update.",
                $"5f54-0:00:01:{action} {relativePath}",
                "Finished downloading 1,024 bytes in 0.100 seconds (10,240 bytes per second)"
            ],
            cancellationToken).ConfigureAwait(false);

    private static (long Length, string Sha256) Fingerprint(byte[] payload) =>
        (payload.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)));

    private static async Task<byte[]> CreateArchiveAsync(
        string marker,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await PfsArchiveWriter.WriteAsync(
            stream,
            [new PfsArchiveItem("marker.txt", System.Text.Encoding.UTF8.GetBytes(marker))],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }

    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(root, recursive: true);
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
}
