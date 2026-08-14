using SpinTexture.Core.Models;

namespace SpinTexture.Core.Textures;

public enum ClassificationConfidence
{
    Low,
    Medium,
    High
}

public sealed record TextureClassification(
    TextureKind Kind,
    ClassificationConfidence Confidence,
    bool CanUseColorUpscaler,
    bool RequiresSeparateAlphaHandling,
    bool RequiresReview,
    IReadOnlyList<string> Reasons);

public sealed class TextureSemanticClassifier
{
    private static readonly HashSet<string> NormalWordTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "normal", "norm", "nrml", "bump"
    };

    // Compact normal suffixes ("wall_n", "armor-ns") only count when they are
    // real delimiter-separated tokens. Legacy diffuse names such as wall1n or
    // qrc2n produce an isolated "n" via the digit-to-letter split, and those
    // must not silently divert a color texture into the preserved normal path.
    private static readonly HashSet<string> NormalSuffixTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "nm", "nrm", "n", "ng", "ngs", "ns"
    };

    private static readonly HashSet<string> MaskTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "mask", "opacity", "alpha", "spec", "specular", "gloss", "glossiness",
        "rough", "roughness", "metal", "metallic", "height", "displacement", "ao",
        "occlusion", "emissive", "coverage", "cvg"
    };

    private static readonly HashSet<string> EnvironmentMapTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "env", "environment", "cubemap"
    };

    private static readonly HashSet<string> UserInterfaceTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ui", "gui", "hud", "interface", "window", "button", "cursor", "font", "glyph", "icon",
        "spell", "spells", "spellbook", "inventory", "inv", "slot", "hotbar", "loading", "splash"
    };

    private static readonly HashSet<string> AnimatedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "anim", "animated", "effect", "effects", "emitter",
        "sprite", "spritesheet", "particle", "flipbook",
        "smoke", "smke", "flame", "spely"
    };

    private static readonly HashSet<string> PackedImageTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "atlas", "palette", "tilemap", "tileset"
    };

    public TextureClassification Classify(string logicalName, TextureMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(metadata);

        var reasons = new List<string>();
        var tokens = Tokenize(logicalName);

        if (!metadata.IsSimpleTwoDimensionalTexture)
        {
            reasons.Add("Texture is a cube, volume, or array resource and requires resource-aware processing.");
            return Result(TextureKind.Unsupported, ClassificationConfidence.High, false, metadata.HasAlpha, true, reasons);
        }

        if (metadata.TexconvFormat is null && metadata.FileFormat == TextureFileFormat.Dds)
        {
            reasons.Add("DDS pixel format cannot be reproduced safely by the current DirectXTex command builder.");
            return Result(TextureKind.Unsupported, ClassificationConfidence.High, false, metadata.HasAlpha, true, reasons);
        }

        if (tokens.Overlaps(NormalWordTokens)
            || TokenizeDelimitedOnly(logicalName).Overlaps(NormalSuffixTokens))
        {
            reasons.Add("The logical name contains a normal- or bump-map token.");
            reasons.Add("Vector data must be interpolated and renormalized instead of processed as color.");
            return Result(TextureKind.Normal, ClassificationConfidence.High, false, false, false, reasons);
        }

        if (tokens.Overlaps(MaskTokens))
        {
            reasons.Add("The logical name contains a scalar or packed-mask token.");
            return Result(TextureKind.Mask, ClassificationConfidence.High, false, metadata.HasAlpha, false, reasons);
        }

        if (Tokenize(Path.GetFileName(logicalName)).Overlaps(EnvironmentMapTokens))
        {
            reasons.Add("The logical name indicates an environment/reflection lookup texture.");
            return Result(TextureKind.Unsupported, ClassificationConfidence.High, false, metadata.HasAlpha, false, reasons);
        }

        if (tokens.Overlaps(AnimatedTokens) || HasCompactLegacyEffectPrefix(logicalName))
        {
            reasons.Add("The logical name indicates an animation, sprite sheet, particle, or flipbook.");
            reasons.Add("Frame or cell boundaries must be known before upscaling.");
            return Result(TextureKind.Animated, ClassificationConfidence.High, false, metadata.HasAlpha, true, reasons);
        }

        if (tokens.Overlaps(PackedImageTokens))
        {
            reasons.Add("The logical name indicates a packed palette, atlas, tile map, or tile set.");
            reasons.Add("Cell boundaries must be known before independent texture reconstruction.");
            return Result(TextureKind.Unsupported, ClassificationConfidence.High, false, metadata.HasAlpha, true, reasons);
        }

        if (tokens.Overlaps(UserInterfaceTokens))
        {
            reasons.Add("The logical name contains a user-interface or glyph token.");
            return Result(TextureKind.UserInterface, ClassificationConfidence.High, true, metadata.HasAlpha, true, reasons);
        }

        if (metadata.AlphaStatus == TextureAlphaStatus.Explicit)
        {
            reasons.Add("The header explicitly declares an alpha channel.");
            reasons.Add("RGB and alpha must be processed separately and alpha coverage validated.");
            return Result(TextureKind.Cutout, ClassificationConfidence.Medium, true, true, false, reasons);
        }

        if (metadata.AlphaStatus == TextureAlphaStatus.Possible)
        {
            reasons.Add("The container can carry alpha, but the header does not prove that it is meaningful.");
            return Result(TextureKind.Color, ClassificationConfidence.Low, true, true, true, reasons);
        }

        reasons.Add("The resource is a supported two-dimensional color texture with no declared alpha channel.");
        return Result(TextureKind.Color, ClassificationConfidence.Medium, true, false, false, reasons);
    }

    private static TextureClassification Result(
        TextureKind kind,
        ClassificationConfidence confidence,
        bool canUseColorUpscaler,
        bool requiresSeparateAlpha,
        bool requiresReview,
        IReadOnlyList<string> reasons) =>
        new(kind, confidence, canUseColorUpscaler, requiresSeparateAlpha, requiresReview, reasons);

    private static HashSet<string> Tokenize(string logicalName)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new List<char>();

        // Keep relative directory names as semantic evidence. Effect/UI/mask
        // folders often carry the only reliable context for compact legacy
        // filenames.
        var semanticPath = Path.ChangeExtension(logicalName, null) ?? logicalName;
        char? previous = null;
        foreach (var character in semanticPath)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (current.Count > 0
                    && ((char.IsUpper(character)
                            && previous.HasValue
                            && char.IsLower(previous.Value))
                        || (char.IsLetter(character)
                            && previous.HasValue
                            && char.IsDigit(previous.Value))))
                {
                    AddToken(tokens, current);
                }

                current.Add(char.ToLowerInvariant(character));
                previous = character;
                continue;
            }

            AddToken(tokens, current);
            previous = null;
        }

        AddToken(tokens, current);
        return tokens;
    }

    private static HashSet<string> TokenizeDelimitedOnly(string logicalName)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new List<char>();
        var semanticPath = Path.ChangeExtension(logicalName, null) ?? logicalName;
        foreach (var character in semanticPath)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Add(char.ToLowerInvariant(character));
                continue;
            }

            AddDelimitedToken(tokens, current);
        }

        AddDelimitedToken(tokens, current);
        return tokens;
    }

    private static void AddDelimitedToken(ISet<string> tokens, List<char> current)
    {
        if (current.Count == 0)
        {
            return;
        }

        var token = new string(current.ToArray());
        tokens.Add(token);

        // "_12ngs" and "_01nrml" style suffixes carry a frame/variant number in
        // front of the semantic letters; strip digits from both ends so those
        // still register. "wall1n" has no delimiter at all and never lands here
        // as a bare "n".
        var start = 0;
        while (start < token.Length && char.IsDigit(token[start]))
        {
            start++;
        }

        var end = token.Length;
        while (end > start && char.IsDigit(token[end - 1]))
        {
            end--;
        }

        if (end > start && (start > 0 || end < token.Length))
        {
            tokens.Add(token[start..end]);
        }

        current.Clear();
    }

    private static bool HasCompactLegacyEffectPrefix(string logicalName)
    {
        var stem = Path.GetFileNameWithoutExtension(logicalName);
        return stem.StartsWith("smoke", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("smke", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("flame", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("spely", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddToken(ISet<string> tokens, List<char> current)
    {
        if (current.Count == 0)
        {
            return;
        }

        var token = new string(current.ToArray());
        tokens.Add(token);
        var semanticLength = token.Length;
        while (semanticLength > 0 && char.IsDigit(token[semanticLength - 1]))
        {
            semanticLength--;
        }

        if (semanticLength > 0 && semanticLength < token.Length)
        {
            tokens.Add(token[..semanticLength]);
        }

        current.Clear();
    }
}
