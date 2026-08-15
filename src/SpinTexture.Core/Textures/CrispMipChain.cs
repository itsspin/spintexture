using System.Buffers.Binary;

namespace SpinTexture.Core.Textures;

/// <summary>
/// "Crisp distance detail": most of what a player actually sees in the world
/// is mip levels, not the top texture, and every halving step washes out a
/// little contrast. This builder generates the full mip chain on the CPU
/// (2x2 box reduction, wrap-aware) and re-sharpens each level below the top
/// with a bounded unsharp pass that grows gently with mip depth, then packs
/// the chain into an uncompressed BGRA DDS. texconv receives that DDS at
/// final dimensions with a matching mip count, so it only block-compresses —
/// it never resizes or regenerates the chain. The top level is written
/// byte-exact; only the distance levels change.
/// </summary>
internal static class CrispMipChain
{
    /// <summary>
    /// Builds the pre-mipped DDS payload. The caller guarantees color (not
    /// vector/mask) content; alpha bytes are carried through untouched.
    /// </summary>
    public static byte[] BuildPreMippedDds(
        TgaPixelBuffer top,
        int mipCount,
        double sharpenStrength,
        bool wrapEdges)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentOutOfRangeException.ThrowIfLessThan(mipCount, 1);
        var strength = (float)Math.Clamp(sharpenStrength, 0d, 1d);
        var levels = new List<(int Width, int Height, byte[] Rgba)>(mipCount)
        {
            (top.Width, top.Height, top.RgbaPixels.ToArray())
        };
        for (var level = 1; level < mipCount; level++)
        {
            var (previousWidth, previousHeight, previousRgba) = levels[level - 1];
            var width = Math.Max(1, previousWidth >> 1);
            var height = Math.Max(1, previousHeight >> 1);
            var reduced = BoxReduce(previousRgba, previousWidth, previousHeight, width, height);
            if (strength > 0f && width >= 4 && height >= 4)
            {
                // Deeper mips have survived more filtering, so they get a
                // slightly stronger (but bounded) contrast recovery.
                var amount = strength * MathF.Min(0.65f, 0.22f * level);
                Unsharp(reduced, width, height, amount, wrapEdges);
            }

            levels.Add((width, height, reduced));
        }

        return WriteDds(levels);
    }

    private static byte[] BoxReduce(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height)
    {
        var result = new byte[width * height * 4];
        var stepX = sourceWidth / width;
        var stepY = sourceHeight / height;
        var samples = stepX * stepY;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (var sampleY = 0; sampleY < stepY; sampleY++)
                {
                    var rowOffset = ((((y * stepY) + sampleY) * sourceWidth) + (x * stepX)) * 4;
                    for (var sampleX = 0; sampleX < stepX; sampleX++)
                    {
                        var offset = rowOffset + (sampleX * 4);
                        r += source[offset];
                        g += source[offset + 1];
                        b += source[offset + 2];
                        a += source[offset + 3];
                    }
                }

                var target = (((y * width) + x)) * 4;
                result[target] = (byte)((r + (samples / 2)) / samples);
                result[target + 1] = (byte)((g + (samples / 2)) / samples);
                result[target + 2] = (byte)((b + (samples / 2)) / samples);
                result[target + 3] = (byte)((a + (samples / 2)) / samples);
            }
        }

        return result;
    }

    private static void Unsharp(byte[] rgba, int width, int height, float amount, bool wrap)
    {
        var pixelCount = width * height;
        var blurred = new float[pixelCount * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, weightSum = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var sampleX = Wrap(x + dx, width, wrap);
                        var sampleY = Wrap(y + dy, height, wrap);
                        // 3x3 tent kernel.
                        var weight = (dx == 0 ? 2f : 1f) * (dy == 0 ? 2f : 1f);
                        var offset = (((sampleY * width) + sampleX)) * 4;
                        r += rgba[offset] * weight;
                        g += rgba[offset + 1] * weight;
                        b += rgba[offset + 2] * weight;
                        weightSum += weight;
                    }
                }

                var target = ((y * width) + x) * 3;
                blurred[target] = r / weightSum;
                blurred[target + 1] = g / weightSum;
                blurred[target + 2] = b / weightSum;
            }
        }

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            var blurOffset = pixel * 3;
            rgba[offset] = ClampByte(rgba[offset] + (amount * (rgba[offset] - blurred[blurOffset])));
            rgba[offset + 1] = ClampByte(rgba[offset + 1] + (amount * (rgba[offset + 1] - blurred[blurOffset + 1])));
            rgba[offset + 2] = ClampByte(rgba[offset + 2] + (amount * (rgba[offset + 2] - blurred[blurOffset + 2])));
        }
    }

    private static int Wrap(int position, int length, bool wrap)
    {
        if (wrap)
        {
            var modulo = position % length;
            return modulo < 0 ? modulo + length : modulo;
        }

        return Math.Clamp(position, 0, length - 1);
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp(value, 0f, 255f);

    /// <summary>
    /// Classic DX9-style uncompressed A8R8G8B8 DDS with a full mip chain —
    /// the most conservative layout every texconv build reads unchanged.
    /// </summary>
    private static byte[] WriteDds(IReadOnlyList<(int Width, int Height, byte[] Rgba)> levels)
    {
        var payloadBytes = levels.Sum(level => level.Rgba.Length);
        var dds = new byte[4 + 124 + payloadBytes];
        var span = dds.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x20534444); // "DDS "
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124); // dwSize
        // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT | MIPMAPCOUNT
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)levels[0].Height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)levels[0].Width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(levels[0].Width * 4)); // pitch
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0); // depth
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)levels.Count);
        // 11 reserved dwords already zero.
        var pixelFormat = span[76..];
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat, 32); // ddpf size
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[4..], 0x1 | 0x40); // ALPHAPIXELS | RGB
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[12..], 32); // bit count
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[16..], 0x00FF0000); // R
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[20..], 0x0000FF00); // G
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[24..], 0x000000FF); // B
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[28..], 0xFF000000); // A
        // caps: COMPLEX | TEXTURE | MIPMAP
        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x8 | 0x1000 | 0x400000);

        var cursor = 4 + 124;
        foreach (var (width, height, rgba) in levels)
        {
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                var offset = pixel * 4;
                // RGBA memory -> A8R8G8B8 little-endian memory order BGRA.
                dds[cursor++] = rgba[offset + 2];
                dds[cursor++] = rgba[offset + 1];
                dds[cursor++] = rgba[offset];
                dds[cursor++] = rgba[offset + 3];
            }
        }

        return dds;
    }
}
