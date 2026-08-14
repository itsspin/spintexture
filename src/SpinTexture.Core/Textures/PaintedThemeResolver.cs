using SpinTexture.Core.Models;

namespace SpinTexture.Core.Textures;

/// <summary>
/// Resolves the user-facing "Follow each zone" choice to a concrete, deterministic
/// painted palette. The first-release mood map is intentionally small and exact:
/// unknown, shared, character, equipment, and renderer archives keep the balanced
/// Classic Painted palette until paired in-game screenshots justify an addition.
/// </summary>
internal static class PaintedThemeResolver
{
    private static readonly IReadOnlyDictionary<string, PaintedTheme> ReviewedZoneThemes =
        new Dictionary<string, PaintedTheme>(StringComparer.OrdinalIgnoreCase)
        {
            // Dark cities, dungeons, and planes. Neriak is deliberately explicit
            // rather than inferred from a filename or texture's average color.
            ["neriaka"] = PaintedTheme.DarkGothic,
            ["neriakb"] = PaintedTheme.DarkGothic,
            ["neriakc"] = PaintedTheme.DarkGothic,
            ["nektulos"] = PaintedTheme.DarkGothic,
            ["befallen"] = PaintedTheme.DarkGothic,
            ["fearplane"] = PaintedTheme.DarkGothic,
            ["hateplane"] = PaintedTheme.DarkGothic,
            ["hateplaneb"] = PaintedTheme.DarkGothic,
            ["guktop"] = PaintedTheme.DarkGothic,
            ["gukbottom"] = PaintedTheme.DarkGothic,
            ["mistmoore"] = PaintedTheme.DarkGothic,
            ["najena"] = PaintedTheme.DarkGothic,
            ["paineel"] = PaintedTheme.DarkGothic,
            ["unrest"] = PaintedTheme.DarkGothic,

            // Bright cities and pastoral/outdoor regions reviewed for a warmer,
            // clearer storybook treatment.
            ["felwithea"] = PaintedTheme.LightStorybook,
            ["felwitheb"] = PaintedTheme.LightStorybook,
            ["gfaydark"] = PaintedTheme.LightStorybook,
            ["rivervale"] = PaintedTheme.LightStorybook,
            ["misty"] = PaintedTheme.LightStorybook,
            ["qeynos"] = PaintedTheme.LightStorybook,
            ["qeynos2"] = PaintedTheme.LightStorybook,
            ["qeytoqrg"] = PaintedTheme.LightStorybook,
            ["qrg"] = PaintedTheme.LightStorybook,
            ["qey2hh1"] = PaintedTheme.LightStorybook,
            ["eastkarana"] = PaintedTheme.LightStorybook,
            ["northkarana"] = PaintedTheme.LightStorybook,
            ["southkarana"] = PaintedTheme.LightStorybook,
            ["halas"] = PaintedTheme.LightStorybook,
            ["everfrost"] = PaintedTheme.LightStorybook
        };

    public static UpscaleOptions ResolveForArtifact(
        UpscaleOptions options,
        string relativeInstallPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeInstallPath);

        if (options.Preset != TexturePreset.Illustrated)
        {
            return options with { PaintedTheme = PaintedTheme.ClassicPainted };
        }

        if (options.PaintedTheme != PaintedTheme.ZoneAware)
        {
            return options;
        }

        var zoneName = TryNormalizeZoneArchive(relativeInstallPath);
        if (zoneName is null)
        {
            return options with { PaintedTheme = PaintedTheme.ClassicPainted };
        }

        if (options.Scope == AssetScope.SelectedZone
            && !string.IsNullOrWhiteSpace(options.SelectedZone)
            && !zoneName.Equals(
                NormalizeSelectedZone(options.SelectedZone),
                StringComparison.OrdinalIgnoreCase))
        {
            return options with { PaintedTheme = PaintedTheme.ClassicPainted };
        }

        return options with
        {
            PaintedTheme = ReviewedZoneThemes.GetValueOrDefault(
                zoneName,
                PaintedTheme.ClassicPainted)
        };
    }

    private static string? TryNormalizeZoneArchive(string relativeInstallPath)
    {
        // Use slash normalization rather than platform-specific Path parsing so
        // recovery/manifests remain deterministic if inspected on another OS.
        var normalizedPath = relativeInstallPath.Replace('\\', '/');
        var fileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..].Trim();
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".s3d", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".eqg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName).Trim().ToLowerInvariant();
        if (stem.EndsWith("_2_obj", StringComparison.Ordinal))
        {
            stem = stem[..^"_2_obj".Length];
        }
        else if (stem.EndsWith("_obj", StringComparison.Ordinal))
        {
            stem = stem[..^"_obj".Length];
        }

        return stem.Length == 0 ? null : stem;
    }

    private static string NormalizeSelectedZone(string selectedZone) =>
        selectedZone.Trim().ToLowerInvariant();
}
