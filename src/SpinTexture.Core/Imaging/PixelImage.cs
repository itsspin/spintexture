using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpinTexture.Core.Imaging;

/// <summary>
/// Small lossless BGRA32 image surface used for seam padding, color-lock blending,
/// alpha preservation, and deterministic validation. DirectXTex remains responsible
/// for game texture decoding and encoding.
/// </summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] pixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        ArgumentNullException.ThrowIfNull(pixels);
        int expected = checked(width * height * 4);
        if (pixels.Length != expected)
        {
            throw new ArgumentException($"Expected {expected:N0} BGRA bytes, received {pixels.Length:N0}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public byte[] Pixels { get; }

    public static PixelImage LoadPng(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return new PixelImage(source.PixelWidth, source.PixelHeight, pixels);
    }

    public void SavePng(string path, bool flushToDisk = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        BitmapSource bitmap = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            Pixels,
            Stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        if (flushToDisk)
        {
            stream.Flush(flushToDisk: true);
        }
    }
}
