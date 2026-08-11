using System.Buffers.Binary;

namespace SpinTexture.Core.Textures;

internal enum TextureTopMipAlphaKind
{
    Unknown,
    Opaque,
    AlphaTestedCutout,
    SoftOrIntermediate,
    FullyTransparent
}

/// <summary>
/// Performs a conservative, allocation-free inspection of the top mip in the
/// legacy block-compressed DDS formats used by EverQuest. Supported layouts
/// distinguish opaque, alpha-tested, soft, and fully transparent data;
/// unsupported or threshold-ambiguous layouts fall back to decoded inspection.
/// </summary>
internal static class TextureAlphaInspector
{
    private const int LegacyDdsHeaderBytes = 128;

    /// <summary>
    /// Classifies the real pixels in the top mip without decoding the texture
    /// through an external process. Unsupported, DX10, or truncated payloads
    /// return Unknown so callers can choose a conservative decoded fallback.
    /// Thresholds intentionally match TgaPixelBuffer.HasLikelyCutoutAlpha.
    /// </summary>
    public static TextureTopMipAlphaKind ClassifyTopMipAlpha(
        ReadOnlySpan<byte> payload,
        TextureMetadata metadata)
    {
        if (metadata.FileFormat != TextureFileFormat.Dds
            || metadata.UsesDx10Header
            || payload.Length < LegacyDdsHeaderBytes
            || !payload[..4].SequenceEqual("DDS "u8))
        {
            return TextureTopMipAlphaKind.Unknown;
        }

        return metadata.TexconvFormat?.ToUpperInvariant() switch
        {
            "BC1_UNORM" => ClassifyBc1(payload, metadata.Width, metadata.Height),
            "BC2_UNORM" => ClassifyBc2(payload, metadata.Width, metadata.Height),
            "BC3_UNORM" => ClassifyBc3(payload, metadata.Width, metadata.Height),
            _ => TextureTopMipAlphaKind.Unknown
        };
    }

    public static bool IsKnownFullyTransparent(
        ReadOnlySpan<byte> payload,
        TextureMetadata metadata) =>
        ClassifyTopMipAlpha(payload, metadata)
            == TextureTopMipAlphaKind.FullyTransparent;

    private static TextureTopMipAlphaKind ClassifyBc1(
        ReadOnlySpan<byte> payload,
        int width,
        int height)
    {
        if (!TryGetBlockLayout(payload, width, height, 8, out var blocksWide, out var blocksHigh))
        {
            return TextureTopMipAlphaKind.Unknown;
        }

        long transparent = 0;
        long opaque = 0;
        long intermediate = 0;
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var block = payload.Slice(
                    LegacyDdsHeaderBytes + (((blockY * blocksWide) + blockX) * 8),
                    8);
                var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block[..2]);
                var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(2, 2));
                var indices = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4, 4));
                var validWidth = Math.Min(4, width - (blockX * 4));
                var validHeight = Math.Min(4, height - (blockY * 4));
                for (var localY = 0; localY < validHeight; localY++)
                {
                    for (var localX = 0; localX < validWidth; localX++)
                    {
                        var shift = ((localY * 4) + localX) * 2;
                        var alpha = color0 <= color1
                            && ((indices >> shift) & 0x3u) == 3u
                                ? byte.MinValue
                                : byte.MaxValue;
                        CountAlpha(alpha, ref transparent, ref opaque, ref intermediate);
                    }
                }
            }
        }

        return FinishClassification(width, height, transparent, opaque, intermediate);
    }

    private static TextureTopMipAlphaKind ClassifyBc2(
        ReadOnlySpan<byte> payload,
        int width,
        int height)
    {
        if (!TryGetBlockLayout(payload, width, height, 16, out var blocksWide, out var blocksHigh))
        {
            return TextureTopMipAlphaKind.Unknown;
        }

        long transparent = 0;
        long opaque = 0;
        long intermediate = 0;
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var block = payload.Slice(
                    LegacyDdsHeaderBytes + (((blockY * blocksWide) + blockX) * 16),
                    16);
                var alphaBits = BinaryPrimitives.ReadUInt64LittleEndian(block[..8]);
                var validWidth = Math.Min(4, width - (blockX * 4));
                var validHeight = Math.Min(4, height - (blockY * 4));
                for (var localY = 0; localY < validHeight; localY++)
                {
                    for (var localX = 0; localX < validWidth; localX++)
                    {
                        var shift = ((localY * 4) + localX) * 4;
                        var alpha = checked((byte)(((alphaBits >> shift) & 0xFuL) * 17));
                        CountAlpha(alpha, ref transparent, ref opaque, ref intermediate);
                    }
                }
            }
        }

        return FinishClassification(width, height, transparent, opaque, intermediate);
    }

    private static TextureTopMipAlphaKind ClassifyBc3(
        ReadOnlySpan<byte> payload,
        int width,
        int height)
    {
        if (!TryGetBlockLayout(payload, width, height, 16, out var blocksWide, out var blocksHigh))
        {
            return TextureTopMipAlphaKind.Unknown;
        }

        long transparent = 0;
        long opaque = 0;
        long intermediate = 0;
        Span<byte> palette = stackalloc byte[8];
        Span<bool> ambiguous = stackalloc bool[8];
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var block = payload.Slice(
                    LegacyDdsHeaderBytes + (((blockY * blocksWide) + blockX) * 16),
                    16);
                BuildBc3AlphaPalette(block[0], block[1], palette);
                BuildBc3AmbiguousPalette(block[0], block[1], ambiguous);
                ulong indices = 0;
                for (var byteIndex = 0; byteIndex < 6; byteIndex++)
                {
                    indices |= (ulong)block[2 + byteIndex] << (byteIndex * 8);
                }

                var validWidth = Math.Min(4, width - (blockX * 4));
                var validHeight = Math.Min(4, height - (blockY * 4));
                for (var localY = 0; localY < validHeight; localY++)
                {
                    for (var localX = 0; localX < validWidth; localX++)
                    {
                        var shift = ((localY * 4) + localX) * 3;
                        var paletteIndex = (int)((indices >> shift) & 0x7uL);
                        if (ambiguous[paletteIndex])
                        {
                            return TextureTopMipAlphaKind.Unknown;
                        }

                        CountAlpha(
                            palette[paletteIndex],
                            ref transparent,
                            ref opaque,
                            ref intermediate);
                    }
                }
            }
        }

        return FinishClassification(width, height, transparent, opaque, intermediate);
    }

    private static bool TryGetBlockLayout(
        ReadOnlySpan<byte> payload,
        int width,
        int height,
        int blockBytes,
        out int blocksWide,
        out int blocksHigh)
    {
        blocksWide = 0;
        blocksHigh = 0;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        try
        {
            blocksWide = checked((width + 3) / 4);
            blocksHigh = checked((height + 3) / 4);
            var required = checked(
                LegacyDdsHeaderBytes + (blocksWide * blocksHigh * blockBytes));
            return payload.Length >= required;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void CountAlpha(
        byte alpha,
        ref long transparent,
        ref long opaque,
        ref long intermediate)
    {
        if (alpha <= 8)
        {
            transparent++;
        }
        else if (alpha >= 247)
        {
            opaque++;
        }
        else
        {
            intermediate++;
        }
    }

    private static TextureTopMipAlphaKind FinishClassification(
        int width,
        int height,
        long transparent,
        long opaque,
        long intermediate)
    {
        var pixels = checked((long)width * height);
        if (transparent == pixels)
        {
            return TextureTopMipAlphaKind.FullyTransparent;
        }

        if (opaque == pixels)
        {
            return TextureTopMipAlphaKind.Opaque;
        }

        return transparent > 0
            && opaque > 0
            && intermediate <= Math.Max(1, pixels / 20)
                ? TextureTopMipAlphaKind.AlphaTestedCutout
                : TextureTopMipAlphaKind.SoftOrIntermediate;
    }

    private static void BuildBc3AmbiguousPalette(
        byte alpha0,
        byte alpha1,
        Span<bool> ambiguous)
    {
        ambiguous.Clear();
        if (alpha0 > alpha1)
        {
            for (var index = 1; index <= 6; index++)
            {
                MarkIfThresholdAmbiguous(
                    ((7 - index) * alpha0) + (index * alpha1),
                    7,
                    ambiguous,
                    index + 1);
            }
        }
        else
        {
            for (var index = 1; index <= 4; index++)
            {
                MarkIfThresholdAmbiguous(
                    ((5 - index) * alpha0) + (index * alpha1),
                    5,
                    ambiguous,
                    index + 1);
            }
        }
    }

    private static void MarkIfThresholdAmbiguous(
        int numerator,
        int divisor,
        Span<bool> ambiguous,
        int paletteIndex)
    {
        var lower = numerator / divisor;
        if (numerator % divisor == 0)
        {
            return;
        }

        var upper = lower + 1;
        ambiguous[paletteIndex] = AlphaBucket(lower) != AlphaBucket(upper);
    }

    private static int AlphaBucket(int alpha) =>
        alpha <= 8 ? 0 : alpha >= 247 ? 2 : 1;

    private static void BuildBc3AlphaPalette(byte alpha0, byte alpha1, Span<byte> palette)
    {
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 1; index <= 6; index++)
            {
                palette[index + 1] = (byte)(((7 - index) * alpha0 + (index * alpha1)) / 7);
            }
        }
        else
        {
            for (var index = 1; index <= 4; index++)
            {
                palette[index + 1] = (byte)(((5 - index) * alpha0 + (index * alpha1)) / 5);
            }

            palette[6] = 0;
            palette[7] = byte.MaxValue;
        }
    }
}
