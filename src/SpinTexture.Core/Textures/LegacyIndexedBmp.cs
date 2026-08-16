using System.Buffers.Binary;

namespace SpinTexture.Core.Textures;

/// <summary>
/// Re-encodes legacy 8-bit EverQuest bitmaps without changing their indexed
/// representation. The original palette and header extension are retained;
/// enhanced RGB pixels are mapped back to the nearest original palette entry.
/// </summary>
internal static class LegacyIndexedBmp
{
    private const int FileHeaderSize = 14;
    private const int MinimumInfoHeaderSize = 40;
    private const int QuantizedColorCount = 32 * 32 * 32;

    public static bool CanEncode(ReadOnlySpan<byte> source)
    {
        // Thin classic trim strips (16-wide beams and pillars) and low-color
        // painted surfaces are legitimate world art; only genuinely tiny or
        // near-degenerate palettes stay on the preserved path.
        return TryReadLayout(source, out var layout)
            && layout.Width >= 16
            && layout.Height >= 16
            && CountUsedIndices(source, layout) >= 8;
    }

    public static byte[] Encode(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        byte? forcedKeyIndex = null,
        bool allowHeuristicKey = true)
    {
        if (!CanEncode(source) || !TryReadLayout(source, out var layout))
        {
            throw new NotSupportedException(
                "Only uncompressed 8-bit Windows BMP textures with a complete palette can be safely reconstructed.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var expectedRgbaLength = checked(width * height * 4);
        if (rgba.Length != expectedRgbaLength)
        {
            throw new ArgumentException(
                $"Expected {expectedRgbaLength:N0} RGBA bytes, received {rgba.Length:N0}.",
                nameof(rgba));
        }

        var stride = checked((width + 3) & ~3);
        var imageBytes = checked(stride * height);
        var outputLength = checked(layout.PixelOffset + imageBytes);
        var output = new byte[outputLength];
        source[..layout.PixelOffset].CopyTo(output);

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2, 4), checked((uint)outputLength));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(
            output.AsSpan(22, 4),
            layout.TopDown ? -height : height);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(34, 4), checked((uint)imageBytes));

        // A caller that knows the renderer's contract (a WLD material flagged
        // palette-masked) dictates the key outright; the statistical border
        // heuristic only serves textures with no authoritative source, and it
        // can fail on layouts whose art touches the border.
        var keyedIndex = forcedKeyIndex is { } forced && forced < layout.ColorCount
            ? forced
            : allowHeuristicKey
                ? FindLikelyKeyedIndex(source, layout)
                : null;
        var paletteLookup = BuildPaletteLookup(source, layout, keyedIndex);
        for (var y = 0; y < height; y++)
        {
            var fileY = layout.TopDown ? y : height - 1 - y;
            var destinationRow = layout.PixelOffset + (fileY * stride);
            var sourceRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (keyedIndex.HasValue
                    && ReadSourceIndexNearest(source, layout, x, y, width, height) == keyedIndex.Value)
                {
                    output[destinationRow + x] = keyedIndex.Value;
                    continue;
                }

                var sourceOffset = sourceRow + (x * 4);
                var red = rgba[sourceOffset];
                var green = rgba[sourceOffset + 1];
                var blue = rgba[sourceOffset + 2];
                var lookupIndex = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
                output[destinationRow + x] = paletteLookup[lookupIndex];
            }
        }

        return output;
    }

    /// <summary>
    /// Checks that an enhanced 8-bit BMP keeps the source's keyed-index mask:
    /// every output pixel is keyed exactly when its nearest source pixel is.
    /// Used to detect earlier enhancements that lost color-key transparency
    /// (they re-encoded keyed backgrounds as opaque near-colors) so a repair
    /// treats them as cache misses instead of carrying them forward.
    /// </summary>
    public static bool KeyMaskPreserved(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> enhanced,
        byte keyIndex = 0)
    {
        if (!TryReadLayout(source, out var sourceLayout)
            || !TryReadLayout(enhanced, out var enhancedLayout)
            || keyIndex >= sourceLayout.ColorCount)
        {
            return false;
        }

        for (var y = 0; y < enhancedLayout.Height; y++)
        {
            var enhancedRow = GetSourceRowOffset(enhancedLayout, y);
            for (var x = 0; x < enhancedLayout.Width; x++)
            {
                var sourceIndex = ReadSourceIndexNearest(
                    source,
                    sourceLayout,
                    x,
                    y,
                    enhancedLayout.Width,
                    enhancedLayout.Height);
                if ((enhanced[enhancedRow + x] == keyIndex) != (sourceIndex == keyIndex))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static byte[] BuildPaletteLookup(
        ReadOnlySpan<byte> source,
        BmpLayout layout,
        byte? excludedKeyIndex)
    {
        var lookup = new byte[QuantizedColorCount];
        for (var redBucket = 0; redBucket < 32; redBucket++)
        {
            var red = (redBucket << 3) | 4;
            for (var greenBucket = 0; greenBucket < 32; greenBucket++)
            {
                var green = (greenBucket << 3) | 4;
                for (var blueBucket = 0; blueBucket < 32; blueBucket++)
                {
                    var blue = (blueBucket << 3) | 4;
                    var bestPaletteIndex = 0;
                    var bestDistance = long.MaxValue;
                    for (var paletteIndex = 0; paletteIndex < layout.ColorCount; paletteIndex++)
                    {
                        if (excludedKeyIndex.HasValue && paletteIndex == excludedKeyIndex.Value)
                        {
                            continue;
                        }

                        var paletteOffset = layout.PaletteOffset + (paletteIndex * 4);
                        var paletteBlue = source[paletteOffset];
                        var paletteGreen = source[paletteOffset + 1];
                        var paletteRed = source[paletteOffset + 2];
                        var redDelta = red - paletteRed;
                        var greenDelta = green - paletteGreen;
                        var blueDelta = blue - paletteBlue;

                        // Green carries most perceived luminance, while red is
                        // slightly more significant than blue. Integer weights
                        // keep the mapping deterministic on every machine.
                        var distance = (3L * redDelta * redDelta)
                                     + (6L * greenDelta * greenDelta)
                                     + (2L * blueDelta * blueDelta);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestPaletteIndex = paletteIndex;
                            if (distance == 0)
                            {
                                break;
                            }
                        }
                    }

                    var lookupIndex = (redBucket << 10) | (greenBucket << 5) | blueBucket;
                    lookup[lookupIndex] = checked((byte)bestPaletteIndex);
                }
            }
        }

        return lookup;
    }

    private static int CountUsedIndices(ReadOnlySpan<byte> source, BmpLayout layout)
    {
        Span<bool> used = stackalloc bool[256];
        var count = 0;
        for (var y = 0; y < layout.Height; y++)
        {
            var rowOffset = GetSourceRowOffset(layout, y);
            for (var x = 0; x < layout.Width; x++)
            {
                var index = source[rowOffset + x];
                if (index >= layout.ColorCount)
                {
                    return -1;
                }
                if (!used[index])
                {
                    used[index] = true;
                    count++;
                }
            }
        }

        return count;
    }

    private static byte? FindLikelyKeyedIndex(ReadOnlySpan<byte> source, BmpLayout layout)
    {
        Span<int> borderCounts = stackalloc int[256];
        Span<int> allCounts = stackalloc int[256];
        var borderSamples = 0;
        var totalSamples = checked(layout.Width * layout.Height);
        for (var y = 0; y < layout.Height; y++)
        {
            var rowOffset = GetSourceRowOffset(layout, y);
            for (var x = 0; x < layout.Width; x++)
            {
                var index = source[rowOffset + x];
                allCounts[index]++;
                if (x == 0 || y == 0 || x == layout.Width - 1 || y == layout.Height - 1)
                {
                    borderCounts[index]++;
                    borderSamples++;
                }
            }
        }

        var candidate = 0;
        for (var index = 1; index < borderCounts.Length; index++)
        {
            if (borderCounts[index] > borderCounts[candidate])
            {
                candidate = index;
            }
        }

        return borderCounts[candidate] >= Math.Ceiling(borderSamples * 0.80)
               && allCounts[candidate] >= Math.Ceiling(totalSamples * 0.15)
            ? checked((byte)candidate)
            : null;
    }

    private static byte ReadSourceIndexNearest(
        ReadOnlySpan<byte> source,
        BmpLayout layout,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight)
    {
        var sourceX = Math.Min(
            layout.Width - 1,
            (int)((long)outputX * layout.Width / outputWidth));
        var sourceY = Math.Min(
            layout.Height - 1,
            (int)((long)outputY * layout.Height / outputHeight));
        return source[GetSourceRowOffset(layout, sourceY) + sourceX];
    }

    private static int GetSourceRowOffset(BmpLayout layout, int normalizedY)
    {
        var fileY = layout.TopDown ? normalizedY : layout.Height - 1 - normalizedY;
        return layout.PixelOffset + (fileY * layout.SourceStride);
    }

    private static bool TryReadLayout(ReadOnlySpan<byte> source, out BmpLayout layout)
    {
        layout = default;
        if (source.Length < FileHeaderSize + MinimumInfoHeaderSize
            || source[0] != (byte)'B'
            || source[1] != (byte)'M')
        {
            return false;
        }

        var pixelOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(10, 4));
        var dibSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(14, 4));
        if (pixelOffsetValue > int.MaxValue
            || dibSizeValue < MinimumInfoHeaderSize
            || dibSizeValue > int.MaxValue)
        {
            return false;
        }

        var pixelOffset = (int)pixelOffsetValue;
        var dibSize = (int)dibSizeValue;
        if (FileHeaderSize + dibSize > source.Length
            || pixelOffset > source.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(26, 2)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(28, 2)) != 8
            || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(30, 4)) != 0)
        {
            return false;
        }

        var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(22, 4));
        if (signedWidth <= 0 || signedHeight is 0 or int.MinValue)
        {
            return false;
        }

        var colorsUsedValue = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(46, 4));
        var colorCount = colorsUsedValue == 0 ? 256 : (int)Math.Min(colorsUsedValue, 256u);
        if (colorCount is <= 0 or > 256)
        {
            return false;
        }

        var paletteOffset = FileHeaderSize + dibSize;
        var paletteBytes = checked(colorCount * 4);
        if (paletteOffset > pixelOffset || paletteBytes > pixelOffset - paletteOffset)
        {
            return false;
        }

        var sourceStride = checked((signedWidth + 3) & ~3);
        var sourceHeight = Math.Abs(signedHeight);
        var requiredPixelBytes = checked(sourceStride * sourceHeight);
        if (requiredPixelBytes > source.Length - pixelOffset)
        {
            return false;
        }

        layout = new BmpLayout(
            pixelOffset,
            paletteOffset,
            colorCount,
            signedWidth,
            sourceHeight,
            sourceStride,
            signedHeight < 0);
        return true;
    }

    private readonly record struct BmpLayout(
        int PixelOffset,
        int PaletteOffset,
        int ColorCount,
        int Width,
        int Height,
        int SourceStride,
        bool TopDown);
}
