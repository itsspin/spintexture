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
        double maximumLinearScale = RealEsrganCommandBuilder.ModelScale)
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

        var largestSourceDimension = Math.Max(sourceWidth, sourceHeight);
        var linearScale = Math.Min(maximumLinearScale, (double)maximumDimension / largestSourceDimension);
        linearScale = Math.Max(1, linearScale);

        var outputWidth = Math.Min(
            maximumDimension,
            Math.Max(sourceWidth, (int)Math.Round(sourceWidth * linearScale, MidpointRounding.AwayFromZero)));
        var outputHeight = Math.Min(
            maximumDimension,
            Math.Max(sourceHeight, (int)Math.Round(sourceHeight * linearScale, MidpointRounding.AwayFromZero)));

        return new UpscaleDimensions(
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            Math.Min((double)outputWidth / sourceWidth, (double)outputHeight / sourceHeight));
    }
}
