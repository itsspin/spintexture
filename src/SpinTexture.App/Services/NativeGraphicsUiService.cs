namespace SpinTexture.App.Services;

public enum NativeGraphicsUiPreset
{
    Balanced,
    Cinematic
}

public sealed record NativeGraphicsUiStatus(
    string StateName,
    string StateBadge,
    string Summary,
    string SettingsPath,
    string? ActiveTransactionPath,
    IReadOnlyList<NativeGraphicsUiSetting> ManagedValues,
    bool NeedsAttention,
    bool CanApply,
    bool CanRestore);

public sealed record NativeGraphicsUiSetting(
    string Section,
    string Key,
    string Value)
{
    public string QualifiedKey => $"[{Section}] {Key}";
    public string DisplayName => NativeGraphicsUiText.GetDisplayName(Key);
    public string DisplayValue => NativeGraphicsUiText.GetDisplayValue(Key, Value);
}

public sealed record NativeGraphicsUiChange(
    string Section,
    string Key,
    string CurrentValue,
    string PlannedValue,
    bool WillAdd,
    bool WillRemove)
{
    public string QualifiedKey => $"[{Section}] {Key}";
    public string ChangeBadge => WillAdd ? "ADD" : WillRemove ? "RESTORE" : "CHANGE";
    public string DisplayName => NativeGraphicsUiText.GetDisplayName(Key);
    public string CurrentDisplayValue => NativeGraphicsUiText.GetDisplayValue(Key, CurrentValue);
    public string PlannedDisplayValue => NativeGraphicsUiText.GetDisplayValue(Key, PlannedValue);
}

public sealed record NativeGraphicsUiPlan(
    NativeGraphicsUiPreset Preset,
    string Name,
    string Eyebrow,
    string Description,
    string PerformanceNote,
    IReadOnlyList<NativeGraphicsUiChange> Changes,
    bool HasChanges,
    bool CanApply)
{
    public string ChoiceBadge => Preset == NativeGraphicsUiPreset.Balanced
        ? "RECOMMENDED"
        : "MAXIMUM";

    public string FeatureSummary => Preset == NativeGraphicsUiPreset.Balanced
        ? "Shadows on  •  Your lighting, bloom, and shadow range stay unchanged"
        : "Shadows on  •  Advanced lighting on  •  Bloom on  •  Maximum shadow range";
}

internal static class NativeGraphicsUiText
{
    public static string GetDisplayName(string key) => key switch
    {
        "Shadows" => "Native shadows",
        "MultiPassLighting" => "Advanced lighting",
        "PostEffects" => "Post effects",
        "Bloom" => "Bloom lighting",
        "ShadowClipPlane" => "Shadow distance",
        _ => key
    };

    public static string GetDisplayValue(string key, string value)
    {
        if (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return "On";
        }

        if (value.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return "Off";
        }

        if (value.Equals("(not set)", StringComparison.OrdinalIgnoreCase))
        {
            return "Not set";
        }

        return key == "ShadowClipPlane" && value == "100"
            ? "Maximum (100)"
            : value;
    }
}

public interface INativeGraphicsService
{
    Task<NativeGraphicsUiStatus> InspectAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<NativeGraphicsUiPlan> PlanAsync(
        string installPath,
        NativeGraphicsUiPreset preset,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        string installPath,
        NativeGraphicsUiPreset preset,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        string installPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
