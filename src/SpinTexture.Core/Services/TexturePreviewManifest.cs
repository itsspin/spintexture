namespace SpinTexture.Core.Services;

public sealed record TexturePreviewEntry(
    string ArchivePath,
    string LogicalName,
    string OriginalImagePath,
    string EnhancedImagePath,
    int OriginalWidth,
    int OriginalHeight,
    int EnhancedWidth,
    int EnhancedHeight);

public sealed record TexturePreviewManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<TexturePreviewEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
    public IReadOnlyList<TextureReviewEntry> ReviewEntries { get; init; } = [];
}

public sealed record TextureReviewEntry(
    string ArchivePath,
    string LogicalName,
    int OriginalWidth,
    int OriginalHeight,
    int EnhancedWidth,
    int EnhancedHeight);

internal sealed class TexturePreviewCollector
{
    private readonly object gate = new();
    private readonly int maximumEntriesPerArchive;
    private readonly SortedSet<int> availableIndices = [];
    private readonly Dictionary<int, Reservation> reservations = [];
    private readonly Dictionary<int, TexturePreviewEntry> entries = [];
    private readonly HashSet<string> reservedFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> archiveReservationCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextureReviewEntry> reviewEntries = new(StringComparer.OrdinalIgnoreCase);

    public TexturePreviewCollector(int maximumEntries)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        maximumEntriesPerArchive = Math.Max(1, (maximumEntries + 2) / 3);
        for (var index = 0; index < maximumEntries; index++)
        {
            availableIndices.Add(index);
        }
    }

    public bool TryReserve(string archivePath, string logicalName, out int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        var archiveKey = NormalizeArchiveKey(archivePath);
        var familyKey = $"{archiveKey}|{CreateFamilyKey(logicalName)}";

        lock (gate)
        {
            archiveReservationCounts.TryGetValue(archiveKey, out var archiveReservations);
            if (availableIndices.Count == 0
                || archiveReservations >= maximumEntriesPerArchive
                || !reservedFamilies.Add(familyKey))
            {
                index = -1;
                return false;
            }

            index = availableIndices.Min;
            availableIndices.Remove(index);
            reservations.Add(index, new Reservation(archiveKey, familyKey));
            archiveReservationCounts[archiveKey] = archiveReservations + 1;
            return true;
        }
    }

    public void Complete(int index, TexturePreviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            if (!reservations.ContainsKey(index))
            {
                throw new InvalidOperationException("The preview reservation is no longer active.");
            }

            entries[index] = entry;
        }
    }

    public void Abandon(int index)
    {
        lock (gate)
        {
            if (!reservations.Remove(index, out var reservation))
            {
                return;
            }

            entries.Remove(index);
            availableIndices.Add(index);
            reservedFamilies.Remove(reservation.FamilyKey);
            var remainingForArchive = archiveReservationCounts[reservation.ArchiveKey] - 1;
            if (remainingForArchive == 0)
            {
                archiveReservationCounts.Remove(reservation.ArchiveKey);
            }
            else
            {
                archiveReservationCounts[reservation.ArchiveKey] = remainingForArchive;
            }
        }
    }

    public IReadOnlyList<TexturePreviewEntry> Snapshot()
    {
        lock (gate)
        {
            return entries.Values
                .OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void RegisterReview(TextureReviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = $"{NormalizeArchiveKey(entry.ArchivePath)}|{entry.LogicalName.Trim()}";
        lock (gate)
        {
            reviewEntries[key] = entry;
        }
    }

    public IReadOnlyList<TextureReviewEntry> ReviewSnapshot()
    {
        lock (gate)
        {
            return reviewEntries.Values
                .OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal static string CreateFamilyKey(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        var stem = Path.GetFileNameWithoutExtension(logicalName).Trim();
        var normalized = new char[stem.Length];
        var length = 0;
        foreach (var character in stem)
        {
            if (char.IsDigit(character))
            {
                continue;
            }

            normalized[length++] = char.ToLowerInvariant(character);
        }

        var family = new string(normalized, 0, length).Trim('_', '-', '.', ' ');
        return family.Length > 0 ? family : stem.ToLowerInvariant();
    }

    private static string NormalizeArchiveKey(string archivePath) =>
        archivePath.Trim().Replace('\\', '/').Trim('/');

    private sealed record Reservation(string ArchiveKey, string FamilyKey);
}
