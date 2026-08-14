using SpinTexture.Core.Models;

namespace SpinTexture.Core.Services;

public enum TextureOptionPreviewSourceProvenance
{
    ClientSource,
    ManagedVerifiedOriginal
}

/// <summary>
/// A client-derived before/after example generated with the same texture
/// processor and safety policy used by a staged pack build.
/// </summary>
public sealed record TextureOptionPreviewSample(
    string SampleKey,
    string Category,
    string Title,
    string ContainerRelativePath,
    string LogicalName,
    string OriginalImagePath,
    string EnhancedImagePath,
    TexturePreset RequestedPreset,
    TexturePreset ResolvedPreset,
    PaintedTheme RequestedTheme,
    PaintedTheme ResolvedTheme,
    string ProcessingRoute,
    bool UsedFallback,
    int SourceWidth,
    int SourceHeight,
    int ResultWidth,
    int ResultHeight,
    int ResultMipCount,
    bool IsSameAs2048,
    TextureOptionPreviewSourceProvenance SourceProvenance)
{
    /// <summary>
    /// The preset that produced the returned pixels. A native fidelity or
    /// availability fallback always resolves to the faithful worker.
    /// </summary>
    public TexturePreset ActualPreset => UsedFallback
        ? TexturePreset.Faithful
        : ResolvedPreset;
}

/// <summary>
/// The immutable cache result for one complete set of texture options.
/// Every image path is underneath the profile's Cache directory.
/// </summary>
public sealed record TextureOptionPreviewResult(
    string CacheKey,
    DateTimeOffset CreatedUtc,
    TexturePreset RequestedPreset,
    PaintedTheme RequestedTheme,
    IReadOnlyList<TextureOptionPreviewSample> Samples)
{
    public bool HasSamples => Samples.Count > 0;
}
