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
}

public sealed record NativeGraphicsUiPlan(
    NativeGraphicsUiPreset Preset,
    string Name,
    string Eyebrow,
    string Description,
    string PerformanceNote,
    IReadOnlyList<NativeGraphicsUiChange> Changes,
    bool HasChanges,
    bool CanApply);

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
