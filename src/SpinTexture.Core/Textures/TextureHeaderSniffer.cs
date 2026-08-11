using System.Buffers.Binary;
using System.Text;

namespace SpinTexture.Core.Textures;

public sealed class TextureHeaderSniffer
{
    private const int MaximumHeaderBytes = 148;

    public bool TryRead(ReadOnlySpan<byte> header, out TextureMetadata? metadata)
    {
        if (TryReadDds(header, out metadata)
            || TryReadBmp(header, out metadata)
            || TryReadTga(header, out metadata))
        {
            return true;
        }

        metadata = null;
        return false;
    }

    public async Task<TextureMetadata?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        long originalPosition = 0;
        if (stream.CanSeek)
        {
            originalPosition = stream.Position;
        }

        var buffer = new byte[MaximumHeaderBytes];
        var bytesRead = 0;

        try
        {
            while (bytesRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(bytesRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }

        return TryRead(buffer.AsSpan(0, bytesRead), out var metadata) ? metadata : null;
    }

    public async Task<TextureMetadata?> ReadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadDds(ReadOnlySpan<byte> header, out TextureMetadata? metadata)
    {
        metadata = null;

        if (header.Length < 128
            || !header[..4].SequenceEqual("DDS "u8)
            || ReadUInt32(header, 4) != 124
            || ReadUInt32(header, 76) != 32)
        {
            return false;
        }

        var height = ReadUInt32(header, 12);
        var width = ReadUInt32(header, 16);
        if (!TryDimension(width, out var parsedWidth) || !TryDimension(height, out var parsedHeight))
        {
            return false;
        }

        var mipCountValue = ReadUInt32(header, 28);
        var mipCount = mipCountValue is 0 or > int.MaxValue ? 1 : (int)mipCountValue;
        var pixelFlags = ReadUInt32(header, 80);
        var fourCc = Encoding.ASCII.GetString(header.Slice(84, 4));
        var rgbBitsValue = ReadUInt32(header, 88);
        var bitsPerPixel = rgbBitsValue <= int.MaxValue ? (int)rgbBitsValue : 0;
        var caps2 = ReadUInt32(header, 112);
        var isCubeMap = (caps2 & 0x0000_0200u) != 0;
        var isVolume = (caps2 & 0x0020_0000u) != 0;
        var alphaStatus = (pixelFlags & 0x3u) != 0
            ? TextureAlphaStatus.Explicit
            : TextureAlphaStatus.None;
        var arraySize = 1;
        var usesDx10Header = string.Equals(fourCc, "DX10", StringComparison.Ordinal);
        string storageFormat;
        string? texconvFormat;
        bool isCompressed;

        if (usesDx10Header)
        {
            if (header.Length < MaximumHeaderBytes)
            {
                return false;
            }

            var dxgiFormat = ReadUInt32(header, 128);
            var resourceDimension = ReadUInt32(header, 132);
            var miscFlag = ReadUInt32(header, 136);
            var arraySizeValue = ReadUInt32(header, 140);
            if (arraySizeValue is 0 or > int.MaxValue)
            {
                return false;
            }

            arraySize = (int)arraySizeValue;
            isCubeMap |= (miscFlag & 0x4u) != 0;
            isVolume |= resourceDimension == 4;
            storageFormat = $"DXGI-{dxgiFormat}";
            texconvFormat = MapDxgiFormat(dxgiFormat, out bitsPerPixel, out isCompressed, out var dxgiAlpha);
            alphaStatus = dxgiAlpha;
        }
        else
        {
            storageFormat = NormalizeFourCc(fourCc, pixelFlags, bitsPerPixel);
            texconvFormat = MapLegacyDdsFormat(storageFormat, bitsPerPixel, out var mappedBits, out isCompressed, out var legacyAlpha);
            if (mappedBits != 0)
            {
                bitsPerPixel = mappedBits;
            }

            if (legacyAlpha > alphaStatus)
            {
                alphaStatus = legacyAlpha;
            }
        }

        metadata = new TextureMetadata(
            TextureFileFormat.Dds,
            $"DDS/{storageFormat}",
            parsedWidth,
            parsedHeight,
            mipCount,
            bitsPerPixel,
            alphaStatus,
            isCompressed,
            IsTopDown: false,
            isCubeMap,
            isVolume,
            arraySize,
            texconvFormat,
            usesDx10Header);

        return true;
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> header, out TextureMetadata? metadata)
    {
        metadata = null;

        if (header.Length < 26 || header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            return false;
        }

        var dibSize = ReadUInt32(header, 14);
        int width;
        int height;
        int bitsPerPixel;
        uint compression = 0;
        bool isTopDown = false;
        TextureAlphaStatus alphaStatus;

        if (dibSize == 12)
        {
            width = ReadUInt16(header, 18);
            height = ReadUInt16(header, 20);
            bitsPerPixel = ReadUInt16(header, 24);
            alphaStatus = TextureAlphaStatus.None;
        }
        else if (dibSize >= 40 && header.Length >= 54)
        {
            var signedWidth = ReadInt32(header, 18);
            var signedHeight = ReadInt32(header, 22);
            if (signedWidth <= 0 || signedHeight == 0 || signedHeight == int.MinValue)
            {
                return false;
            }

            width = signedWidth;
            height = Math.Abs(signedHeight);
            isTopDown = signedHeight < 0;
            bitsPerPixel = ReadUInt16(header, 28);
            compression = ReadUInt32(header, 30);

            uint alphaMask = 0;
            if (dibSize >= 56 && header.Length >= 70)
            {
                alphaMask = ReadUInt32(header, 66);
            }

            alphaStatus = compression == 6 || alphaMask != 0
                ? TextureAlphaStatus.Explicit
                : bitsPerPixel == 32
                    ? TextureAlphaStatus.Possible
                    : TextureAlphaStatus.None;
        }
        else
        {
            return false;
        }

        if (width <= 0 || height <= 0 || bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return false;
        }

        var compressionName = compression switch
        {
            0 => "RGB",
            1 => "RLE8",
            2 => "RLE4",
            3 => "BITFIELDS",
            4 => "JPEG",
            5 => "PNG",
            6 => "ALPHABITFIELDS",
            _ => $"COMPRESSION-{compression}"
        };

        metadata = new TextureMetadata(
            TextureFileFormat.Bmp,
            $"BMP/{compressionName}/{bitsPerPixel}bpp",
            width,
            height,
            MipCount: 1,
            bitsPerPixel,
            alphaStatus,
            IsCompressed: compression is 1 or 2 or 4 or 5,
            isTopDown,
            IsCubeMap: false,
            IsVolumeTexture: false,
            ArraySize: 1,
            TexconvFormat: bitsPerPixel == 32 ? "B8G8R8A8_UNORM" : "B8G8R8X8_UNORM");

        return true;
    }

    private static bool TryReadTga(ReadOnlySpan<byte> header, out TextureMetadata? metadata)
    {
        metadata = null;

        if (header.Length < 18)
        {
            return false;
        }

        var colorMapType = header[1];
        var imageType = header[2];
        var width = ReadUInt16(header, 12);
        var height = ReadUInt16(header, 14);
        var pixelDepth = header[16];
        var descriptor = header[17];
        var alphaBits = descriptor & 0x0f;

        if (colorMapType is not (0 or 1)
            || imageType is not (1 or 2 or 3 or 9 or 10 or 11)
            || width == 0
            || height == 0
            || pixelDepth is not (8 or 15 or 16 or 24 or 32)
            || alphaBits > 8)
        {
            return false;
        }

        if (imageType is 1 or 9 && colorMapType != 1)
        {
            return false;
        }

        if (imageType is not (1 or 9) && colorMapType != 0)
        {
            return false;
        }

        var imageTypeName = imageType switch
        {
            1 => "COLOR-MAPPED",
            2 => "TRUE-COLOR",
            3 => "GRAYSCALE",
            9 => "RLE-COLOR-MAPPED",
            10 => "RLE-TRUE-COLOR",
            11 => "RLE-GRAYSCALE",
            _ => "UNKNOWN"
        };

        var alphaStatus = alphaBits > 0
            ? TextureAlphaStatus.Explicit
            : pixelDepth == 32
                ? TextureAlphaStatus.Possible
                : TextureAlphaStatus.None;

        metadata = new TextureMetadata(
            TextureFileFormat.Tga,
            $"TGA/{imageTypeName}/{pixelDepth}bpp",
            width,
            height,
            MipCount: 1,
            pixelDepth,
            alphaStatus,
            IsCompressed: imageType >= 9,
            IsTopDown: (descriptor & 0x20) != 0,
            IsCubeMap: false,
            IsVolumeTexture: false,
            ArraySize: 1,
            TexconvFormat: pixelDepth == 32 ? "B8G8R8A8_UNORM" : "B8G8R8X8_UNORM");

        return true;
    }

    private static string NormalizeFourCc(string fourCc, uint pixelFlags, int bitsPerPixel)
    {
        if ((pixelFlags & 0x4u) != 0)
        {
            return fourCc.TrimEnd('\0', ' ');
        }

        if ((pixelFlags & 0x0002_0000u) != 0)
        {
            return $"LUMINANCE-{bitsPerPixel}";
        }

        if ((pixelFlags & 0x40u) != 0)
        {
            return $"RGB-{bitsPerPixel}";
        }

        return $"PIXELFLAGS-{pixelFlags:X8}";
    }

    private static string? MapLegacyDdsFormat(
        string storageFormat,
        int originalBits,
        out int bitsPerPixel,
        out bool isCompressed,
        out TextureAlphaStatus alphaStatus)
    {
        bitsPerPixel = originalBits;
        isCompressed = false;
        alphaStatus = TextureAlphaStatus.None;

        switch (storageFormat.ToUpperInvariant())
        {
            case "DXT1":
                bitsPerPixel = 4;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Possible;
                return "BC1_UNORM";
            case "DXT3":
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC2_UNORM";
            case "DXT5":
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC3_UNORM";
            case "DXT2":
            case "DXT4":
                // These legacy FourCCs contain premultiplied alpha. Encoding
                // them as ordinary DXT3/DXT5 would silently change blending
                // semantics in the Direct3D 9 client, so preserve them intact.
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return null;
            case "ATI1":
            case "BC4U":
                bitsPerPixel = 4;
                isCompressed = true;
                return "BC4_UNORM";
            case "BC4S":
                bitsPerPixel = 4;
                isCompressed = true;
                return "BC4_SNORM";
            case "ATI2":
            case "BC5U":
                bitsPerPixel = 8;
                isCompressed = true;
                return "BC5_UNORM";
            case "BC5S":
                bitsPerPixel = 8;
                isCompressed = true;
                return "BC5_SNORM";
            case "RGB-32":
                alphaStatus = TextureAlphaStatus.Possible;
                return "B8G8R8A8_UNORM";
            case "RGB-24":
                return "B8G8R8X8_UNORM";
            default:
                return null;
        }
    }

    private static string? MapDxgiFormat(
        uint dxgiFormat,
        out int bitsPerPixel,
        out bool isCompressed,
        out TextureAlphaStatus alphaStatus)
    {
        bitsPerPixel = 0;
        isCompressed = false;
        alphaStatus = TextureAlphaStatus.None;

        switch (dxgiFormat)
        {
            case 28:
                bitsPerPixel = 32;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "R8G8B8A8_UNORM";
            case 29:
                bitsPerPixel = 32;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "R8G8B8A8_UNORM_SRGB";
            case 71:
                bitsPerPixel = 4;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Possible;
                return "BC1_UNORM";
            case 72:
                bitsPerPixel = 4;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Possible;
                return "BC1_UNORM_SRGB";
            case 74:
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC2_UNORM";
            case 75:
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC2_UNORM_SRGB";
            case 77:
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC3_UNORM";
            case 78:
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC3_UNORM_SRGB";
            case 80:
                bitsPerPixel = 4;
                isCompressed = true;
                return "BC4_UNORM";
            case 81:
                bitsPerPixel = 4;
                isCompressed = true;
                return "BC4_SNORM";
            case 83:
                bitsPerPixel = 8;
                isCompressed = true;
                return "BC5_UNORM";
            case 84:
                bitsPerPixel = 8;
                isCompressed = true;
                return "BC5_SNORM";
            case 87:
                bitsPerPixel = 32;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "B8G8R8A8_UNORM";
            case 91:
                bitsPerPixel = 32;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "B8G8R8A8_UNORM_SRGB";
            case 95:
                bitsPerPixel = 8;
                isCompressed = true;
                return "BC6H_UF16";
            case 96:
                bitsPerPixel = 8;
                isCompressed = true;
                return "BC6H_SF16";
            case 98:
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC7_UNORM";
            case 99:
                bitsPerPixel = 8;
                isCompressed = true;
                alphaStatus = TextureAlphaStatus.Explicit;
                return "BC7_UNORM_SRGB";
            default:
                return null;
        }
    }

    private static bool TryDimension(uint value, out int dimension)
    {
        if (value is 0 or > int.MaxValue)
        {
            dimension = 0;
            return false;
        }

        dimension = (int)value;
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(offset, sizeof(ushort)));

    private static uint ReadUInt32(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(offset, sizeof(uint)));

    private static int ReadInt32(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(value.Slice(offset, sizeof(int)));
}
