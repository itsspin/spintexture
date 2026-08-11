using SpinTexture.Core.Models;

namespace SpinTexture.Core.Textures;

/// <summary>
/// Conservative boundary for loose, single-image effect art. Packed animation
/// sheets remain protected because changing their cell boundaries can corrupt
/// the effect even when the resulting image looks valid in isolation.
/// </summary>
public static class SpellEffectTexturePolicy
{
    private static readonly HashSet<string> EffectDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "SpellEffects",
        "ActorEffects",
        "EnvEmitterEffects",
        "RenderEffects"
    };

    private static readonly string[] PackedTerms =
    [
        "anim", "atlas", "flipbook", "sequence", "sheet", "sprite", "strip", "tilemap"
    ];

    public static bool IsEffectPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var segment = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            2,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return segment is not null && EffectDirectories.Contains(segment);
    }

    public static bool CanEnhance(string relativePath, TextureMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(metadata);
        var extension = Path.GetExtension(relativePath);
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var semantic = new TextureSemanticClassifier().Classify(
            Path.GetFileName(relativePath),
            metadata);
        return IsEffectPath(relativePath)
            && (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
            && metadata.IsSimpleTwoDimensionalTexture
            && metadata.Width >= 32
            && metadata.Height >= 32
            && Math.Max(metadata.Width, metadata.Height)
                <= Math.Min(metadata.Width, metadata.Height) * 2
            && semantic.Kind is not (TextureKind.Normal or TextureKind.Mask or TextureKind.Unsupported)
            && !PackedTerms.Any(term => stem.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static TextureClassification CreateColorClassification(TextureMetadata metadata) =>
        new(
            metadata.HasAlpha ? TextureKind.Cutout : TextureKind.Color,
            ClassificationConfidence.Medium,
            CanUseColorUpscaler: true,
            RequiresSeparateAlphaHandling: metadata.HasAlpha,
            RequiresReview: true,
            ["Loose single-image effect selected by the conservative spell-effect policy."]);
}
