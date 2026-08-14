using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedPackSourceRepairSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestActiveBuildSourcesUseManagedOriginalsAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestImmutableWorldSourceRepairAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestCombinedSourceMismatchAndSafetyRepairAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TestCombinedSourceMismatchAndSafetyRepairAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-source-safety-repair-{Guid.NewGuid():N}");
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
            string[] relativePaths = ["sky.s3d", "clean.s3d", "dirty.s3d"];
            var originals = relativePaths.ToDictionary(
                path => path,
                path => System.Text.Encoding.UTF8.GetBytes($"original::{path}"),
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in relativePaths)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(installPath, path),
                    originals[path],
                    cancellationToken).ConfigureAwait(false);
            }

            var priorDirtySource = "managed-enhanced::dirty.s3d"u8.ToArray();
            var baselineOutputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["sky.s3d"] = "bad-enhanced::sky.s3d"u8.ToArray(),
                ["clean.s3d"] = "good-enhanced::clean.s3d"u8.ToArray(),
                ["dirty.s3d"] = "double-enhanced::dirty.s3d"u8.ToArray()
            };
            var buildId = "build-source-and-safety";
            var buildDirectory = Path.Combine(paths.StagingPath, buildId);
            var payloadDirectory = Path.Combine(buildDirectory, "payload");
            Directory.CreateDirectory(payloadDirectory);
            var entries = new List<BuildManifestEntry>();
            foreach (var path in relativePaths)
            {
                var source = path == "dirty.s3d" ? priorDirtySource : originals[path];
                await File.WriteAllBytesAsync(
                    Path.Combine(payloadDirectory, path),
                    baselineOutputs[path],
                    cancellationToken).ConfigureAwait(false);
                var sourceFingerprint = Fingerprint(source);
                var outputFingerprint = Fingerprint(baselineOutputs[path]);
                entries.Add(new BuildManifestEntry(
                    path,
                    sourceFingerprint.Length,
                    sourceFingerprint.Sha256,
                    outputFingerprint.Length,
                    outputFingerprint.Sha256));
            }

            var options = new UpscaleOptions(
                TexturePreset.Faithful,
                AssetScope.WorldOnly,
                2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false);
            var manifestPath = Path.Combine(buildDirectory, "manifest.json");
            await new ManifestStore().WriteBuildManifestAsync(
                manifestPath,
                new BuildManifest(
                    BuildManifest.CurrentSchemaVersion,
                    buildId,
                    DateTimeOffset.UtcNow,
                    installPath,
                    options,
                    entries),
                cancellationToken).ConfigureAwait(false);
            await using (var reportStream = new FileStream(
                Path.Combine(buildDirectory, "texture-report.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    reportStream,
                    new TextureBuildReport(
                    TextureBuildReport.CurrentSchemaVersion,
                    buildId,
                    DateTimeOffset.UtcNow,
                    installPath,
                    buildDirectory,
                    3,
                    new TextureBuildStatistics(
                        3,
                        3,
                        0,
                        3,
                        3,
                        new Dictionary<string, int>(),
                        []))
                    {
                        TexturePipelineRevision = 3,
                        PaintedProfileRevision = TextureBuildReport.CurrentIllustratedProfileRevision
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var transactionDirectory = Path.Combine(paths.BackupPath, "prior-dirty");
            var backupRelativePath = Path.Combine("payload", "dirty.s3d");
            Directory.CreateDirectory(Path.Combine(transactionDirectory, "payload"));
            await File.WriteAllBytesAsync(
                Path.Combine(transactionDirectory, backupRelativePath),
                originals["dirty.s3d"],
                cancellationToken).ConfigureAwait(false);
            var originalFingerprint = Fingerprint(originals["dirty.s3d"]);
            var installedFingerprint = Fingerprint(priorDirtySource);
            await new ManifestStore().WriteInstallManifestAsync(
                Path.Combine(transactionDirectory, "install-manifest.json"),
                new InstallManifest(
                    InstallManifest.CurrentSchemaVersion,
                    "prior-dirty",
                    DateTimeOffset.UtcNow,
                    installPath,
                    "prior",
                    Path.Combine(paths.StagingPath, "prior", "manifest.json"),
                    InstallTransactionState.Restored,
                    [
                        new InstalledArtifact(
                            "dirty.s3d",
                            true,
                            originalFingerprint.Length,
                            originalFingerprint.Sha256,
                            backupRelativePath,
                            installedFingerprint.Length,
                            installedFingerprint.Sha256)
                    ]),
                cancellationToken).ConfigureAwait(false);

            var rebuild = new DelegateStagedArtifactBuilder(async (context, token) =>
            {
                var source = await File.ReadAllBytesAsync(context.SourcePath, token)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(
                    context.DestinationPath,
                    source.Concat("::fresh"u8.ToArray()).ToArray(),
                    token).ConfigureAwait(false);
            });
            var targeted = new DelegateStagedArtifactBuilder(async (context, token) =>
            {
                var output = context.RelativeInstallPath.Equals(
                    "sky.s3d",
                    StringComparison.OrdinalIgnoreCase)
                    ? await File.ReadAllBytesAsync(context.SourcePath, token).ConfigureAwait(false)
                    : baselineOutputs[context.RelativeInstallPath];
                await File.WriteAllBytesAsync(context.DestinationPath, output, token)
                    .ConfigureAwait(false);
            });
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                manifestStore: new ManifestStore(),
                stagedPackCatalogService: new StagedPackCatalogService());
            var repaired = await workflow.RepairStagedPackSourceMismatchAndSafetyAsync(
                paths,
                manifestPath,
                rebuild,
                targeted,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            Assert(
                repaired.StagedBuild.Manifest.Entries.All(entry =>
                    !entry.RelativeInstallPath.Equals("sky.s3d", StringComparison.OrdinalIgnoreCase)),
                "combined repair must omit source-restored sky.s3d");
            AssertEqual(2, repaired.StagedBuild.Manifest.Entries.Count, "combined replacement count");
            Assert(repaired.Report.IsSourceMismatchRepair, "combined source-repair flag");
            Assert(repaired.Report.IsSafetyRepair, "combined safety-repair flag");
            AssertEqual(
                TextureProcessingPipeline.CurrentRevision,
                repaired.Report.TexturePipelineRevision,
                "combined pipeline revision");
            AssertEqual(
                TextureBuildReport.CurrentIllustratedProfileRevision,
                repaired.Report.PaintedProfileRevision,
                "combined source/safety repair must preserve baseline visual-profile provenance");
            AssertSequenceEqual(
                baselineOutputs["clean.s3d"],
                await File.ReadAllBytesAsync(
                    Path.Combine(repaired.StagedBuild.BuildDirectory, "payload", "clean.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "combined repair reuses clean output exactly");
            AssertSequenceEqual(
                originals["dirty.s3d"].Concat("::fresh"u8.ToArray()).ToArray(),
                await File.ReadAllBytesAsync(
                    Path.Combine(repaired.StagedBuild.BuildDirectory, "payload", "dirty.s3d"),
                    cancellationToken).ConfigureAwait(false),
                "combined repair rebuilds contaminated output from original");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestActiveBuildSourcesUseManagedOriginalsAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-active-build-source-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        var archiveRelativePath = "terrain_test.s3d";
        var looseRelativePath = Path.Combine("Textures", "terrain_test.dds");
        var archivePath = Path.Combine(installPath, archiveRelativePath);
        var loosePath = Path.Combine(installPath, looseRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var originalArchive = "original-pfs-archive"u8.ToArray();
            var enhancedArchive = "enhanced-pfs-archive-with-larger-textures"u8.ToArray();
            var originalLoose = "original-loose-texture"u8.ToArray();
            var enhancedLoose = "enhanced-loose-texture-with-mipmaps"u8.ToArray();
            await File.WriteAllBytesAsync(archivePath, originalArchive, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(loosePath, originalLoose, cancellationToken)
                .ConfigureAwait(false);

            var outputByPath = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [archiveRelativePath] = enhancedArchive,
                [looseRelativePath] = enhancedLoose
            };
            var builder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    outputByPath[context.RelativeInstallPath],
                    token));
            var staged = await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        paths,
                        new UpscaleOptions(
                            TexturePreset.Faithful,
                            AssetScope.AllSafeTextures,
                            1024,
                            GenerateMipMaps: true,
                            InstallAfterBuild: false),
                        [
                            new StagedBuildItem(archiveRelativePath, builder),
                            new StagedBuildItem(looseRelativePath, builder)
                        ],
                        "build-active-source-test"),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var apply = await transactions.ApplyAsync(
                    paths,
                    staged.ManifestPath,
                    cancellationToken: cancellationToken,
                    applyId: "apply-active-source-test")
                .ConfigureAwait(false);
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store),
                stagedPackCatalogService: new StagedPackCatalogService(store));
            var health = await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.EnhancedActive,
                health.State,
                "synthetic active install health");

            var resolver = await workflow
                .PrepareBuildOriginalSourcesAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            var resolvedArchive = await resolver
                .ResolveAsync(archiveRelativePath, archivePath, cancellationToken)
                .ConfigureAwait(false);
            var resolvedLoose = await resolver
                .ResolveAsync(looseRelativePath, loosePath, cancellationToken)
                .ConfigureAwait(false);

            Assert(
                PathGuard.IsPathUnderRoot(paths.BackupPath, resolvedArchive.Path),
                "active archive build source must come from the managed backup");
            Assert(
                PathGuard.IsPathUnderRoot(paths.BackupPath, resolvedLoose.Path),
                "active loose-texture build source must come from the managed backup");
            AssertSequenceEqual(
                originalArchive,
                await File.ReadAllBytesAsync(resolvedArchive.Path, cancellationToken)
                    .ConfigureAwait(false),
                "resolved archive original bytes");
            AssertSequenceEqual(
                originalLoose,
                await File.ReadAllBytesAsync(resolvedLoose.Path, cancellationToken)
                    .ConfigureAwait(false),
                "resolved loose-texture original bytes");
            AssertSequenceEqual(
                enhancedArchive,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "source resolution must not change the live enhanced archive");
            AssertSequenceEqual(
                enhancedLoose,
                await File.ReadAllBytesAsync(loosePath, cancellationToken).ConfigureAwait(false),
                "source resolution must not change the live enhanced loose texture");
            AssertEqual(
                apply.ApplyId,
                (await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                    .ConfigureAwait(false)).ApplyId!,
                "source resolution must not replace the active transaction");

            // The expected fingerprint must survive the handoff from planning to
            // StagedBuildService. A same-length mutation after resolution must fail
            // before any artifact builder runs.
            var originalFingerprint = await FileIntegrity
                .FingerprintAsync(resolvedArchive.Path, cancellationToken)
                .ConfigureAwait(false);
            var mutatedArchive = (byte[])originalArchive.Clone();
            mutatedArchive[0] ^= 0x7F;
            await File.WriteAllBytesAsync(
                    resolvedArchive.Path,
                    mutatedArchive,
                    cancellationToken)
                .ConfigureAwait(false);
            var builderInvocations = 0;
            var guardedBuilder = new DelegateStagedArtifactBuilder(
                (context, token) =>
                {
                    builderInvocations++;
                    return File.WriteAllBytesAsync(
                        context.DestinationPath,
                        enhancedArchive,
                        token);
                });
            await AssertThrowsAsync<InvalidDataException>(
                    () => new StagedBuildService().BuildAsync(
                        new StagedBuildRequest(
                            paths,
                            staged.Manifest.Options,
                            [
                                new StagedBuildItem(
                                    archiveRelativePath,
                                    guardedBuilder,
                                    resolvedArchive.Path,
                                    originalFingerprint.Length,
                                    originalFingerprint.Sha256)
                            ],
                            "build-source-race-test"),
                        cancellationToken: cancellationToken),
                    "same-length managed-backup mutation must fail closed")
                .ConfigureAwait(false);
            AssertEqual(0, builderInvocations, "mutated source must fail before builder invocation");
            Assert(
                !Directory.Exists(Path.Combine(paths.StagingPath, "build-source-race-test")),
                "failed source handoff must clean its partial staging directory");

            var corruptResolver = await workflow
                .PrepareBuildOriginalSourcesAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            await AssertThrowsAsync<InvalidDataException>(
                    () => corruptResolver.ResolveAsync(
                        archiveRelativePath,
                        archivePath,
                        cancellationToken),
                    "corrupt managed original must fail during build-source planning")
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestImmutableWorldSourceRepairAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-world-source-repair-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            await File.WriteAllBytesAsync(
                    Path.Combine(installPath, "eqgame.exe"),
                    "synthetic-eqgame"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            string[] relativePaths =
            [
                "clean_a.s3d",
                "clean_b.s3d",
                "clean_c.s3d",
                "airplane.s3d",
                "airplane_obj.s3d"
            ];
            var originals = relativePaths.ToDictionary(
                relativePath => relativePath,
                relativePath => System.Text.Encoding.UTF8.GetBytes($"original::{relativePath}"),
                StringComparer.OrdinalIgnoreCase);
            var priorEnhanced = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["airplane.s3d"] = "prior-enhanced::airplane.s3d"u8.ToArray(),
                ["airplane_obj.s3d"] = "prior-enhanced::airplane_obj.s3d"u8.ToArray()
            };
            foreach (var relativePath in relativePaths)
            {
                await File.WriteAllBytesAsync(
                        Path.Combine(installPath, relativePath),
                        originals[relativePath],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var baselineBuildId = "build-contaminated-world";
            var baselineDirectory = Path.Combine(paths.StagingPath, baselineBuildId);
            var baselinePayloadDirectory = Path.Combine(baselineDirectory, "payload");
            Directory.CreateDirectory(baselinePayloadDirectory);
            var baselineEntries = new List<BuildManifestEntry>(relativePaths.Length);
            var baselinePayloadBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in relativePaths)
            {
                var sourceBytes = priorEnhanced.TryGetValue(relativePath, out var enhancedSource)
                    ? enhancedSource
                    : originals[relativePath];
                var stagedBytes = System.Text.Encoding.UTF8.GetBytes(
                    $"baseline-output::{relativePath}::{Convert.ToHexString(sourceBytes)}");
                baselinePayloadBytes.Add(relativePath, stagedBytes);
                var payloadPath = Path.Combine(baselinePayloadDirectory, relativePath);
                await File.WriteAllBytesAsync(payloadPath, stagedBytes, cancellationToken)
                    .ConfigureAwait(false);
                var sourceFingerprint = Fingerprint(sourceBytes);
                var stagedFingerprint = Fingerprint(stagedBytes);
                baselineEntries.Add(new BuildManifestEntry(
                    relativePath,
                    sourceFingerprint.Length,
                    sourceFingerprint.Sha256,
                    stagedFingerprint.Length,
                    stagedFingerprint.Sha256));
            }

            var options = new UpscaleOptions(
                TexturePreset.Illustrated,
                AssetScope.WorldCharactersAndEquipment,
                2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false,
                PaintedTheme: PaintedTheme.DarkGothic);
            var baselineManifestPath = Path.Combine(baselineDirectory, "manifest.json");
            await new ManifestStore().WriteBuildManifestAsync(
                    baselineManifestPath,
                    new BuildManifest(
                        BuildManifest.CurrentSchemaVersion,
                        baselineBuildId,
                        DateTimeOffset.UtcNow.AddMinutes(-10),
                        installPath,
                        options,
                        baselineEntries.AsReadOnly()),
                    cancellationToken)
                .ConfigureAwait(false);

            var transactionId = "apply-prior-world";
            var transactionDirectory = Path.Combine(paths.BackupPath, transactionId);
            var transactionPayloadDirectory = Path.Combine(transactionDirectory, "payload");
            Directory.CreateDirectory(transactionPayloadDirectory);
            var installedArtifacts = new List<InstalledArtifact>();
            foreach (var relativePath in priorEnhanced.Keys)
            {
                var originalBytes = originals[relativePath];
                var originalFingerprint = Fingerprint(originalBytes);
                var installedFingerprint = Fingerprint(priorEnhanced[relativePath]);
                var backupPath = Path.Combine(transactionPayloadDirectory, relativePath);
                await File.WriteAllBytesAsync(backupPath, originalBytes, cancellationToken)
                    .ConfigureAwait(false);
                installedArtifacts.Add(new InstalledArtifact(
                    relativePath,
                    OriginalExisted: true,
                    originalFingerprint.Length,
                    originalFingerprint.Sha256,
                    Path.GetRelativePath(transactionDirectory, backupPath),
                    installedFingerprint.Length,
                    installedFingerprint.Sha256,
                    InstalledLastWriteTimeUtcTicks: DateTime.UtcNow.Ticks));
            }

            await new ManifestStore().WriteInstallManifestAsync(
                    Path.Combine(transactionDirectory, "install-manifest.json"),
                    new InstallManifest(
                        InstallManifest.CurrentSchemaVersion,
                        transactionId,
                        DateTimeOffset.UtcNow.AddMinutes(-15),
                        installPath,
                        "build-prior-world",
                        Path.Combine(paths.StagingPath, "build-prior-world", "manifest.json"),
                        InstallTransactionState.Restored,
                        installedArtifacts.AsReadOnly()),
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(transactionDirectory, "restore-complete.json"),
                    "{}",
                    cancellationToken)
                .ConfigureAwait(false);

            var baselineFingerprints = await FingerprintTreeAsync(
                    baselineDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store),
                stagedPackCatalogService: new StagedPackCatalogService(store));
            var stagingBeforeDiscovery = Directory.EnumerateDirectories(paths.StagingPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidates = await workflow.FindStagedPackSourceRepairCandidatesAsync(
                    paths,
                    [baselineManifestPath, baselineManifestPath],
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(1, candidates.Count, "candidate discovery must deduplicate manifests");
            Assert(candidates.Contains(baselineManifestPath), "restored provenance must identify the contaminated World pack");
            AssertSequenceEqual(
                stagingBeforeDiscovery,
                Directory.EnumerateDirectories(paths.StagingPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                "candidate discovery must not create staging output");

            var rebuiltPaths = new List<string>();
            var deterministicRebuild = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    rebuiltPaths.Add(context.RelativeInstallPath);
                    AssertEqual(
                        TexturePreset.Illustrated,
                        context.Options.Preset,
                        "source-repair rebuild must retain painted preset");
                    AssertEqual(
                        PaintedTheme.DarkGothic,
                        context.Options.PaintedTheme,
                        "source-repair rebuild must retain painted theme");
                    var sourceBytes = await File.ReadAllBytesAsync(context.SourcePath, token)
                        .ConfigureAwait(false);
                    var marker = System.Text.Encoding.UTF8.GetBytes(
                        $"::rebuilt::{context.RelativeInstallPath}");
                    await File.WriteAllBytesAsync(
                            context.DestinationPath,
                            sourceBytes.Concat(marker).ToArray(),
                            token)
                        .ConfigureAwait(false);
                });

            // Exact catalog verification happens before source-repair planning.
            // A same-length baseline payload mutation after that trust boundary
            // must not be hard-linked and blessed into a new manifest.
            var racedPayloadPath = Path.Combine(
                baselinePayloadDirectory,
                "clean_a.s3d");
            var racedPayloadLastWriteUtc = File.GetLastWriteTimeUtc(racedPayloadPath);
            var racedPayloadBytes = (byte[])baselinePayloadBytes["clean_a.s3d"].Clone();
            racedPayloadBytes[0] ^= 0x3C;
            var raceTriggered = false;
            var stagingCountBeforeReuseRace = Directory
                .EnumerateDirectories(paths.StagingPath)
                .Count();
            var raceProgress = new InlineProgress<ProgressUpdate>(update =>
            {
                if (!raceTriggered && update.Stage == "Build")
                {
                    File.WriteAllBytes(racedPayloadPath, racedPayloadBytes);
                    raceTriggered = true;
                }
            });
            await AssertThrowsAsync<InvalidDataException>(
                    () => workflow.RepairStagedPackSourceMismatchAsync(
                        paths,
                        baselineManifestPath,
                        deterministicRebuild,
                        raceProgress,
                        cancellationToken),
                    "same-length baseline mutation after exact verification must fail closed")
                .ConfigureAwait(false);
            Assert(raceTriggered, "reuse race fixture must mutate after exact verification");
            AssertEqual(0, rebuiltPaths.Count, "reuse race must fail before the rebuild builder runs");
            AssertEqual(
                stagingCountBeforeReuseRace,
                Directory.EnumerateDirectories(paths.StagingPath).Count(),
                "failed reuse race must clean its partial staged repair");
            await File.WriteAllBytesAsync(
                    racedPayloadPath,
                    baselinePayloadBytes["clean_a.s3d"],
                    cancellationToken)
                .ConfigureAwait(false);
            File.SetLastWriteTimeUtc(racedPayloadPath, racedPayloadLastWriteUtc);

            var repaired = await workflow.RepairStagedPackSourceMismatchAsync(
                    paths,
                    baselineManifestPath,
                    deterministicRebuild,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            Assert(!PathGuard.SamePath(repaired.StagedBuild.ManifestPath, baselineManifestPath),
                "source repair must create a new immutable build");
            AssertEqual(3, repaired.Report.ReusedArtifacts, "whole artifacts reused");
            AssertEqual(2, repaired.Report.RebuiltArtifacts, "contaminated artifacts rebuilt");
            Assert(repaired.Report.IsSourceMismatchRepair, "source repair report marker");
            AssertEqual(baselineBuildId, repaired.Report.BaselineBuildId!, "source repair baseline provenance");
            AssertSequenceEqual(
                ["airplane.s3d", "airplane_obj.s3d"],
                rebuiltPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                "only contaminated archives may enter the rebuild builder");

            var repairedManifest = repaired.StagedBuild.Manifest;
            AssertEqual(
                options,
                repairedManifest.Options,
                "source repair manifest must preserve complete painted options");
            foreach (var relativePath in relativePaths)
            {
                var entry = repairedManifest.Entries.Single(candidate =>
                    candidate.RelativeInstallPath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                var originalFingerprint = Fingerprint(originals[relativePath]);
                AssertEqual(originalFingerprint.Length, entry.SourceLength, $"repaired source length for {relativePath}");
                AssertEqual(originalFingerprint.Sha256, entry.SourceSha256, $"repaired source hash for {relativePath}");
                var repairedPayloadPath = Path.Combine(
                    repaired.StagedBuild.BuildDirectory,
                    "payload",
                    relativePath);
                var repairedPayload = await File.ReadAllBytesAsync(
                        repairedPayloadPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (priorEnhanced.ContainsKey(relativePath))
                {
                    Assert(
                        !repairedPayload.AsSpan().SequenceEqual(baselinePayloadBytes[relativePath]),
                        $"contaminated payload must not be reused for {relativePath}");
                    Assert(
                        repairedPayload.AsSpan(0, originals[relativePath].Length)
                            .SequenceEqual(originals[relativePath]),
                        $"rebuild input must be the exact original for {relativePath}");
                }
                else
                {
                    AssertSequenceEqual(
                        baselinePayloadBytes[relativePath],
                        repairedPayload,
                        $"clean whole payload reuse for {relativePath}");
                }
            }

            var exact = await new StagedPackCatalogService().InspectAsync(
                    paths,
                    repaired.StagedBuild.ManifestPath,
                    StagedPackVerificationMode.Exact,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(StagedPackValidationState.Ready, exact.State, "exact repaired-pack verification");
            await AssertFingerprintsUnchangedAsync(
                    baselineFingerprints,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var relativePath in relativePaths)
            {
                AssertSequenceEqual(
                    originals[relativePath],
                    await File.ReadAllBytesAsync(
                            Path.Combine(installPath, relativePath),
                            cancellationToken)
                        .ConfigureAwait(false),
                    $"source repair must not write live {relativePath}");
            }

            await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [repaired.StagedBuild.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in repaired.StagedBuild.Manifest.Entries)
            {
                AssertSequenceEqual(
                    await File.ReadAllBytesAsync(
                            Path.Combine(
                                repaired.StagedBuild.BuildDirectory,
                                "payload",
                                entry.RelativeInstallPath),
                            cancellationToken)
                        .ConfigureAwait(false),
                    await File.ReadAllBytesAsync(
                            Path.Combine(installPath, entry.RelativeInstallPath),
                            cancellationToken)
                        .ConfigureAwait(false),
                    $"repaired pack apply payload for {entry.RelativeInstallPath}");
            }

            await workflow.RestoreLatestAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var relativePath in relativePaths)
            {
                AssertSequenceEqual(
                    originals[relativePath],
                    await File.ReadAllBytesAsync(
                            Path.Combine(installPath, relativePath),
                            cancellationToken)
                        .ConfigureAwait(false),
                    $"repaired pack restore original for {relativePath}");
            }
            await AssertFingerprintsUnchangedAsync(
                    baselineFingerprints,
                    cancellationToken)
                .ConfigureAwait(false);

            var historicalBackupPath = Path.Combine(
                transactionPayloadDirectory,
                "airplane.s3d");
            var corruptHistoricalBackup = await File.ReadAllBytesAsync(
                    historicalBackupPath,
                    cancellationToken)
                .ConfigureAwait(false);
            corruptHistoricalBackup[0] ^= 0x55;
            await File.WriteAllBytesAsync(
                    historicalBackupPath,
                    corruptHistoricalBackup,
                    cancellationToken)
                .ConfigureAwait(false);
            var stagingCountBeforeCorruptProvenance = Directory
                .EnumerateDirectories(paths.StagingPath)
                .Count();
            var invocationCountBeforeCorruptProvenance = rebuiltPaths.Count;
            await AssertThrowsAsync<InvalidDataException>(
                    () => workflow.RepairStagedPackSourceMismatchAsync(
                        paths,
                        baselineManifestPath,
                        deterministicRebuild,
                        cancellationToken: cancellationToken),
                    "corrupt historical provenance backup must fail source repair")
                .ConfigureAwait(false);
            AssertEqual(
                invocationCountBeforeCorruptProvenance,
                rebuiltPaths.Count,
                "corrupt provenance must fail before rebuild invocation");
            AssertEqual(
                stagingCountBeforeCorruptProvenance,
                Directory.EnumerateDirectories(paths.StagingPath).Count(),
                "corrupt provenance must not create a staged repair");

            var unknownBuildId = "build-unknown-world-source";
            var unknownDirectory = Path.Combine(paths.StagingPath, unknownBuildId);
            var unknownPayloadDirectory = Path.Combine(unknownDirectory, "payload");
            Directory.CreateDirectory(unknownPayloadDirectory);
            var unknownRelativePath = "clean_a.s3d";
            var unknownSource = "unmanaged-client-version"u8.ToArray();
            var unknownPayload = "unknown-source-output"u8.ToArray();
            await File.WriteAllBytesAsync(
                    Path.Combine(unknownPayloadDirectory, unknownRelativePath),
                    unknownPayload,
                    cancellationToken)
                .ConfigureAwait(false);
            var unknownSourceFingerprint = Fingerprint(unknownSource);
            var unknownPayloadFingerprint = Fingerprint(unknownPayload);
            var unknownManifestPath = Path.Combine(unknownDirectory, "manifest.json");
            await new ManifestStore().WriteBuildManifestAsync(
                    unknownManifestPath,
                    new BuildManifest(
                        BuildManifest.CurrentSchemaVersion,
                        unknownBuildId,
                        DateTimeOffset.UtcNow,
                        installPath,
                        options,
                        [
                            new BuildManifestEntry(
                                unknownRelativePath,
                                unknownSourceFingerprint.Length,
                                unknownSourceFingerprint.Sha256,
                                unknownPayloadFingerprint.Length,
                                unknownPayloadFingerprint.Sha256)
                        ]),
                    cancellationToken)
                .ConfigureAwait(false);
            var stagingCountBeforeUnknown = Directory.EnumerateDirectories(paths.StagingPath).Count();
            var invocationCountBeforeUnknown = rebuiltPaths.Count;
            await AssertThrowsAsync<InvalidOperationException>(
                    () => workflow.RepairStagedPackSourceMismatchAsync(
                        paths,
                        unknownManifestPath,
                        deterministicRebuild,
                        cancellationToken: cancellationToken),
                    "unknown client-version mismatch must fail closed")
                .ConfigureAwait(false);
            AssertEqual(invocationCountBeforeUnknown, rebuiltPaths.Count, "unknown mismatch must not invoke the builder");
            AssertEqual(
                stagingCountBeforeUnknown,
                Directory.EnumerateDirectories(paths.StagingPath).Count(),
                "unknown mismatch must not create a staged repair");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static FileFingerprint Fingerprint(byte[] bytes) => new(
        bytes.LongLength,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

    private static async Task<IReadOnlyDictionary<string, FileFingerprint>> FingerprintTreeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result.Add(
                path,
                await FileIntegrity.FingerprintAsync(path, cancellationToken)
                    .ConfigureAwait(false));
        }

        return result;
    }

    private static async Task AssertFingerprintsUnchangedAsync(
        IReadOnlyDictionary<string, FileFingerprint> expected,
        CancellationToken cancellationToken)
    {
        foreach (var pair in expected)
        {
            AssertEqual(
                pair.Value,
                await FileIntegrity.FingerprintAsync(pair.Key, cancellationToken)
                    .ConfigureAwait(false),
                $"immutable baseline fingerprint for {pair.Key}");
        }
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

    private static void AssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
