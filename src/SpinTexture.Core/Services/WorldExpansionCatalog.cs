using System.Collections.Frozen;
using SpinTexture.Core.Models;

namespace SpinTexture.Core.Services;

/// <summary>
/// Maps exact EverQuest zone short names to expansion-era build groups.
/// Current EQL aliases follow the non-out-of-era entries published at
/// https://eqlwiki.com/Zones. Unknown, custom, and modern replacement zones are
/// intentionally kept in <see cref="WorldExpansion.OtherInstalled" /> so a
/// similar prefix can never silently select an inaccessible zone.
/// </summary>
public static class WorldExpansionCatalog
{
    private static readonly string[] KnownExtensions =
    [
        ".s3d",
        ".eqg",
        ".zon",
        ".xmi",
        ".emt"
    ];

    private static readonly FrozenDictionary<string, WorldExpansion> ExpansionByZone =
        CreateCatalog();

    public static IReadOnlyList<WorldExpansion> OrderedGroups { get; } =
        Array.AsReadOnly(
        [
            WorldExpansion.CurrentEql,
            WorldExpansion.Kunark,
            WorldExpansion.Velious,
            WorldExpansion.Luclin,
            WorldExpansion.PlanesOfPower,
            WorldExpansion.OtherInstalled
        ]);

    public static WorldExpansion Classify(string? zoneName)
    {
        string shortName = NormalizeZoneShortName(zoneName);
        return ExpansionByZone.GetValueOrDefault(shortName, WorldExpansion.OtherInstalled);
    }

    public static bool IsSelected(string? zoneName, WorldExpansion selectedExpansions)
    {
        WorldExpansion expansion = Classify(zoneName);
        return (selectedExpansions & expansion) == expansion;
    }

    /// <summary>
    /// Normalizes a short name or world-archive filename without touching the
    /// filesystem. Only recognized zone archive suffixes are removed.
    /// </summary>
    public static string NormalizeZoneShortName(string? zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
        {
            return string.Empty;
        }

        string candidate = zoneName.Trim();
        int separatorIndex = Math.Max(candidate.LastIndexOf('/'), candidate.LastIndexOf('\\'));
        if (separatorIndex >= 0)
        {
            candidate = candidate[(separatorIndex + 1)..];
        }

        foreach (string extension in KnownExtensions)
        {
            if (candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[..^extension.Length];
                break;
            }
        }

        if (candidate.EndsWith("_2_obj", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^"_2_obj".Length];
        }
        else if (candidate.EndsWith("_obj", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^"_obj".Length];
        }

        return candidate.Trim().ToLowerInvariant();
    }

    public static string GetDisplayName(WorldExpansion expansion)
    {
        return expansion switch
        {
            WorldExpansion.None => "None",
            WorldExpansion.CurrentEql => "Current EverQuest Legends",
            WorldExpansion.Kunark => "Kunark",
            WorldExpansion.Velious => "Velious",
            WorldExpansion.Luclin => "Luclin",
            WorldExpansion.PlanesOfPower => "Planes of Power",
            WorldExpansion.OtherInstalled => "Other installed zones",
            WorldExpansion.All => "All installed zones",
            _ => throw new ArgumentOutOfRangeException(
                nameof(expansion),
                expansion,
                "A display name is available only for a canonical expansion group.")
        };
    }

    private static FrozenDictionary<string, WorldExpansion> CreateCatalog()
    {
        var catalog = new Dictionary<string, WorldExpansion>(StringComparer.OrdinalIgnoreCase);

        Add(
            WorldExpansion.CurrentEql,
            // Cities and custom/current EQL destinations.
            "qeynos", "qeynos2", "oggok", "grobb", "freporte", "freportw", "freportn",
            "newsebexp", "kaladima", "kaladimb", "gfaydark", "felwithea", "felwitheb",
            "akanon", "neriaka", "neriakb", "neriakc", "rivervale", "halas", "qrg",
            "erudnext", "erudnint", "kerraridge", "paineel",
            // Odus, Antonica, Faydwer, and the currently accessible planes.
            "erudsxing", "hole", "tox", "stonebrunt", "warrens", "qeytoqrg", "qey2hh1",
            "northkarana", "eastkarana", "southkarana", "paw", "qcat", "rathemtn", "arena",
            "lakerathe", "feerrott", "fearplane", "cazicthule", "jaggedpine", "kithicor",
            "nektulos", "misty", "unrest", "everfrost", "permafrost", "runnyeye",
            "beholder", "blackburrow", "innothule", "gukbottom", "guktop", "sro", "oasis",
            "nro", "befallen", "commons", "ecommons", "highkeep", "highpass", "najena",
            "lavastorm", "soldunga", "soldungb", "soltemple", "oot", "butcher", "cauldron",
            "kedge", "lfaydark", "mistmoore", "steamfont", "crushbone", "airplane",
            "hateplane", "tutorial");

        Add(
            WorldExpansion.Kunark,
            "burningwood", "dreadlands", "emeraldjungle", "fieldofbone", "firiona",
            "frontiermtns", "lakeofillomen", "overthere", "skyfire", "swampofnohope",
            "timorous", "trakanon", "warslikswood", "cabeast", "cabwest", "chardok",
            "citymist", "dalnir", "charasis", "kaesora", "karnor", "kurn", "nurga",
            "sebilis", "droga", "veeshan");

        Add(
            WorldExpansion.Velious,
            "cobaltscar", "eastwastes", "greatdivide", "iceclad", "wakening", "westwastes",
            "thurgadinb", "kael", "skyshrine", "thurgadina", "growthplane", "mischiefplane",
            "pomischief", "crystal", "necropolis", "sirens", "sleeper", "templeveeshan",
            "frozenshadow", "velketor");

        Add(
            WorldExpansion.Luclin,
            "acrylia", "akheva", "bazaar", "dawnshroud", "echo", "fungusgrove", "griegsend",
            "grimling", "hollowshade", "katta", "letalis", "maiden", "mseru", "netherbian",
            "nexus", "paludal", "scarlet", "shadeweaver", "shadowhaven", "sharvahl", "sseru",
            "ssratemple", "tenebrous", "thedeep", "thegrey", "twilight", "umbral", "vexthal");

        Add(
            WorldExpansion.PlanesOfPower,
            "poknowledge", "potranquility", "pojustice", "podisease", "poinnovation",
            "ponightmare", "nightmareb", "ponightmareb", "postorms", "povalor", "bothunder",
            "hohonora", "hohonorb", "codecay", "potorment", "potactics", "solrotower", "poair",
            "powater", "pofire", "poeartha", "poearthb", "potimea", "potimeb", "timea",
            "timeb");

        return catalog.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        void Add(WorldExpansion expansion, params string[] zoneNames)
        {
            foreach (string zoneName in zoneNames)
            {
                if (!catalog.TryAdd(zoneName, expansion))
                {
                    throw new InvalidOperationException(
                        $"Zone short name '{zoneName}' appears more than once in the expansion catalog.");
                }
            }
        }
    }
}
