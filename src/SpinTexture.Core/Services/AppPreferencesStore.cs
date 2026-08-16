using System.Text.Json;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public sealed record AppPreferences(
    string? LastInstallPath,
    PaintedStyleSettings? PaintedStyle = null,
    double BakedDepth = 0,
    double EmissiveGlow = 0,
    bool FullResolutionRepaint = false,
    double MipSharpen = 0)
{
    public static AppPreferences Empty { get; } = new((string?)null);
}

public sealed class AppPreferencesStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string settingsPath;
    private readonly object writeQueueGate = new();
    private Task writeQueue = Task.CompletedTask;

    public AppPreferencesStore(string? settingsPath = null)
    {
        this.settingsPath = settingsPath is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpinTexture",
                "app-settings.json")
            : Path.GetFullPath(settingsPath);
    }

    public AppPreferences Read()
    {
        if (!File.Exists(settingsPath))
        {
            return AppPreferences.Empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize<PreferencesDocument>(
                File.ReadAllText(settingsPath),
                JsonOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                return AppPreferences.Empty;
            }

            return new AppPreferences(
                NormalizeOptionalPath(document.LastInstallPath),
                document.PaintedStyle?.Clamped(),
                Math.Clamp(document.BakedDepth, 0d, 1d),
                Math.Clamp(document.EmissiveGlow, 0d, 1d),
                document.FullResolutionRepaint,
                Math.Clamp(document.MipSharpen, 0d, 1d));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return AppPreferences.Empty;
        }
    }

    public Task WriteLastInstallPathAsync(
        string installPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        if (!Directory.Exists(normalized) || !File.Exists(Path.Combine(normalized, "eqgame.exe")))
        {
            throw new DirectoryNotFoundException(
                "The remembered EverQuest directory must exist and contain eqgame.exe.");
        }

        return EnqueueWriteAsync(
            current => new PreferencesDocument(
                CurrentSchemaVersion,
                normalized,
                current.PaintedStyle,
                current.BakedDepth,
                current.EmissiveGlow,
                current.FullResolutionRepaint,
                current.MipSharpen),
            cancellationToken);
    }

    public Task WritePaintedStyleAsync(
        PaintedStyleSettings? paintedStyle,
        CancellationToken cancellationToken = default)
    {
        var clamped = paintedStyle?.Clamped();
        return EnqueueWriteAsync(
            current => new PreferencesDocument(
                CurrentSchemaVersion,
                current.LastInstallPath,
                clamped,
                current.BakedDepth,
                current.EmissiveGlow,
                current.FullResolutionRepaint,
                current.MipSharpen),
            cancellationToken);
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public Task WriteEnhancementsAsync(
        double bakedDepth,
        double emissiveGlow,
        bool fullResolutionRepaint,
        double mipSharpen,
        CancellationToken cancellationToken = default)
    {
        var clampedBakedDepth = Math.Clamp(bakedDepth, 0d, 1d);
        var clampedEmissiveGlow = Math.Clamp(emissiveGlow, 0d, 1d);
        var clampedMipSharpen = Math.Clamp(mipSharpen, 0d, 1d);
        return EnqueueWriteAsync(
            current => new PreferencesDocument(
                CurrentSchemaVersion,
                current.LastInstallPath,
                current.PaintedStyle,
                clampedBakedDepth,
                clampedEmissiveGlow,
                fullResolutionRepaint,
                clampedMipSharpen),
            cancellationToken);
    }

    private Task EnqueueWriteAsync(
        Func<AppPreferences, PreferencesDocument> update,
        CancellationToken cancellationToken)
    {
        lock (writeQueueGate)
        {
            writeQueue = ContinueQueuedWriteAsync(writeQueue, update, cancellationToken);
            return writeQueue;
        }
    }

    private async Task ContinueQueuedWriteAsync(
        Task previousWrite,
        Func<AppPreferences, PreferencesDocument> update,
        CancellationToken cancellationToken)
    {
        try
        {
            await previousWrite.ConfigureAwait(false);
        }
        catch
        {
            // A failed best-effort preference update must not permanently
            // poison the queue. The original caller still observes its error.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var document = update(Read());
        var parent = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("The preferences path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = AtomicFile.CreateTemporarySiblingPath(settingsPath);
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.CommitTemporaryFile(temporaryPath, settingsPath);
        }
        catch
        {
            AtomicFile.TryDelete(temporaryPath);
            throw;
        }
    }

    private sealed record PreferencesDocument(
        int SchemaVersion,
        string? LastInstallPath,
        PaintedStyleSettings? PaintedStyle = null,
        double BakedDepth = 0,
        double EmissiveGlow = 0,
        bool FullResolutionRepaint = false,
        double MipSharpen = 0);
}
