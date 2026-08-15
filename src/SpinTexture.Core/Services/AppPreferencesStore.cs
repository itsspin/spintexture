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

    public async Task WriteLastInstallPathAsync(
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

        var current = Read();
        var document = new PreferencesDocument(
            CurrentSchemaVersion,
            normalized,
            current.PaintedStyle,
            current.BakedDepth,
            current.EmissiveGlow,
            current.FullResolutionRepaint,
            current.MipSharpen);
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

    public async Task WritePaintedStyleAsync(
        PaintedStyleSettings? paintedStyle,
        CancellationToken cancellationToken = default)
    {
        var current = Read();
        var document = new PreferencesDocument(
            CurrentSchemaVersion,
            current.LastInstallPath,
            paintedStyle?.Clamped(),
            current.BakedDepth,
            current.EmissiveGlow,
            current.FullResolutionRepaint,
            current.MipSharpen);
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

    public async Task WriteEnhancementsAsync(
        double bakedDepth,
        double emissiveGlow,
        bool fullResolutionRepaint,
        double mipSharpen,
        CancellationToken cancellationToken = default)
    {
        var current = Read();
        var document = new PreferencesDocument(
            CurrentSchemaVersion,
            current.LastInstallPath,
            current.PaintedStyle,
            Math.Clamp(bakedDepth, 0d, 1d),
            Math.Clamp(emissiveGlow, 0d, 1d),
            fullResolutionRepaint,
            Math.Clamp(mipSharpen, 0d, 1d));
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
