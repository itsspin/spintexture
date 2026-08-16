using System.Buffers.Binary;

namespace SpinTexture.Core.Textures;

/// <summary>
/// The decoded pixels of a classic client texture, in 32-bit BGRA rows from
/// the top-left corner, ready for direct display.
/// </summary>
public sealed record DecodedTexturePreview(int Width, int Height, byte[] BgraPixels);

/// <summary>
/// Pure managed decoding for the legacy texture containers the classic client
/// ships inside PFS archives: uncompressed 8/24/32-bit Windows bitmaps and
/// uncompressed 24/32-bit Targa images. Block-compressed DDS payloads are not
/// handled here; callers fall back to the native toolchain for those.
/// </summary>
public static class ClassicTextureDecoder
{
    private const int MaximumPreviewDimension = 8192;

    public static DecodedTexturePreview? TryDecode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2 && payload[0] == (byte)'B' && payload[1] == (byte)'M')
        {
            return TryDecodeBmp(payload);
        }

        return TryDecodeTga(payload);
    }

    private static DecodedTexturePreview? TryDecodeBmp(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 54)
        {
            return null;
        }

        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(payload[10..]);
        var infoSize = BinaryPrimitives.ReadInt32LittleEndian(payload[14..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(payload[18..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[22..]);
        var planes = BinaryPrimitives.ReadInt16LittleEndian(payload[26..]);
        var bitsPerPixel = BinaryPrimitives.ReadInt16LittleEndian(payload[28..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(payload[30..]);
        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        if (infoSize < 40
            || planes != 1
            || compression != 0
            || width <= 0
            || height <= 0
            || width > MaximumPreviewDimension
            || height > MaximumPreviewDimension
            || pixelOffset <= 0
            || pixelOffset > payload.Length)
        {
            return null;
        }

        return bitsPerPixel switch
        {
            8 => DecodeIndexedBmp(payload, pixelOffset, infoSize, width, height, topDown),
            24 or 32 => DecodeRichBmp(payload, pixelOffset, width, height, topDown, bitsPerPixel),
            _ => null
        };
    }

    private static DecodedTexturePreview? DecodeIndexedBmp(
        ReadOnlySpan<byte> payload,
        int pixelOffset,
        int infoSize,
        int width,
        int height,
        bool topDown)
    {
        var paletteColors = BinaryPrimitives.ReadInt32LittleEndian(payload[46..]);
        if (paletteColors <= 0 || paletteColors > 256)
        {
            paletteColors = 256;
        }

        var paletteOffset = 14 + infoSize;
        if (paletteOffset + paletteColors * 4 > payload.Length)
        {
            return null;
        }

        var stride = (width + 3) & ~3;
        if (pixelOffset + (long)stride * height > payload.Length)
        {
            return null;
        }

        var bgra = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
        {
            var sourceRow = topDown ? row : height - 1 - row;
            var rowStart = pixelOffset + sourceRow * stride;
            for (var x = 0; x < width; x++)
            {
                var index = payload[rowStart + x];
                var color = index < paletteColors ? paletteOffset + index * 4 : paletteOffset;
                var destination = (row * width + x) * 4;
                bgra[destination] = payload[color];
                bgra[destination + 1] = payload[color + 1];
                bgra[destination + 2] = payload[color + 2];
                bgra[destination + 3] = byte.MaxValue;
            }
        }

        return new DecodedTexturePreview(width, height, bgra);
    }

    private static DecodedTexturePreview? DecodeRichBmp(
        ReadOnlySpan<byte> payload,
        int pixelOffset,
        int width,
        int height,
        bool topDown,
        int bitsPerPixel)
    {
        var bytesPerPixel = bitsPerPixel / 8;
        var stride = ((width * bytesPerPixel) + 3) & ~3;
        if (pixelOffset + (long)stride * height > payload.Length)
        {
            return null;
        }

        var bgra = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
        {
            var sourceRow = topDown ? row : height - 1 - row;
            var rowStart = pixelOffset + sourceRow * stride;
            for (var x = 0; x < width; x++)
            {
                var source = rowStart + x * bytesPerPixel;
                var destination = (row * width + x) * 4;
                bgra[destination] = payload[source];
                bgra[destination + 1] = payload[source + 1];
                bgra[destination + 2] = payload[source + 2];
                bgra[destination + 3] = bytesPerPixel == 4 ? payload[source + 3] : byte.MaxValue;
            }
        }

        return new DecodedTexturePreview(width, height, bgra);
    }

    private static DecodedTexturePreview? TryDecodeTga(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 18)
        {
            return null;
        }

        var idLength = payload[0];
        var colorMapType = payload[1];
        var imageType = payload[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        var bitsPerPixel = payload[16];
        var descriptor = payload[17];
        if (colorMapType != 0
            || imageType != 2
            || width == 0
            || height == 0
            || width > MaximumPreviewDimension
            || height > MaximumPreviewDimension
            || bitsPerPixel is not (24 or 32))
        {
            return null;
        }

        var bytesPerPixel = bitsPerPixel / 8;
        var pixelOffset = 18 + idLength;
        if (pixelOffset + (long)width * height * bytesPerPixel > payload.Length)
        {
            return null;
        }

        // Descriptor bit 5: origin at the top; otherwise rows start at the bottom.
        var topDown = (descriptor & 0x20) != 0;
        var bgra = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
        {
            var sourceRow = topDown ? row : height - 1 - row;
            var rowStart = pixelOffset + sourceRow * width * bytesPerPixel;
            for (var x = 0; x < width; x++)
            {
                var source = rowStart + x * bytesPerPixel;
                var destination = (row * width + x) * 4;
                bgra[destination] = payload[source];
                bgra[destination + 1] = payload[source + 1];
                bgra[destination + 2] = payload[source + 2];
                bgra[destination + 3] = bytesPerPixel == 4 ? payload[source + 3] : byte.MaxValue;
            }
        }

        return new DecodedTexturePreview(width, height, bgra);
    }
}
