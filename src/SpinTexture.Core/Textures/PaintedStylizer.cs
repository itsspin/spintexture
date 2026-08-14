using SpinTexture.Core.Models;

namespace SpinTexture.Core.Textures;

/// <summary>
/// Deterministic multi-pass painterly stylization for neural texture
/// reconstructions. The pass sequence is:
/// coarse underpainting (structure-oriented sector filter on a reduced grid),
/// fine oriented sector filtering, contrast-masked recombination so detailed
/// regions stay legible while flat regions simplify, luminance detail
/// re-injection, flow-aligned stroke grain (line integral convolution of a
/// periodic noise field), low-frequency hue/value jitter, and subtle canvas
/// grain. All sampling wraps when <c>wrapEdges</c> is set and every noise
/// lattice is periodic with the image, so tiled world materials stay seamless.
/// The input alpha plane is copied to the output byte-for-byte.
/// </summary>
public static class PaintedStylizer
{
    private const int SectorCount = 8;
    private const int DirectionCount = 16;
    private const int AnisotropyLevels = 3;

    /// <summary>
    /// Full pipeline at a given overall strength. Strength only mixes the
    /// stylized result back toward the input, so callers that probe several
    /// strengths should call <see cref="RenderStylized"/> once and
    /// <see cref="MixToward"/> per candidate instead.
    /// </summary>
    public static byte[] Render(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        double strength,
        bool wrapEdges,
        int neuralScale,
        PaintedStyleSettings settings)
    {
        if (!double.IsFinite(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        if (strength == 0)
        {
            return rgba.ToArray();
        }

        var stylized = RenderStylized(rgba, width, height, wrapEdges, neuralScale, settings);
        return strength >= 1 ? stylized : MixToward(rgba, stylized, strength);
    }

    /// <summary>
    /// Blends <paramref name="input"/> toward <paramref name="stylized"/> by
    /// <paramref name="strength"/>; alpha is copied from the input exactly.
    /// </summary>
    public static byte[] MixToward(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> stylized,
        double strength)
    {
        if (input.Length != stylized.Length || (input.Length % 4) != 0)
        {
            throw new ArgumentException("Buffers must be equal-length RGBA planes.", nameof(stylized));
        }

        if (!double.IsFinite(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        var mix = (float)strength;
        var output = new byte[input.Length];
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            output[offset] = MixByte(input[offset], stylized[offset], mix);
            output[offset + 1] = MixByte(input[offset + 1], stylized[offset + 1], mix);
            output[offset + 2] = MixByte(input[offset + 2], stylized[offset + 2], mix);
            output[offset + 3] = input[offset + 3];
        }

        return output;
    }

    /// <summary>
    /// The complete painterly stylization at full strength. The input alpha
    /// plane is copied byte-for-byte; all sampling wraps when
    /// <paramref name="wrapEdges"/> is set so tiling materials stay seamless.
    /// </summary>
    public static byte[] RenderStylized(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        bool wrapEdges,
        int neuralScale,
        PaintedStyleSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentNullException.ThrowIfNull(settings);
        if (rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Pixel buffer does not match the given dimensions.", nameof(rgba));
        }

        neuralScale = Math.Clamp(neuralScale, 1, 8);
        settings = settings.Clamped();
        var input = rgba.ToArray();

        var pixels = width * height;
        var luma = new float[pixels];
        for (var i = 0; i < pixels; i++)
        {
            luma[i] = Luma(input, i * 4);
        }

        // --- structure tensor: orientation + anisotropy + contrast ---
        var field = FlowField.Create(luma, width, height, wrapEdges);

        // --- coarse underpainting on a reduced grid ---
        var reduce = Math.Clamp(neuralScale, 2, 4);
        var strokeRadius = (int)Math.Clamp(
            Math.Round((1.5 + (4.5 * settings.StrokeSize)) * neuralScale / 2.0),
            2,
            12);
        var coarse = RenderCoarse(input, width, height, wrapEdges, reduce, strokeRadius, out var coarseWidth, out var coarseHeight);

        // --- fine oriented sector filter at full resolution ---
        var fine = SectorFilter.Run(input, luma, field, width, height, wrapEdges, strokeRadius);

        // --- contrast mask: where detail must survive ---
        var mask = BuildDetailMask(field.Contrast, width, height, wrapEdges, settings.DetailPreservation);

        // --- luminance high-frequency band for re-injection ---
        var blurredLuma = GaussianBlur(luma, width, height, wrapEdges, radius: Math.Max(2, neuralScale));

        // --- periodic noise fields ---
        var noise = new PeriodicNoise(width, height);
        var strokeGrain = noise.FlowGrain(field, wrapEdges, length: Math.Max(3, strokeRadius));

        var result = ComposePasses(
            input,
            luma,
            blurredLuma,
            fine,
            coarse,
            coarseWidth,
            coarseHeight,
            mask,
            field,
            strokeGrain,
            noise,
            width,
            height,
            reduce,
            settings);

        return result;
    }

    private static byte[] ComposePasses(
        byte[] input,
        float[] luma,
        float[] blurredLuma,
        byte[] fine,
        byte[] coarse,
        int coarseWidth,
        int coarseHeight,
        float[] mask,
        FlowField field,
        float[] strokeGrain,
        PeriodicNoise noise,
        int width,
        int height,
        int reduce,
        PaintedStyleSettings settings)
    {
        var result = new byte[input.Length];
        var strokeStrength = (float)settings.StrokeStrength;
        var detailPreservation = (float)settings.DetailPreservation;
        var colorSimplification = (float)settings.ColorSimplification;
        var canvasGrain = (float)settings.CanvasGrain;

        Parallel.For(0, height, y =>
        {
            var row = y * width;
            var coarseV = (y + 0.5f) / reduce - 0.5f;
            for (var x = 0; x < width; x++)
            {
                var pixel = row + x;
                var offset = pixel * 4;
                var m = mask[pixel];

                // Fine pass color.
                float fr = fine[offset];
                float fg = fine[offset + 1];
                float fb = fine[offset + 2];
                var fl = Luma(fr, fg, fb);

                // Coarse underpainting sampled bilinearly (wrapped grid).
                SampleBilinear(coarse, coarseWidth, coarseHeight, (x + 0.5f) / reduce - 0.5f, coarseV, out var cr, out var cg, out var cb);
                var cl = Luma(cr, cg, cb);

                // Value: detailed areas keep the fine pass, flat areas relax
                // into the broad underpainting planes.
                var valueMix = 0.18f + (0.82f * m);
                var l = cl + ((fl - cl) * valueMix);

                // Chroma: color simplification pulls hue planes toward the
                // coarse pass except where the detail mask protects them.
                var chromaMix = Math.Max(valueMix, 1f - (colorSimplification * (1f - (0.7f * m))));
                var chromaR = (cr - cl) + (((fr - fl) - (cr - cl)) * chromaMix);
                var chromaB = (cb - cl) + (((fb - fl) - (cb - cl)) * chromaMix);

                // Luminance detail re-injection keeps text, masonry lines, and
                // fabric weave legible inside the painted planes.
                var highFrequency = luma[pixel] - blurredLuma[pixel];
                l += highFrequency * detailPreservation * (0.30f + (0.70f * m)) * 1.25f;

                // The strongest detail regions (signage, icons, glyph edges)
                // additionally pull back toward the source color so thin
                // strokes cannot be eaten by the sector planes.
                var protect = detailPreservation * SmoothStep(0.70f, 0.97f, m) * 0.45f;
                if (protect > 0f)
                {
                    var sourceLuma = luma[pixel];
                    l += (sourceLuma - l) * protect;
                    chromaR += ((input[offset] - sourceLuma) - chromaR) * protect;
                    chromaB += ((input[offset + 2] - sourceLuma) - chromaB) * protect;
                }

                // Flow-aligned stroke grain: strongest where the flow is
                // coherent, restrained inside high-detail regions.
                var anisotropy = field.Anisotropy[pixel];
                var strokeAmp = strokeStrength * (3.0f + (7.5f * anisotropy)) * (1f - (0.55f * m));
                l += (strokeGrain[pixel] - 0.5f) * strokeAmp;

                // Low-frequency painterly jitter: value drift plus a subtle
                // warm/cool temperature swing per region.
                var jitter = noise.LowFrequency(x, y);
                var warmCool = noise.LowFrequencyAlt(x, y) - 0.5f;
                l += (jitter - 0.5f) * strokeStrength * 9.0f;
                chromaR += warmCool * strokeStrength * 7.0f;
                chromaB -= warmCool * strokeStrength * 5.0f;

                // Canvas grain reads mostly in the flat painted planes.
                var canvas = noise.FineGrain(x, y);
                l += (canvas - 0.5f) * canvasGrain * 7.0f * (1f - (0.5f * m));

                l = SoftClipLuma(l);
                var r = l + chromaR;
                var b = l + chromaB;
                var g = (l - (0.2126f * r) - (0.0722f * b)) / 0.7152f;
                result[offset] = ClampByte(r);
                result[offset + 1] = ClampByte(g);
                result[offset + 2] = ClampByte(b);
                result[offset + 3] = input[offset + 3];
            }
        });

        return result;
    }

    private static byte[] RenderCoarse(
        byte[] input,
        int width,
        int height,
        bool wrapEdges,
        int reduce,
        int strokeRadius,
        out int coarseWidth,
        out int coarseHeight)
    {
        coarseWidth = Math.Max(1, width / reduce);
        coarseHeight = Math.Max(1, height / reduce);
        var small = BoxReduce(input, width, height, coarseWidth, coarseHeight);
        var smallPixels = coarseWidth * coarseHeight;
        var smallLuma = new float[smallPixels];
        for (var i = 0; i < smallPixels; i++)
        {
            smallLuma[i] = Luma(small, i * 4);
        }

        var smallField = FlowField.Create(smallLuma, coarseWidth, coarseHeight, wrapEdges);
        var radius = Math.Clamp(strokeRadius, 2, 8);
        return SectorFilter.Run(small, smallLuma, smallField, coarseWidth, coarseHeight, wrapEdges, radius);
    }

    private static float[] BuildDetailMask(
        float[] contrast,
        int width,
        int height,
        bool wrapEdges,
        double detailPreservation)
    {
        var blurred = GaussianBlur(contrast, width, height, wrapEdges, radius: 3);
        var high = (float)Lerp(0.30, 0.10, detailPreservation);
        var low = high * 0.30f;
        var mask = new float[blurred.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = SmoothStep(low, high, blurred[i] / 255f);
        }

        return mask;
    }

    // ------------------------------------------------------------------
    // flow field (structure tensor)
    // ------------------------------------------------------------------

    internal sealed class FlowField
    {
        public required float[] DirectionX { get; init; }
        public required float[] DirectionY { get; init; }
        public required float[] Anisotropy { get; init; }
        public required float[] Contrast { get; init; }
        public required byte[] DirectionIndex { get; init; }
        public required byte[] AnisotropyLevel { get; init; }

        public static FlowField Create(float[] luma, int width, int height, bool wrapEdges)
        {
            var pixels = width * height;
            var e = new float[pixels];
            var f = new float[pixels];
            var g = new float[pixels];
            var contrast = new float[pixels];

            Parallel.For(0, height, y =>
            {
                var up = WrapOrClamp(y - 1, height, wrapEdges) * width;
                var down = WrapOrClamp(y + 1, height, wrapEdges) * width;
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var left = WrapOrClamp(x - 1, width, wrapEdges);
                    var right = WrapOrClamp(x + 1, width, wrapEdges);
                    var gx = ((luma[up + right] + (2 * luma[row + right]) + luma[down + right])
                        - (luma[up + left] + (2 * luma[row + left]) + luma[down + left])) / 8f;
                    var gy = ((luma[down + left] + (2 * luma[down + x]) + luma[down + right])
                        - (luma[up + left] + (2 * luma[up + x]) + luma[up + right])) / 8f;
                    var index = row + x;
                    e[index] = gx * gx;
                    f[index] = gx * gy;
                    g[index] = gy * gy;
                    contrast[index] = MathF.Sqrt((gx * gx) + (gy * gy));
                }
            });

            e = GaussianBlur(e, width, height, wrapEdges, radius: 4);
            f = GaussianBlur(f, width, height, wrapEdges, radius: 4);
            g = GaussianBlur(g, width, height, wrapEdges, radius: 4);

            var directionX = new float[pixels];
            var directionY = new float[pixels];
            var anisotropy = new float[pixels];
            var directionIndex = new byte[pixels];
            var anisotropyLevel = new byte[pixels];
            Parallel.For(0, pixels, index =>
            {
                var difference = e[index] - g[index];
                var span = MathF.Sqrt((difference * difference) + (4f * f[index] * f[index]));
                var trace = e[index] + g[index];
                var a = trace < 1e-6f ? 0f : span / (trace + 1e-6f);
                // Major eigenvector angle, rotated a quarter turn to follow the
                // edge tangent (the direction a brush would travel).
                var angle = 0.5f * MathF.Atan2(2f * f[index], difference) + (MathF.PI / 2f);
                directionX[index] = MathF.Cos(angle);
                directionY[index] = MathF.Sin(angle);
                anisotropy[index] = a;
                var normalized = angle / MathF.PI;
                normalized -= MathF.Floor(normalized);
                directionIndex[index] = (byte)(Math.Clamp((int)(normalized * DirectionCount), 0, DirectionCount - 1));
                anisotropyLevel[index] = a < 0.25f ? (byte)0 : a < 0.6f ? (byte)1 : (byte)2;
            });

            return new FlowField
            {
                DirectionX = directionX,
                DirectionY = directionY,
                Anisotropy = anisotropy,
                Contrast = contrast,
                DirectionIndex = directionIndex,
                AnisotropyLevel = anisotropyLevel
            };
        }
    }

    // ------------------------------------------------------------------
    // oriented sector filter (anisotropic Kuwahara style)
    // ------------------------------------------------------------------

    private static class SectorFilter
    {
        private readonly record struct SampleEntry(short OffsetX, short OffsetY, byte Sector, float Weight);

        public static byte[] Run(
            byte[] input,
            float[] luma,
            FlowField field,
            int width,
            int height,
            bool wrapEdges,
            int radius,
            int iterations = 2)
        {
            var current = input;
            var currentLuma = luma;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                current = RunOnce(current, currentLuma, field, width, height, wrapEdges, radius);
                if (iteration + 1 < iterations)
                {
                    currentLuma = new float[width * height];
                    for (var i = 0; i < currentLuma.Length; i++)
                    {
                        currentLuma[i] = Luma(current, i * 4);
                    }
                }
            }

            return current;
        }

        private static byte[] RunOnce(
            byte[] input,
            float[] luma,
            FlowField field,
            int width,
            int height,
            bool wrapEdges,
            int radius)
        {
            var tables = BuildTables(radius);
            var output = new byte[input.Length];

            Parallel.For(0, height, y =>
            {
                Span<float> sumWeight = stackalloc float[SectorCount];
                Span<float> sumR = stackalloc float[SectorCount];
                Span<float> sumG = stackalloc float[SectorCount];
                Span<float> sumB = stackalloc float[SectorCount];
                Span<float> sumL = stackalloc float[SectorCount];
                Span<float> sumL2 = stackalloc float[SectorCount];
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x;
                    var table = tables[(field.AnisotropyLevel[pixel] * DirectionCount) + field.DirectionIndex[pixel]];
                    sumWeight.Clear();
                    sumR.Clear();
                    sumG.Clear();
                    sumB.Clear();
                    sumL.Clear();
                    sumL2.Clear();

                    foreach (var entry in table)
                    {
                        var sampleX = WrapOrClamp(x + entry.OffsetX, width, wrapEdges);
                        var sampleY = WrapOrClamp(y + entry.OffsetY, height, wrapEdges);
                        var samplePixel = (sampleY * width) + sampleX;
                        var sampleOffset = samplePixel * 4;
                        var w = entry.Weight;
                        var sector = entry.Sector;
                        var l = luma[samplePixel];
                        sumWeight[sector] += w;
                        sumR[sector] += input[sampleOffset] * w;
                        sumG[sector] += input[sampleOffset + 1] * w;
                        sumB[sector] += input[sampleOffset + 2] * w;
                        sumL[sector] += l * w;
                        sumL2[sector] += l * l * w;
                    }

                    float totalWeight = 0;
                    float outR = 0;
                    float outG = 0;
                    float outB = 0;
                    for (var sector = 0; sector < SectorCount; sector++)
                    {
                        var w = sumWeight[sector];
                        if (w < 0.5f)
                        {
                            continue;
                        }

                        var meanL = sumL[sector] / w;
                        var variance = Math.Max(0f, (sumL2[sector] / w) - (meanL * meanL));
                        // Low-variance sectors dominate decisively: this is
                        // what turns noise into confident painted planes with
                        // crisp clump boundaries instead of an airbrushed blur.
                        var scaled = variance * 0.09f;
                        var squared = scaled * scaled;
                        var sectorWeight = 1f / (1f + (squared * squared * squared));
                        totalWeight += sectorWeight;
                        outR += sumR[sector] / w * sectorWeight;
                        outG += sumG[sector] / w * sectorWeight;
                        outB += sumB[sector] / w * sectorWeight;
                    }

                    var offset = pixel * 4;
                    if (totalWeight <= 0)
                    {
                        output[offset] = input[offset];
                        output[offset + 1] = input[offset + 1];
                        output[offset + 2] = input[offset + 2];
                    }
                    else
                    {
                        output[offset] = ClampByte(outR / totalWeight);
                        output[offset + 1] = ClampByte(outG / totalWeight);
                        output[offset + 2] = ClampByte(outB / totalWeight);
                    }

                    output[offset + 3] = input[offset + 3];
                }
            });

            return output;
        }

        private static SampleEntry[][] BuildTables(int radius)
        {
            var tables = new SampleEntry[AnisotropyLevels * DirectionCount][];
            ReadOnlySpan<float> elongation = [1.2f, 1.8f, 2.6f];
            for (var level = 0; level < AnisotropyLevels; level++)
            {
                var major = radius * elongation[level];
                var minor = radius / elongation[level];
                for (var direction = 0; direction < DirectionCount; direction++)
                {
                    var angle = direction * MathF.PI / DirectionCount;
                    var cos = MathF.Cos(angle);
                    var sin = MathF.Sin(angle);
                    var entries = new List<SampleEntry>();
                    var reach = (int)MathF.Ceiling(major);
                    for (var dy = -reach; dy <= reach; dy++)
                    {
                        for (var dx = -reach; dx <= reach; dx++)
                        {
                            // Rotate into the ellipse frame aligned with flow.
                            var along = (dx * cos) + (dy * sin);
                            var across = (-dx * sin) + (dy * cos);
                            var u = along / major;
                            var v = across / minor;
                            var rho2 = (u * u) + (v * v);
                            if (rho2 > 1f)
                            {
                                continue;
                            }

                            var radialWeight = (1f - rho2) * (1f - rho2);
                            if (dx == 0 && dy == 0)
                            {
                                // The center pixel anchors every sector equally.
                                var centerWeight = radialWeight / SectorCount;
                                for (var sector = 0; sector < SectorCount; sector++)
                                {
                                    entries.Add(new SampleEntry(0, 0, (byte)sector, centerWeight));
                                }

                                continue;
                            }

                            // Smooth sector membership: split each sample across
                            // its two nearest sectors with a raised-cosine blend
                            // so near-tie boundaries cannot flicker into
                            // single-pixel speckles along strong edges.
                            var sectorAngle = MathF.Atan2(v, u);
                            var position = (sectorAngle + MathF.PI) / (2f * MathF.PI) * SectorCount;
                            var lower = (int)MathF.Floor(position - 0.5f);
                            var fraction = position - 0.5f - lower;
                            var blend = 0.5f - (0.5f * MathF.Cos(fraction * MathF.PI));
                            var sectorA = ((lower % SectorCount) + SectorCount) % SectorCount;
                            var sectorB = (sectorA + 1) % SectorCount;
                            if (blend < 0.999f)
                            {
                                entries.Add(new SampleEntry((short)dx, (short)dy, (byte)sectorA, radialWeight * (1f - blend)));
                            }

                            if (blend > 0.001f)
                            {
                                entries.Add(new SampleEntry((short)dx, (short)dy, (byte)sectorB, radialWeight * blend));
                            }
                        }
                    }

                    tables[(level * DirectionCount) + direction] = entries.ToArray();
                }
            }

            return tables;
        }
    }

    // ------------------------------------------------------------------
    // periodic noise
    // ------------------------------------------------------------------

    internal sealed class PeriodicNoise
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _lowCellsX;
        private readonly int _lowCellsY;
        private readonly int _fineCellsX;
        private readonly int _fineCellsY;

        public PeriodicNoise(int width, int height)
        {
            _width = width;
            _height = height;
            _lowCellsX = Math.Max(4, width / 96);
            _lowCellsY = Math.Max(4, height / 96);
            _fineCellsX = Math.Max(8, width / 3);
            _fineCellsY = Math.Max(8, height / 3);
        }

        public float LowFrequency(int x, int y) =>
            Value(x, y, _lowCellsX, _lowCellsY, seed: 0x51ED_2A17);

        public float LowFrequencyAlt(int x, int y) =>
            Value(x, y, _lowCellsX, _lowCellsY, seed: 0x0BAD_5EED);

        public float FineGrain(int x, int y) =>
            Value(x, y, _fineCellsX, _fineCellsY, seed: 0x00C4_57A5);

        /// <summary>
        /// Line-integral convolution of periodic white noise along the local
        /// flow direction: the brush-hair striations of the painted look.
        /// </summary>
        public float[] FlowGrain(FlowField field, bool wrapEdges, int length)
        {
            var grain = new float[_width * _height];
            var width = _width;
            var height = _height;
            Parallel.For(0, height, y =>
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x;
                    var dirX = field.DirectionX[pixel];
                    var dirY = field.DirectionY[pixel];
                    float sum = 0;
                    var samples = 0;
                    for (var t = -length; t <= length; t++)
                    {
                        var sampleX = WrapOrClamp((int)MathF.Round(x + (dirX * t)), width, wrapEdges);
                        var sampleY = WrapOrClamp((int)MathF.Round(y + (dirY * t)), height, wrapEdges);
                        sum += White(sampleX, sampleY, 0x7A1E_11CE);
                        samples++;
                    }

                    // Recenter and mildly expand: LIC averaging compresses the
                    // dynamic range as stroke length grows.
                    var value = sum / samples;
                    grain[pixel] = Math.Clamp(0.5f + ((value - 0.5f) * MathF.Sqrt(samples) * 0.6f), 0f, 1f);
                }
            });

            return grain;
        }

        private float Value(int x, int y, int cellsX, int cellsY, int seed)
        {
            var u = (float)x * cellsX / _width;
            var v = (float)y * cellsY / _height;
            var x0 = (int)MathF.Floor(u);
            var y0 = (int)MathF.Floor(v);
            var fx = SmoothStep(0f, 1f, u - x0);
            var fy = SmoothStep(0f, 1f, v - y0);
            var x1 = x0 + 1;
            var y1 = y0 + 1;
            var v00 = White(Mod(x0, cellsX), Mod(y0, cellsY), seed);
            var v10 = White(Mod(x1, cellsX), Mod(y0, cellsY), seed);
            var v01 = White(Mod(x0, cellsX), Mod(y1, cellsY), seed);
            var v11 = White(Mod(x1, cellsX), Mod(y1, cellsY), seed);
            var top = v00 + ((v10 - v00) * fx);
            var bottom = v01 + ((v11 - v01) * fx);
            return top + ((bottom - top) * fy);
        }

        private static float White(int x, int y, int seed)
        {
            unchecked
            {
                var h = (uint)(x * 374761393) + (uint)(y * 668265263) + (uint)seed;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }

    // ------------------------------------------------------------------
    // shared helpers
    // ------------------------------------------------------------------

    private static float[] GaussianBlur(float[] source, int width, int height, bool wrapEdges, int radius)
    {
        var weights = new float[(radius * 2) + 1];
        var sigma = Math.Max(0.75f, radius / 2f);
        float total = 0;
        for (var i = -radius; i <= radius; i++)
        {
            var w = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            weights[i + radius] = w;
            total += w;
        }

        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] /= total;
        }

        var scratch = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                float sum = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    sum += source[row + WrapOrClamp(x + i, width, wrapEdges)] * weights[i + radius];
                }

                scratch[row + x] = sum;
            }
        });

        var destination = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                float sum = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    sum += scratch[(WrapOrClamp(y + i, height, wrapEdges) * width) + x] * weights[i + radius];
                }

                destination[(y * width) + x] = sum;
            }
        });

        return destination;
    }

    private static byte[] BoxReduce(byte[] input, int width, int height, int targetWidth, int targetHeight)
    {
        var output = new byte[targetWidth * targetHeight * 4];
        Parallel.For(0, targetHeight, ty =>
        {
            var top = ty * height / targetHeight;
            var bottom = Math.Max(top + 1, (ty + 1) * height / targetHeight);
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var left = tx * width / targetWidth;
                var right = Math.Max(left + 1, (tx + 1) * width / targetWidth);
                float r = 0, g = 0, b = 0, a = 0;
                var samples = 0;
                for (var y = top; y < bottom; y++)
                {
                    var row = y * width;
                    for (var x = left; x < right; x++)
                    {
                        var offset = (row + x) * 4;
                        r += input[offset];
                        g += input[offset + 1];
                        b += input[offset + 2];
                        a += input[offset + 3];
                        samples++;
                    }
                }

                var target = ((ty * targetWidth) + tx) * 4;
                output[target] = ClampByte(r / samples);
                output[target + 1] = ClampByte(g / samples);
                output[target + 2] = ClampByte(b / samples);
                output[target + 3] = ClampByte(a / samples);
            }
        });

        return output;
    }

    private static void SampleBilinear(
        byte[] image,
        int width,
        int height,
        float x,
        float y,
        out float r,
        out float g,
        out float b)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var px0 = Mod(x0, width);
        var px1 = Mod(x0 + 1, width);
        var py0 = Mod(y0, height);
        var py1 = Mod(y0 + 1, height);
        var o00 = ((py0 * width) + px0) * 4;
        var o10 = ((py0 * width) + px1) * 4;
        var o01 = ((py1 * width) + px0) * 4;
        var o11 = ((py1 * width) + px1) * 4;
        r = Blend(image[o00], image[o10], image[o01], image[o11], fx, fy);
        g = Blend(image[o00 + 1], image[o10 + 1], image[o01 + 1], image[o11 + 1], fx, fy);
        b = Blend(image[o00 + 2], image[o10 + 2], image[o01 + 2], image[o11 + 2], fx, fy);
    }

    private static float Blend(byte v00, byte v10, byte v01, byte v11, float fx, float fy)
    {
        var top = v00 + ((v10 - v00) * fx);
        var bottom = v01 + ((v11 - v01) * fx);
        return top + ((bottom - top) * fy);
    }

    private static float SoftClipLuma(float value)
    {
        // Compress gently into [1, 254] so the painted pass cannot grow the
        // clipped-pixel fraction past the fidelity gate.
        if (value < 24f)
        {
            var t = Math.Max(0f, value) / 24f;
            return 1f + (23f * t * t * (3f - (2f * t)));
        }

        if (value > 231f)
        {
            var t = Math.Min(1f, (Math.Min(255f, value) - 231f) / 24f);
            return 231f + (23f * t * (2f - t));
        }

        return value;
    }

    private static int WrapOrClamp(int value, int size, bool wrap) =>
        wrap ? Mod(value, size) : Math.Clamp(value, 0, size - 1);

    private static int Mod(int value, int size)
    {
        var result = value % size;
        return result < 0 ? result + size : result;
    }

    private static float Luma(byte[] rgba, int offset) =>
        (0.2126f * rgba[offset]) + (0.7152f * rgba[offset + 1]) + (0.0722f * rgba[offset + 2]);

    private static float Luma(float r, float g, float b) =>
        (0.2126f * r) + (0.7152f * g) + (0.0722f * b);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static byte MixByte(byte from, byte to, float mix) =>
        (byte)Math.Clamp((int)MathF.Round(from + ((to - from) * mix)), 0, 255);

    private static byte ClampByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}
