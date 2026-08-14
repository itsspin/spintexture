using System.Buffers.Binary;
using SpinTexture.Core.Imaging;
using SpinTexture.Core.Models;

namespace SpinTexture.Core.Textures;

public sealed record TextureColorStatistics(
    long Samples,
    double MeanRed,
    double MeanGreen,
    double MeanBlue,
    double MeanLuminance,
    double LuminanceStandardDeviation,
    double ExtremeLuminanceFraction);

public sealed record TextureColorGainAnchor(
    TgaPixelBuffer Image,
    double RedGain,
    double GreenGain,
    double BlueGain);

/// <summary>
/// A deliberately small, dependency-free 32-bit working buffer for texconv-produced TGA files.
/// Pixels are normalized to top-left RGBA order in memory.
/// </summary>
public sealed class TgaPixelBuffer
{
    private readonly byte[] _rgba;

    private TgaPixelBuffer(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    internal ReadOnlyMemory<byte> RgbaPixels => _rgba;

    public static async Task<TgaPixelBuffer> ReadPngFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return FromPixelImage(PixelImage.LoadPng(path));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WritePngFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToPixelImage().SavePng(path, flushToDisk: false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TgaPixelBuffer> ReadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Read(bytes);
    }

    public static TgaPixelBuffer Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            throw new InvalidDataException("TGA data is shorter than its header.");
        }

        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        var bitsPerPixel = data[16];
        var descriptor = data[17];

        if (colorMapType != 0 || imageType != 2 || width == 0 || height == 0 || bitsPerPixel is not (24 or 32))
        {
            throw new NotSupportedException("The seam-safe working stage requires an uncompressed 24- or 32-bit true-color TGA.");
        }

        var bytesPerPixel = bitsPerPixel / 8;
        var pixelOffset = checked(18 + idLength);
        var pixelBytes = checked(width * height * bytesPerPixel);
        if (pixelOffset > data.Length || pixelBytes > data.Length - pixelOffset)
        {
            throw new InvalidDataException("TGA pixel data is truncated.");
        }

        var topOrigin = (descriptor & 0x20) != 0;
        var rightOrigin = (descriptor & 0x10) != 0;
        var rgba = new byte[checked(width * height * 4)];

        for (var fileY = 0; fileY < height; fileY++)
        {
            var targetY = topOrigin ? fileY : height - 1 - fileY;
            for (var fileX = 0; fileX < width; fileX++)
            {
                var targetX = rightOrigin ? width - 1 - fileX : fileX;
                var sourceIndex = pixelOffset + ((fileY * width) + fileX) * bytesPerPixel;
                var targetIndex = ((targetY * width) + targetX) * 4;
                rgba[targetIndex] = data[sourceIndex + 2];
                rgba[targetIndex + 1] = data[sourceIndex + 1];
                rgba[targetIndex + 2] = data[sourceIndex];
                rgba[targetIndex + 3] = bytesPerPixel == 4 ? data[sourceIndex + 3] : byte.MaxValue;
            }
        }

        return new TgaPixelBuffer(width, height, rgba);
    }

    public async Task WriteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var bytes = ToBytes();
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal PixelImage ToPixelImage()
    {
        var bgra = new byte[_rgba.Length];
        for (var index = 0; index < _rgba.Length; index += 4)
        {
            bgra[index] = _rgba[index + 2];
            bgra[index + 1] = _rgba[index + 1];
            bgra[index + 2] = _rgba[index];
            bgra[index + 3] = _rgba[index + 3];
        }

        return new PixelImage(Width, Height, bgra);
    }

    internal static TgaPixelBuffer FromPixelImage(PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rgba = new byte[image.Pixels.Length];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            rgba[index] = image.Pixels[index + 2];
            rgba[index + 1] = image.Pixels[index + 1];
            rgba[index + 2] = image.Pixels[index];
            rgba[index + 3] = image.Pixels[index + 3];
        }

        return new TgaPixelBuffer(image.Width, image.Height, rgba);
    }

    public TgaPixelBuffer WithOpaqueAlpha()
    {
        var pixels = (byte[])_rgba.Clone();
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = byte.MaxValue;
        }

        return new TgaPixelBuffer(Width, Height, pixels);
    }

    public bool HasLikelyCutoutAlpha()
    {
        var transparent = 0;
        var opaque = 0;
        var intermediate = 0;
        for (var index = 3; index < _rgba.Length; index += 4)
        {
            var alpha = _rgba[index];
            if (alpha <= 8)
            {
                transparent++;
            }
            else if (alpha >= 247)
            {
                opaque++;
            }
            else
            {
                intermediate++;
            }
        }

        var pixels = _rgba.Length / 4;
        return transparent > 0
            && opaque > 0
            && intermediate <= Math.Max(1, pixels / 20);
    }

    public bool HasSoftTranslucentAlpha()
    {
        var intermediate = 0;
        for (var index = 3; index < _rgba.Length; index += 4)
        {
            var alpha = _rgba[index];
            if (alpha > 8 && alpha < 247)
            {
                intermediate++;
            }
        }

        var pixels = _rgba.Length / 4;
        return intermediate > Math.Max(1, pixels / 20);
    }

    public bool IsFullyTransparent(byte threshold = 8)
    {
        for (var index = 3; index < _rgba.Length; index += 4)
        {
            if (_rgba[index] > threshold)
            {
                return false;
            }
        }

        return true;
    }

    public TgaPixelBuffer DilateRgbIntoTransparentPixels(byte visibleThreshold = 128)
    {
        var pixels = (byte[])_rgba.Clone();
        var pixelCount = checked(Width * Height);
        var visited = new bool[pixelCount];
        var queue = new int[pixelCount];
        var head = 0;
        var tail = 0;

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            if (pixels[(pixel * 4) + 3] >= visibleThreshold)
            {
                visited[pixel] = true;
                queue[tail++] = pixel;
            }
        }

        // An entirely transparent texture has no meaningful color to extend.
        if (tail == 0)
        {
            return new TgaPixelBuffer(Width, Height, pixels);
        }

        while (head < tail)
        {
            var pixel = queue[head++];
            var x = pixel % Width;
            var y = pixel / Width;
            if (x > 0)
            {
                Visit(pixel - 1, pixel);
            }

            if (x + 1 < Width)
            {
                Visit(pixel + 1, pixel);
            }

            if (y > 0)
            {
                Visit(pixel - Width, pixel);
            }

            if (y + 1 < Height)
            {
                Visit(pixel + Width, pixel);
            }
        }

        return new TgaPixelBuffer(Width, Height, pixels);

        void Visit(int target, int source)
        {
            if (visited[target])
            {
                return;
            }

            var targetOffset = target * 4;
            var sourceOffset = source * 4;
            pixels[targetOffset] = pixels[sourceOffset];
            pixels[targetOffset + 1] = pixels[sourceOffset + 1];
            pixels[targetOffset + 2] = pixels[sourceOffset + 2];
            visited[target] = true;
            queue[tail++] = target;
        }
    }

    public TextureColorStatistics CalculateVisibleColorStatistics(byte alphaThreshold = 8)
    {
        long samples = 0;
        long extremes = 0;
        double redSum = 0;
        double greenSum = 0;
        double blueSum = 0;
        double luminanceMean = 0;
        double luminanceMoment = 0;

        for (var offset = 0; offset < _rgba.Length; offset += 4)
        {
            if (_rgba[offset + 3] < alphaThreshold)
            {
                continue;
            }

            var red = _rgba[offset];
            var green = _rgba[offset + 1];
            var blue = _rgba[offset + 2];
            var luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
            samples++;
            redSum += red;
            greenSum += green;
            blueSum += blue;
            var delta = luminance - luminanceMean;
            luminanceMean += delta / samples;
            luminanceMoment += delta * (luminance - luminanceMean);
            if (luminance <= 1 || luminance >= 254)
            {
                extremes++;
            }
        }

        if (samples == 0)
        {
            return new TextureColorStatistics(0, 0, 0, 0, 0, 0, 0);
        }

        return new TextureColorStatistics(
            samples,
            redSum / samples,
            greenSum / samples,
            blueSum / samples,
            luminanceMean,
            Math.Sqrt(luminanceMoment / samples),
            (double)extremes / samples);
    }

    public TextureColorGainAnchor AnchorVisibleChannelMeansFrom(
        TgaPixelBuffer source,
        double minimumGain = 0.90,
        double maximumGain = 1.10,
        byte alphaThreshold = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (minimumGain <= 0 || maximumGain < minimumGain)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGain));
        }

        var sourceStatistics = source.CalculateVisibleColorStatistics(alphaThreshold);
        var enhancedStatistics = CalculateVisibleColorStatistics(alphaThreshold);
        var redGain = CalculateStableGain(
            sourceStatistics.MeanRed,
            enhancedStatistics.MeanRed,
            minimumGain,
            maximumGain);
        var greenGain = CalculateStableGain(
            sourceStatistics.MeanGreen,
            enhancedStatistics.MeanGreen,
            minimumGain,
            maximumGain);
        var blueGain = CalculateStableGain(
            sourceStatistics.MeanBlue,
            enhancedStatistics.MeanBlue,
            minimumGain,
            maximumGain);
        var output = (byte[])_rgba.Clone();
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            output[offset] = ClampByte(output[offset] * redGain);
            output[offset + 1] = ClampByte(output[offset + 1] * greenGain);
            output[offset + 2] = ClampByte(output[offset + 2] * blueGain);
        }

        return new TextureColorGainAnchor(
            new TgaPixelBuffer(Width, Height, output),
            redGain,
            greenGain,
            blueGain);
    }

    /// <summary>
    /// Applies a bounded, deterministic fantasy-painting grade while retaining
    /// the reconstructed luminance structure and the exact alpha plane. The
    /// transform gently mutes chroma, warms shadow color, moves foliage greens
    /// toward olive, and introduces very light painted tone planes. It does not
    /// synthesize geometry or add random noise, so wrapped texture borders stay
    /// deterministic and repeatable.
    /// </summary>
    public TgaPixelBuffer ApplyRusticPaintedGrade(double strength = 1)
    {
        if (!double.IsFinite(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        if (strength == 0)
        {
            return new TgaPixelBuffer(Width, Height, (byte[])_rgba.Clone());
        }

        var output = (byte[])_rgba.Clone();
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            var red = (double)_rgba[offset];
            var green = (double)_rgba[offset + 1];
            var blue = (double)_rgba[offset + 2];
            var luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
            var normalizedLuma = luma / 255;
            var shadow = 1 - normalizedLuma;
            var midtone = 1 - Math.Abs((normalizedLuma * 2) - 1);
            var greenDominance = Math.Max(0, green - Math.Max(red, blue)) / 255;
            var blueDominance = Math.Max(0, blue - Math.Max(red, green)) / 255;

            // Preserve the illustrated model's structure while taking the edge
            // off synthetic saturation that can look out of place on old meshes.
            var saturationScale = 1 - (strength * (0.075 + (0.045 * shadow)));
            // Shape midtones without crushing already-dark source texels. This
            // retains painted depth while preventing the grade from growing
            // clipped-black regions on armor, hair, and night textures.
            var targetLuma = luma
                - (strength * (1.5 + (2.5 * shadow)) * midtone);
            var paintedBand = Math.Round(targetLuma / 14) * 14;
            targetLuma += (paintedBand - targetLuma) * (0.16 * strength * midtone);
            targetLuma = Math.Clamp(targetLuma, 0, 255);

            var candidateRed = targetLuma
                + ((red - luma) * saturationScale)
                + (strength * ((3.5 * shadow) + (5 * greenDominance) + midtone));
            var candidateGreen = targetLuma
                + ((green - luma) * saturationScale)
                + (strength * (0.75 * midtone));
            var candidateBlue = targetLuma
                + ((blue - luma) * saturationScale)
                - (strength * ((5 * shadow) + (5 * greenDominance) + (2 * blueDominance)));

            // Split-toning must not change the intended brightness curve. This
            // correction keeps the grade visible in hue/chroma without crushing
            // shadows or blowing highlights.
            var candidateLuma = (0.2126 * candidateRed)
                + (0.7152 * candidateGreen)
                + (0.0722 * candidateBlue);
            var lumaCorrection = targetLuma - candidateLuma;
            output[offset] = ClampByte(candidateRed + lumaCorrection);
            output[offset + 1] = ClampByte(candidateGreen + lumaCorrection);
            output[offset + 2] = ClampByte(candidateBlue + lumaCorrection);
            // Preserve source alpha byte-for-byte.
            output[offset + 3] = _rgba[offset + 3];
        }

        return new TgaPixelBuffer(Width, Height, output);
    }

    /// <summary>
    /// Gives an illustrated neural reconstruction a graphic hand-painted finish.
    /// The pass consolidates nearby, already-similar colors into broad value planes
    /// and reinforces only dark/light ridges that are present in the reconstruction.
    /// It does not trace a global outline or synthesize detail. Sampling can wrap for
    /// repeating world materials, and the input alpha plane is retained byte-for-byte.
    /// </summary>
    public TgaPixelBuffer ApplyGraphicPaintedFinish(
        double strength = 1,
        bool wrapEdges = true)
    {
        if (!double.IsFinite(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        if (strength == 0)
        {
            return new TgaPixelBuffer(Width, Height, (byte[])_rgba.Clone());
        }

        // Precomputed neighbors keep this bounded post-process inexpensive for
        // 2K/4K assets and make its edge behavior explicit and deterministic.
        var previousX = new int[Width];
        var nextX = new int[Width];
        var previousY = new int[Height];
        var nextY = new int[Height];
        for (var x = 0; x < Width; x++)
        {
            previousX[x] = wrapEdges ? Mod(x - 1, Width) : Math.Max(0, x - 1);
            nextX[x] = wrapEdges ? Mod(x + 1, Width) : Math.Min(Width - 1, x + 1);
        }

        for (var y = 0; y < Height; y++)
        {
            previousY[y] = wrapEdges ? Mod(y - 1, Height) : Math.Max(0, y - 1);
            nextY[y] = wrapEdges ? Mod(y + 1, Height) : Math.Min(Height - 1, y + 1);
        }

        var output = new byte[_rgba.Length];
        var similarityThreshold = 28 + (8 * (1 - strength));
        var planeStep = 12 + (6 * strength);
        var chromaStep = 9 + (3 * strength);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = ((y * Width) + x) * 4;
                var red = (double)_rgba[offset];
                var green = (double)_rgba[offset + 1];
                var blue = (double)_rgba[offset + 2];
                var centerLuma = Luminance(_rgba, offset);

                double redSum = red * 4;
                double greenSum = green * 4;
                double blueSum = blue * 4;
                double weightSum = 4;
                var localLumaSum = centerLuma;
                var localMinimum = centerLuma;
                var localMaximum = centerLuma;
                Accumulate(previousX[x], y);
                Accumulate(nextX[x], y);
                Accumulate(x, previousY[y]);
                Accumulate(x, nextY[y]);

                var normalizedLuma = centerLuma / 255;
                var midtone = 1 - Math.Abs((normalizedLuma * 2) - 1);
                var smoothingMix = strength * (0.20 + (0.16 * midtone));
                var smoothedRed = red + (((redSum / weightSum) - red) * smoothingMix);
                var smoothedGreen = green + (((greenSum / weightSum) - green) * smoothingMix);
                var smoothedBlue = blue + (((blueSum / weightSum) - blue) * smoothingMix);
                var smoothedLuma = (0.2126 * smoothedRed)
                    + (0.7152 * smoothedGreen)
                    + (0.0722 * smoothedBlue);

                // A partially blended value/chroma quantization creates painted
                // planes without producing hard posterization bands.
                var planeLuma = Math.Round(smoothedLuma / planeStep) * planeStep;
                var planeMix = strength * (0.24 + (0.12 * midtone));
                var targetLuma = smoothedLuma + ((planeLuma - smoothedLuma) * planeMix);
                var redChroma = smoothedRed - smoothedLuma;
                var blueChroma = smoothedBlue - smoothedLuma;
                var planeRedChroma = Math.Round(redChroma / chromaStep) * chromaStep;
                var planeBlueChroma = Math.Round(blueChroma / chromaStep) * chromaStep;
                var chromaMix = 0.24 * strength;
                redChroma += (planeRedChroma - redChroma) * chromaMix;
                blueChroma += (planeBlueChroma - blueChroma) * chromaMix;

                // Reinforce existing structural valleys/ridges only. This brings
                // out forms already inferred by the model instead of drawing a
                // uniform cartoon outline around every color transition.
                var localMean = localLumaSum / 5;
                var localRange = localMaximum - localMinimum;
                if (localRange > 18)
                {
                    var signedRidge = localMean - centerLuma;
                    var ridgeMagnitude = Math.Min(
                        7.5,
                        ((localRange - 18) * 0.055) + (Math.Abs(signedRidge) * 0.045));
                    if (signedRidge > 2)
                    {
                        targetLuma -= ridgeMagnitude * strength;
                    }
                    else if (signedRidge < -4)
                    {
                        targetLuma += ridgeMagnitude * strength * 0.42;
                    }
                }

                targetLuma = Math.Clamp(targetLuma, 1, 254);
                var candidateRed = targetLuma + redChroma;
                var candidateBlue = targetLuma + blueChroma;
                var candidateGreen = (targetLuma
                    - (0.2126 * candidateRed)
                    - (0.0722 * candidateBlue)) / 0.7152;
                output[offset] = ClampByte(candidateRed);
                output[offset + 1] = ClampByte(candidateGreen);
                output[offset + 2] = ClampByte(candidateBlue);
                output[offset + 3] = _rgba[offset + 3];

                void Accumulate(int sampleX, int sampleY)
                {
                    var sampleOffset = ((sampleY * Width) + sampleX) * 4;
                    var sampleLuma = Luminance(_rgba, sampleOffset);
                    localLumaSum += sampleLuma;
                    localMinimum = Math.Min(localMinimum, sampleLuma);
                    localMaximum = Math.Max(localMaximum, sampleLuma);
                    if (Math.Abs(sampleLuma - centerLuma) > similarityThreshold)
                    {
                        return;
                    }

                    redSum += _rgba[sampleOffset];
                    greenSum += _rgba[sampleOffset + 1];
                    blueSum += _rgba[sampleOffset + 2];
                    weightSum++;
                }
            }
        }

        return new TgaPixelBuffer(Width, Height, output);
    }

    /// <summary>
    /// Applies one bounded art-direction palette after the shared graphic-paint
    /// finish. Themes reshape only source-derived color and luminance; they never
    /// add random marks, alter geometry, or change alpha. ZoneAware must be resolved
    /// to a concrete theme by the artifact builder before this pixel stage.
    /// </summary>
    public TgaPixelBuffer ApplyPaintedTheme(
        PaintedTheme theme,
        double strength = 1)
    {
        if (!Enum.IsDefined(theme) || theme == PaintedTheme.ZoneAware)
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        if (!double.IsFinite(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        if (theme == PaintedTheme.ClassicPainted || strength == 0)
        {
            return new TgaPixelBuffer(Width, Height, (byte[])_rgba.Clone());
        }

        var output = (byte[])_rgba.Clone();
        var imageMeanLuma = CalculateVisibleColorStatistics().MeanLuminance;
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            // Hidden cutout color was already edge-dilated for safe filtering.
            // Keep it unchanged so a global theme statistic cannot recolor BC
            // blocks behind alpha and create a fringe at the visible boundary.
            if (_rgba[offset + 3] == byte.MinValue)
            {
                continue;
            }

            var red = (double)_rgba[offset];
            var green = (double)_rgba[offset + 1];
            var blue = (double)_rgba[offset + 2];
            var luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
            var normalizedLuma = luma / 255;
            var shadow = 1 - normalizedLuma;
            var highlight = normalizedLuma;
            var midtone = 1 - Math.Abs((normalizedLuma * 2) - 1);
            var peakChannel = Math.Max(red, Math.Max(green, blue)) / 255;
            var chroma = (Math.Max(red, Math.Max(green, blue))
                - Math.Min(red, Math.Min(green, blue))) / 255;

            // A zone mood is a restrained material/shadow direction, not a
            // blanket color wash. Preserve source-derived emissive paint,
            // stained glass, signs, magic, metals, and other vivid accents even
            // inside a dark zone such as Neriak. Near-white highlights receive
            // the same protection. A small residual treatment keeps the palette
            // cohesive without erasing the zone's authored color contrast.
            var vividAccent = SmoothStep(0.18, 0.62, chroma)
                * SmoothStep(0.35, 0.82, peakChannel);
            var brightAccent = SmoothStep(0.62, 0.94, normalizedLuma);
            var accentProtection = Math.Max(vividAccent, brightAccent);
            var localStrength = strength * (1 - (0.82 * accentProtection));

            double targetLuma;
            double saturationScale;
            double redBias;
            double greenBias;
            double blueBias;
            switch (theme)
            {
                case PaintedTheme.LightStorybook:
                    // Open the darkest painted planes without flattening their
                    // form, then add a restrained parchment-gold warmth.
                    targetLuma = luma
                        + (localStrength * ((9.2 * shadow) - (1.38 * highlight)) * midtone);
                    saturationScale = 1 + (0.161 * localStrength * midtone);
                    redBias = localStrength * ((7.82 * shadow) + (3.68 * highlight));
                    greenBias = localStrength * ((2.30 * shadow) + (2.07 * highlight));
                    blueBias = -localStrength * ((5.98 * shadow) + (1.61 * midtone));
                    break;

                case PaintedTheme.DarkGothic:
                    // Deepen midtone planes while keeping black detail readable.
                    // Cool blue-green shadows and restrained warm highlights fit
                    // underground/dark-city materials without forcing a new hue.
                    targetLuma = luma
                        - (localStrength * ((9.89 * midtone) + (2.42 * highlight)));
                    saturationScale = 1 - (0.092 * localStrength * shadow);
                    redBias = localStrength * ((2.99 * highlight) - (5.29 * shadow));
                    greenBias = localStrength * ((1.50 * highlight) + (1.73 * shadow));
                    blueBias = localStrength * ((6.90 * shadow) - (1.15 * highlight));
                    break;

                case PaintedTheme.ComicInk:
                    // Strengthen existing light/dark planes around the current
                    // mid-gray axis. This is texture-space contrast, not a mesh
                    // outline, so silhouettes and UV alignment remain unchanged.
                    var contrast = 1 + (0.324 * localStrength * (0.55 + (0.45 * midtone)));
                    targetLuma = imageMeanLuma + ((luma - imageMeanLuma) * contrast);
                    var inkBand = Math.Round(targetLuma / 20) * 20;
                    targetLuma += (inkBand - targetLuma) * (0.459 * localStrength * midtone);
                    saturationScale = 1 + (0.27 * localStrength * midtone);
                    redBias = 0;
                    greenBias = 0;
                    blueBias = 0;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(theme));
            }

            targetLuma = Math.Clamp(targetLuma, 1, 254);
            var candidateRed = targetLuma + ((red - luma) * saturationScale) + redBias;
            var candidateGreen = targetLuma + ((green - luma) * saturationScale) + greenBias;
            var candidateBlue = targetLuma + ((blue - luma) * saturationScale) + blueBias;

            // Biases carry palette direction, while this correction keeps the
            // intended theme curve in charge of brightness and protects the
            // stylized fidelity budget on already-dark or already-bright art.
            var candidateLuma = (0.2126 * candidateRed)
                + (0.7152 * candidateGreen)
                + (0.0722 * candidateBlue);
            var lumaCorrection = targetLuma - candidateLuma;
            output[offset] = ClampByte(candidateRed + lumaCorrection);
            output[offset + 1] = ClampByte(candidateGreen + lumaCorrection);
            output[offset + 2] = ClampByte(candidateBlue + lumaCorrection);
            output[offset + 3] = _rgba[offset + 3];
        }

        return new TgaPixelBuffer(Width, Height, output);
    }

    public TgaPixelBuffer AddWrappedBorder(int padding)
    {
        return AddBorder(padding, wrap: true);
    }

    public TgaPixelBuffer AddClampedBorder(int padding)
    {
        return AddBorder(padding, wrap: false);
    }

    private TgaPixelBuffer AddBorder(int padding, bool wrap)
    {
        if (padding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padding));
        }

        if (padding == 0)
        {
            return new TgaPixelBuffer(Width, Height, (byte[])_rgba.Clone());
        }

        var outputWidth = checked(Width + (padding * 2));
        var outputHeight = checked(Height + (padding * 2));
        if (outputWidth > ushort.MaxValue || outputHeight > ushort.MaxValue)
        {
            throw new NotSupportedException("The wrapped working TGA would exceed the TGA dimension limit.");
        }

        var output = new byte[checked(outputWidth * outputHeight * 4)];
        for (var outputY = 0; outputY < outputHeight; outputY++)
        {
            var sourceY = wrap
                ? Mod(outputY - padding, Height)
                : Math.Clamp(outputY - padding, 0, Height - 1);
            for (var outputX = 0; outputX < outputWidth; outputX++)
            {
                var sourceX = wrap
                    ? Mod(outputX - padding, Width)
                    : Math.Clamp(outputX - padding, 0, Width - 1);
                Buffer.BlockCopy(
                    _rgba,
                    ((sourceY * Width) + sourceX) * 4,
                    output,
                    ((outputY * outputWidth) + outputX) * 4,
                    4);
            }
        }

        return new TgaPixelBuffer(outputWidth, outputHeight, output);
    }

    public TgaPixelBuffer Crop(int left, int top, int width, int height)
    {
        if (left < 0 || top < 0 || width <= 0 || height <= 0
            || left > Width - width || top > Height - height)
        {
            throw new ArgumentOutOfRangeException(nameof(left), "Crop rectangle must be contained by the image.");
        }

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(
                _rgba,
                (((top + y) * Width) + left) * 4,
                output,
                y * width * 4,
                width * 4);
        }

        return new TgaPixelBuffer(width, height, output);
    }

    public TgaPixelBuffer WithScaledAlphaFrom(
        TgaPixelBuffer source,
        bool preserveCoverage,
        bool wrapEdges = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        var output = (byte[])_rgba.Clone();

        for (var y = 0; y < Height; y++)
        {
            var sourceY = (((y + 0.5) * source.Height) / Height) - 0.5;
            var y0 = (int)Math.Floor(sourceY);
            var yFraction = sourceY - y0;
            var y1 = y0 + 1;

            for (var x = 0; x < Width; x++)
            {
                var sourceX = (((x + 0.5) * source.Width) / Width) - 0.5;
                var x0 = (int)Math.Floor(sourceX);
                var xFraction = sourceX - x0;
                var x1 = x0 + 1;
                var alpha = BilinearAlpha(
                    source,
                    x0,
                    y0,
                    x1,
                    y1,
                    xFraction,
                    yFraction,
                    wrapEdges);
                output[((y * Width) + x) * 4 + 3] = (byte)Math.Clamp((int)Math.Round(alpha), 0, 255);
            }
        }

        if (preserveCoverage)
        {
            MatchCoverage(source._rgba, output, alphaThreshold: 128);
        }

        return new TgaPixelBuffer(Width, Height, output);
    }

    public TgaPixelBuffer RenormalizeNormals(bool reconstructPositiveZ = false)
    {
        var output = (byte[])_rgba.Clone();
        for (var index = 0; index < output.Length; index += 4)
        {
            var x = (output[index] / 127.5) - 1;
            var y = (output[index + 1] / 127.5) - 1;
            var z = reconstructPositiveZ
                ? Math.Sqrt(Math.Max(0, 1 - (x * x) - (y * y)))
                : (output[index + 2] / 127.5) - 1;
            var length = Math.Sqrt((x * x) + (y * y) + (z * z));
            if (length < 0.000_001)
            {
                x = 0;
                y = 0;
                z = 1;
            }
            else
            {
                x /= length;
                y /= length;
                z /= length;
            }

            output[index] = EncodeNormalComponent(x);
            output[index + 1] = EncodeNormalComponent(y);
            output[index + 2] = EncodeNormalComponent(z);
        }

        return new TgaPixelBuffer(Width, Height, output);
    }

    private byte[] ToBytes()
    {
        var output = new byte[checked(18 + (Width * Height * 4))];
        output[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12, 2), checked((ushort)Width));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(14, 2), checked((ushort)Height));
        output[16] = 32;
        output[17] = 0x28; // Top-left origin, eight alpha bits.

        var outputIndex = 18;
        for (var index = 0; index < _rgba.Length; index += 4)
        {
            output[outputIndex++] = _rgba[index + 2];
            output[outputIndex++] = _rgba[index + 1];
            output[outputIndex++] = _rgba[index];
            output[outputIndex++] = _rgba[index + 3];
        }

        return output;
    }

    private static double BilinearAlpha(
        TgaPixelBuffer source,
        int x0,
        int y0,
        int x1,
        int y1,
        double xFraction,
        double yFraction,
        bool wrapEdges)
    {
        var a00 = source.AlphaAt(x0, y0, wrapEdges);
        var a10 = source.AlphaAt(x1, y0, wrapEdges);
        var a01 = source.AlphaAt(x0, y1, wrapEdges);
        var a11 = source.AlphaAt(x1, y1, wrapEdges);
        var top = a00 + ((a10 - a00) * xFraction);
        var bottom = a01 + ((a11 - a01) * xFraction);
        return top + ((bottom - top) * yFraction);
    }

    private byte AlphaAt(int x, int y, bool wrapEdges)
    {
        var sampledX = wrapEdges ? Mod(x, Width) : Math.Clamp(x, 0, Width - 1);
        var sampledY = wrapEdges ? Mod(y, Height) : Math.Clamp(y, 0, Height - 1);
        return _rgba[((sampledY * Width) + sampledX) * 4 + 3];
    }

    private static double Luminance(byte[] rgba, int offset) =>
        (0.2126 * rgba[offset]) + (0.7152 * rgba[offset + 1]) + (0.0722 * rgba[offset + 2]);

    private static byte ClampByte(double value) =>
        (byte)Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue);

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var normalized = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return normalized * normalized * (3 - (2 * normalized));
    }

    private static double CalculateStableGain(
        double sourceMean,
        double enhancedMean,
        double minimumGain,
        double maximumGain)
    {
        // Very dark channels have an unstable ratio and usually represent an
        // intentional palette bias. Leave them untouched instead of amplifying
        // quantization noise.
        if (sourceMean < 8 || enhancedMean < 8)
        {
            return 1;
        }

        return Math.Clamp(sourceMean / enhancedMean, minimumGain, maximumGain);
    }

    private static void MatchCoverage(byte[] source, byte[] destination, byte alphaThreshold)
    {
        var sourcePixels = source.Length / 4;
        var destinationPixels = destination.Length / 4;
        var coveredSourcePixels = CountCovered(source, alphaThreshold);
        var targetCoveredPixels = (int)Math.Round(
            (double)coveredSourcePixels / sourcePixels * destinationPixels,
            MidpointRounding.AwayFromZero);

        var low = -255;
        var high = 255;
        while (low <= high)
        {
            var adjustment = low + ((high - low) / 2);
            var covered = CountCovered(destination, alphaThreshold, adjustment);
            if (covered < targetCoveredPixels)
            {
                low = adjustment + 1;
            }
            else
            {
                high = adjustment - 1;
            }
        }

        var selectedAdjustment = Math.Clamp(low, -255, 255);
        for (var index = 3; index < destination.Length; index += 4)
        {
            // Preserve fully transparent and fully opaque source regions.
            // Coverage correction is only needed on the interpolated edge
            // samples; shifting the endpoints makes foliage and decals
            // globally translucent and creates visible halos.
            if (destination[index] is > byte.MinValue and < byte.MaxValue)
            {
                destination[index] = (byte)Math.Clamp(
                    destination[index] + selectedAdjustment,
                    0,
                    255);
            }
        }
    }

    private static int CountCovered(byte[] pixels, byte threshold, int adjustment = 0)
    {
        var count = 0;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index];
            var adjusted = alpha is byte.MinValue or byte.MaxValue
                ? alpha
                : Math.Clamp(alpha + adjustment, 0, 255);
            if (adjusted >= threshold)
            {
                count++;
            }
        }

        return count;
    }

    private static int Mod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static byte EncodeNormalComponent(double value) =>
        (byte)Math.Clamp((int)Math.Round((Math.Clamp(value, -1, 1) + 1) * 127.5), 0, 255);
}
