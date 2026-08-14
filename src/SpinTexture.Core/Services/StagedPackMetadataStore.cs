using System.Text.Json;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

/// <summary>
/// Optional user-editable pack metadata carried in a pack-meta.json sidecar
/// beside manifest.json. Nothing in pack identity, validation, composition,
/// installation, or repair reads this file: it exists so packs can have a
/// stable human name and notes without depending on folder-name conventions,
/// and renaming can never invalidate a pack.
/// </summary>
public sealed record StagedPackUserMetadata(
    int SchemaVersion,
    string? DisplayName,
    string? Notes,
    DateTimeOffset? ModifiedUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDisplayNameLength = 80;
    public const int MaximumNotesLength = 1000;
}

public static class StagedPackMetadataStore
{
    public const string FileName = "pack-meta.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Reads the sidecar if present and well-formed; a missing or corrupt
    /// sidecar is never an error because packs must stay usable without it.
    /// </summary>
    public static StagedPackUserMetadata? TryRead(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        var path = Path.Combine(buildDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<StagedPackUserMetadata>(
                File.ReadAllText(path),
                JsonOptions);
            if (document is null
                || document.SchemaVersion != StagedPackUserMetadata.CurrentSchemaVersion)
            {
                return null;
            }

            return document with
            {
                DisplayName = NormalizeDisplayName(document.DisplayName),
                Notes = NormalizeNotes(document.Notes)
            };
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(
        string buildDirectory,
        string? displayName,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        if (!Directory.Exists(buildDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The staged pack directory does not exist: {buildDirectory}");
        }

        var document = new StagedPackUserMetadata(
            StagedPackUserMetadata.CurrentSchemaVersion,
            NormalizeDisplayName(displayName),
            NormalizeNotes(notes),
            DateTimeOffset.UtcNow);
        var path = Path.Combine(buildDirectory, FileName);
        if (document.DisplayName is null && document.Notes is null)
        {
            AtomicFile.TryDelete(path);
            return;
        }

        var temporaryPath = AtomicFile.CreateTemporarySiblingPath(path);
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.CommitTemporaryFile(temporaryPath, path);
        }
        catch
        {
            AtomicFile.TryDelete(temporaryPath);
            throw;
        }
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= StagedPackUserMetadata.MaximumDisplayNameLength
            ? trimmed
            : trimmed[..StagedPackUserMetadata.MaximumDisplayNameLength];
    }

    private static string? NormalizeNotes(string? notes)
    {
        var trimmed = notes?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= StagedPackUserMetadata.MaximumNotesLength
            ? trimmed
            : trimmed[..StagedPackUserMetadata.MaximumNotesLength];
    }
}
