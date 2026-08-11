namespace SpinTexture.Core.Imaging;

public static class TextureImageTransforms
{
    public static PixelImage AddWrapPadding(PixelImage source, int padding)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (padding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padding));
        }

        if (padding == 0)
        {
            return new PixelImage(source.Width, source.Height, (byte[])source.Pixels.Clone());
        }

        int width = checked(source.Width + padding * 2);
        int height = checked(source.Height + padding * 2);
        var destination = new byte[checked(width * height * 4)];

        for (int y = 0; y < height; y++)
        {
            int sourceY = PositiveModulo(y - padding, source.Height);
            for (int x = 0; x < width; x++)
            {
                int sourceX = PositiveModulo(x - padding, source.Width);
                int sourceOffset = (sourceY * source.Width + sourceX) * 4;
                int destinationOffset = (y * width + x) * 4;
                Buffer.BlockCopy(source.Pixels, sourceOffset, destination, destinationOffset, 4);
            }
        }

        return new PixelImage(width, height, destination);
    }

    public static PixelImage Crop(PixelImage source, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > source.Width || y + height > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Crop rectangle must be inside the source image.");
        }

        var destination = new byte[checked(width * height * 4)];
        int rowBytes = checked(width * 4);
        for (int row = 0; row < height; row++)
        {
            int sourceOffset = ((y + row) * source.Width + x) * 4;
            Buffer.BlockCopy(source.Pixels, sourceOffset, destination, row * rowBytes, rowBytes);
        }

        return new PixelImage(width, height, destination);
    }

    /// <summary>
    /// Adds enhanced luminance detail while retaining baseline chroma and alpha.
    /// A strength of zero returns the deterministic baseline; one uses all enhanced luminance.
    /// </summary>
    public static PixelImage BlendLuminanceLocked(PixelImage baseline, PixelImage enhanced, double strength)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(enhanced);
        if (baseline.Width != enhanced.Width || baseline.Height != enhanced.Height)
        {
            throw new ArgumentException("Baseline and enhanced images must have identical dimensions.", nameof(enhanced));
        }

        strength = Math.Clamp(strength, 0, 1);
        var output = new byte[baseline.Pixels.Length];
        for (int offset = 0; offset < output.Length; offset += 4)
        {
            double baseBlue = baseline.Pixels[offset];
            double baseGreen = baseline.Pixels[offset + 1];
            double baseRed = baseline.Pixels[offset + 2];
            double enhancedBlue = enhanced.Pixels[offset];
            double enhancedGreen = enhanced.Pixels[offset + 1];
            double enhancedRed = enhanced.Pixels[offset + 2];

            double baseLuma = 0.2126 * baseRed + 0.7152 * baseGreen + 0.0722 * baseBlue;
            double enhancedLuma = 0.2126 * enhancedRed + 0.7152 * enhancedGreen + 0.0722 * enhancedBlue;
            double targetLuma = baseLuma + (enhancedLuma - baseLuma) * strength;

            double blueDifference = baseBlue - baseLuma;
            double redDifference = baseRed - baseLuma;
            double targetRed = targetLuma + redDifference;
            double targetBlue = targetLuma + blueDifference;
            double targetGreen = (targetLuma - 0.2126 * targetRed - 0.0722 * targetBlue) / 0.7152;

            output[offset] = ClampByte(targetBlue);
            output[offset + 1] = ClampByte(targetGreen);
            output[offset + 2] = ClampByte(targetRed);
            output[offset + 3] = baseline.Pixels[offset + 3];
        }

        return new PixelImage(baseline.Width, baseline.Height, output);
    }

    public static double CalculateOppositeEdgeError(PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        double error = 0;
        long samples = 0;

        for (int y = 0; y < image.Height; y++)
        {
            int left = y * image.Width * 4;
            int right = (y * image.Width + image.Width - 1) * 4;
            for (int channel = 0; channel < 4; channel++)
            {
                error += Math.Abs(image.Pixels[left + channel] - image.Pixels[right + channel]);
                samples++;
            }
        }

        for (int x = 0; x < image.Width; x++)
        {
            int top = x * 4;
            int bottom = ((image.Height - 1) * image.Width + x) * 4;
            for (int channel = 0; channel < 4; channel++)
            {
                error += Math.Abs(image.Pixels[top + channel] - image.Pixels[bottom + channel]);
                samples++;
            }
        }

        return samples == 0 ? 0 : error / samples;
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue);
}
