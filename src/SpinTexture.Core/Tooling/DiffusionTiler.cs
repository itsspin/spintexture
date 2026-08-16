using SpinTexture.Core.Textures;

namespace SpinTexture.Core.Tooling;

/// <summary>
/// Full-resolution repaint support for the external diffusion worker. The
/// worker's own diffusion pass is bounded (default 1152px edge) and anything
/// larger is bicubically upscaled, which softens painted detail on big
/// textures. When the user opts in, oversized inputs are split into
/// overlapping tiles that each fit the diffusion bound, the worker paints
/// every tile as an ordinary file (the worker contract is unchanged and
/// unaware of tiling), and the painted tiles are blended back with linear
/// ramps across the overlaps. The tile plan is a pure function of the input
/// dimensions, so repairs and resumes reproduce the identical grid.
/// </summary>
public static class DiffusionTiler
{
    // BlendTiles holds four float accumulators, one float weight, the final
    // RGBA image, and every decoded painted tile. Staying below this bound
    // avoids multi-gigabyte allocations and large-object-heap collapse on the
    // 2K/4K choices the UI exposes.
    public const long MaximumBlendWorkingSetBytes = 768L * 1024 * 1024;

    // A non-tiled worker retry retains only one decoded 4x output rather than
    // every painted tile plus float blend accumulators. Keep that single RGBA
    // image bounded as well: the post-processing path creates additional
    // cropped/theme buffers before encoding.
    public const long MaximumBoundedExternalOutputBytes = 320L * 1024 * 1024;

    /// <summary>
    /// Tiles are sized so their 4x output (1152) exactly matches the worker's
    /// default diffusion bound: each tile is painted at full resolution with
    /// no internal downscale.
    /// </summary>
    public const int TileInputSize = 288;

    /// <summary>
    /// Input-pixel overlap between neighboring tiles (128px at 4x) — wide
    /// enough for the blend ramp to hide any tile-local paint variation.
    /// </summary>
    public const int TileOverlap = 32;

    public readonly record struct TilePosition(int X, int Y);

    public static bool NeedsTiling(int width, int height, int tileSize = TileInputSize) =>
        width > tileSize || height > tileSize;

    /// <summary>
    /// Deterministic tile origins along one axis: fixed stride, with the last
    /// tile pulled back so it ends exactly at the edge. Every tile is exactly
    /// <paramref name="tileSize"/> long (the axis length when shorter).
    /// </summary>
    public static IReadOnlyList<int> PlanAxis(int length, int tileSize = TileInputSize, int overlap = TileOverlap)
    {
        if (length <= tileSize)
        {
            return [0];
        }

        var stride = tileSize - overlap;
        var positions = new List<int>();
        for (var position = 0; ; position += stride)
        {
            if (position + tileSize >= length)
            {
                positions.Add(length - tileSize);
                break;
            }

            positions.Add(position);
        }

        return positions;
    }

    public static IReadOnlyList<TilePosition> PlanTiles(
        int width,
        int height,
        int tileSize = TileInputSize,
        int overlap = TileOverlap)
    {
        var plan = new List<TilePosition>();
        foreach (var y in PlanAxis(height, tileSize, overlap))
        {
            foreach (var x in PlanAxis(width, tileSize, overlap))
            {
                plan.Add(new TilePosition(x, y));
            }
        }

        return plan;
    }

    public static long EstimateBlendWorkingSetBytes(
        int inputWidth,
        int inputHeight,
        int scale,
        int tileSize = TileInputSize,
        int overlap = TileOverlap)
    {
        if (inputWidth <= 0 || inputHeight <= 0 || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputWidth));
        }

        var outputPixels = checked(
            (long)inputWidth * scale * inputHeight * scale);
        var tileWidth = Math.Min(tileSize, inputWidth);
        var tileHeight = Math.Min(tileSize, inputHeight);
        var tileCount = PlanTiles(inputWidth, inputHeight, tileSize, overlap).Count;
        var retainedTilePixels = checked(
            (long)tileWidth * scale * tileHeight * scale * tileCount);
        const long fixedHeadroom = 64L * 1024 * 1024;
        return checked(
            (outputPixels * 24L)
            + (retainedTilePixels * 4L)
            + fixedHeadroom);
    }

    public static bool CanBlendWithinMemoryBudget(
        int inputWidth,
        int inputHeight,
        int scale,
        long memoryBudgetBytes = MaximumBlendWorkingSetBytes) =>
        memoryBudgetBytes > 0
        && EstimateBlendWorkingSetBytes(inputWidth, inputHeight, scale)
            <= memoryBudgetBytes;

    public static long EstimateExternalOutputBytes(
        int inputWidth,
        int inputHeight,
        int scale)
    {
        if (inputWidth <= 0 || inputHeight <= 0 || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputWidth));
        }

        return checked((long)inputWidth * scale * inputHeight * scale * 4L);
    }

    public static bool CanUseBoundedExternalPass(
        int inputWidth,
        int inputHeight,
        int scale,
        long outputBudgetBytes = MaximumBoundedExternalOutputBytes) =>
        outputBudgetBytes > 0
        && EstimateExternalOutputBytes(inputWidth, inputHeight, scale)
            <= outputBudgetBytes;

    /// <summary>
    /// Blends painted tiles (each scaled by <paramref name="scale"/>) back
    /// into one image. Weights ramp linearly across each tile's margin, and
    /// the per-pixel normalization keeps the result exact wherever tiles
    /// agree — splitting an image and blending it unchanged is the identity.
    /// </summary>
    public static TgaPixelBuffer BlendTiles(
        IReadOnlyList<(TilePosition Position, TgaPixelBuffer Painted)> tiles,
        int inputWidth,
        int inputHeight,
        int scale,
        int overlap = TileOverlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tiles.Count, 1);
        var outputWidth = inputWidth * scale;
        var outputHeight = inputHeight * scale;
        var accumulator = new float[outputWidth * outputHeight * 4];
        var weights = new float[outputWidth * outputHeight];
        // The ramp width tracks the overlap; a tile that touches the image
        // edge keeps full weight there so borders never fade.
        var ramp = Math.Max(1, overlap * scale);
        foreach (var (position, painted) in tiles)
        {
            var tileWidth = painted.Width;
            var tileHeight = painted.Height;
            var originX = position.X * scale;
            var originY = position.Y * scale;
            var source = painted.RgbaPixels.Span;
            for (var y = 0; y < tileHeight; y++)
            {
                var globalY = originY + y;
                var weightY = AxisWeight(y, tileHeight, originY, outputHeight, ramp);
                for (var x = 0; x < tileWidth; x++)
                {
                    var globalX = originX + x;
                    var weight = weightY * AxisWeight(x, tileWidth, originX, outputWidth, ramp);
                    var target = ((globalY * outputWidth) + globalX);
                    var sourceOffset = (((y * tileWidth) + x) * 4);
                    var targetOffset = target * 4;
                    accumulator[targetOffset] += source[sourceOffset] * weight;
                    accumulator[targetOffset + 1] += source[sourceOffset + 1] * weight;
                    accumulator[targetOffset + 2] += source[sourceOffset + 2] * weight;
                    accumulator[targetOffset + 3] += source[sourceOffset + 3] * weight;
                    weights[target] += weight;
                }
            }
        }

        var rgba = new byte[outputWidth * outputHeight * 4];
        for (var pixel = 0; pixel < outputWidth * outputHeight; pixel++)
        {
            var weight = weights[pixel];
            if (weight <= 0f)
            {
                throw new InvalidDataException(
                    "The diffusion tile plan left a pixel uncovered; the tiled repaint cannot be assembled.");
            }

            var offset = pixel * 4;
            rgba[offset] = (byte)Math.Clamp(MathF.Round(accumulator[offset] / weight), 0f, 255f);
            rgba[offset + 1] = (byte)Math.Clamp(MathF.Round(accumulator[offset + 1] / weight), 0f, 255f);
            rgba[offset + 2] = (byte)Math.Clamp(MathF.Round(accumulator[offset + 2] / weight), 0f, 255f);
            rgba[offset + 3] = (byte)Math.Clamp(MathF.Round(accumulator[offset + 3] / weight), 0f, 255f);
        }

        return TgaPixelBuffer.FromRgba(outputWidth, outputHeight, rgba);
    }

    private static float AxisWeight(int local, int tileLength, int origin, int outputLength, int ramp)
    {
        // Distance into the tile from each edge, except edges flush with the
        // image border, which keep full weight.
        var weight = 1f;
        if (origin > 0)
        {
            weight = Math.Min(weight, (local + 1) / (float)ramp);
        }

        if (origin + tileLength < outputLength)
        {
            weight = Math.Min(weight, (tileLength - local) / (float)ramp);
        }

        return Math.Clamp(weight, 0.0001f, 1f);
    }
}
