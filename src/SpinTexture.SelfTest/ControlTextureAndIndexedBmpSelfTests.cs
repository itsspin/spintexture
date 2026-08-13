using System.Buffers.Binary;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

internal static class ControlTextureAndIndexedBmpSelfTests
{
    private const byte KeyedPaletteIndex = 7;
    private const int PaletteColorCount = 32;

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestInvisibleManControlTextureIsPreservedAsync(cancellationToken).ConfigureAwait(false);
        await TestFreshCharacterBuildEnhancesEligibleIndexedBitmapAsync(cancellationToken)
            .ConfigureAwait(false);
        TestPaintedPresetRouting();
        TestRichIndexedBitmapRoundTrip();
    }

    private static void TestPaintedPresetRouting()
    {
        var indexed = new TextureMetadata(
            TextureFileFormat.Bmp,
            "BI_RGB",
            64,
            64,
            1,
            8,
            TextureAlphaStatus.None,
            IsCompressed: false,
            IsTopDown: false,
            IsCubeMap: false,
            IsVolumeTexture: false,
            1,
            null,
            UsesDx10Header: false);
        var illustrated = new UpscaleOptions(
            TexturePreset.Illustrated,
            AssetScope.WorldOnly,
            MaximumDimension: 2048,
            GenerateMipMaps: true,
            InstallAfterBuild: false,
            SelectedZone: null);

        AssertEqual(
            TexturePreset.Illustrated,
            PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                illustrated,
                indexed,
                "brick01.bmp",
                allowExplicitEffectArt: false).Preset,
            "painted indexed world art must not be silently redirected to Texture HD");
        AssertEqual(
            TexturePreset.RusticPainted,
            PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                illustrated with { Preset = TexturePreset.RusticPainted },
                indexed,
                "armor0401.bmp",
                allowExplicitEffectArt: false).Preset,
            "rustic indexed armor art must reach the painted model");
        AssertEqual(
            TexturePreset.ClassicHd,
            PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                illustrated with { Preset = TexturePreset.MaximumDetail },
                indexed,
                "armor0401.bmp",
                allowExplicitEffectArt: false).Preset,
            "non-painted indexed art should retain the palette-stable Texture HD route");
        AssertEqual(
            TexturePreset.ClassicHd,
            PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                illustrated,
                indexed,
                "water01.bmp",
                allowExplicitEffectArt: false).Preset,
            "animated indexed materials must retain the conservative route");

        var ordinaryColor = indexed with
        {
            FileFormat = TextureFileFormat.Dds,
            PayloadFormat = "DXT1",
            BitsPerPixel = 4,
            IsCompressed = true,
            TexconvFormat = "BC1_UNORM"
        };
        AssertEqual(
            TexturePreset.Illustrated,
            PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                illustrated,
                ordinaryColor,
                "fresgn01.dds",
                allowExplicitEffectArt: false).Preset,
            "explicit painted signs should retain the selected art direction");
        AssertEqual(
            TexturePreset.Faithful,
            PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                illustrated,
                ordinaryColor,
                "fire01.dds",
                allowExplicitEffectArt: false).Preset,
            "animated material sequences should remain faithful outside the explicit effect scope");
    }

    private static async Task TestFreshCharacterBuildEnhancesEligibleIndexedBitmapAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-fresh-indexed-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "global4_chr.s3d");
        var destinationPath = Path.Combine(root, "staged", "global4_chr.s3d");
        var sourceBitmap = CreateIndexedBitmap(width: 32, height: 32, richPaletteUse: true);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        try
        {
            await using (var sourceStream = new FileStream(
                             sourcePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await PfsArchiveWriter.WriteAsync(
                    sourceStream,
                    [
                        new PfsArchiveItem("global4_chr.wld", CreateDeterministicBytes(257, seed: 41)),
                        new PfsArchiveItem("armor0401.bmp", sourceBitmap)
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var batchCalls = 0;
            var counter = new TextureBuildCounter();
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
                processBatch: async (requests, token) =>
                {
                    batchCalls++;
                    AssertEqual(1, requests.Count, "fresh indexed character batch size");
                    var request = requests[0];
                    AssertEqual(
                        TexturePreset.ClassicHd,
                        request.Options.Preset,
                        "eligible indexed BMPs should use the palette-stable texture route");
                    var source = await File.ReadAllBytesAsync(request.SourcePath, token)
                        .ConfigureAwait(false);
                    var dimensions = UpscaleDimensions.Calculate(
                        request.Metadata.Width,
                        request.Metadata.Height,
                        request.Options.MaximumDimension);
                    var rgba = CreateNearestIndexedRgba(
                        source,
                        dimensions.OutputWidth,
                        dimensions.OutputHeight);
                    var encoded = LegacyIndexedBmp.Encode(
                        source,
                        dimensions.OutputWidth,
                        dimensions.OutputHeight,
                        rgba);
                    await File.WriteAllBytesAsync(request.DestinationPath, encoded, token)
                        .ConfigureAwait(false);
                    return
                    [
                        new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                request.DestinationPath,
                                dimensions,
                                "Synthetic palette-stable fresh-build reconstruction",
                                TimeSpan.Zero),
                            Error: null)
                    ];
                });

            await builder.BuildAsync(
                new StagedArtifactBuildContext(
                    "global4_chr.s3d",
                    sourcePath,
                    destinationPath,
                    Path.Combine(root, "work"),
                    Path.Combine(root, "previews"),
                    new UpscaleOptions(
                        TexturePreset.MaximumDetail,
                        AssetScope.CharactersAndEquipmentOnly,
                        MaximumDimension: 128,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: null)),
                cancellationToken).ConfigureAwait(false);

            AssertEqual(1, batchCalls, "fresh indexed build batch invocation count");
            var statistics = counter.Snapshot();
            AssertEqual(1, statistics.EnhancedTextures, "fresh indexed build enhanced count");
            AssertEqual(0, statistics.ReusedTextures, "fresh indexed build must not depend on repair reuse");
            AssertEqual(0, statistics.PreservedTextures, "eligible indexed texture should not be preserved");

            await using var stagedArchive = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var enhancedBitmap = await stagedArchive.ReadEntryAsync(
                "armor0401.bmp",
                cancellationToken).ConfigureAwait(false);
            var sniffer = new TextureHeaderSniffer();
            Assert(sniffer.TryRead(enhancedBitmap, out var metadata), "enhanced indexed BMP header");
            Assert(metadata is not null, "enhanced indexed BMP metadata");
            AssertEqual(128, metadata!.Width, "enhanced indexed BMP width");
            AssertEqual(128, metadata.Height, "enhanced indexed BMP height");
            AssertEqual(8, metadata.BitsPerPixel, "enhanced indexed BMP bit depth");
            Assert(
                LegacyIndexedBmp.CanEncode(enhancedBitmap),
                "fresh output should retain a valid rich indexed representation");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestInvisibleManControlTextureIsPreservedAsync(
        CancellationToken cancellationToken)
    {
        var sentinel = CreateFullyTransparentMagentaDxt5(width: 8, height: 8);
        AssertFullyTransparentMagentaDxt5(sentinel);

        var sniffer = new TextureHeaderSniffer();
        Assert(sniffer.TryRead(sentinel, out var metadata), "invisible-man DDS should have a valid header");
        Assert(metadata is not null, "invisible-man DDS metadata should be populated");
        Assert(
            TextureAlphaInspector.IsKnownFullyTransparent(sentinel, metadata!),
            "the compressed-alpha preflight should prove the sentinel is fully transparent without running native tools");
        var visibleVariant = sentinel.ToArray();
        visibleVariant[128] = byte.MaxValue;
        Assert(
            !TextureAlphaInspector.IsKnownFullyTransparent(visibleVariant, metadata!),
            "a DXT5 block with visible alpha must not be treated as a transparent renderer control");
        var classification = new TextureSemanticClassifier().Classify("ivmhe0001.dds", metadata!);
        Assert(
            classification.CanUseColorUpscaler,
            "the generic header/name classifier should demonstrate why the explicit control-texture guard is required");
        Assert(
            !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                "IVM_CHR.S3D",
                "IVMHE0001.DDS"),
            "character/equipment scope must reject the invisible-man control texture case-insensitively");
        Assert(
            CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                "ivm_chr.s3d",
                "ivmhe0002.dds"),
            "the invisible-man guard should remain narrowly scoped to the audited sentinel member");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-control-texture-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "ivm_chr.s3d");
        var destinationPath = Path.Combine(root, "staged", "ivm_chr.s3d");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        try
        {
            await using (var sourceStream = new FileStream(
                             sourcePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await PfsArchiveWriter.WriteAsync(
                    sourceStream,
                    [
                        new PfsArchiveItem("ivm_chr.wld", CreateDeterministicBytes(257, seed: 31)),
                        new PfsArchiveItem("ivmhe0001.dds", sentinel)
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var counter = new TextureBuildCounter();
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
                // Bypass the audited filename deny here so the generic
                // compressed-alpha guard itself is exercised.
                filterCharacterEquipmentEntries: false);
            await builder.BuildAsync(
                new StagedArtifactBuildContext(
                    "ivm_chr.s3d",
                    sourcePath,
                    destinationPath,
                    Path.Combine(root, "work"),
                    Path.Combine(root, "previews"),
                    new UpscaleOptions(
                        TexturePreset.MaximumDetail,
                        AssetScope.CharactersAndEquipmentOnly,
                        MaximumDimension: 2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: null)),
                cancellationToken).ConfigureAwait(false);

            var sourceArchiveBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            var stagedArchiveBytes = await File.ReadAllBytesAsync(destinationPath, cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                sourceArchiveBytes,
                stagedArchiveBytes,
                "an archive containing only the invisible-man sentinel must be staged byte-exact");
            AssertEqual(
                0,
                counter.Snapshot().EnhancedTextures,
                "the invisible-man sentinel must never reach the texture processor");
            Assert(
                counter.Snapshot().PreservedReasons.ContainsKey(
                    "Fully transparent renderer-control texture"),
                "the archive builder should report the generic transparent-control protection");

            await using var stagedArchive = await PfsArchive.OpenAsync(
                destinationPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var stagedSentinel = await stagedArchive.ReadEntryAsync(
                "ivmhe0001.dds",
                cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                sentinel,
                stagedSentinel,
                "the invisible-man sentinel member must remain byte-exact");
            AssertFullyTransparentMagentaDxt5(stagedSentinel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void TestRichIndexedBitmapRoundTrip()
    {
        const int sourceWidth = 32;
        const int sourceHeight = 32;
        const int scale = 4;
        const int outputWidth = sourceWidth * scale;
        const int outputHeight = sourceHeight * scale;

        var source = CreateIndexedBitmap(sourceWidth, sourceHeight, richPaletteUse: true);
        Assert(LegacyIndexedBmp.CanEncode(source), "rich 8-bit BI_RGB texture should be eligible");

        var enhancedRgba = new byte[outputWidth * outputHeight * 4];
        for (var y = 0; y < outputHeight; y++)
        {
            for (var x = 0; x < outputWidth; x++)
            {
                var offset = ((y * outputWidth) + x) * 4;
                enhancedRgba[offset] = checked((byte)((x * 13 + y * 3) & 0xFF));
                enhancedRgba[offset + 1] = checked((byte)((x * 5 + y * 11) & 0xFF));
                enhancedRgba[offset + 2] = checked((byte)((x * 7 + y * 17) & 0xFF));
                enhancedRgba[offset + 3] = byte.MaxValue;
            }
        }

        var encoded = LegacyIndexedBmp.Encode(source, outputWidth, outputHeight, enhancedRgba);
        var pixelOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(10, 4)));
        var outputPixelOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(10, 4)));
        AssertEqual(pixelOffset, outputPixelOffset, "indexed BMP pixel offset");
        AssertEqual((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(28, 2)), "indexed BMP bit depth");
        AssertEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(30, 4)), "indexed BMP BI_RGB compression");
        AssertEqual(outputWidth, BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(18, 4)), "indexed BMP output width");
        AssertEqual(outputHeight, BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(22, 4)), "indexed BMP output height");
        AssertEqual(
            PaletteColorCount,
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(46, 4))),
            "indexed BMP declared palette size");

        for (var index = 0; index < pixelOffset; index++)
        {
            if (IsRewrittenSizeOrDimensionByte(index))
            {
                continue;
            }

            AssertEqual(
                source[index],
                encoded[index],
                $"indexed BMP header/palette byte {index}");
        }

        var expectedStride = (outputWidth + 3) & ~3;
        var expectedImageBytes = expectedStride * outputHeight;
        AssertEqual(pixelOffset + expectedImageBytes, encoded.Length, "indexed BMP output length");
        AssertEqual(
            checked((uint)encoded.Length),
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(2, 4)),
            "indexed BMP file-size field");
        AssertEqual(
            checked((uint)expectedImageBytes),
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(34, 4)),
            "indexed BMP image-size field");

        var keyedPixels = 0;
        for (var y = 0; y < outputHeight; y++)
        {
            for (var x = 0; x < outputWidth; x++)
            {
                var outputIndex = ReadNormalizedIndex(encoded, x, y);
                Assert(
                    outputIndex < PaletteColorCount,
                    "every reconstructed pixel must reference the declared palette");

                var sourceIndex = ReadNormalizedIndex(source, x / scale, y / scale);
                if (sourceIndex == KeyedPaletteIndex)
                {
                    keyedPixels++;
                    AssertEqual(
                        KeyedPaletteIndex,
                        outputIndex,
                        "nearest-neighbor keyed border mask");
                }
                else
                {
                    Assert(
                        outputIndex != KeyedPaletteIndex,
                        "opaque artwork must not be quantized into the keyed palette entry");
                }
            }
        }

        Assert(keyedPixels > 0, "indexed BMP fixture should contain a keyed border");
        Assert(
            keyedPixels < outputWidth * outputHeight,
            "indexed BMP fixture should retain non-keyed artwork inside its border");

        var lowColor = CreateIndexedBitmap(sourceWidth, sourceHeight, richPaletteUse: false);
        Assert(!LegacyIndexedBmp.CanEncode(lowColor), "low-color indexed BMP should remain protected");
        AssertThrows<NotSupportedException>(
            () => LegacyIndexedBmp.Encode(lowColor, outputWidth, outputHeight, enhancedRgba),
            "low-color indexed BMP encode rejection");

        var tiny = CreateIndexedBitmap(width: 16, height: 16, richPaletteUse: true);
        Assert(!LegacyIndexedBmp.CanEncode(tiny), "tiny indexed BMP should remain protected");
        AssertThrows<NotSupportedException>(
            () => LegacyIndexedBmp.Encode(tiny, width: 64, height: 64, new byte[64 * 64 * 4]),
            "tiny indexed BMP encode rejection");
    }

    private static byte[] CreateFullyTransparentMagentaDxt5(int width, int height)
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
            bytes[offset] = 0;
            bytes[offset + 1] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8, 2), 0xF81F);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10, 2), 0xF81F);
        }

        return bytes;
    }

    private static void AssertFullyTransparentMagentaDxt5(ReadOnlySpan<byte> bytes)
    {
        Assert(bytes.Length >= 144, "DXT5 sentinel should contain at least one block");
        Assert(bytes[..4].SequenceEqual("DDS "u8), "DXT5 sentinel magic");
        Assert(bytes.Slice(84, 4).SequenceEqual("DXT5"u8), "DXT5 sentinel format");
        AssertEqual(0, (bytes.Length - 128) % 16, "DXT5 sentinel block alignment");

        for (var offset = 128; offset < bytes.Length; offset += 16)
        {
            AssertEqual((byte)0, bytes[offset], "DXT5 sentinel alpha endpoint zero");
            AssertEqual((byte)0, bytes[offset + 1], "DXT5 sentinel alpha endpoint one");
            Assert(
                bytes.Slice(offset + 2, 6).IndexOfAnyExcept((byte)0) < 0,
                "DXT5 sentinel alpha selectors should all select transparent alpha");
            AssertEqual(
                (ushort)0xF81F,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 8, 2)),
                "DXT5 sentinel first RGB565 endpoint");
            AssertEqual(
                (ushort)0xF81F,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 10, 2)),
                "DXT5 sentinel second RGB565 endpoint");
            Assert(
                bytes.Slice(offset + 12, 4).IndexOfAnyExcept((byte)0) < 0,
                "DXT5 sentinel color selectors should all select magenta");
        }
    }

    private static byte[] CreateIndexedBitmap(int width, int height, bool richPaletteUse)
    {
        const int fileHeaderSize = 14;
        const int dibHeaderSize = 56;
        const int paletteGapBytes = 11;
        var paletteOffset = fileHeaderSize + dibHeaderSize;
        var pixelOffset = paletteOffset + (PaletteColorCount * 4) + paletteGapBytes;
        var stride = (width + 3) & ~3;
        var imageBytes = checked(stride * height);
        var bytes = new byte[pixelOffset + imageBytes];

        "BM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), checked((uint)bytes.Length));
        bytes[6] = 0x51;
        bytes[7] = 0x27;
        bytes[8] = 0xA3;
        bytes[9] = 0x6C;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), checked((uint)pixelOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), dibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34, 4), checked((uint)imageBytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(38, 4), 2_835);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(42, 4), 2_835);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(46, 4), PaletteColorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(50, 4), 23);
        for (var index = 54; index < fileHeaderSize + dibHeaderSize; index++)
        {
            bytes[index] = checked((byte)(0x80 + index - 54));
        }

        for (var paletteIndex = 0; paletteIndex < PaletteColorCount; paletteIndex++)
        {
            var offset = paletteOffset + (paletteIndex * 4);
            bytes[offset] = checked((byte)((paletteIndex * 47 + 13) & 0xFF));
            bytes[offset + 1] = checked((byte)((paletteIndex * 71 + 29) & 0xFF));
            bytes[offset + 2] = checked((byte)((paletteIndex * 97 + 43) & 0xFF));
            bytes[offset + 3] = checked((byte)((paletteIndex * 19 + 5) & 0xFF));
        }

        var keyOffset = paletteOffset + (KeyedPaletteIndex * 4);
        bytes[keyOffset] = byte.MaxValue;
        bytes[keyOffset + 1] = byte.MinValue;
        bytes[keyOffset + 2] = byte.MaxValue;
        bytes[keyOffset + 3] = byte.MinValue;
        for (var index = paletteOffset + (PaletteColorCount * 4); index < pixelOffset; index++)
        {
            bytes[index] = checked((byte)(0xD0 + index - (paletteOffset + (PaletteColorCount * 4))));
        }

        var borderWidth = Math.Max(1, Math.Min(5, Math.Min(width, height) / 4));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var keyed = x < borderWidth
                    || y < borderWidth
                    || x >= width - borderWidth
                    || y >= height - borderWidth;
                byte paletteIndex;
                if (keyed)
                {
                    paletteIndex = KeyedPaletteIndex;
                }
                else if (!richPaletteUse)
                {
                    paletteIndex = 3;
                }
                else
                {
                    paletteIndex = checked((byte)((x * 5 + y * 3) % PaletteColorCount));
                    if (paletteIndex == KeyedPaletteIndex)
                    {
                        paletteIndex = PaletteColorCount - 1;
                    }
                }

                WriteNormalizedIndex(bytes, width, height, pixelOffset, x, y, paletteIndex);
            }
        }

        return bytes;
    }

    private static byte ReadNormalizedIndex(ReadOnlySpan<byte> bitmap, int x, int y)
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(bitmap.Slice(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bitmap.Slice(22, 4));
        var height = Math.Abs(signedHeight);
        var pixelOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bitmap.Slice(10, 4)));
        var stride = (width + 3) & ~3;
        var fileY = signedHeight < 0 ? y : height - 1 - y;
        return bitmap[pixelOffset + (fileY * stride) + x];
    }

    private static void WriteNormalizedIndex(
        Span<byte> bitmap,
        int width,
        int height,
        int pixelOffset,
        int x,
        int y,
        byte paletteIndex)
    {
        var stride = (width + 3) & ~3;
        var fileY = height - 1 - y;
        bitmap[pixelOffset + (fileY * stride) + x] = paletteIndex;
    }

    private static bool IsRewrittenSizeOrDimensionByte(int index) =>
        index is >= 2 and < 6
            or >= 18 and < 26
            or >= 34 and < 38;

    private static byte[] CreateDeterministicBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        var state = (uint)seed + 1;
        for (var index = 0; index < bytes.Length; index++)
        {
            state = (state * 1_664_525) + 1_013_904_223;
            bytes[index] = checked((byte)(state >> 24));
        }

        return bytes;
    }

    private static byte[] CreateNearestIndexedRgba(
        ReadOnlySpan<byte> bitmap,
        int width,
        int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        var sourceWidth = BinaryPrimitives.ReadInt32LittleEndian(bitmap.Slice(18, 4));
        var sourceHeight = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bitmap.Slice(22, 4)));
        var dibSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bitmap.Slice(14, 4)));
        var paletteOffset = 14 + dibSize;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / width));
                var sourceY = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / height));
                var paletteIndex = ReadNormalizedIndex(bitmap, sourceX, sourceY);
                var colorOffset = paletteOffset + (paletteIndex * 4);
                var outputOffset = ((y * width) + x) * 4;
                rgba[outputOffset] = bitmap[colorOffset + 2];
                rgba[outputOffset + 1] = bitmap[colorOffset + 1];
                rgba[outputOffset + 2] = bitmap[colorOffset];
                rgba[outputOffset + 3] = byte.MaxValue;
            }
        }

        return rgba;
    }

    private static void AssertThrows<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Self-test failed: {description} did not throw {typeof(TException).Name}.");
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
                $"Self-test failed: {description}. Expected '{expected}', received '{actual}'.");
        }
    }

    private static void AssertSequenceEqual(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }
}
