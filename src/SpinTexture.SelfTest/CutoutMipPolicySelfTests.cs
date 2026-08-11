using System.Buffers.Binary;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

internal static class CutoutMipPolicySelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        TestDirectCompressedAlphaClassification();
        await TestPfsDetectedCutoutRejectsStaleReuseAsync(cancellationToken).ConfigureAwait(false);
        await TestFailedStaleCutoutRestoresOriginalAsync(cancellationToken).ConfigureAwait(false);
        await TestUnexpectedBaselineMemberFailsClosedAsync(cancellationToken).ConfigureAwait(false);
        await TestBaselineFingerprintRaceFailsClosedAsync(cancellationToken).ConfigureAwait(false);
        await TestLooseDetectedCutoutUsesResultMipMetadataAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TestDirectCompressedAlphaClassification()
    {
        var opaqueBc1 = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: false);
        var cutoutBc1 = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: true);
        AssertEqual(TextureTopMipAlphaKind.Opaque, Classify(opaqueBc1), "opaque BC1 top mip");
        AssertEqual(
            TextureTopMipAlphaKind.AlphaTestedCutout,
            Classify(cutoutBc1),
            "mixed-index BC1 top mip");

        var partialBc1 = CreateDxt1(5, 3, mipCount: 1, includeTransparentBlocks: false);
        // Mark only padding texels in the final partial block transparent. A
        // classifier that scans the complete 4x4 block would report a false
        // cutout; the real 5x3 image remains fully opaque.
        var secondBlockOffset = 128 + 8;
        uint paddingOnlyIndices = 0;
        for (var localY = 0; localY < 4; localY++)
        {
            for (var localX = 1; localX < 4; localX++)
            {
                paddingOnlyIndices |= 3u << (((localY * 4) + localX) * 2);
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            partialBc1.AsSpan(secondBlockOffset + 4, 4),
            paddingOnlyIndices);
        AssertEqual(
            TextureTopMipAlphaKind.Opaque,
            Classify(partialBc1),
            "partial BC1 edge blocks must ignore padding texels");
        AssertEqual(
            TextureTopMipAlphaKind.Unknown,
            Classify(cutoutBc1.AsSpan(0, cutoutBc1.Length - 1).ToArray()),
            "truncated BC1 top mip");

        var softBc2 = CreateDxt3(8, 8, alphaNibble: 8);
        AssertEqual(
            TextureTopMipAlphaKind.SoftOrIntermediate,
            Classify(softBc2),
            "intermediate BC2 alpha");
        AssertEqual(
            TextureTopMipAlphaKind.AlphaTestedCutout,
            Classify(CreateDxt5(8, 8, Bc3AlphaPattern.Binary)),
            "binary BC3 alpha");
        AssertEqual(
            TextureTopMipAlphaKind.SoftOrIntermediate,
            Classify(CreateDxt5(8, 8, Bc3AlphaPattern.Soft)),
            "soft BC3 alpha");
        AssertEqual(
            TextureTopMipAlphaKind.FullyTransparent,
            Classify(CreateDxt5(8, 8, Bc3AlphaPattern.Transparent)),
            "fully transparent BC3 alpha");
        AssertEqual(
            TextureTopMipAlphaKind.Unknown,
            Classify(CreateThresholdAmbiguousDxt5()),
            "BC3 interpolation that can cross an 8/9 threshold must use decoded fallback");

        static TextureTopMipAlphaKind Classify(byte[] payload)
        {
            var sniffer = new TextureHeaderSniffer();
            Assert(sniffer.TryRead(payload, out var metadata), "compressed-alpha fixture header");
            return TextureAlphaInspector.ClassifyTopMipAlpha(payload, metadata!);
        }
    }

    private static async Task TestPfsDetectedCutoutRejectsStaleReuseAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-cutout-pfs-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source", "forest.s3d");
        var reusablePath = Path.Combine(root, "reuse", "forest.s3d");
        var destinationPath = Path.Combine(root, "staged", "forest.s3d");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(reusablePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        try
        {
            var sourceCutout = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: true);
            var sourceOpaque = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: false);
            var sourceIdentical = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: false);
            var transparentControl = CreateDxt5(8, 8, Bc3AlphaPattern.Transparent);
            var staleFullChain = CreateDxt1(64, 64, mipCount: 7, includeTransparentBlocks: true);
            var enhancedOpaque = CreateDxt1(64, 64, mipCount: 7, includeTransparentBlocks: false);
            var invalidChangedControl = CreateDxt5(8, 8, Bc3AlphaPattern.Binary);
            await WriteArchiveAsync(
                sourcePath,
                [
                    new PfsArchiveItem("tree70.dds", sourceCutout),
                    new PfsArchiveItem("rock01.dds", sourceOpaque),
                    new PfsArchiveItem("ground01.dds", sourceIdentical),
                    new PfsArchiveItem("invisible_control.dds", transparentControl)
                ],
                cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(
                reusablePath,
                [
                    new PfsArchiveItem("tree70.dds", staleFullChain),
                    new PfsArchiveItem("rock01.dds", enhancedOpaque),
                    new PfsArchiveItem("ground01.dds", sourceIdentical),
                    new PfsArchiveItem("invisible_control.dds", invalidChangedControl)
                ],
                cancellationToken).ConfigureAwait(false);

            var tools = new ToolchainDiscovery().Discover(
                new ProjectPaths(root, Path.Combine(root, "workspace")));
            Assert(tools.TexconvPath is not null, "cutout reuse test requires bundled texconv");
            var counter = new TextureBuildCounter();
            var nativeRunner = new CountingRejectingNativeProcessRunner();
            var processedNames = new List<string>();
            var reusableFingerprint = await FileIntegrity
                .FingerprintAsync(reusablePath, cancellationToken)
                .ConfigureAwait(false);
            var builder = new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(tools, nativeRunner),
                counter,
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = reusablePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = new StagedPackFileFingerprint(
                        reusableFingerprint.Length,
                        reusableFingerprint.Sha256)
                },
                processBatch: async (requests, token) =>
                {
                    var outcomes = new List<NativeTextureProcessOutcome>(requests.Count);
                    foreach (var request in requests)
                    {
                        processedNames.Add(Path.GetFileName(request.SourcePath));
                        AssertEqual(
                            TextureKind.Color,
                            request.Classification.Kind,
                            "DXT1 tree cutout should exercise decoded detection beyond semantic classification");
                        var dimensions = UpscaleDimensions.Calculate(
                            request.Metadata.Width,
                            request.Metadata.Height,
                            request.Options.MaximumDimension);
                        var expectedMipCount = TextureMipPolicy.Calculate(
                            dimensions.OutputWidth,
                            dimensions.OutputHeight,
                            generateMipMaps: true,
                            useCutoutFloor: true);
                        await File.WriteAllBytesAsync(
                            request.DestinationPath,
                            CreateDxt1(
                                dimensions.OutputWidth,
                                dimensions.OutputHeight,
                                expectedMipCount,
                                includeTransparentBlocks: true),
                            token).ConfigureAwait(false);
                        outcomes.Add(new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                request.DestinationPath,
                                dimensions,
                                "Synthetic detected-cutout reconstruction",
                                TimeSpan.Zero,
                                expectedMipCount,
                                UsedCutoutMipFloor: true),
                            Error: null));
                    }

                    return outcomes;
                });

            await builder.BuildAsync(
                new StagedArtifactBuildContext(
                    "forest.s3d",
                    sourcePath,
                    destinationPath,
                    Path.Combine(root, "work"),
                    Path.Combine(root, "previews"),
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.SelectedZone,
                        MaximumDimension: 64,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "forest")),
                cancellationToken).ConfigureAwait(false);

            AssertEqual(1, processedNames.Count, "only stale changed cutout should be reprocessed");
            AssertEqual(
                0,
                nativeRunner.InvocationCount,
                "supported BC1 reuse inspection must not launch texconv per texture");
            var statistics = counter.Snapshot();
            AssertEqual(1, statistics.EnhancedTextures, "stale cutout should be freshly enhanced");
            AssertEqual(1, statistics.ReusedTextures, "changed opaque enhancement should be reused");
            AssertEqual(2, statistics.PreservedTextures, "source-identical and protected textures should stay original");

            await using var reusable = await PfsArchive.OpenAsync(
                    reusablePath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using var rebuilt = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var cutoutPayload = await rebuilt.ReadEntryAsync(
                "tree70.dds",
                cancellationToken).ConfigureAwait(false);
            var opaquePayload = await rebuilt.ReadEntryAsync(
                "rock01.dds",
                cancellationToken).ConfigureAwait(false);
            var groundPayload = await rebuilt.ReadEntryAsync(
                "ground01.dds",
                cancellationToken).ConfigureAwait(false);
            var controlPayload = await rebuilt.ReadEntryAsync(
                "invisible_control.dds",
                cancellationToken).ConfigureAwait(false);
            var sniffer = new TextureHeaderSniffer();
            Assert(sniffer.TryRead(cutoutPayload, out var cutoutMetadata), "rebuilt cutout DDS header");
            AssertEqual(1, cutoutMetadata!.MipCount, "64-square cutout should retain only its top level");
            AssertSequenceEqual(enhancedOpaque, opaquePayload, "changed opaque enhancement should be carried forward");
            AssertSequenceEqual(sourceIdentical, groundPayload, "source-identical opaque entry should remain original");
            AssertSequenceEqual(
                transparentControl,
                controlPayload,
                "changed fully-transparent protected member must be restored to exact original");
            var reusableOpaqueEntry = reusable.GetEntry("rock01.dds");
            var rebuiltOpaqueEntry = rebuilt.GetEntry("rock01.dds");
            AssertEqual(
                reusableOpaqueEntry.StoredLength,
                rebuiltOpaqueEntry.StoredLength,
                "carried opaque stored chunk length");
            var reusableStoredHash = await reusable.ComputeStoredEntrySha256Async(
                    reusableOpaqueEntry,
                    cancellationToken).ConfigureAwait(false);
            var rebuiltStoredHash = await rebuilt.ComputeStoredEntrySha256Async(
                    rebuiltOpaqueEntry,
                    cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                reusableStoredHash,
                rebuiltStoredHash,
                "carried opaque stored chunk bytes should be raw-copied");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestLooseDetectedCutoutUsesResultMipMetadataAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-cutout-loose-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "tree70.dds");
        var destinationPath = Path.Combine(root, "staged", "tree70.dds");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath,
                CreateDxt1(256, 256, mipCount: 1, includeTransparentBlocks: true),
                cancellationToken).ConfigureAwait(false);
            var counter = new TextureBuildCounter();
            var unavailableTools = new ExternalToolPaths(
                TexconvPath: null,
                RealEsrganPath: null,
                RealEsrganModelsPath: null,
                TextureUpscalerPath: null,
                TextureModelsPath: null,
                Diagnostics: []);
            var builder = new LooseTextureArtifactBuilder(
                new NativeTextureProcessor(unavailableTools),
                counter,
                processTexture: async (request, token) =>
                {
                    AssertEqual(
                        TextureKind.Color,
                        request.Classification.Kind,
                        "loose DXT1 cutout should remain semantically ambiguous before decoded detection");
                    var dimensions = UpscaleDimensions.Calculate(
                        request.Metadata.Width,
                        request.Metadata.Height,
                        request.Options.MaximumDimension);
                    var expectedMipCount = TextureMipPolicy.Calculate(
                        dimensions.OutputWidth,
                        dimensions.OutputHeight,
                        request.Options.GenerateMipMaps,
                        useCutoutFloor: true);
                    Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
                    await File.WriteAllBytesAsync(
                        request.DestinationPath,
                        CreateDxt1(
                            dimensions.OutputWidth,
                            dimensions.OutputHeight,
                            expectedMipCount,
                            includeTransparentBlocks: true),
                        token).ConfigureAwait(false);
                    return new NativeTextureProcessResult(
                        request.DestinationPath,
                        dimensions,
                        "Synthetic loose detected-cutout reconstruction",
                        TimeSpan.Zero,
                        expectedMipCount,
                        UsedCutoutMipFloor: true);
                });

            await builder.BuildAsync(
                new StagedArtifactBuildContext(
                    "tree70.dds",
                    sourcePath,
                    destinationPath,
                    Path.Combine(root, "work"),
                    Path.Combine(root, "previews"),
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.WorldOnly,
                        MaximumDimension: 1024,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false)),
                cancellationToken).ConfigureAwait(false);

            var output = await new TextureHeaderSniffer()
                .ReadFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            Assert(output is not null, "loose cutout output should remain a readable DDS");
            AssertEqual(1, output!.MipCount, "loose 1024-square detected cutout mip count");
            AssertEqual(1, counter.Snapshot().EnhancedTextures, "loose cutout should pass exact result validation");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestFailedStaleCutoutRestoresOriginalAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-cutout-failure-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source.s3d");
        var baselinePath = Path.Combine(root, "baseline.s3d");
        var destinationPath = Path.Combine(root, "repaired.s3d");
        Directory.CreateDirectory(root);

        try
        {
            var sourceCutout = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: true);
            var staleCutout = CreateDxt1(64, 64, mipCount: 7, includeTransparentBlocks: true);
            await WriteArchiveAsync(
                sourcePath,
                [new PfsArchiveItem("tree70.dds", sourceCutout)],
                cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(
                baselinePath,
                [new PfsArchiveItem("tree70.dds", staleCutout)],
                cancellationToken).ConfigureAwait(false);
            var baselineBefore = await FileIntegrity
                .FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var unavailableTools = new ExternalToolPaths(
                TexconvPath: null,
                RealEsrganPath: null,
                RealEsrganModelsPath: null,
                TextureUpscalerPath: null,
                TextureModelsPath: null,
                Diagnostics: []);
            var counter = new TextureBuildCounter();
            var builder = new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(unavailableTools),
                counter,
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = new StagedPackFileFingerprint(
                        baselineBefore.Length,
                        baselineBefore.Sha256)
                },
                processBatch: (requests, _) =>
                    Task.FromResult<IReadOnlyList<NativeTextureProcessOutcome>>(
                        requests.Select(_ => new NativeTextureProcessOutcome(
                                Result: null,
                                Error: new NotSupportedException("Synthetic cutout failure.")))
                            .ToArray()));

            await builder.BuildAsync(
                new StagedArtifactBuildContext(
                    "forest.s3d",
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
            var repairedCutout = await repaired
                .ReadEntryAsync("tree70.dds", cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                sourceCutout,
                repairedCutout,
                "failed stale cutout must restore exact original instead of retired mip output");
            AssertEqual(1, counter.Snapshot().PreservedTextures, "failed cutout preserve count");
            AssertEqual(
                baselineBefore,
                await FileIntegrity.FingerprintAsync(baselinePath, cancellationToken)
                    .ConfigureAwait(false),
                "failed repair must not mutate its baseline archive");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestUnexpectedBaselineMemberFailsClosedAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-cutout-unexpected-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source.s3d");
        var baselinePath = Path.Combine(root, "baseline.s3d");
        var destinationPath = Path.Combine(root, "repaired.s3d");
        Directory.CreateDirectory(root);

        try
        {
            var sourceOpaque = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: false);
            var enhancedOpaque = CreateDxt1(64, 64, mipCount: 7, includeTransparentBlocks: false);
            await WriteArchiveAsync(
                sourcePath,
                [
                    new PfsArchiveItem("rock01.dds", sourceOpaque),
                    new PfsArchiveItem("forest.wld", "original-geometry"u8.ToArray())
                ],
                cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(
                baselinePath,
                [
                    new PfsArchiveItem("rock01.dds", enhancedOpaque),
                    new PfsArchiveItem("forest.wld", "unexpected-changed-geometry"u8.ToArray())
                ],
                cancellationToken).ConfigureAwait(false);
            var baselineFingerprint = await FileIntegrity
                .FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var unavailableTools = new ExternalToolPaths(
                TexconvPath: null,
                RealEsrganPath: null,
                RealEsrganModelsPath: null,
                TextureUpscalerPath: null,
                TextureModelsPath: null,
                Diagnostics: []);
            var builder = new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(unavailableTools),
                new TextureBuildCounter(),
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = new StagedPackFileFingerprint(
                        baselineFingerprint.Length,
                        baselineFingerprint.Sha256)
                },
                processBatch: (_, _) => throw new InvalidOperationException(
                    "Valid carried opaque output must not enter processing."));

            await AssertThrowsAsync<InvalidDataException>(
                () => builder.BuildAsync(
                    new StagedArtifactBuildContext(
                        "forest.s3d",
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
                    cancellationToken),
                "unexpected changed non-texture baseline member must fail final allowlist validation")
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestBaselineFingerprintRaceFailsClosedAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-cutout-baseline-race-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source.s3d");
        var baselinePath = Path.Combine(root, "baseline.s3d");
        Directory.CreateDirectory(root);

        try
        {
            var sourceOpaque = CreateDxt1(16, 16, mipCount: 1, includeTransparentBlocks: false);
            var enhancedOpaque = CreateDxt1(64, 64, mipCount: 7, includeTransparentBlocks: false);
            await WriteArchiveAsync(
                sourcePath,
                [new PfsArchiveItem("rock01.dds", sourceOpaque)],
                cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(
                baselinePath,
                [new PfsArchiveItem("rock01.dds", enhancedOpaque)],
                cancellationToken).ConfigureAwait(false);
            var expected = await FileIntegrity
                .FingerprintAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(baselinePath, cancellationToken)
                .ConfigureAwait(false);
            bytes[Math.Min(24, bytes.Length - 1)] ^= 0x5A;
            await File.WriteAllBytesAsync(baselinePath, bytes, cancellationToken)
                .ConfigureAwait(false);

            var unavailableTools = new ExternalToolPaths(
                TexconvPath: null,
                RealEsrganPath: null,
                RealEsrganModelsPath: null,
                TextureUpscalerPath: null,
                TextureModelsPath: null,
                Diagnostics: []);
            var builder = new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(unavailableTools),
                new TextureBuildCounter(),
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = baselinePath
                },
                reuseArchiveFingerprints: new Dictionary<string, StagedPackFileFingerprint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["forest.s3d"] = new StagedPackFileFingerprint(
                        expected.Length,
                        expected.Sha256)
                });
            await AssertThrowsAsync<InvalidDataException>(
                () => builder.BuildAsync(
                    new StagedArtifactBuildContext(
                        "forest.s3d",
                        sourcePath,
                        Path.Combine(root, "repaired.s3d"),
                        Path.Combine(root, "work"),
                        Path.Combine(root, "previews"),
                        new UpscaleOptions(
                            TexturePreset.Faithful,
                            AssetScope.WorldOnly,
                            MaximumDimension: 64,
                            GenerateMipMaps: true,
                            InstallAfterBuild: false)),
                    cancellationToken),
                "same-length baseline mutation after catalog fingerprint must fail before reuse")
                .ConfigureAwait(false);
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
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateDxt1(
        int width,
        int height,
        int mipCount,
        bool includeTransparentBlocks)
    {
        var blocksWide = checked((width + 3) / 4);
        var blocksHigh = checked((height + 3) / 4);
        var blockCount = checked(blocksWide * blocksHigh);
        var bytes = new byte[128 + (blockCount * 8)];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0008_1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(blockCount * 8)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), checked((uint)mipCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x4);
        "DXT1"u8.CopyTo(bytes.AsSpan(84, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), mipCount > 1 ? 0x0040_1008u : 0x1000u);

        for (var block = 0; block < blockCount; block++)
        {
            var offset = 128 + (block * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 0x0000);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), 0xFFFF);
            var transparent = includeTransparentBlocks && (block % 2 == 0);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(offset + 4, 4),
                transparent ? uint.MaxValue : 0u);
        }

        return bytes;
    }

    private static byte[] CreateDxt3(int width, int height, byte alphaNibble)
    {
        var blockCount = checked(((width + 3) / 4) * ((height + 3) / 4));
        var bytes = CreateLegacyDds(width, height, "DXT3", blockCount * 16);
        var packedNibble = (ulong)(alphaNibble & 0xF);
        packedNibble |= packedNibble << 4;
        packedNibble |= packedNibble << 8;
        packedNibble |= packedNibble << 16;
        packedNibble |= packedNibble << 32;
        for (var block = 0; block < blockCount; block++)
        {
            var offset = 128 + (block * 16);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), packedNibble);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10, 2), 0x0000);
        }

        return bytes;
    }

    private static byte[] CreateDxt5(int width, int height, Bc3AlphaPattern pattern)
    {
        var blockCount = checked(((width + 3) / 4) * ((height + 3) / 4));
        var bytes = CreateLegacyDds(width, height, "DXT5", blockCount * 16);
        for (var block = 0; block < blockCount; block++)
        {
            var offset = 128 + (block * 16);
            bytes[offset] = pattern == Bc3AlphaPattern.Transparent ? (byte)0 : byte.MaxValue;
            bytes[offset + 1] = 0;
            ulong indices = pattern switch
            {
                Bc3AlphaPattern.Binary => BuildBc3Indices(index => index % 2 == 0 ? 0 : 1),
                Bc3AlphaPattern.Soft => BuildBc3Indices(_ => 2),
                Bc3AlphaPattern.Transparent => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(pattern))
            };
            for (var byteIndex = 0; byteIndex < 6; byteIndex++)
            {
                bytes[offset + 2 + byteIndex] = checked((byte)((indices >> (byteIndex * 8)) & 0xFF));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10, 2), 0x0000);
        }

        return bytes;

        static ulong BuildBc3Indices(Func<int, int> select)
        {
            ulong indices = 0;
            for (var pixel = 0; pixel < 16; pixel++)
            {
                indices |= (ulong)select(pixel) << (pixel * 3);
            }

            return indices;
        }
    }

    private static byte[] CreateThresholdAmbiguousDxt5()
    {
        var bytes = CreateLegacyDds(4, 4, "DXT5", topMipBytes: 16);
        bytes[128] = 9;
        bytes[129] = 3;
        // Palette index 2 is (6*9 + 3)/7 = 8.142857. Integer and
        // float-to-byte conversion can land on opposite sides of the cutout
        // threshold, so direct classification must decline this rare block.
        ulong indices = 0;
        for (var pixel = 0; pixel < 16; pixel++)
        {
            indices |= 2uL << (pixel * 3);
        }

        for (var byteIndex = 0; byteIndex < 6; byteIndex++)
        {
            bytes[130 + byteIndex] = checked((byte)((indices >> (byteIndex * 8)) & 0xFF));
        }

        return bytes;
    }

    private static byte[] CreateLegacyDds(
        int width,
        int height,
        string fourCc,
        int topMipBytes)
    {
        var bytes = new byte[128 + topMipBytes];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0008_1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)topMipBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x4);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(bytes.AsSpan(84, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        return bytes;
    }

    private enum Bc3AlphaPattern
    {
        Binary,
        Soft,
        Transparent
    }

    private sealed class CountingRejectingNativeProcessRunner : INativeProcessRunner
    {
        public int InvocationCount { get; private set; }

        public Task<NativeProcessResult> RunAsync(
            NativeProcessCommand command,
            IProgress<NativeOutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            throw new InvalidOperationException(
                $"Direct compressed-alpha classification unexpectedly launched {command.DisplayName ?? command.ExecutablePath}.");
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
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
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }
}
