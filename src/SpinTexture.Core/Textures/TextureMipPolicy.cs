namespace SpinTexture.Core.Textures;

/// <summary>
/// Defines the exact mip chain emitted for enhanced textures. Alpha-tested
/// cutouts intentionally stop at 4x4: smaller levels can collapse sparse
/// coverage into a uniformly translucent texel and make distant foliage or
/// similar geometry disappear in the legacy renderer.
/// </summary>
public static class TextureMipPolicy
{
    public const int CutoutMinimumMipDimension = 4;

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

        if (!generateMipMaps)
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
            if (useCutoutFloor
                && (nextWidth < CutoutMinimumMipDimension
                    || nextHeight < CutoutMinimumMipDimension))
            {
                break;
            }

            currentWidth = nextWidth;
            currentHeight = nextHeight;
            count++;
        }

        return count;
    }
}
