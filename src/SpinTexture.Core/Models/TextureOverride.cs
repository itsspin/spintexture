namespace SpinTexture.Core.Models;

public enum TextureOverrideAction
{
    PreserveOriginal,
    Reprocess
}

/// <summary>
/// A narrowly scoped, review-driven choice. ArchivePath is relative to the
/// client root; LogicalName is the exact member name inside a PFS archive (or
/// empty for a loose texture).
/// </summary>
public sealed record TextureOverride(
    string ArchivePath,
    string LogicalName,
    TextureOverrideAction Action,
    TexturePreset? Preset = null);

internal static class TextureOverridePolicy
{
    public static IReadOnlyDictionary<string, TextureOverride> CreateLookup(
        IReadOnlyList<TextureOverride>? overrides)
    {
        ValidateAll(overrides);
        return overrides?.ToDictionary(
                item => CreateKey(item.ArchivePath, item.LogicalName),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, TextureOverride>(StringComparer.OrdinalIgnoreCase);
    }

    public static TextureOverride? Find(
        IReadOnlyDictionary<string, TextureOverride> overrides,
        string archivePath,
        string logicalName) =>
        overrides.TryGetValue(CreateKey(archivePath, logicalName), out var match)
            ? match
            : null;

    public static TextureOverride? Find(
        IReadOnlyList<TextureOverride>? overrides,
        string archivePath,
        string logicalName)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        var normalizedArchive = Normalize(archivePath);
        var normalizedLogical = Normalize(logicalName);
        TextureOverride? match = null;
        foreach (var candidate in overrides)
        {
            Validate(candidate);
            if (!Normalize(candidate.ArchivePath).Equals(
                    normalizedArchive,
                    StringComparison.OrdinalIgnoreCase)
                || !Normalize(candidate.LogicalName).Equals(
                    normalizedLogical,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException(
                    $"Duplicate texture choice for {archivePath} / {logicalName}.");
            }

            match = candidate;
        }

        return match;
    }

    public static void ValidateAll(IReadOnlyList<TextureOverride>? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in overrides)
        {
            Validate(item);
            if (!keys.Add($"{Normalize(item.ArchivePath)}|{Normalize(item.LogicalName)}"))
            {
                throw new InvalidDataException(
                    $"Duplicate texture choice for {item.ArchivePath} / {item.LogicalName}.");
            }
        }
    }

    private static void Validate(TextureOverride item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.ArchivePath)
            || string.IsNullOrWhiteSpace(item.LogicalName)
            || Path.IsPathRooted(item.ArchivePath)
            || Path.IsPathRooted(item.LogicalName)
            || item.ArchivePath.Split('/', '\\').Any(segment => segment == "..")
            || item.LogicalName.Split('/', '\\').Any(segment => segment == "..")
            || (item.Action == TextureOverrideAction.PreserveOriginal
                && item.Preset is not null))
        {
            throw new InvalidDataException("A texture choice contains an unsafe path or incompatible preset.");
        }
    }

    private static string Normalize(string value) =>
        value.Trim().Replace('\\', '/').Trim('/');

    private static string CreateKey(string archivePath, string logicalName) =>
        $"{Normalize(archivePath)}|{Normalize(logicalName)}";
}
