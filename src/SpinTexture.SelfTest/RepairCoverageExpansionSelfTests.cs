using System.Buffers.Binary;
using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class RepairCoverageExpansionSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        const int cap = 1024;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-repair-omitted-archive-{Guid.NewGuid():N}");
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

            var baselineSourceTexture = CreateTarga(16, 16, seed: 19);
            var baselineEnhancedTexture = CreateTarga(64, 64, seed: 37);
            var omittedOgreArmor = CreateTarga(cap, cap, seed: 71);
            var baselineSourcePath = Path.Combine(installPath, "globalogf_chr.s3d");
            var omittedSourcePath = Path.Combine(installPath, "globalogm_chr.s3d");
            await WriteArchiveAsync(
                baselineSourcePath,
                [new PfsArchiveItem("ogfbody01.tga", baselineSourceTexture)],
                cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(
                omittedSourcePath,
                [new PfsArchiveItem("ogmhesk11.tga", omittedOgreArmor)],
                cancellationToken).ConfigureAwait(false);

            var omittedSourceBefore = await FileIntegrity.FingerprintAsync(
                    omittedSourcePath,
                    cancellationToken)
                .ConfigureAwait(false);
            var options = new UpscaleOptions(
                TexturePreset.RusticPainted,
                AssetScope.CharactersAndEquipmentOnly,
                cap,
                GenerateMipMaps: true,
                InstallAfterBuild: false);
            var baseline = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    options,
                    [
                        new StagedBuildItem(
                            "globalogf_chr.s3d",
                            new DelegateStagedArtifactBuilder(
                                async (context, token) =>
                                {
                                    await WriteArchiveAsync(
                                        context.DestinationPath,
                                        [new PfsArchiveItem(
                                            "ogfbody01.tga",
                                            baselineEnhancedTexture)],
                                        token).ConfigureAwait(false);
                                }))
                    ],
                    BuildId: "build-omitted-ogre-baseline"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var baselineReport = new TextureBuildReport(
                TextureBuildReport.CurrentSchemaVersion,
                baseline.BuildId,
                DateTimeOffset.UtcNow,
                installPath,
                baseline.BuildDirectory,
                SelectedArchives: 2,
                new TextureBuildStatistics(
                    DiscoveredTextures: 2,
                    EnhancedTextures: 1,
                    PreservedTextures: 1,
                    SourceTextureBytes:
                        baselineSourceTexture.LongLength + omittedOgreArmor.LongLength,
                    EnhancedTextureBytes: baselineEnhancedTexture.LongLength,
                    new Dictionary<string, int>(),
                    []))
            {
                TexturePipelineRevision = 9,
                PaintedProfileRevision = TextureBuildReport.CurrentRusticPaintedProfileRevision,
                UsedExternalArtisticWorker = false,
                PaintedRendererOutcome = PaintedRendererOutcome.BuiltInOnly,
                AppliedRepairRuleIds = TextureProcessingPipeline
                    .GetCurrentRepairRuleIds(
                        options.Scope,
                        ["globalogf_chr.s3d"],
                        options.Preset)
                    .Where(rule => !rule.Equals(
                            TextureProcessingPipeline.LegacyMaterialClassificationRuleId,
                            StringComparison.Ordinal)
                        && !rule.Equals(
                            TextureProcessingPipeline.PaintedAtCapRepaintRuleId,
                            StringComparison.Ordinal))
                    .ToArray()
            };
            await WriteJsonAsync(
                Path.Combine(baseline.BuildDirectory, "texture-report.json"),
                baselineReport,
                cancellationToken).ConfigureAwait(false);

            var baselinePayloadPath = Path.Combine(
                baseline.BuildDirectory,
                "payload",
                "globalogf_chr.s3d");
            var baselinePayloadBefore = await FileIntegrity.FingerprintAsync(
                    baselinePayloadPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var repaired = await new TexturePackWorkflow(clientClosedGuard: () => { })
                .RepairStagedPackAsync(
                    paths,
                    baseline.ManifestPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var repairedNames = repaired.StagedBuild.Manifest.Entries
                .Select(entry => entry.RelativeInstallPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert(
                repairedNames.SetEquals(
                    ["globalogf_chr.s3d", "globalogm_chr.s3d"]),
                "v10 repair retains the baseline archive and adds the formerly omitted Ogre archive");
            AssertEqual(
                baselinePayloadBefore,
                await FileIntegrity.FingerprintAsync(
                    baselinePayloadPath,
                    cancellationToken).ConfigureAwait(false),
                "coverage expansion repair leaves the immutable baseline payload unchanged");
            AssertEqual(
                omittedSourceBefore,
                await FileIntegrity.FingerprintAsync(
                    omittedSourcePath,
                    cancellationToken).ConfigureAwait(false),
                "coverage expansion repair never mutates the live omitted archive");

            var repairedOgrePath = Path.Combine(
                repaired.StagedBuild.BuildDirectory,
                "payload",
                "globalogm_chr.s3d");
            await using var repairedOgre = await PfsArchive.OpenAsync(
                repairedOgrePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var repairedArmor = await repairedOgre.ReadEntryAsync(
                "ogmhesk11.tga",
                cancellationToken).ConfigureAwait(false);
            Assert(
                !omittedOgreArmor.AsSpan().SequenceEqual(repairedArmor),
                "the omitted at-cap Ogre armor receives a real same-size painted treatment");
            Assert(
                repaired.Report.Statistics.EnhancedTextures >= 1
                && repaired.Report.Statistics.ReusedTextures >= 1,
                "repair reports both newly enhanced omitted work and retained baseline work");
            Assert(
                repaired.Report.Statistics.Warnings.Any(warning =>
                    warning.Contains("omitted entirely", StringComparison.OrdinalIgnoreCase)
                    && warning.Contains("1 produced", StringComparison.OrdinalIgnoreCase)),
                "repair report states how many omitted archives produced committed additions");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WriteArchiveAsync(
        string path,
        IReadOnlyList<PfsArchiveItem> items,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        await PfsArchiveWriter.WriteAsync(
            stream,
            items,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateTarga(int width, int height, byte seed)
    {
        var bytes = new byte[18 + checked(width * height * 4)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(12, 2),
            checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(14, 2),
            checked((ushort)height));
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
}
