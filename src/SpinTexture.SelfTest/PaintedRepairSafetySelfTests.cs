using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

internal static class PaintedRepairSafetySelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestOrdinaryCombinedRepairPreservesPaintedIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestLegacyPaintedManualReprocessFailsClosedAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var failure in Enum.GetValues<PaintedFailure>())
        {
            await TestRequestedProfileFailureStopsRepairAsync(failure, cancellationToken)
                .ConfigureAwait(false);
        }

        await TestInvalidPriorPaintedOutputIsNotRestoredToOriginalAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestFilteredChangedPaintedMemberFailsClosedAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestFailureCannotPublishMixedReplacementAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestRetriedPreservedFailureKeepsOriginalAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestVerifiedOriginalAdditionFailureKeepsOriginalAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestRetriedPreservedFailureSurvivesForeignCompressionAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestPaintedRendererMismatchStopsRepairAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A Graphic Painted Fantasy pack records which renderer produced it (the
    /// external diffusion worker or the built-in stylization). A repair that
    /// would regenerate textures with the other renderer must stop instead of
    /// quietly mixing two art styles into one pack.
    /// </summary>
    private static async Task TestPaintedRendererMismatchStopsRepairAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-painted-renderer-{Guid.NewGuid():N}");
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
            await File.WriteAllTextAsync(
                    Path.Combine(installPath, "paintzone.xmi"),
                    "synthetic-zone-marker",
                    cancellationToken)
                .ConfigureAwait(false);
            var archivePath = Path.Combine(installPath, "paintzone.s3d");
            await WriteArchiveAsync(
                    archivePath,
                    [new("surface.tga", CreateTarga(32, 32, seed: 83))],
                    cancellationToken)
                .ConfigureAwait(false);
            var enhancedArchive = Path.Combine(root, "enhanced-paintzone.s3d");
            await WriteArchiveAsync(
                    enhancedArchive,
                    [new("surface.tga", CreateTarga(128, 128, seed: 84))],
                    cancellationToken)
                .ConfigureAwait(false);

            var options = new UpscaleOptions(
                TexturePreset.Illustrated,
                AssetScope.WorldOnly,
                MaximumDimension: 1024,
                GenerateMipMaps: false,
                InstallAfterBuild: false,
                PaintedTheme: PaintedTheme.ComicInk,
                ArtisticWorkerFingerprint: new string('d', 64),
                ArtisticWorkerPreset: "configured-but-unused");
            var baseline = await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        paths,
                        options,
                        [
                            new StagedBuildItem(
                                "paintzone.s3d",
                                new DelegateStagedArtifactBuilder(async (context, token) =>
                                {
                                    var bytes = await File.ReadAllBytesAsync(enhancedArchive, token)
                                        .ConfigureAwait(false);
                                    await File.WriteAllBytesAsync(context.DestinationPath, bytes, token)
                                        .ConfigureAwait(false);
                                }))
                        ],
                        "painted-renderer-baseline"),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            // Current painted profile, one missing targeted rule (expanded
            // coverage) so the repair reaches regeneration planning, and a
            // recorded diffusion renderer that this environment does not have.
            var recordedRules = TextureProcessingPipeline
                .GetCurrentRepairRuleIds(options.Scope, ["paintzone.s3d"])
                .Where(ruleId => !ruleId.Equals(
                    TextureProcessingPipeline.ExpandedClassicCoverageRuleId,
                    StringComparison.Ordinal))
                .ToArray();
            var rendererReport = new TextureBuildReport(
                TextureBuildReport.CurrentSchemaVersion,
                baseline.BuildId,
                DateTimeOffset.UtcNow,
                installPath,
                baseline.BuildDirectory,
                1,
                new TextureBuildStatistics(
                    1,
                    1,
                    0,
                    1,
                    1,
                    new Dictionary<string, int>(),
                    []))
            {
                TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision,
                PaintedProfileRevision =
                    TextureBuildReport.CurrentIllustratedProfileRevision,
                UsedExternalArtisticWorker = true,
                PaintedRendererOutcome = PaintedRendererOutcome.ExternalOnly,
                AppliedRepairRuleIds = recordedRules
            };
            var reportPath = Path.Combine(baseline.BuildDirectory, "texture-report.json");
            await using (var reportStream = new FileStream(
                reportPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        reportStream,
                        rendererReport,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var baselineFingerprint = await FileIntegrity.FingerprintAsync(
                    Path.Combine(baseline.BuildDirectory, "payload", "paintzone.s3d"),
                    cancellationToken)
                .ConfigureAwait(false);
            var failure = await AssertThrowsAsync<InvalidOperationException>(
                    () => new TexturePackWorkflow(clientClosedGuard: () => { })
                        .RepairStagedPackAsync(
                            paths,
                            baseline.ManifestPath,
                            cancellationToken: cancellationToken),
                    "a diffusion-rendered painted pack must not be repaired by the built-in renderer")
                .ConfigureAwait(false);
            Assert(
                failure.Message.Contains("diffusion repaint worker", StringComparison.OrdinalIgnoreCase),
                "renderer mismatch failure should name the missing diffusion worker");
            AssertEqual(
                baselineFingerprint,
                await FileIntegrity.FingerprintAsync(
                        Path.Combine(baseline.BuildDirectory, "payload", "paintzone.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "renderer mismatch leaves the immutable baseline unchanged");

            await WriteReportAsync(
                    reportPath,
                    rendererReport with
                    {
                        PaintedRendererOutcome = PaintedRendererOutcome.Mixed
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(
                (await File.ReadAllTextAsync(reportPath, cancellationToken)
                    .ConfigureAwait(false))
                .Contains(
                    "\"paintedRendererOutcome\": \"mixed\"",
                    StringComparison.Ordinal),
                "the repair fixture uses the production camel-case string-enum report format");
            var mixedFailure = await AssertThrowsAsync<InvalidOperationException>(
                    () => new TexturePackWorkflow(clientClosedGuard: () => { })
                        .RepairStagedPackAsync(
                            paths,
                            baseline.ManifestPath,
                            cancellationToken: cancellationToken),
                    "a mixed-renderer pack without per-member provenance must not be repaired")
                .ConfigureAwait(false);
            Assert(
                mixedFailure.Message.Contains("both external-diffusion and built-in", StringComparison.OrdinalIgnoreCase),
                "mixed-renderer repair refusal must clearly name the integrity problem");

            await WriteReportAsync(
                    reportPath,
                    rendererReport with
                    {
                        UsedExternalArtisticWorker = null,
                        ArtisticWorkerFingerprint = null,
                        ArtisticWorkerPreset = null,
                        PaintedRendererOutcome = PaintedRendererOutcome.Unknown
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var unknownFailure = await AssertThrowsAsync<InvalidOperationException>(
                    () => new TexturePackWorkflow(clientClosedGuard: () => { })
                        .RepairStagedPackAsync(
                            paths,
                            baseline.ManifestPath,
                            cancellationToken: cancellationToken),
                    "a legacy painted pack with unknown renderer provenance must not gain new pixels")
                .ConfigureAwait(false);
            Assert(
                unknownFailure.Message.Contains(
                    "predates reliable painted-renderer provenance",
                    StringComparison.OrdinalIgnoreCase),
                "unknown-renderer repair refusal must direct the user to a complete rebuild");

            await WriteReportAsync(
                    reportPath,
                    rendererReport with
                    {
                        SchemaVersion = 3,
                        UsedExternalArtisticWorker = true,
                        PaintedRendererOutcome = PaintedRendererOutcome.ExternalOnly
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var legacyAvailabilityFailure = await AssertThrowsAsync<InvalidOperationException>(
                    () => new TexturePackWorkflow(clientClosedGuard: () => { })
                        .RepairStagedPackAsync(
                            paths,
                            baseline.ManifestPath,
                            cancellationToken: cancellationToken),
                    "a schema-3 availability flag must not be promoted into renderer provenance")
                .ConfigureAwait(false);
            Assert(
                legacyAvailabilityFailure.Message.Contains(
                    "predates reliable painted-renderer provenance",
                    StringComparison.OrdinalIgnoreCase),
                "legacy worker availability must require a complete rebuild");

            var fakeWorkerPath = Path.Combine(root, "fake-artistic-worker.exe");
            await File.WriteAllBytesAsync(
                    fakeWorkerPath,
                    "synthetic-worker"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var previousWorker = Environment.GetEnvironmentVariable("SPINTEXTURE_ARTISTIC_WORKER");
            try
            {
                await WriteReportAsync(
                        reportPath,
                        rendererReport with
                        {
                            UsedExternalArtisticWorker = false,
                            PaintedRendererOutcome = PaintedRendererOutcome.BuiltInOnly
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                // The manifest deliberately contains the fingerprint of the
                // worker that was configured when the build started. Its
                // completed report is authoritative: every external job fell
                // back, so a built-in repair with that worker disabled remains
                // reproducible instead of becoming impossible.
                Environment.SetEnvironmentVariable("SPINTEXTURE_ARTISTIC_WORKER", null);
                var builtInRepair = await new TexturePackWorkflow(clientClosedGuard: () => { })
                    .RepairStagedPackAsync(
                        paths,
                        baseline.ManifestPath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Assert(
                    builtInRepair.Report.PaintedRendererOutcome
                        == PaintedRendererOutcome.BuiltInOnly
                    && builtInRepair.Report.ArtisticWorkerFingerprint is null,
                    "built-in-only repair must ignore a configured-but-unused manifest worker identity");

                Environment.SetEnvironmentVariable("SPINTEXTURE_ARTISTIC_WORKER", fakeWorkerPath);
                var builtInFailure = await AssertThrowsAsync<InvalidOperationException>(
                        () => new TexturePackWorkflow(clientClosedGuard: () => { })
                            .RepairStagedPackAsync(
                                paths,
                                baseline.ManifestPath,
                                cancellationToken: cancellationToken),
                        "a built-in-only pack must not silently adopt an installed external worker")
                    .ConfigureAwait(false);
                Assert(
                    builtInFailure.Message.Contains("built-in painterly", StringComparison.OrdinalIgnoreCase)
                    && builtInFailure.Message.Contains("enabled", StringComparison.OrdinalIgnoreCase),
                    "built-in-only repair refusal must explain that the external worker must be disabled");

                await WriteReportAsync(
                        reportPath,
                        rendererReport with
                        {
                            UsedExternalArtisticWorker = false,
                            PaintedRendererOutcome = PaintedRendererOutcome.BuiltInOnly,
                            AppliedRepairRuleIds = TextureProcessingPipeline
                                .GetCurrentRepairRuleIds(
                                    options.Scope,
                                    ["paintzone.s3d"],
                                    options.Preset)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                var manualMismatch = await AssertThrowsAsync<InvalidOperationException>(
                        () => new TexturePackWorkflow(clientClosedGuard: () => { })
                            .RepairStagedPackAsync(
                                paths,
                                baseline.ManifestPath,
                                cancellationToken: cancellationToken,
                                textureOverrides:
                                [
                                    new TextureOverride(
                                        "paintzone.s3d",
                                        "surface.tga",
                                        TextureOverrideAction.Reprocess,
                                        TexturePreset.Illustrated)
                                ]),
                        "manual reprocess must enforce the baseline renderer route")
                    .ConfigureAwait(false);
                Assert(
                    manualMismatch.Message.Contains(
                        "built-in painterly",
                        StringComparison.OrdinalIgnoreCase),
                    "manual renderer mismatch must fail before generating mixed pixels");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SPINTEXTURE_ARTISTIC_WORKER", previousWorker);
            }
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestLegacyPaintedManualReprocessFailsClosedAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-painted-stale-manual-{Guid.NewGuid():N}");
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
            await File.WriteAllTextAsync(
                    Path.Combine(installPath, "paintzone.xmi"),
                    "synthetic-zone-marker",
                    cancellationToken)
                .ConfigureAwait(false);

            var archivePath = Path.Combine(installPath, "paintzone.s3d");
            await WriteArchiveAsync(
                    archivePath,
                    [new("surface.tga", CreateTarga(32, 32, seed: 61))],
                    cancellationToken)
                .ConfigureAwait(false);
            var enhancedArchive = Path.Combine(root, "enhanced-paintzone.s3d");
            await WriteArchiveAsync(
                    enhancedArchive,
                    [new("surface.tga", CreateTarga(128, 128, seed: 62))],
                    cancellationToken)
                .ConfigureAwait(false);

            var options = new UpscaleOptions(
                TexturePreset.Illustrated,
                AssetScope.WorldOnly,
                MaximumDimension: 1024,
                GenerateMipMaps: false,
                InstallAfterBuild: false,
                PaintedTheme: PaintedTheme.DarkGothic);
            var baseline = await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        paths,
                        options,
                        [
                            new StagedBuildItem(
                                "paintzone.s3d",
                                new DelegateStagedArtifactBuilder(async (context, token) =>
                                {
                                    var bytes = await File.ReadAllBytesAsync(enhancedArchive, token)
                                        .ConfigureAwait(false);
                                    await File.WriteAllBytesAsync(context.DestinationPath, bytes, token)
                                        .ConfigureAwait(false);
                                }))
                        ],
                        "legacy-painted-manual-baseline"),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using (var reportStream = new FileStream(
                Path.Combine(baseline.BuildDirectory, "texture-report.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        reportStream,
                        new TextureBuildReport(
                            TextureBuildReport.CurrentSchemaVersion,
                            baseline.BuildId,
                            DateTimeOffset.UtcNow,
                            installPath,
                            baseline.BuildDirectory,
                            1,
                            new TextureBuildStatistics(
                                1,
                                1,
                                0,
                                1,
                                1,
                                new Dictionary<string, int>(),
                                []))
                        {
                            TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision,
                            PaintedProfileRevision =
                                TextureBuildReport.CurrentIllustratedProfileRevision - 1
                        },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var baselineFingerprint = await FileIntegrity.FingerprintAsync(
                    Path.Combine(baseline.BuildDirectory, "payload", "paintzone.s3d"),
                    cancellationToken)
                .ConfigureAwait(false);
            var failure = await AssertThrowsAsync<InvalidOperationException>(
                    () => new TexturePackWorkflow(clientClosedGuard: () => { })
                        .RepairStagedPackAsync(
                            paths,
                            baseline.ManifestPath,
                            cancellationToken: cancellationToken,
                            textureOverrides:
                            [
                                new TextureOverride(
                                    "paintzone.s3d",
                                    "surface.tga",
                                    TextureOverrideAction.Reprocess,
                                    TexturePreset.Illustrated)
                            ]),
                    "a stale painted pack must reject a mixed-revision manual redo")
                .ConfigureAwait(false);
            Assert(
                failure.Message.Contains("will not mix", StringComparison.OrdinalIgnoreCase),
                "stale painted redo should explain that algorithm revisions cannot be mixed");
            AssertEqual(
                baselineFingerprint,
                await FileIntegrity.FingerprintAsync(
                        Path.Combine(baseline.BuildDirectory, "payload", "paintzone.s3d"),
                        cancellationToken)
                    .ConfigureAwait(false),
                "stale painted redo leaves the immutable baseline unchanged");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestFilteredChangedPaintedMemberFailsClosedAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-painted-filtered-prior-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.s3d");
            var baselinePath = Path.Combine(root, "baseline.s3d");
            var destinationPath = Path.Combine(root, "repaired.s3d");
            var source = CreateTarga(32, 32, seed: 21);
            var priorPainted = CreateTarga(128, 128, seed: 22);
            await WriteArchiveAsync(sourcePath, [new("binside.bmp", source)], cancellationToken)
                .ConfigureAwait(false);
            await WriteArchiveAsync(baselinePath, [new("binside.bmp", priorPainted)], cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var builder = new PfsTextureArchiveBuilder(
                UnavailableProcessor(),
                new TextureBuildCounter(),
                filterCharacterEquipmentEntries: true,
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                requireRequestedVisualProfile: true,
                allowRequestedVisualProfileRegeneration: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["global_chr.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(StringComparer.OrdinalIgnoreCase)
                {
                    ["global_chr.s3d"] = new(fingerprint.Length, fingerprint.Sha256)
                });
            var context = new StagedArtifactBuildContext(
                "global_chr.s3d",
                sourcePath,
                destinationPath,
                Path.Combine(root, "work"),
                Path.Combine(root, "previews"),
                PaintedOptions());

            await AssertThrowsAsync<InvalidDataException>(
                    () => builder.BuildAsync(context, cancellationToken),
                    "a now-filtered changed painted member must not be replaced with source pixels")
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestRequestedProfileFailureStopsRepairAsync(
        PaintedFailure failure,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-painted-repair-{failure}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.s3d");
            var baselinePath = Path.Combine(root, "baseline.s3d");
            var destinationPath = Path.Combine(root, "repaired.s3d");
            var sourceTexture = CreateTarga(32, 32, seed: 11);
            await WriteArchiveAsync(sourcePath, [new("retry.tga", sourceTexture)], cancellationToken)
                .ConfigureAwait(false);
            await WriteArchiveAsync(baselinePath, [new("retry.tga", sourceTexture)], cancellationToken)
                .ConfigureAwait(false);
            var baselineFingerprint = await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var requestsSeen = 0;
            var builder = CreateStrictBuilder(
                baselinePath,
                async (requests, token) =>
                {
                    requestsSeen += requests.Count;
                    AssertEqual(1, requests.Count, $"{failure} request count");
                    AssertEqual(TexturePreset.Illustrated, requests[0].Options.Preset, $"{failure} preset");
                    AssertEqual(PaintedTheme.DarkGothic, requests[0].Options.PaintedTheme, $"{failure} theme");
                    if (failure == PaintedFailure.ProcessingError)
                    {
                        return [new NativeTextureProcessOutcome(null, new NotSupportedException("synthetic painted failure"))];
                    }

                    var output = failure == PaintedFailure.InvalidOutput
                        ? "not-an-image"u8.ToArray()
                        : CreateTarga(128, 128, seed: 37);
                    await File.WriteAllBytesAsync(requests[0].DestinationPath, output, token)
                        .ConfigureAwait(false);
                    var route = failure == PaintedFailure.FaithfulFallback
                        ? "Synthetic faithful fallback"
                        : "Synthetic illustrated dark gothic finishing";
                    return
                    [
                        new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                requests[0].DestinationPath,
                                UpscaleDimensions.Calculate(32, 32, 128),
                                route,
                                TimeSpan.Zero),
                            null)
                    ];
                });

            var failureException = await AssertThrowsAsync<InvalidDataException>(
                    () => builder.BuildAsync(
                        CreateContext(root, sourcePath, destinationPath, forceReprocess: true),
                        cancellationToken),
                    $"painted {failure} must stop repair")
                .ConfigureAwait(false);
            if (requestsSeen != 1)
            {
                throw new InvalidOperationException(
                    $"Self-test failed: {failure} processed request count; expected '1', got '{requestsSeen}'. Inner repair error: {failureException}");
            }
            Assert(!File.Exists(destinationPath), $"{failure} must not publish an archive");
            AssertEqual(
                baselineFingerprint,
                await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken).ConfigureAwait(false),
                $"{failure} baseline immutability");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestInvalidPriorPaintedOutputIsNotRestoredToOriginalAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-painted-invalid-prior-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.s3d");
            var baselinePath = Path.Combine(root, "baseline.s3d");
            var destinationPath = Path.Combine(root, "repaired.s3d");
            await WriteArchiveAsync(
                    sourcePath,
                    [new("painted.tga", CreateTarga(16, 16, seed: 3))],
                    cancellationToken)
                .ConfigureAwait(false);
            // It differs from the original but is not the 64x64 result required by
            // this pack, so a targeted repair must fail rather than substitute source pixels.
            await WriteArchiveAsync(
                    baselinePath,
                    [new("painted.tga", CreateTarga(32, 32, seed: 5))],
                    cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var builder = new PfsTextureArchiveBuilder(
                UnavailableProcessor(),
                new TextureBuildCounter(),
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                requireRequestedVisualProfile: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["painted.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(StringComparer.OrdinalIgnoreCase)
                {
                    ["painted.s3d"] = new(fingerprint.Length, fingerprint.Sha256)
                },
                processBatch: (_, _) => throw new InvalidOperationException("Invalid prior output must fail before AI."));

            await AssertThrowsAsync<InvalidDataException>(
                    () => builder.BuildAsync(CreateContext(root, sourcePath, destinationPath), cancellationToken),
                    "invalid prior painted output must fail closed")
                .ConfigureAwait(false);
            Assert(!File.Exists(destinationPath), "invalid prior painted output must not publish original pixels");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestFailureCannotPublishMixedReplacementAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-painted-atomic-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);
        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var firstSource = Path.Combine(installPath, "first.s3d");
            var secondSource = Path.Combine(installPath, "painted.s3d");
            var baselinePath = Path.Combine(root, "painted-baseline.s3d");
            await File.WriteAllBytesAsync(firstSource, "original-first"u8.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            var sourceTexture = CreateTarga(32, 32, seed: 71);
            await WriteArchiveAsync(secondSource, [new("retry.tga", sourceTexture)], cancellationToken)
                .ConfigureAwait(false);
            await WriteArchiveAsync(baselinePath, [new("retry.tga", sourceTexture)], cancellationToken)
                .ConfigureAwait(false);
            var strictBuilder = CreateStrictBuilder(
                baselinePath,
                (_, _) => Task.FromResult<IReadOnlyList<NativeTextureProcessOutcome>>(
                    [new(null, new NotSupportedException("synthetic second-artifact failure"))]));
            var buildId = "painted-atomic-repair";

            await AssertThrowsAsync<InvalidDataException>(
                    () => new StagedBuildService().BuildAsync(
                        new StagedBuildRequest(
                            paths,
                            PaintedOptions(forceReprocess: true),
                            [
                                new StagedBuildItem(
                                    "first.s3d",
                                    new DelegateStagedArtifactBuilder(async (context, token) =>
                                    {
                                        var bytes = await File.ReadAllBytesAsync(context.SourcePath, token)
                                            .ConfigureAwait(false);
                                        await File.WriteAllBytesAsync(
                                                context.DestinationPath,
                                                bytes.Concat("::painted"u8.ToArray()).ToArray(),
                                                token)
                                            .ConfigureAwait(false);
                                    })),
                                new StagedBuildItem("painted.s3d", strictBuilder)
                            ],
                            buildId,
                            RequireAllItems: true),
                        cancellationToken: cancellationToken),
                    "one failed painted artifact must abort the replacement")
                .ConfigureAwait(false);
            Assert(
                !Directory.Exists(Path.Combine(paths.StagingPath, buildId)),
                "failed repair must remove every partial artifact and never publish a mixed manifest");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task TestOrdinaryCombinedRepairPreservesPaintedIdentityAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-painted-options-repair-{Guid.NewGuid():N}");
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
            await File.WriteAllTextAsync(
                    Path.Combine(installPath, "qeynos.xmi"),
                    "synthetic-zone-marker",
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(installPath, "skyfire.xmi"),
                    "synthetic-kunark-zone-marker",
                    cancellationToken)
                .ConfigureAwait(false);

            string[] archiveNames = ["qeynos.s3d", "qeynos_obj.s3d", "hero_chr.s3d"];
            var enhancedArchives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (archiveName, index) in archiveNames.Select((name, index) => (name, index)))
            {
                var sourceArchive = Path.Combine(installPath, archiveName);
                await WriteArchiveAsync(
                        sourceArchive,
                        [new("surface.tga", CreateTarga(32, 32, checked((byte)(10 + index))))],
                        cancellationToken)
                    .ConfigureAwait(false);
                var enhancedArchive = Path.Combine(root, $"enhanced-{archiveName}");
                await WriteArchiveAsync(
                        enhancedArchive,
                        [new("surface.tga", CreateTarga(128, 128, checked((byte)(40 + index))))],
                        cancellationToken)
                    .ConfigureAwait(false);
                enhancedArchives.Add(archiveName, enhancedArchive);
            }
            string[] unselectedKunarkArchives = ["skyfire.s3d", "skyfire_obj.s3d"];
            foreach (var (archiveName, index) in unselectedKunarkArchives.Select((name, index) => (name, index)))
            {
                await WriteArchiveAsync(
                        Path.Combine(installPath, archiveName),
                        [new("kunark-surface.tga", CreateTarga(32, 32, checked((byte)(80 + index))))],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var paintedOptions = new UpscaleOptions(
                TexturePreset.Illustrated,
                AssetScope.WorldCharactersAndEquipment,
                MaximumDimension: 1024,
                GenerateMipMaps: false,
                InstallAfterBuild: false,
                PaintedTheme: PaintedTheme.DarkGothic,
                WorldExpansions: WorldExpansion.CurrentEql);
            var baseline = await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        paths,
                        paintedOptions,
                        archiveNames.Select(archiveName => new StagedBuildItem(
                                archiveName,
                                new DelegateStagedArtifactBuilder(async (context, token) =>
                                {
                                    var bytes = await File.ReadAllBytesAsync(
                                            enhancedArchives[archiveName],
                                            token)
                                        .ConfigureAwait(false);
                                    await File.WriteAllBytesAsync(context.DestinationPath, bytes, token)
                                        .ConfigureAwait(false);
                                })))
                            .ToArray(),
                        "painted-combined-baseline"),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using (var reportStream = new FileStream(
                Path.Combine(baseline.BuildDirectory, "texture-report.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        reportStream,
                        new TextureBuildReport(
                            TextureBuildReport.CurrentSchemaVersion,
                            baseline.BuildId,
                            DateTimeOffset.UtcNow,
                            installPath,
                            baseline.BuildDirectory,
                            archiveNames.Length,
                            new TextureBuildStatistics(
                                archiveNames.Length,
                                archiveNames.Length,
                                0,
                                archiveNames.Length,
                                archiveNames.Length,
                                new Dictionary<string, int>(),
                                []))
                        {
                            TexturePipelineRevision = 0,
                            PaintedProfileRevision = TextureBuildReport.CurrentIllustratedProfileRevision,
                            UsedExternalArtisticWorker = false,
                            PaintedRendererOutcome = PaintedRendererOutcome.BuiltInOnly
                        },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            var baselineFingerprints = baseline.Manifest.Entries.ToDictionary(
                entry => entry.RelativeInstallPath,
                entry => new FileFingerprint(entry.StagedLength, entry.StagedSha256),
                StringComparer.OrdinalIgnoreCase);

            var repaired = await new TexturePackWorkflow(clientClosedGuard: () => { })
                .RepairStagedPackAsync(
                    paths,
                    baseline.ManifestPath,
                    retryPreset: TexturePreset.Faithful,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            AssertEqual(
                paintedOptions,
                repaired.StagedBuild.Manifest.Options,
                "ordinary combined repair must ignore a Faithful retry request and retain painted options");
            AssertEqual(TexturePreset.Illustrated, repaired.StagedBuild.Manifest.Options.Preset, "repaired preset");
            AssertEqual(PaintedTheme.DarkGothic, repaired.StagedBuild.Manifest.Options.PaintedTheme, "repaired theme");
            AssertEqual(
                WorldExpansion.CurrentEql,
                repaired.StagedBuild.Manifest.Options.WorldExpansions!.Value,
                "ordinary repair must preserve the explicit Current EQL World selection");
            Assert(
                repaired.StagedBuild.Manifest.Entries.All(entry =>
                    !unselectedKunarkArchives.Contains(
                        entry.RelativeInstallPath,
                        StringComparer.OrdinalIgnoreCase)),
                "ordinary Current EQL repair must never gain detected Kunark Skyfire archives");
            AssertEqual(archiveNames.Length, repaired.Report.Statistics.ReusedTextures, "painted member reuse count");
            AssertEqual(0, repaired.Report.Statistics.EnhancedTextures, "valid painted members must not be regenerated");
            AssertEqual(0, repaired.Report.Statistics.FallbackTextures, "painted repair fallback count");
            AssertEqual(
                TextureBuildReport.CurrentIllustratedProfileRevision,
                repaired.Report.PaintedProfileRevision,
                "ordinary repair must retain painted profile provenance");
            foreach (var archiveName in archiveNames)
            {
                var baselinePayload = Path.Combine(baseline.BuildDirectory, "payload", archiveName);
                var repairedPayload = Path.Combine(repaired.StagedBuild.BuildDirectory, "payload", archiveName);
                AssertEqual(
                    baselineFingerprints[archiveName],
                    await FileIntegrity.FingerprintAsync(baselinePayload, cancellationToken).ConfigureAwait(false),
                    $"immutable baseline archive {archiveName}");
                await using var baselineArchive = await PfsArchive.OpenAsync(
                    baselinePayload,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await using var repairedArchive = await PfsArchive.OpenAsync(
                    repairedPayload,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                AssertSequenceEqual(
                    await baselineArchive.ReadEntryAsync("surface.tga", cancellationToken).ConfigureAwait(false),
                    await repairedArchive.ReadEntryAsync("surface.tga", cancellationToken).ConfigureAwait(false),
                    $"valid painted texture bytes for {archiveName}");
            }
        }
        finally
        {
            DeleteTree(root);
        }
    }

    /// <summary>
    /// A member the completed pack already ships as its byte-identical
    /// original (previously preserved) is merely re-attempted by the
    /// expanded-coverage retry. If that attempt fails, keeping the original
    /// is the status quo — the strict painted repair must continue instead
    /// of aborting the whole pack over a texture that was never painted.
    /// </summary>
    private static async Task TestRetriedPreservedFailureKeepsOriginalAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-painted-retry-preserved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.s3d");
            var baselinePath = Path.Combine(root, "baseline.s3d");
            var destinationPath = Path.Combine(root, "repaired.s3d");
            var originalTexture = CreateTarga(32, 32, seed: 11);
            await WriteArchiveAsync(sourcePath, [new("retry.tga", originalTexture)], cancellationToken)
                .ConfigureAwait(false);
            await WriteArchiveAsync(baselinePath, [new("retry.tga", originalTexture)], cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var builder = new PfsTextureArchiveBuilder(
                UnavailableProcessor(),
                new TextureBuildCounter(),
                retryUnchangedEntries: true,
                rebuildFromReuseArchive: true,
                requireRequestedVisualProfile: true,
                allowRequestedVisualProfileRegeneration: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["painted.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(StringComparer.OrdinalIgnoreCase)
                {
                    ["painted.s3d"] = new(fingerprint.Length, fingerprint.Sha256)
                },
                processBatch: (requests, _) =>
                {
                    AssertEqual(1, requests.Count, "retried preserved request count");
                    return Task.FromResult<IReadOnlyList<NativeTextureProcessOutcome>>(
                    [
                        new NativeTextureProcessOutcome(
                            null,
                            new NotSupportedException("synthetic painted failure"))
                    ]);
                });

            await builder.BuildAsync(
                    CreateContext(root, sourcePath, destinationPath),
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(
                File.Exists(destinationPath),
                "retried preserved failure still publishes the repaired archive");
            await using var repairedArchive = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                originalTexture,
                await repairedArchive.ReadEntryAsync("retry.tga", cancellationToken).ConfigureAwait(false),
                "retried preserved member keeps its original bytes");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    /// <summary>
    /// A coverage-rule addition starts from an exact verified original rather
    /// than an older painted artifact. One rejected member may stay original
    /// while another member is painted successfully; strict route validation
    /// still prevents accepting a different renderer as the replacement.
    /// </summary>
    private static async Task TestVerifiedOriginalAdditionFailureKeepsOriginalAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-painted-verified-addition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.s3d");
            var destinationPath = Path.Combine(root, "addition.s3d");
            var rejectedOriginal = CreateTarga(32, 32, seed: 51);
            var paintableOriginal = CreateTarga(48, 48, seed: 63);
            await WriteArchiveAsync(
                    sourcePath,
                    [
                        new("rejected.tga", rejectedOriginal),
                        new("paintable.tga", paintableOriginal)
                    ],
                    cancellationToken)
                .ConfigureAwait(false);

            var rejectedUnsafeBaselineConfiguration = false;
            try
            {
                _ = new PfsTextureArchiveBuilder(
                    UnavailableProcessor(),
                    new TextureBuildCounter(),
                    rebuildFromReuseArchive: true,
                    requireRequestedVisualProfile: true,
                    allowRequestedProfileFailureToKeepOriginal: true);
            }
            catch (ArgumentException)
            {
                rejectedUnsafeBaselineConfiguration = true;
            }
            Assert(
                rejectedUnsafeBaselineConfiguration,
                "verified-original failure retention cannot weaken a baseline-reuse builder");

            byte[]? painted = null;
            var builder = new PfsTextureArchiveBuilder(
                UnavailableProcessor(),
                new TextureBuildCounter(),
                requireRequestedVisualProfile: true,
                allowRequestedVisualProfileRegeneration: true,
                allowRequestedProfileFailureToKeepOriginal: true,
                processBatch: async (requests, token) =>
                {
                    AssertEqual(2, requests.Count, "verified-addition painted request count");
                    var outcomes = new NativeTextureProcessOutcome[requests.Count];
                    for (var index = 0; index < requests.Count; index++)
                    {
                        var request = requests[index];
                        if (string.Equals(
                                request.LogicalName,
                                "rejected.tga",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            outcomes[index] = new NativeTextureProcessOutcome(
                                null,
                                new NotSupportedException("synthetic fidelity rejection"));
                            continue;
                        }

                        var dimensions = UpscaleDimensions.Calculate(
                            request.Metadata.Width,
                            request.Metadata.Height,
                            request.Options.MaximumDimension);
                        painted = CreateTarga(
                            dimensions.OutputWidth,
                            dimensions.OutputHeight,
                            seed: 79);
                        await File.WriteAllBytesAsync(
                                request.DestinationPath,
                                painted,
                                token)
                            .ConfigureAwait(false);
                        outcomes[index] = new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                request.DestinationPath,
                                dimensions,
                                "Synthetic illustrated dark gothic finishing",
                                TimeSpan.Zero,
                                UsedBuiltInPaintedRenderer: true),
                            null);
                    }

                    return outcomes;
                });

            await builder.BuildAsync(
                    CreateContext(root, sourcePath, destinationPath),
                    cancellationToken)
                .ConfigureAwait(false);
            await using var result = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                rejectedOriginal,
                await result.ReadEntryAsync("rejected.tga", cancellationToken)
                    .ConfigureAwait(false),
                "a rejected verified-original addition member remains byte-identical");
            AssertSequenceEqual(
                painted ?? throw new InvalidOperationException("painted fixture was not produced"),
                await result.ReadEntryAsync("paintable.tga", cancellationToken)
                    .ConfigureAwait(false),
                "a successful member still makes the verified-original archive a real addition");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    /// <summary>
    /// Real client archives were compressed by the game's own packer, so a
    /// preserved member's stored deflate stream rarely matches this writer's.
    /// A repair that keeps such a member original must still validate and
    /// publish: the decoded member bytes are the safety invariant, not the
    /// compressed stream that carries them.
    /// </summary>
    private static async Task TestRetriedPreservedFailureSurvivesForeignCompressionAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-painted-foreign-stored-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.s3d");
            var baselinePath = Path.Combine(root, "baseline.s3d");
            var destinationPath = Path.Combine(root, "repaired.s3d");
            var preservedTexture = CreateTarga(32, 32, seed: 11);
            var paintableTexture = CreateTarga(48, 48, seed: 23);
            // A smaller chunk size stands in for the game packer's different
            // deflate output: identical member bytes, different stored bytes.
            await WriteArchiveAsync(
                    sourcePath,
                    [new("retry.tga", preservedTexture), new("wall.tga", paintableTexture)],
                    cancellationToken,
                    new PfsArchiveWriteOptions { ChunkSize = 1024 })
                .ConfigureAwait(false);
            await WriteArchiveAsync(
                    baselinePath,
                    [new("retry.tga", preservedTexture), new("wall.tga", paintableTexture)],
                    cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var paintedDimensions = UpscaleDimensions.Calculate(48, 48, 128);
            var paintedTexture = CreateTarga(
                paintedDimensions.OutputWidth,
                paintedDimensions.OutputHeight,
                seed: 41);
            var builder = new PfsTextureArchiveBuilder(
                UnavailableProcessor(),
                new TextureBuildCounter(),
                retryUnchangedEntries: true,
                rebuildFromReuseArchive: true,
                requireRequestedVisualProfile: true,
                allowRequestedVisualProfileRegeneration: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["painted.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(StringComparer.OrdinalIgnoreCase)
                {
                    ["painted.s3d"] = new(fingerprint.Length, fingerprint.Sha256)
                },
                processBatch: async (requests, token) =>
                {
                    AssertEqual(2, requests.Count, "foreign-compression retry request count");
                    var outcomes = new NativeTextureProcessOutcome[requests.Count];
                    for (var index = 0; index < requests.Count; index++)
                    {
                        var request = requests[index];
                        if (request.Metadata.Width == 32)
                        {
                            outcomes[index] = new NativeTextureProcessOutcome(
                                null,
                                new NotSupportedException("synthetic painted failure"));
                            continue;
                        }

                        await File.WriteAllBytesAsync(request.DestinationPath, paintedTexture, token)
                            .ConfigureAwait(false);
                        outcomes[index] = new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                request.DestinationPath,
                                paintedDimensions,
                                "Synthetic illustrated dark gothic finishing",
                                TimeSpan.Zero),
                            null);
                    }

                    return outcomes;
                });

            await builder.BuildAsync(
                    CreateContext(root, sourcePath, destinationPath),
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(
                File.Exists(destinationPath),
                "foreign-compression repair publishes the repaired archive");
            await using var repairedArchive = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                preservedTexture,
                await repairedArchive.ReadEntryAsync("retry.tga", cancellationToken).ConfigureAwait(false),
                "foreign-compression preserved member keeps its original bytes");
            AssertSequenceEqual(
                paintedTexture,
                await repairedArchive.ReadEntryAsync("wall.tga", cancellationToken).ConfigureAwait(false),
                "foreign-compression painted member ships the regenerated bytes");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static PfsTextureArchiveBuilder CreateStrictBuilder(
        string baselinePath,
        Func<IReadOnlyList<NativeTextureProcessRequest>, CancellationToken,
            Task<IReadOnlyList<NativeTextureProcessOutcome>>> processBatch) =>
        new(
            UnavailableProcessor(),
            new TextureBuildCounter(),
            requireRequestedVisualProfile: true,
            allowRequestedVisualProfileRegeneration: true,
            reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["painted.s3d"] = baselinePath
            },
            processBatch: processBatch);

    private static NativeTextureProcessor UnavailableProcessor() => new(
        new ExternalToolPaths(null, null, null, null, null, []));

    private static StagedArtifactBuildContext CreateContext(
        string root,
        string sourcePath,
        string destinationPath,
        bool forceReprocess = false) => new(
        "painted.s3d",
        sourcePath,
        destinationPath,
        Path.Combine(root, "work", Guid.NewGuid().ToString("N")),
        Path.Combine(root, "previews"),
        PaintedOptions(forceReprocess));

    private static UpscaleOptions PaintedOptions(bool forceReprocess = false) => new(
        TexturePreset.Illustrated,
        AssetScope.WorldCharactersAndEquipment,
        MaximumDimension: 128,
        GenerateMipMaps: false,
        InstallAfterBuild: false,
        TextureOverrides: forceReprocess
            ?
            [
                new TextureOverride(
                    "painted.s3d",
                    "retry.tga",
                    TextureOverrideAction.Reprocess,
                    TexturePreset.Illustrated)
            ]
            : null,
        PaintedTheme: PaintedTheme.DarkGothic);

    private static async Task WriteArchiveAsync(
        string path,
        IReadOnlyList<PfsArchiveItem> entries,
        CancellationToken cancellationToken,
        PfsArchiveWriteOptions? options = null)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await PfsArchiveWriter.WriteAsync(stream, entries, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteReportAsync(
        string path,
        TextureBuildReport report,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await JsonSerializer.SerializeAsync(
                stream,
                report,
                options,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] CreateTarga(int width, int height, byte seed)
    {
        var bytes = new byte[18 + checked(width * height * 4)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), checked((ushort)height));
        bytes[16] = 32;
        bytes[17] = 8;
        var state = (uint)seed + 1;
        for (var offset = 18; offset < bytes.Length; offset += 4)
        {
            state = state * 1_664_525 + 1_013_904_223;
            bytes[offset] = (byte)(state >> 24);
            bytes[offset + 1] = (byte)(state >> 16);
            bytes[offset + 2] = (byte)(state >> 8);
            bytes[offset + 3] = byte.MaxValue;
        }

        return bytes;
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action, string description)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Self-test failed: {description}; expected {typeof(TException).Name}.");
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

    private static void AssertSequenceEqual(byte[] expected, byte[] actual, string description)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private enum PaintedFailure
    {
        ProcessingError,
        FaithfulFallback,
        InvalidOutput
    }
}
