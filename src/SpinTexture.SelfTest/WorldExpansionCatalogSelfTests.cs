using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class WorldExpansionCatalogSelfTests
{
    public static void Run()
    {
        TestStableFlags();
        TestCurrentEqlAliases();
        TestCurrentEqlCustomZones();
        TestKunarkAndVeliousAliases();
        TestInstalledLaterExpansionLeftovers();
        TestUnknownAndExactMatchBoundary();
        TestSafeNormalization();
        Console.WriteLine("World expansion catalog tests passed.");
    }

    private static void TestStableFlags()
    {
        AssertEqual(0, (int)WorldExpansion.None, "None flag");
        AssertEqual(1, (int)WorldExpansion.CurrentEql, "Current EQL flag");
        AssertEqual(2, (int)WorldExpansion.Kunark, "Kunark flag");
        AssertEqual(4, (int)WorldExpansion.Velious, "Velious flag");
        AssertEqual(8, (int)WorldExpansion.Luclin, "Luclin flag");
        AssertEqual(16, (int)WorldExpansion.PlanesOfPower, "Planes of Power flag");
        AssertEqual(32, (int)WorldExpansion.OtherInstalled, "Other installed flag");
        AssertEqual(63, (int)WorldExpansion.All, "All expansion flags");
        AssertSequenceEqual(
            [
                WorldExpansion.CurrentEql,
                WorldExpansion.Kunark,
                WorldExpansion.Velious,
                WorldExpansion.Luclin,
                WorldExpansion.PlanesOfPower,
                WorldExpansion.OtherInstalled
            ],
            WorldExpansionCatalog.OrderedGroups,
            "canonical display order");
    }

    private static void TestCurrentEqlAliases()
    {
        AssertGroup(WorldExpansion.CurrentEql, "qeynos", "South Qeynos");
        AssertGroup(WorldExpansion.CurrentEql, "qeynos2", "North Qeynos");
        AssertGroup(WorldExpansion.CurrentEql, "freporte", "East Freeport");
        AssertGroup(WorldExpansion.CurrentEql, "freportw", "West Freeport");
        AssertGroup(WorldExpansion.CurrentEql, "freportn", "North Freeport");
        AssertGroup(WorldExpansion.CurrentEql, "sro", "South Ro");
        AssertGroup(WorldExpansion.CurrentEql, "nro", "North Ro");
        AssertGroup(WorldExpansion.CurrentEql, "airplane", "Plane of Sky");
        AssertGroup(WorldExpansion.CurrentEql, "hateplane", "Plane of Hate");
        AssertGroup(WorldExpansion.CurrentEql, "tutorial", "current tutorial");
    }

    private static void TestCurrentEqlCustomZones()
    {
        AssertGroup(WorldExpansion.CurrentEql, "newsebexp", "New Sebilis Expedition");
        AssertGroup(WorldExpansion.CurrentEql, "kerraridge", "current Kerra Island");
    }

    private static void TestKunarkAndVeliousAliases()
    {
        AssertGroup(WorldExpansion.Kunark, "fieldofbone", "Field of Bone");
        AssertGroup(WorldExpansion.Kunark, "cabeast", "East Cabilis");
        AssertGroup(WorldExpansion.Kunark, "sebilis", "Old Sebilis");
        AssertGroup(WorldExpansion.Kunark, "veeshan", "Veeshan's Peak");
        AssertGroup(WorldExpansion.Velious, "iceclad", "Iceclad Ocean");
        AssertGroup(WorldExpansion.Velious, "thurgadinb", "Icewell Keep");
        AssertGroup(WorldExpansion.Velious, "mischiefplane", "Plane of Mischief");
        AssertGroup(WorldExpansion.Velious, "pomischief", "installed Plane of Mischief alias");
        AssertGroup(WorldExpansion.Velious, "templeveeshan", "Temple of Veeshan");
    }

    private static void TestInstalledLaterExpansionLeftovers()
    {
        AssertGroup(WorldExpansion.Luclin, "bazaar", "Bazaar Luclin leftover");
        AssertGroup(
            WorldExpansion.PlanesOfPower,
            "poknowledge",
            "Plane of Knowledge Planes of Power leftover");
        AssertGroup(WorldExpansion.PlanesOfPower, "ponightmareb", "Lair of Terris-Thule alias");
        AssertGroup(WorldExpansion.PlanesOfPower, "timea", "Plane of Time A alias");
        AssertGroup(WorldExpansion.PlanesOfPower, "timeb", "Plane of Time B alias");
    }

    private static void TestUnknownAndExactMatchBoundary()
    {
        AssertGroup(WorldExpansion.OtherInstalled, "anniversarytower", "unknown modern zone");
        AssertGroup(WorldExpansion.OtherInstalled, "custom_kerra", "unknown custom zone");
        AssertGroup(WorldExpansion.OtherInstalled, "qeynos_future", "current alias prefix leak");
        AssertGroup(WorldExpansion.OtherInstalled, "newsebexpansion", "custom alias prefix leak");
        AssertGroup(WorldExpansion.OtherInstalled, "bazaar_annex", "Luclin alias prefix leak");
        AssertGroup(WorldExpansion.OtherInstalled, null, "null zone name");
        AssertGroup(WorldExpansion.OtherInstalled, " ", "empty zone name");
        Assert(
            !WorldExpansionCatalog.IsSelected("bazaar", WorldExpansion.CurrentEql),
            "an unselected exact expansion must remain excluded");
        Assert(
            WorldExpansionCatalog.IsSelected(
                "newsebexp",
                WorldExpansion.CurrentEql | WorldExpansion.Kunark),
            "a selected exact expansion must be included");
    }

    private static void TestSafeNormalization()
    {
        AssertEqual(
            "qeynos",
            WorldExpansionCatalog.NormalizeZoneShortName(@" C:\EverQuest\QEYNOS.S3D "),
            "archive path normalization");
        AssertEqual(
            "qeynos",
            WorldExpansionCatalog.NormalizeZoneShortName("zones/Qeynos_2_Obj.EQG"),
            "secondary object archive normalization");
        AssertEqual(
            "qeynos_chr",
            WorldExpansionCatalog.NormalizeZoneShortName("qeynos_chr.s3d"),
            "character archives must not be mistaken for world zones");
        AssertEqual(
            WorldExpansion.CurrentEql,
            WorldExpansionCatalog.Classify("zones/QEYNOS_OBJ.S3D"),
            "normalized object archive classification");
        AssertEqual(
            WorldExpansion.OtherInstalled,
            WorldExpansionCatalog.Classify("qeynos.s3d.bak"),
            "unrecognized double extension must remain unknown");
    }

    private static void AssertGroup(WorldExpansion expected, string? zoneName, string description)
    {
        AssertEqual(expected, WorldExpansionCatalog.Classify(zoneName), description);
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"World expansion catalog assertion failed: {description}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"World expansion catalog assertion failed for {description}: " +
                $"expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string description)
        where T : notnull
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"World expansion catalog assertion failed for {description}: " +
                $"expected '{string.Join(", ", expected)}', got '{string.Join(", ", actual)}'.");
        }
    }
}
