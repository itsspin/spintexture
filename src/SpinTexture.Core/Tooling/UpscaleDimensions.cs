namespace SpinTexture.Core.Tooling;

public sealed record UpscaleDimensions(
    int SourceWidth,
    int SourceHeight,
    int OutputWidth,
    int OutputHeight,
    double LinearScale)
{
    public bool RequiresUpscale => OutputWidth > SourceWidth || OutputHeight > SourceHeight;

    public int RequiredNeuralScale
    {
        get
        {
            if (!RequiresUpscale)
            {
                return 1;
            }

            var requiredScale = Math.Max(
                (double)OutputWidth / SourceWidth,
                (double)OutputHeight / SourceHeight);
            return Math.Clamp(
                (int)Math.Ceiling(requiredScale),
                RealEsrganCommandBuilder.MinimumOutputScale,
                RealEsrganCommandBuilder.ModelScale);
        }
    }

    public static UpscaleDimensions Calculate(
        int sourceWidth,
        int sourceHeight,
        int maximumDimension,
        double maximumLinearScale = RealEsrganCommandBuilder.ModelScale,
        int dimensionAlignment = 1)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be positive.");
        }

        if (maximumDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        }

        if (maximumLinearScale < 1 || !double.IsFinite(maximumLinearScale))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLinearScale));
        }

        if (dimensionAlignment is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensionAlignment));
        }

        var largestSourceDimension = Math.Max(sourceWidth, sourceHeight);
        var linearScale = Math.Min(maximumLinearScale, (double)maximumDimension / largestSourceDimension);
        linearScale = Math.Max(1, linearScale);

        var outputWidth = Math.Min(
            maximumDimension,
            Math.Max(sourceWidth, (int)Math.Round(sourceWidth * linearScale, MidpointRounding.AwayFromZero)));
        var outputHeight = Math.Min(
            maximumDimension,
            Math.Max(sourceHeight, (int)Math.Round(sourceHeight * linearScale, MidpointRounding.AwayFromZero)));

        if (dimensionAlignment > 1)
        {
            // Block-compressed encoders refuse non-multiple-of-4 top levels;
            // a capped non-power-of-two source would otherwise fail encoding
            // and silently fall back to the preserved original.
            outputWidth = AlignDimension(outputWidth, dimensionAlignment, maximumDimension);
            outputHeight = AlignDimension(outputHeight, dimensionAlignment, maximumDimension);
        }

        return new UpscaleDimensions(
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            Math.Min((double)outputWidth / sourceWidth, (double)outputHeight / sourceHeight));
    }

    private static int AlignDimension(int value, int alignment, int maximum)
    {
        var aligned = (int)Math.Round((double)value / alignment, MidpointRounding.AwayFromZero) * alignment;
        return Math.Clamp(aligned, alignment, maximum - (maximum % alignment));
    }
}
