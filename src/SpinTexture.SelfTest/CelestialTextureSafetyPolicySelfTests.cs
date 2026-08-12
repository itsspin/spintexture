using SpinTexture.Core.Textures;

namespace SpinTexture.SelfTest;

internal static class CelestialTextureSafetyPolicySelfTests
{
    public static void Run()
    {
        TestLegacySkyArchiveBoundary();
        TestNativeSkyResourceBoundary();
        TestAuditedLooseCelestialSprites();
        TestConservativeNegativeBoundary();
    }

    private static void TestNativeSkyResourceBoundary()
    {
        AssertEqual(
            CelestialTextureSafetyPolicy.SkyResourcePreservedReason,
            CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                Path.Combine("Resources", "sky", "stars.dds")),
            "native star atlas");
        AssertEqual(
            CelestialTextureSafetyPolicy.SkyResourcePreservedReason,
            CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                Path.Combine("resources", "SKY", "fear_comet_stars.dds")),
            "case-insensitive native sky resource");
        AssertNull(
            CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                Path.Combine("Resources", "skybox", "stars.dds")),
            "similar resource directory");
        AssertNull(
            CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                Path.Combine("Resources", "sky", "..", "stars.dds")),
            "native sky traversal");
    }

    private static void TestLegacySkyArchiveBoundary()
    {
        AssertEqual(
            CelestialTextureSafetyPolicy.LegacySkyArchivePreservedReason,
            CelestialTextureSafetyPolicy.GetPreservedReason("sky.s3d", "sun.bmp"),
            "legacy sun");
        AssertEqual(
            CelestialTextureSafetyPolicy.LegacySkyArchivePreservedReason,
            CelestialTextureSafetyPolicy.GetPreservedReason("SKY.S3D", "sky.wld"),
            "entire legacy sky archive");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason("sky.eqg", "sky_c.dds"),
            "modern sky model archive must not be blanket-protected");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason("mysky.s3d", "sun.bmp"),
            "similar archive name");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"zones{Path.DirectorySeparatorChar}sky.s3d",
                "sun.bmp"),
            "non-canonical nested archive");
    }

    private static void TestAuditedLooseCelestialSprites()
    {
        string[] actorEffects =
        [
            "8starsp501.dds",
            "flare_blsp501.tga",
            "flare_whsp501.tga",
            "flaresp601.dds",
            "cloudsp501.dds",
            "sunburst.dds"
        ];
        string[] spellEffects =
        [
            "8starsp501.dds",
            "bluestarsp501.dds",
            "cloudsp501.dds",
            "comet1.dds",
            "cometsp601.dds",
            "flare.dds",
            "flare01.dds",
            "flare_blsp501.tga",
            "flare_blue.dds",
            "flare_blue01.dds",
            "flare_green01.dds",
            "flare_grey02.dds",
            "flare_grey03.dds",
            "flare_orange02.dds",
            "flare_orange04.dds",
            "flare_whsp501.tga",
            "flaresp601.dds",
            "fullmoon01.dds",
            "fullmoon02.dds",
            "glowflare02.tga",
            "glowflare02_env.dds",
            "glowflare03.dds",
            "glowflare03.tga",
            "halo1.dds",
            "halo2.dds",
            "halo3.dds",
            "prism_halo.dds",
            "prism_cloud.dds",
            "solro_sun.dds",
            "solstice_moon.dds",
            "star_sp_001.dds",
            "stara.dds"
        ];

        AssertProtected("ActorEffects", actorEffects);
        AssertProtected("SpellEffects", spellEffects);
    }

    private static void TestConservativeNegativeBoundary()
    {
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"EnvEmitterEffects{Path.DirectorySeparatorChar}star_spin01.dds",
                "star_spin01.dds"),
            "environment emitter is outside the audited loose boundary");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"SpellEffects{Path.DirectorySeparatorChar}moonstone.dds",
                "moonstone.dds"),
            "moon substring");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"SpellEffects{Path.DirectorySeparatorChar}halogen.dds",
                "halogen.dds"),
            "halo substring");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"ActorEffects{Path.DirectorySeparatorChar}flaregun.dds",
                "flaregun.dds"),
            "flare substring");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"SpellEffects{Path.DirectorySeparatorChar}sunburst-copy.dds",
                "sunburst-copy.dds"),
            "modified celestial-like filename");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"SpellEffects{Path.DirectorySeparatorChar}ordinary.dds",
                "sunburst.dds"),
            "mismatched logical name");
        AssertNull(
            CelestialTextureSafetyPolicy.GetPreservedReason(
                $"SpellEffects{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}sunburst.dds",
                "sunburst.dds"),
            "traversal path");
    }

    private static void AssertProtected(string directory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            AssertEqual(
                CelestialTextureSafetyPolicy.CelestialSpritePreservedReason,
                CelestialTextureSafetyPolicy.GetPreservedReason(
                    $"{directory}{Path.DirectorySeparatorChar}{name}",
                    name),
                $"{directory}/{name}");
        }
    }

    private static void AssertNull(string? value, string message)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(
                $"Celestial safety policy assertion failed for {message}: expected no preservation reason, got '{value}'.");
        }
    }

    private static void AssertEqual(string expected, string? actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Celestial safety policy assertion failed for {message}: expected '{expected}', got '{actual ?? "<null>"}'.");
        }
    }
}
