using System.Buffers.Binary;
using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class WorldCutoutRepairSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-world-cutout-repair-{Guid.NewGuid():N}");
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
                    Path.Combine(installPath, "forest.xmi"),
                    "synthetic-zone-marker",
                    cancellationToken)
                .ConfigureAwait(false);

            var originalRock = CreateTarga(16, 16, seed: 17);
            var enhancedRock = CreateTarga(64, 64, seed: 29);
            var unchangedGround = CreateTarga(16, 16, seed: 43);
            var unchangedTree = CreateTarga(16, 16, seed: 57);
            var enhancedTree = CreateTarga(64, 64, seed: 71);
            var originalArchivePath = Path.Combine(installPath, "forest.s3d");
            await WriteArchiveAsync(
                    originalArchivePath,
                    [
                        new PfsArchiveItem("rock01.tga", originalRock),
                        new PfsArchiveItem("ground01.tga", unchangedGround)
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            var originalObjectArchivePath = Path.Combine(installPath, "forest_obj.s3d");
            await WriteArchiveAsync(
                    originalObjectArchivePath,
                    [new PfsArchiveItem("tree01.tga", unchangedTree)],
                    cancellationToken)
                .ConfigureAwait(false);

            var candidateArchivePath = Path.Combine(root, "candidate-forest.s3d");
            await WriteArchiveAsync(
                    candidateArchivePath,
                    [
                        new PfsArchiveItem("rock01.tga", enhancedRock),
                        new PfsArchiveItem("ground01.tga", unchangedGround)
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            var candidateBytes = await File.ReadAllBytesAsync(
                    candidateArchivePath,
                    cancellationToken)
                .ConfigureAwait(false);
            var candidateObjectArchivePath = Path.Combine(root, "candidate-forest_obj.s3d");
            await WriteArchiveAsync(
                    candidateObjectArchivePath,
                    [new PfsArchiveItem("tree01.tga", enhancedTree)],
                    cancellationToken)
                .ConfigureAwait(false);
            var untouchedObjectArchiveBytes = await File.ReadAllBytesAsync(
                    candidateObjectArchivePath,
                    cancellationToken)
                .ConfigureAwait(false);

            var options = new UpscaleOptions(
                TexturePreset.MaximumDetail,
                AssetScope.SelectedZone,
                MaximumDimension: 1024,
                GenerateMipMaps: true,
                InstallAfterBuild: false,
                SelectedZone: "forest");
            var baseline = await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        paths,
                        options,
                        [
                            new StagedBuildItem(
                                "forest.s3d",
                                new DelegateStagedArtifactBuilder(
                                    (context, token) => File.WriteAllBytesAsync(
                                        context.DestinationPath,
                                        candidateBytes,
                                        token))),
                            new StagedBuildItem(
                                "forest_obj.s3d",
                                new DelegateStagedArtifactBuilder(
                                    (context, token) => File.WriteAllBytesAsync(
                                        context.DestinationPath,
                                        untouchedObjectArchiveBytes,
                                        token)))
                        ],
                        BuildId: "build-world-revision-1"),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var baselineReport = new TextureBuildReport(
                TextureBuildReport.CurrentSchemaVersion,
                baseline.BuildId,
                DateTimeOffset.UtcNow,
                installPath,
                baseline.BuildDirectory,
                SelectedArchives: 2,
                new TextureBuildStatistics(
                    DiscoveredTextures: 3,
                    EnhancedTextures: 2,
                    PreservedTextures: 1,
                    SourceTextureBytes:
                        originalRock.LongLength +
                        unchangedGround.LongLength +
                        unchangedTree.LongLength,
                    EnhancedTextureBytes: enhancedRock.LongLength + enhancedTree.LongLength,
                    new Dictionary<string, int>(),
                    []))
            {
                TexturePipelineRevision = 1,
                PaintedProfileRevision = TextureBuildReport.CurrentIllustratedProfileRevision
            };
            await WriteJsonAsync(
                    Path.Combine(baseline.BuildDirectory, "texture-report.json"),
                    baselineReport,
                    cancellationToken)
                .ConfigureAwait(false);

            var baselinePayloadPath = Path.Combine(
                baseline.BuildDirectory,
                "payload",
                "forest.s3d");
            var baselinePayloadBefore = await FileIntegrity
                .FingerprintAsync(baselinePayloadPath, cancellationToken)
                .ConfigureAwait(false);
            var baselineUntouchedPayloadPath = Path.Combine(
                baseline.BuildDirectory,
                "payload",
                "forest_obj.s3d");
            var baselineUntouchedPayloadBefore = await FileIntegrity
                .FingerprintAsync(baselineUntouchedPayloadPath, cancellationToken)
                .ConfigureAwait(false);
            var baselineManifestBefore = await FileIntegrity
                .FingerprintAsync(baseline.ManifestPath, cancellationToken)
                .ConfigureAwait(false);

            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            await transactions.ApplyAsync(
                    paths,
                    baseline.ManifestPath,
                    cancellationToken: cancellationToken,
                    applyId: "apply-world-revision-1")
                .ConfigureAwait(false);
            var liveEnhancedBeforeRepair = await FileIntegrity
                .FingerprintAsync(originalArchivePath, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                baselinePayloadBefore,
                liveEnhancedBeforeRepair,
                "synthetic baseline should be active before repair");

            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: new InstallHealthService(store),
                stagedPackCatalogService: new StagedPackCatalogService(store));
            var repaired = await workflow.RepairStagedPackAsync(
                    paths,
                    baseline.ManifestPath,
                    TexturePreset.Faithful,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            Assert(
                !PathGuard.SamePath(
                    baseline.BuildDirectory,
                    repaired.StagedBuild.BuildDirectory),
                "revision repair must create a new immutable build directory");
            AssertEqual(2, repaired.StagedBuild.Manifest.Entries.Count, "full baseline artifact count");
            var baselineSourceLineage = baseline.Manifest.Entries.ToDictionary(
                entry => entry.RelativeInstallPath,
                entry => entry.SourceSha256,
                StringComparer.OrdinalIgnoreCase);
            foreach (var repairedEntry in repaired.StagedBuild.Manifest.Entries)
            {
                AssertEqual(
                    baselineSourceLineage[repairedEntry.RelativeInstallPath],
                    repairedEntry.SourceSha256,
                    $"replacement source lineage for {repairedEntry.RelativeInstallPath}");
            }

            var repairedUntouchedPayloadPath = Path.Combine(
                repaired.StagedBuild.BuildDirectory,
                "payload",
                "forest_obj.s3d");
            AssertEqual(
                baselineUntouchedPayloadBefore,
                await FileIntegrity.FingerprintAsync(
                        repairedUntouchedPayloadPath,
                        cancellationToken)
                    .ConfigureAwait(false),
                "untouched baseline archive payload must remain exact in replacement");
            AssertEqual(
                options.Preset,
                repaired.StagedBuild.Manifest.Options.Preset,
                "revision repair must preserve the baseline visual preset");
            Assert(repaired.Report.IsIncrementalRepair, "revision repair report kind");
            Assert(repaired.Report.IsCutoutMipRepair, "cutout revision provenance flag");
            AssertEqual(baseline.BuildId, repaired.Report.BaselineBuildId!, "baseline build provenance");
            AssertEqual(1, repaired.Report.BaselineTexturePipelineRevision, "baseline pipeline revision");
            AssertEqual(
                TextureProcessingPipeline.CurrentRevision,
                repaired.Report.TexturePipelineRevision,
                "replacement pipeline revision");
            AssertEqual(
                TextureBuildReport.CurrentIllustratedProfileRevision,
                repaired.Report.PaintedProfileRevision,
                "safety repair must preserve baseline visual-profile provenance");
            AssertEqual(2, repaired.Report.Statistics.ReusedTextures, "opaque prior enhancement reuse count");
            AssertEqual(0, repaired.Report.Statistics.EnhancedTextures, "no stale cutout in synthetic opaque fixture");

            AssertEqual(
                baselinePayloadBefore,
                await FileIntegrity.FingerprintAsync(baselinePayloadPath, cancellationToken)
                    .ConfigureAwait(false),
                "repair must not mutate baseline payload");
            AssertEqual(
                baselineUntouchedPayloadBefore,
                await FileIntegrity.FingerprintAsync(
                        baselineUntouchedPayloadPath,
                        cancellationToken)
                    .ConfigureAwait(false),
                "repair must not mutate untouched baseline archive payload");
            AssertEqual(
                baselineManifestBefore,
                await FileIntegrity.FingerprintAsync(baseline.ManifestPath, cancellationToken)
                    .ConfigureAwait(false),
                "repair must not mutate baseline manifest");
            AssertEqual(
                liveEnhancedBeforeRepair,
                await FileIntegrity.FingerprintAsync(originalArchivePath, cancellationToken)
                    .ConfigureAwait(false),
                "staging repair must not modify the active install");

            var inspected = await new StagedPackCatalogService(store).InspectAsync(
                    paths,
                    repaired.StagedBuild.ManifestPath,
                    StagedPackVerificationMode.Exact,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(inspected.IsReady, "replacement must remain exact-verifiable and install-compatible");

            await workflow.RestoreLatestAsync(
                    paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [repaired.StagedBuild.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var active = await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(InstallHealthState.EnhancedActive, active.State, "replacement install health");
            var activeManifest = await store.ReadInstallManifestAsync(
                    active.InstallManifestPath!,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                repaired.StagedBuild.BuildId,
                activeManifest.BuildId,
                "replacement pack should install through normal selection/composition machinery");

            // Exact verification is a precondition, not merely catalog metadata.
            // Corrupting the old baseline must fail before another build appears.
            var stagingCountBeforeCorruptionAttempt = Directory
                .EnumerateDirectories(paths.StagingPath)
                .Count();
            await File.AppendAllTextAsync(
                    baselinePayloadPath,
                    "corrupt",
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertThrowsAsync<InvalidDataException>(
                    () => workflow.RepairStagedPackAsync(
                        paths,
                        baseline.ManifestPath,
                        cancellationToken: cancellationToken),
                    "corrupt baseline payload must block revision repair")
                .ConfigureAwait(false);
            AssertEqual(
                stagingCountBeforeCorruptionAttempt,
                Directory.EnumerateDirectories(paths.StagingPath).Count(),
                "failed exact verification must not leave a partial replacement build");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task WriteArchiveAsync(
        string path,
        IReadOnlyList<PfsArchiveItem> items,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        await PfsArchiveWriter.WriteAsync(
                stream,
                items,
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
            state = (state * 1_664_525) + 1_013_904_223;
            bytes[offset] = checked((byte)(state >> 24));
            bytes[offset + 1] = checked((byte)((state >> 16) & 0xFF));
            bytes[offset + 2] = checked((byte)((state >> 8) & 0xFF));
            bytes[offset + 3] = byte.MaxValue;
        }

        return bytes;
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
                stream,
                value,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                },
                cancellationToken)
            .ConfigureAwait(false);
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

    private static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temporary test cleanup is best effort.
        }
    }
}
