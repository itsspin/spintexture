namespace SpinTexture.Core.Textures;

/// <summary>
/// Defines the exact mip chain emitted for enhanced textures. Alpha-tested
/// cutouts intentionally keep only their enhanced top level. The legacy
/// renderer applies its alpha test after minification, and generated soft-alpha
/// levels can cross that cutoff as the camera angle changes. That makes crossed
/// foliage planes appear and disappear in a distance halo even when nominal
/// coverage is preserved. The source assets use this same single-level policy.
/// </summary>
public static class TextureMipPolicy
{
    public static int Calculate(
        int width,
        int height,
        bool generateMipMaps,
        bool useCutoutFloor)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Texture height must be positive.");
        }

        if (!generateMipMaps || useCutoutFloor)
        {
            return 1;
        }

        var currentWidth = width;
        var currentHeight = height;
        var count = 1;
        while (currentWidth > 1 || currentHeight > 1)
        {
            var nextWidth = Math.Max(1, currentWidth >> 1);
            var nextHeight = Math.Max(1, currentHeight >> 1);
            currentWidth = nextWidth;
            currentHeight = nextHeight;
            count++;
        }

        return count;
    }
}
