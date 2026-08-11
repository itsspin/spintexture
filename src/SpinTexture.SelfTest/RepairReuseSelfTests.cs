using System.Buffers.Binary;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

internal static class RepairReuseSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-repair-reuse-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source", "ivm_chr.s3d");
        var reusablePath = Path.Combine(root, "baseline", "ivm_chr.s3d");
        var destinationPath = Path.Combine(root, "repaired", "ivm_chr.s3d");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(reusablePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        try
        {
            var originalBody = CreateTarga(32, 32, seed: 11);
            var enhancedBody = CreateTarga(128, 128, seed: 23);
            var missingBody = CreateTarga(32, 32, seed: 37);
            var transparentControl = CreateDxt5(8, 8, fullyTransparent: true);
            var brokenOpaqueControl = CreateDxt5(32, 32, fullyTransparent: false);

            await WriteArchiveAsync(
                    sourcePath,
                    [
                        new PfsArchiveItem("body0001.tga", originalBody),
                        new PfsArchiveItem("missing0001.tga", missingBody),
                        new PfsArchiveItem("ivmhe0001.dds", transparentControl)
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteArchiveAsync(
                    reusablePath,
                    [
                        new PfsArchiveItem("body0001.tga", enhancedBody),
                        new PfsArchiveItem("missing0001.tga", missingBody),
                        new PfsArchiveItem("ivmhe0001.dds", brokenOpaqueControl)
                    ],
                    cancellationToken)
                .ConfigureAwait(false);

            var reusableBefore = await FileIntegrity
                .FingerprintAsync(reusablePath, cancellationToken)
                .ConfigureAwait(false);
            var counter = new TextureBuildCounter();
            var batchInvocations = 0;
            var processedRequests = 0;
            var unavailableTools = new ExternalToolPaths(
                TexconvPath: null,
                RealEsrganPath: null,
                RealEsrganModelsPath: null,
                TextureUpscalerPath: null,
                TextureModelsPath: null,
                Diagnostics: []);
            var builder = new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(unavailableTools),
                counter,
                filterCharacterEquipmentEntries: true,
                reuseArchivePaths: new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["ivm_chr.s3d"] = reusablePath
                },
                processBatch: (requests, _) =>
                {
                    batchInvocations++;
                    processedRequests += requests.Count;
                    return Task.FromResult<IReadOnlyList<NativeTextureProcessOutcome>>(
                        requests.Select(_ => new NativeTextureProcessOutcome(
                                Result: null,
                                Error: new NotSupportedException(
                                    "Synthetic missing-only retry stop.")))
                            .ToArray());
                });

            await builder.BuildAsync(
                    new StagedArtifactBuildContext(
                        "ivm_chr.s3d",
                        sourcePath,
                        destinationPath,
                        Path.Combine(root, "work"),
                        Path.Combine(root, "previews"),
                        new UpscaleOptions(
                            TexturePreset.Faithful,
                            AssetScope.CharactersAndEquipmentOnly,
                            MaximumDimension: 1024,
                            GenerateMipMaps: true,
                            InstallAfterBuild: false)),
                    cancellationToken)
                .ConfigureAwait(false);

            var statistics = counter.Snapshot();
            AssertEqual(2, statistics.DiscoveredTextures, "eligible repair texture count");
            AssertEqual(1, statistics.ReusedTextures, "byte-exact reused texture count");
            AssertEqual(0, statistics.EnhancedTextures, "newly enhanced texture count with tools unavailable");
            AssertEqual(1, statistics.PreservedTextures, "only unchanged missing texture should be retried safely");
            AssertEqual(1, batchInvocations, "missing-only retry batch invocation count");
            AssertEqual(1, processedRequests, "only the unchanged texture should enter processing");
            Assert(
                statistics.Warnings.Any(warning =>
                    warning.Contains("missing0001.tga", StringComparison.OrdinalIgnoreCase)),
                "the unchanged missing texture should reach the retry route");
            Assert(
                statistics.Warnings.All(warning =>
                    !warning.Contains("body0001.tga", StringComparison.OrdinalIgnoreCase)),
                "the prior successful texture must not be processed again");

            await using var repaired = await PfsArchive.OpenAsync(
                    destinationPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                enhancedBody,
                await repaired.ReadEntryAsync("body0001.tga", cancellationToken)
                    .ConfigureAwait(false),
                "prior enhanced body should be reused byte-for-byte");
            AssertSequenceEqual(
                missingBody,
                await repaired.ReadEntryAsync("missing0001.tga", cancellationToken)
                    .ConfigureAwait(false),
                "failed retry should remain original");
            AssertSequenceEqual(
                transparentControl,
                await repaired.ReadEntryAsync("ivmhe0001.dds", cancellationToken)
                    .ConfigureAwait(false),
                "newly protected invisible-man control should revert to the original sentinel");

            var reusableAfter = await FileIntegrity
                .FingerprintAsync(reusablePath, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                reusableBefore,
                reusableAfter,
                "repair must not modify its baseline staged archive");
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

    private static byte[] CreateDxt5(int width, int height, bool fullyTransparent)
    {
        var blockCount = checked(((width + 3) / 4) * ((height + 3) / 4));
        var bytes = new byte[128 + (blockCount * 16)];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0008_1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(blockCount * 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x4);
        "DXT5"u8.CopyTo(bytes.AsSpan(84, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        for (var block = 0; block < blockCount; block++)
        {
            var offset = 128 + (block * 16);
            bytes[offset] = fullyTransparent ? (byte)0 : byte.MaxValue;
            bytes[offset + 1] = fullyTransparent ? (byte)0 : byte.MaxValue;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), 0xF81F);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10, 2), 0xF81F);
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

    private static void AssertSequenceEqual(
        byte[] expected,
        byte[] actual,
        string description)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }
}
