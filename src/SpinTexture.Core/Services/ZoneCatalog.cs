using SpinTexture.Core.Models;

namespace SpinTexture.Core.Services;

public sealed record ZoneAssetSet(
    string Name,
    IReadOnlyList<string> WorldArchives,
    WorldExpansion Expansion);

public static class ZoneCatalog
{
    public static IReadOnlyList<ZoneAssetSet> Discover(string installPath)
    {
        string root = Path.GetFullPath(installPath);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);
        var zoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in files.Values)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".xmi", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".zon", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".emt", StringComparison.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                if (HasMainZoneArchive(files, name))
                {
                    zoneNames.Add(name);
                }
            }
        }

        // Some classic zones have no surviving companion metadata, so accept an archive
        // when a zone-specific character list proves that the short name is loadable.
        foreach (string characterList in Directory.EnumerateFiles(root, "*_chr.txt", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileNameWithoutExtension(characterList);
            string name = fileName[..^"_chr".Length];
            if (HasMainZoneArchive(files, name))
            {
                zoneNames.Add(name);
            }
        }

        // Several classic/Kunark clients ship no XMI/ZON/EMT marker, but a matching
        // main archive plus object archive is a conservative, useful zone signature.
        foreach (string fileName in files.Keys)
        {
            string extension = Path.GetExtension(fileName);
            if (!EverQuestInstall.IsPfsArchiveExtension(extension))
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(fileName);
            if (name.Equals("load", StringComparison.OrdinalIgnoreCase)
                || name.Equals("load2", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_obj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_chr", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("global", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("gequip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasMainZoneArchive(files, name) && HasObjectArchive(files, name))
            {
                zoneNames.Add(name);
            }
        }

        return zoneNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ZoneAssetSet(
                name,
                FindWorldArchives(files, name),
                WorldExpansionCatalog.Classify(name)))
            .Where(zone => zone.WorldArchives.Count != 0)
            .ToArray();
    }

    private static bool HasMainZoneArchive(IReadOnlyDictionary<string, string> files, string zoneName)
    {
        return files.ContainsKey(zoneName + ".s3d") || files.ContainsKey(zoneName + ".eqg");
    }

    private static bool HasObjectArchive(IReadOnlyDictionary<string, string> files, string zoneName)
    {
        return files.ContainsKey(zoneName + "_obj.s3d") || files.ContainsKey(zoneName + "_obj.eqg");
    }

    private static IReadOnlyList<string> FindWorldArchives(IReadOnlyDictionary<string, string> files, string zoneName)
    {
        var archives = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(files, archives, zoneName + ".s3d");
        AddIfPresent(files, archives, zoneName + ".eqg");
        AddIfPresent(files, archives, zoneName + "_obj.s3d");
        AddIfPresent(files, archives, zoneName + "_obj.eqg");
        AddIfPresent(files, archives, zoneName + "_2_obj.s3d");
        AddIfPresent(files, archives, zoneName + "_2_obj.eqg");
        return archives.ToArray();
    }

    private static void AddIfPresent(
        IReadOnlyDictionary<string, string> files,
        ISet<string> archives,
        string fileName)
    {
        if (files.TryGetValue(fileName, out string? path))
        {
            archives.Add(path);
        }
    }
}
