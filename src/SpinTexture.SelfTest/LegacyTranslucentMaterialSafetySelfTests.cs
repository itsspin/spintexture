using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

internal static class LegacyTranslucentMaterialSafetySelfTests
{
    private static readonly byte[] StringKey =
        [0x95, 0x3A, 0xC5, 0x2A, 0x95, 0x7A, 0x95, 0x6A];

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        TestWldMaterialGraphDetection();
        await TestPfsRepairRestoresWaterAndCarriesUnrelatedTextureAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TestWldMaterialGraphDetection()
    {
        var payload = CreateLegacyWld(["ow1.bmp", "ow2.bmp", "ow3.bmp", "ow4.bmp"]);
        var protectedNames = LegacyTranslucentMaterialSafetyPolicy
            .FindProtectedTextureNames(payload);

        AssertEqual(4, protectedNames.Count, "all animated translucent frames");
        foreach (var expected in new[] { "ow1.bmp", "ow2.bmp", "ow3.bmp", "ow4.bmp" })
        {
            AssertTrue(protectedNames.Contains(expected), $"protected {expected}");
        }

        AssertEqual(
            0,
            LegacyTranslucentMaterialSafetyPolicy
                .FindProtectedTextureNames("not-a-wld"u8).Count,
            "invalid WLD fails closed");

        var unsupportedVersion = payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(unsupportedVersion.AsSpan(4, 4), 0x1000C800);
        AssertEqual(
            0,
            LegacyTranslucentMaterialSafetyPolicy
                .FindProtectedTextureNames(unsupportedVersion).Count,
            "unsupported WLD layout is not guessed");

        var sentinelTerminated = payload.Concat(
            new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }).ToArray();
        AssertEqual(
            4,
            LegacyTranslucentMaterialSafetyPolicy
                .FindProtectedTextureNames(sentinelTerminated).Count,
            "real-client WLD end sentinel is accepted");

        // Masked (palette color-key) materials are a separate contract: they
        // are enhanced with an enforced key, not preserved as originals.
        AssertEqual(
            0,
            LegacyTranslucentMaterialSafetyPolicy.FindMaskedTextureNames(payload).Count,
            "blended materials are not reported as masked");
        var maskedPayload = CreateLegacyWld(["blade.bmp"], materialParameters: 0x80000013);
        AssertTrue(
            LegacyTranslucentMaterialSafetyPolicy.FindMaskedTextureNames(maskedPayload)
                .Contains("blade.bmp"),
            "masked materials report their bitmaps for color-key enforcement");
        AssertEqual(
            0,
            LegacyTranslucentMaterialSafetyPolicy.FindProtectedTextureNames(maskedPayload).Count,
            "masked-only materials are not preserved as translucent originals");
        var maskedBlended = CreateLegacyWld(["glasspane.bmp"], materialParameters: 0x80000017);
        AssertTrue(
            LegacyTranslucentMaterialSafetyPolicy.FindProtectedTextureNames(maskedBlended)
                .Contains("glasspane.bmp")
            && LegacyTranslucentMaterialSafetyPolicy.FindMaskedTextureNames(maskedBlended).Count == 0,
            "masked-and-blended materials stay under whole-original preservation");
    }

    private static async Task TestPfsRepairRestoresWaterAndCarriesUnrelatedTextureAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-translucent-material-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        var sourcePath = Path.Combine(installPath, "freporte.s3d");
        var baselinePath = Path.Combine(root, "baseline.s3d");
        var destinationPath = Path.Combine(root, "repaired.s3d");
        Directory.CreateDirectory(installPath);

        try
        {
            var wld = CreateLegacyWld(["ow1.dds", "ow2.dds"]);
            var sourceWater1 = CreateDxt1(16, 16, mipCount: 1);
            var sourceWater2 = CreateDxt1(16, 16, mipCount: 1);
            sourceWater2[^1] ^= 0x11;
            var enlargedWater1 = CreateDxt1(64, 64, mipCount: 7);
            var enlargedWater2 = CreateDxt1(64, 64, mipCount: 7);
            enlargedWater2[^1] ^= 0x22;
            var sourceRock = CreateDxt1(16, 16, mipCount: 1);
            var enhancedRock = CreateDxt1(64, 64, mipCount: 7);

            await WriteArchiveAsync(
                sourcePath,
                [
                    new PfsArchiveItem("freporte.wld", wld),
                    new PfsArchiveItem("ow1.dds", sourceWater1),
                    new PfsArchiveItem("ow2.dds", sourceWater2),
                    new PfsArchiveItem("rock.dds", sourceRock)
                ],
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(
                baselinePath,
                [
                    new PfsArchiveItem("freporte.wld", wld),
                    new PfsArchiveItem("ow1.dds", enlargedWater1),
                    new PfsArchiveItem("ow2.dds", enlargedWater2),
                    new PfsArchiveItem("rock.dds", enhancedRock)
                ],
                cancellationToken).ConfigureAwait(false);

            var baselineFingerprint = await FileIntegrity
                .FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var builder = new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(new ExternalToolPaths(null, null, null, null, null, [])),
                new TextureBuildCounter(),
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["freporte.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["freporte.s3d"] = new(
                        baselineFingerprint.Length,
                        baselineFingerprint.Sha256)
                },
                processBatch: (_, _) => throw new InvalidOperationException(
                    "Protected water and valid carried rock must not enter AI processing."));

            await builder.BuildAsync(
                new StagedArtifactBuildContext(
                    "freporte.s3d",
                    sourcePath,
                    destinationPath,
                    Path.Combine(root, "work"),
                    Path.Combine(root, "previews"),
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.WorldOnly,
                        MaximumDimension: 64,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false)),
                cancellationToken).ConfigureAwait(false);

            await using var repaired = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertBytesEqual(
                sourceWater1,
                await repaired.ReadEntryAsync("ow1.dds", cancellationToken).ConfigureAwait(false),
                "first water frame restored byte-for-byte");
            AssertBytesEqual(
                sourceWater2,
                await repaired.ReadEntryAsync("ow2.dds", cancellationToken).ConfigureAwait(false),
                "second water frame restored byte-for-byte");
            AssertBytesEqual(
                enhancedRock,
                await repaired.ReadEntryAsync("rock.dds", cancellationToken).ConfigureAwait(false),
                "unrelated enhanced texture carried byte-for-byte");
            AssertBytesEqual(
                wld,
                await repaired.ReadEntryAsync("freporte.wld", cancellationToken).ConfigureAwait(false),
                "material graph remains original");

            // Exercise the public immutable pack-repair route as well as the
            // builder seam. An older completed zone pack must be advanced to
            // revision 6 without rerunning AI or losing the enhanced rock.
            var paths = new ProjectPaths(installPath, workspacePath);
            var baselineBuilder = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    var bytes = await File.ReadAllBytesAsync(baselinePath, token)
                        .ConfigureAwait(false);
                    await File.WriteAllBytesAsync(context.DestinationPath, bytes, token)
                        .ConfigureAwait(false);
                });
            var stagedBaseline = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.SelectedZone,
                        MaximumDimension: 1024,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "freporte"),
                    [new StagedBuildItem("freporte.s3d", baselineBuilder)],
                    "build-water-safety-baseline"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (var reportStream = new FileStream(
                Path.Combine(stagedBaseline.BuildDirectory, "texture-report.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    reportStream,
                    new TextureBuildReport(
                        TextureBuildReport.CurrentSchemaVersion,
                        stagedBaseline.BuildId,
                        DateTimeOffset.UtcNow,
                        installPath,
                        stagedBaseline.BuildDirectory,
                        1,
                        new TextureBuildStatistics(
                            3,
                            2,
                            0,
                            3,
                            2,
                            new Dictionary<string, int>(),
                            []))
                    {
                        TexturePipelineRevision = 5,
                        AppliedRepairRuleIds = TextureProcessingPipeline
                            .GetCurrentRepairRuleIds(
                                AssetScope.SelectedZone,
                                ["freporte.s3d"])
                            .Where(rule => !rule.Equals(
                                TextureProcessingPipeline.LegacyTranslucentMaterialSafetyRuleId,
                                StringComparison.Ordinal))
                            .ToArray()
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var repairedPack = await new TexturePackWorkflow(
                    clientClosedGuard: () => { })
                .RepairStagedPackAsync(
                    paths,
                    stagedBaseline.ManifestPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertTrue(repairedPack.Report.IsSafetyRepair, "pack repair safety report");
            AssertTrue(
                repairedPack.Report.AppliedRepairRuleIds.Contains(
                    TextureProcessingPipeline.LegacyTranslucentMaterialSafetyRuleId,
                    StringComparer.Ordinal),
                "pack repair records translucent-material rule");
            await using var packArchive = await PfsArchive.OpenAsync(
                Path.Combine(
                    repairedPack.StagedBuild.BuildDirectory,
                    "payload",
                    "freporte.s3d"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertBytesEqual(
                sourceWater1,
                await packArchive.ReadEntryAsync("ow1.dds", cancellationToken)
                    .ConfigureAwait(false),
                "immutable pack repair restores water");
            AssertBytesEqual(
                enhancedRock,
                await packArchive.ReadEntryAsync("rock.dds", cancellationToken)
                    .ConfigureAwait(false),
                "immutable pack repair retains enhanced rock");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateLegacyWld(
        IReadOnlyList<string> textureNames,
        uint materialParameters = 0x80000007)
    {
        var fragments = new List<(uint Type, byte[] Data)>();
        foreach (var textureName in textureNames)
        {
            fragments.Add((0x03, EncodeBitmapName(textureName)));
        }

        var bitmapInfo = new byte[12 + textureNames.Count * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapInfo.AsSpan(0, 4), 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapInfo.AsSpan(4, 4), (uint)textureNames.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapInfo.AsSpan(8, 4), 200);
        for (var index = 0; index < textureNames.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bitmapInfo.AsSpan(12 + index * 4, 4),
                checked((uint)index + 1));
        }

        fragments.Add((0x04, bitmapInfo));
        var bitmapInfoReference = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bitmapInfoReference.AsSpan(0, 4),
            checked((uint)textureNames.Count + 1));
        fragments.Add((0x05, bitmapInfoReference));

        var material = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(4, 4), materialParameters);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(8, 4), 0x00010101);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(16, 4), 0x3F800000);
        BinaryPrimitives.WriteUInt32LittleEndian(
            material.AsSpan(20, 4),
            checked((uint)textureNames.Count + 2));
        fragments.Add((0x30, material));

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x54503D02u);
        writer.Write(0x00015500u);
        writer.Write((uint)fragments.Count);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        foreach (var fragment in fragments)
        {
            writer.Write(checked((uint)fragment.Data.Length + 4));
            writer.Write(fragment.Type);
            writer.Write(0);
            writer.Write(fragment.Data);
        }

        writer.Flush();
        return output.ToArray();
    }

    private static byte[] EncodeBitmapName(string name)
    {
        var plain = Encoding.ASCII.GetBytes(name + '\0');
        var data = new byte[sizeof(uint) + sizeof(ushort) + plain.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), checked((ushort)plain.Length));
        for (var index = 0; index < plain.Length; index++)
        {
            data[6 + index] = (byte)(plain[index] ^ StringKey[index % StringKey.Length]);
        }

        return data;
    }

    private static byte[] CreateDxt1(int width, int height, int mipCount)
    {
        var bytes = new byte[128 + CalculateDxt1PayloadBytes(width, height, mipCount)];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0002100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), (uint)mipCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x4);
        Encoding.ASCII.GetBytes("DXT1").CopyTo(bytes, 84);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), mipCount > 1 ? 0x00401008u : 0x1000u);
        for (var offset = 128; offset < bytes.Length; offset += 8)
        {
            bytes[offset] = 0x00;
            bytes[offset + 1] = 0xF8;
            bytes[offset + 2] = 0xE0;
            bytes[offset + 3] = 0x07;
        }

        return bytes;
    }

    private static int CalculateDxt1PayloadBytes(int width, int height, int mipCount)
    {
        var total = 0;
        for (var mip = 0; mip < mipCount; mip++)
        {
            total += Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return total;
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
        await PfsArchiveWriter.WriteAsync(stream, items, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Assertion failed ({message}): byte payloads differ.");
        }
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException($"Assertion failed ({message}).");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Assertion failed ({message}): expected {expected}, actual {actual}.");
        }
    }
}
