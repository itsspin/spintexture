namespace SpinTexture.Core.Textures;

/// <summary>
/// Bakes restrained lighting cues directly into diffuse color, because the
/// classic client's fixed-function renderer has no normal maps or shaders to
/// add them at draw time. Two independent, optional passes:
///
/// <para><b>Baked depth</b> derives a height proxy from luminance and applies
/// multi-scale cavity shading — recesses (mortar lines, plank gaps, carved
/// detail) darken more than ridges lighten, which reads as ambient occlusion
/// and makes surfaces pop under flat lighting. The shading is multiplicative
/// and hue-preserving with hard gain bounds.</para>
///
/// <para><b>Emissive glow</b> lifts already-bright, saturated regions
/// (windows, lava cracks, runes, spell light) toward the clip point and adds
/// a soft wrapped bloom so they read as light sources at night and in
/// dungeons.</para>
///
/// Both passes are deterministic, tile-safe (blurs wrap when the texture
/// wraps), and never touch the alpha plane.
/// </summary>
internal static class LightingBaker
{
    public static byte[] Bake(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        double bakedDepth,
        double emissiveGlow,
        bool wrapEdges)
    {
        var depth = (float)Math.Clamp(bakedDepth, 0d, 1d);
        var glow = (float)Math.Clamp(emissiveGlow, 0d, 1d);
        var result = rgba.ToArray();
        if ((depth <= 0f && glow <= 0f) || width < 8 || height < 8)
        {
            return result;
        }

        var pixelCount = width * height;
        var luma = new float[pixelCount];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            luma[pixel] = (0.299f * result[offset])
                + (0.587f * result[offset + 1])
                + (0.114f * result[offset + 2]);
        }

        // Radii track resolution so the carved look reads the same at every
        // output size; the clamp keeps small icons and huge walls sane.
        var radiusScale = Math.Clamp(Math.Min(width, height) / 512f, 0.5f, 4f);

        if (depth > 0f)
        {
            ApplyCavityShading(result, luma, width, height, depth, radiusScale, wrapEdges);
        }

        if (glow > 0f)
        {
            ApplyEmissiveGlow(result, width, height, glow, radiusScale, wrapEdges);
        }

        return result;
    }

    private static void ApplyCavityShading(
        byte[] rgba,
        float[] luma,
        int width,
        int height,
        float depth,
        float radiusScale,
        bool wrapEdges)
    {
        var fine = BoxBlur(luma, width, height, Math.Max(2, (int)MathF.Round(3f * radiusScale)), wrapEdges);
        var medium = BoxBlur(luma, width, height, Math.Max(4, (int)MathF.Round(9f * radiusScale)), wrapEdges);
        var coarse = BoxBlur(luma, width, height, Math.Max(8, (int)MathF.Round(22f * radiusScale)), wrapEdges);
        var pixelCount = width * height;
        // Concavities dig deeper than ridges lift: that asymmetry is what
        // makes the result read as occlusion instead of sharpening.
        var maximumGain = 1f + (0.18f * depth);
        var minimumGain = 1f - (0.35f * depth);
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var structure = (0.50f * (luma[pixel] - fine[pixel]))
                + (0.35f * (luma[pixel] - medium[pixel]))
                + (0.40f * (luma[pixel] - coarse[pixel]));
            var shaped = structure >= 0f ? structure * 0.35f : structure;
            var gain = Math.Clamp(
                1f + (depth * 0.9f * (shaped / 255f) * 4.5f),
                minimumGain,
                maximumGain);
            var offset = pixel * 4;
            rgba[offset] = ClampByte(rgba[offset] * gain);
            rgba[offset + 1] = ClampByte(rgba[offset + 1] * gain);
            rgba[offset + 2] = ClampByte(rgba[offset + 2] * gain);
        }
    }

    private static void ApplyEmissiveGlow(
        byte[] rgba,
        int width,
        int height,
        float glow,
        float radiusScale,
        bool wrapEdges)
    {
        var pixelCount = width * height;
        var glowR = new float[pixelCount];
        var glowG = new float[pixelCount];
        var glowB = new float[pixelCount];
        var masks = new float[pixelCount];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            float r = rgba[offset], g = rgba[offset + 1], b = rgba[offset + 2];
            var maximum = MathF.Max(r, MathF.Max(g, b));
            var minimum = MathF.Min(r, MathF.Min(g, b));
            var brightness = maximum / 255f;
            var chroma = (maximum - minimum) / 255f;
            // Bright *and* colorful reads as a light source; bright neutral
            // (plain white walls, sky) needs to be much brighter to count.
            var colorfulness = 0.55f + (0.45f * SmoothStep(0.15f, 0.50f, chroma));
            var mask = SmoothStep(0.62f, 0.90f, brightness * colorfulness);
            masks[pixel] = mask;
            glowR[pixel] = r * mask;
            glowG[pixel] = g * mask;
            glowB[pixel] = b * mask;
        }

        var radius = Math.Max(6, (int)MathF.Round(14f * radiusScale));
        var bloomR = BoxBlur(glowR, width, height, radius, wrapEdges);
        var bloomG = BoxBlur(glowG, width, height, radius, wrapEdges);
        var bloomB = BoxBlur(glowB, width, height, radius, wrapEdges);
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            var mask = masks[pixel];
            // Push emitting pixels toward the clip point, then lay the soft
            // bloom on top so the light appears to spill slightly.
            var push = glow * mask;
            rgba[offset] = ClampByte(
                rgba[offset] + (push * ((0.30f * (rgba[offset] - 127.5f)) + 26f)) + (glow * 0.45f * bloomR[pixel]));
            rgba[offset + 1] = ClampByte(
                rgba[offset + 1] + (push * ((0.30f * (rgba[offset + 1] - 127.5f)) + 26f)) + (glow * 0.45f * bloomG[pixel]));
            rgba[offset + 2] = ClampByte(
                rgba[offset + 2] + (push * ((0.30f * (rgba[offset + 2] - 127.5f)) + 26f)) + (glow * 0.45f * bloomB[pixel]));
        }
    }

    /// <summary>
    /// Two-pass separable box blur (run twice for a near-Gaussian response).
    /// Edges wrap when the texture tiles so baked shading stays seamless.
    /// </summary>
    private static float[] BoxBlur(float[] source, int width, int height, int radius, bool wrap)
    {
        var pass = BlurAxis(source, width, height, radius, wrap, horizontal: true);
        pass = BlurAxis(pass, width, height, radius, wrap, horizontal: false);
        pass = BlurAxis(pass, width, height, radius, wrap, horizontal: true);
        return BlurAxis(pass, width, height, radius, wrap, horizontal: false);
    }

    private static float[] BlurAxis(
        float[] source,
        int width,
        int height,
        int radius,
        bool wrap,
        bool horizontal)
    {
        var result = new float[source.Length];
        var lineLength = horizontal ? width : height;
        var lineCount = horizontal ? height : width;
        var stride = horizontal ? 1 : width;
        var lineStride = horizontal ? width : 1;
        var effectiveRadius = Math.Min(radius, lineLength - 1);
        var window = (2 * effectiveRadius) + 1;
        for (var line = 0; line < lineCount; line++)
        {
            var baseIndex = line * lineStride;
            float sum = 0;
            for (var tap = -effectiveRadius; tap <= effectiveRadius; tap++)
            {
                sum += source[baseIndex + (Reflect(tap, lineLength, wrap) * stride)];
            }

            for (var position = 0; position < lineLength; position++)
            {
                result[baseIndex + (position * stride)] = sum / window;
                var outgoing = Reflect(position - effectiveRadius, lineLength, wrap);
                var incoming = Reflect(position + effectiveRadius + 1, lineLength, wrap);
                sum += source[baseIndex + (incoming * stride)]
                    - source[baseIndex + (outgoing * stride)];
            }
        }

        return result;
    }

    private static int Reflect(int position, int length, bool wrap)
    {
        if (wrap)
        {
            var modulo = position % length;
            return modulo < 0 ? modulo + length : modulo;
        }

        if (position < 0)
        {
            return 0;
        }

        return position >= length ? length - 1 : position;
    }

    private static byte ClampByte(float value) =>
        (byte)Math.Clamp(value, 0f, 255f);

    private static float SmoothStep(float edgeLow, float edgeHigh, float value)
    {
        var t = Math.Clamp((value - edgeLow) / (edgeHigh - edgeLow), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
