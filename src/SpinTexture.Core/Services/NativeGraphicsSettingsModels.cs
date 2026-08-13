using SpinTexture.Core.Models;

namespace SpinTexture.Core.Services;

public enum NativeGraphicsPreset
{
    Balanced,
    Cinematic,
    CinematicNoBloom
}

public enum NativeGraphicsSettingsState
{
    Unavailable,
    Original,
    Applied,
    Modified,
    RecoveryRequired
}

public sealed record NativeGraphicsSettingValue(
    string Section,
    string Key,
    bool Exists,
    string? Value);

public sealed record NativeGraphicsSettingChange(
    string Section,
    string Key,
    bool CurrentExists,
    string? CurrentValue,
    string PlannedValue,
    bool WillAdd)
{
    public bool WillRemove => PlannedValue == NativeGraphicsSettingsPlan.RemovedValue;
}

public sealed record NativeGraphicsSettingsStatus(
    NativeGraphicsSettingsState State,
    string SettingsPath,
    NativeGraphicsPreset? AppliedPreset,
    string? ActiveTransactionManifestPath,
    IReadOnlyList<NativeGraphicsSettingValue> ManagedValues,
    string Summary,
    bool HasVerifiedRestorePoint = false,
    bool ThirdPersonPitchLockEnabled = false)
{
    public bool CanApply => State is NativeGraphicsSettingsState.Original
        or NativeGraphicsSettingsState.Applied;

    // Renderer capability controls whether another preset may be applied. It does
    // not invalidate an already-verified backup, which must remain restorable.
    public bool CanRestore => HasVerifiedRestorePoint;
}

public sealed record NativeGraphicsSettingsPlan(
    NativeGraphicsPreset Preset,
    NativeGraphicsSettingsStatus Status,
    IReadOnlyList<NativeGraphicsSettingChange> Changes,
    string Summary,
    bool ThirdPersonPitchLockEnabled = false)
{
    public const string RemovedValue = "(remove; originally absent)";
    public bool HasChanges => Changes.Count != 0;
}

public sealed record NativeGraphicsApplyResult(
    string TransactionId,
    string TransactionManifestPath,
    NativeGraphicsSettingsPlan Plan,
    NativeGraphicsSettingsStatus Status);

public sealed record NativeGraphicsRestoreResult(
    string TransactionId,
    int RestoredSettings,
    DateTimeOffset RestoredUtc,
    NativeGraphicsSettingsStatus Status);
