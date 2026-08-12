namespace SpinTexture.Core.Textures;

/// <summary>
/// Identifies legacy sky-renderer assets whose dimensions, indexed palettes,
/// color keys, or sprite borders are part of the renderer contract. These
/// assets must remain byte-compatible with the verified EverQuest originals.
/// </summary>
public static class CelestialTextureSafetyPolicy
{
    public const string LegacySkyArchivePreservedReason =
        "Protected legacy sky renderer archive";

    public const string CelestialSpritePreservedReason =
        "Protected celestial sprite texture";

    public const string SkyResourcePreservedReason =
        "Protected native sky renderer resource";

    // Exact names observed in the supported client. Keep this intentionally
    // narrow: generic words such as "star", "flare", or "moon" must not turn
    // unrelated future art into a protected texture through substring matches.
    private static readonly HashSet<string> CelestialSpritePaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ActorEffects/8starsp501.dds",
            "ActorEffects/flare_blsp501.tga",
            "ActorEffects/flare_whsp501.tga",
            "ActorEffects/flaresp601.dds",
            "ActorEffects/cloudsp501.dds",
            "ActorEffects/sunburst.dds",
            "SpellEffects/8starsp501.dds",
            "SpellEffects/bluestarsp501.dds",
            "SpellEffects/cloudsp501.dds",
            "SpellEffects/comet1.dds",
            "SpellEffects/cometsp601.dds",
            "SpellEffects/flare.dds",
            "SpellEffects/flare01.dds",
            "SpellEffects/flare_blsp501.tga",
            "SpellEffects/flare_blue.dds",
            "SpellEffects/flare_blue01.dds",
            "SpellEffects/flare_green01.dds",
            "SpellEffects/flare_grey02.dds",
            "SpellEffects/flare_grey03.dds",
            "SpellEffects/flare_orange02.dds",
            "SpellEffects/flare_orange04.dds",
            "SpellEffects/flare_whsp501.tga",
            "SpellEffects/flaresp601.dds",
            "SpellEffects/fullmoon01.dds",
            "SpellEffects/fullmoon02.dds",
            "SpellEffects/glowflare02.tga",
            "SpellEffects/glowflare02_env.dds",
            "SpellEffects/glowflare03.dds",
            "SpellEffects/glowflare03.tga",
            "SpellEffects/halo1.dds",
            "SpellEffects/halo2.dds",
            "SpellEffects/halo3.dds",
            "SpellEffects/prism_halo.dds",
            "SpellEffects/prism_cloud.dds",
            "SpellEffects/solro_sun.dds",
            "SpellEffects/solstice_moon.dds",
            "SpellEffects/star_sp_001.dds",
            "SpellEffects/stara.dds"
        };

    /// <summary>
    /// Returns a stable reporting reason when an archive member or loose
    /// texture must be retained from the verified original; otherwise null.
    /// </summary>
    public static string? GetPreservedReason(
        string relativeInstallPath,
        string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeInstallPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var pathSegments = TrySplitSafeRelativePath(relativeInstallPath);
        if (pathSegments is null)
        {
            return null;
        }

        if (pathSegments.Length == 1
            && pathSegments[0].Equals("sky.s3d", StringComparison.OrdinalIgnoreCase))
        {
            // Protect every member, including sky.wld and palette.bmp. The
            // archive combines renderer metadata with dimension- and
            // palette-sensitive sky, cloud, sun, moon, and planet art.
            return LegacySkyArchivePreservedReason;
        }

        if (pathSegments.Length != 2)
        {
            return null;
        }

        var relativeFileName = pathSegments[1];
        var logicalFileName = Path.GetFileName(
            logicalName.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        if (!relativeFileName.Equals(logicalFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var canonicalPath = $"{pathSegments[0]}/{relativeFileName}";
        return CelestialSpritePaths.Contains(canonicalPath)
            ? CelestialSpritePreservedReason
            : null;
    }

    /// <summary>
    /// Identifies loose resources owned by EQL's native sky renderer. These
    /// files are renderer atlases or configuration data rather than ordinary
    /// world art and must never be staged or repaired as enhanced textures.
    /// </summary>
    public static string? GetSkyResourcePreservedReason(string relativeInstallPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeInstallPath);
        var segments = TrySplitSafeRelativePath(relativeInstallPath);
        return segments is { Length: >= 3 }
            && segments[0].Equals("Resources", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("sky", StringComparison.OrdinalIgnoreCase)
                ? SkyResourcePreservedReason
                : null;
    }

    private static string[]? TrySplitSafeRelativePath(string path)
    {
        var normalized = path.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return null;
        }

        var segments = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
                ? null
                : segments;
    }
}
