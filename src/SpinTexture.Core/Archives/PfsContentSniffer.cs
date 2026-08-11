using System.Buffers.Binary;
using System.Text;

namespace SpinTexture.Core.Archives;

internal static class PfsContentSniffer
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PfsContentMetadata Sniff(ReadOnlySpan<byte> bytes, string filename)
    {
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual("DDS "u8))
        {
            return SniffDds(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return SniffBitmap(bytes);
        }

        if (bytes.Length >= PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return SniffPng(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return SniffJpeg(bytes);
        }

        if (Path.GetExtension(filename).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            return SniffTarga(bytes);
        }

        return new PfsContentMetadata(PfsContentKind.Unknown);
    }

    private static PfsContentMetadata SniffDds(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 128 || ReadUInt32(bytes, 4) != 124)
        {
            return new PfsContentMetadata(PfsContentKind.Dds);
        }

        var width = ToDimension(ReadUInt32(bytes, 16));
        var height = ToDimension(ReadUInt32(bytes, 12));
        var mipCountValue = ReadUInt32(bytes, 28);
        var mipCount = mipCountValue is 0 or > int.MaxValue ? 1 : (int)mipCountValue;
        var pixelFormatFlags = ReadUInt32(bytes, 80);
        var fourCcBytes = bytes.Slice(84, 4);
        var fourCc = IsPrintableAscii(fourCcBytes)
            ? Encoding.ASCII.GetString(fourCcBytes)
            : string.Empty;

        string pixelFormat;
        bool hasAlpha;
        if (!string.IsNullOrWhiteSpace(fourCc))
        {
            pixelFormat = fourCc.TrimEnd('\0', ' ');
            hasAlpha = pixelFormat is "DXT2" or "DXT3" or "DXT4" or "DXT5";

            if (pixelFormat == "DX10" && bytes.Length >= 148)
            {
                var dxgiFormat = ReadUInt32(bytes, 128);
                pixelFormat = GetDxgiFormatName(dxgiFormat);
                hasAlpha = dxgiFormat is 74 or 75 or 77 or 78 or 98 or 99;
            }
        }
        else
        {
            var bitsPerPixel = ReadUInt32(bytes, 88);
            pixelFormat = bitsPerPixel == 0 ? "RGB" : $"RGB{bitsPerPixel}";
            var alphaMask = ReadUInt32(bytes, 104);
            hasAlpha = (pixelFormatFlags & 0x1) != 0 || alphaMask != 0;
        }

        return new PfsContentMetadata(
            PfsContentKind.Dds,
            width,
            height,
            mipCount,
            pixelFormat,
            hasAlpha);
    }

    private static PfsContentMetadata SniffBitmap(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26)
        {
            return new PfsContentMetadata(PfsContentKind.Bitmap);
        }

        var dibSize = ReadUInt32(bytes, 14);
        int width;
        int height;
        uint bitsPerPixel;
        uint compression;

        if (dibSize == 12 && bytes.Length >= 26)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[24..]);
            compression = 0;
        }
        else if (dibSize >= 40 && bytes.Length >= 34)
        {
            width = PositiveDimension(BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]));
            height = PositiveDimension(BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]));
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]);
            compression = ReadUInt32(bytes, 30);
        }
        else
        {
            return new PfsContentMetadata(PfsContentKind.Bitmap);
        }

        return new PfsContentMetadata(
            PfsContentKind.Bitmap,
            width,
            height,
            1,
            $"BMP{bitsPerPixel}/C{compression}",
            bitsPerPixel == 32);
    }

    private static PfsContentMetadata SniffPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return new PfsContentMetadata(PfsContentKind.Png);
        }

        var width = ToDimension(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]));
        var height = ToDimension(BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        var bitDepth = bytes[24];
        var colorType = bytes[25];
        var hasAlpha = colorType is 4 or 6;

        return new PfsContentMetadata(
            PfsContentKind.Png,
            width,
            height,
            1,
            $"PNG{bitDepth}/C{colorType}",
            hasAlpha);
    }

    private static PfsContentMetadata SniffJpeg(ReadOnlySpan<byte> bytes)
    {
        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            while (position < bytes.Length && bytes[position] != 0xFF)
            {
                position++;
            }

            while (position < bytes.Length && bytes[position] == 0xFF)
            {
                position++;
            }

            if (position >= bytes.Length)
            {
                break;
            }

            var marker = bytes[position++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (position + 2 > bytes.Length)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (segmentLength < 2 || position + segmentLength > bytes.Length)
            {
                break;
            }

            if (IsStartOfFrame(marker) && segmentLength >= 8)
            {
                var precision = bytes[position + 2];
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]);
                var components = bytes[position + 7];
                return new PfsContentMetadata(
                    PfsContentKind.Jpeg,
                    width,
                    height,
                    1,
                    $"JPEG{precision}/C{components}");
            }

            position += segmentLength;
        }

        return new PfsContentMetadata(PfsContentKind.Jpeg);
    }

    private static PfsContentMetadata SniffTarga(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 18)
        {
            return new PfsContentMetadata(PfsContentKind.Unknown);
        }

        var colorMapType = bytes[1];
        var imageType = bytes[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        var bitsPerPixel = bytes[16];
        var alphaBits = bytes[17] & 0x0F;

        var valid = colorMapType <= 1
            && imageType is 1 or 2 or 3 or 9 or 10 or 11
            && width > 0
            && height > 0
            && bitsPerPixel is 8 or 15 or 16 or 24 or 32;

        return valid
            ? new PfsContentMetadata(
                PfsContentKind.Targa,
                width,
                height,
                1,
                $"TGA{bitsPerPixel}/T{imageType}",
                alphaBits > 0 || bitsPerPixel == 32)
            : new PfsContentMetadata(PfsContentKind.Unknown);
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or
        0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or
        0xCD or 0xCE or 0xCF;

    private static string GetDxgiFormatName(uint format) => format switch
    {
        71 => "BC1_UNORM",
        72 => "BC1_UNORM_SRGB",
        74 => "BC2_UNORM",
        75 => "BC2_UNORM_SRGB",
        77 => "BC3_UNORM",
        78 => "BC3_UNORM_SRGB",
        80 => "BC4_UNORM",
        81 => "BC4_SNORM",
        83 => "BC5_UNORM",
        84 => "BC5_SNORM",
        95 => "BC6H_UF16",
        96 => "BC6H_SF16",
        98 => "BC7_UNORM",
        99 => "BC7_UNORM_SRGB",
        _ => $"DXGI_{format}"
    };

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static int ToDimension(uint value) => value is > 0 and <= int.MaxValue ? (int)value : 0;

    private static int PositiveDimension(int value)
    {
        if (value == int.MinValue)
        {
            return 0;
        }

        return Math.Abs(value);
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0 && (value < 0x20 || value > 0x7E))
            {
                return false;
            }
        }

        return true;
    }
}
