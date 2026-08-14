namespace SpinTexture.Core.Models;

/// <summary>
/// User-facing Graphic Painted Fantasy style controls, each normalized to
/// [0, 1]. A null value on <see cref="UpscaleOptions"/> means "use defaults"
/// and keeps older manifests, checkpoints, and repair identity comparisons
/// byte-compatible.
/// </summary>
public sealed record PaintedStyleSettings(
    double StrokeSize,
    double StrokeStrength,
    double DetailPreservation,
    double ColorSimplification,
    double CanvasGrain)
{
    public static PaintedStyleSettings Default { get; } = new(0.5, 0.55, 0.6, 0.5, 0.35);

    /// <summary>Returns an equivalent instance with every control clamped to [0, 1].</summary>
    public PaintedStyleSettings Clamped() => new(
        ClampUnit(StrokeSize),
        ClampUnit(StrokeStrength),
        ClampUnit(DetailPreservation),
        ClampUnit(ColorSimplification),
        ClampUnit(CanvasGrain));

    private static double ClampUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;
}
