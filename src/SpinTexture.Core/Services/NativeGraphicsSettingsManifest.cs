namespace SpinTexture.Core.Services;

internal enum NativeGraphicsTransactionState
{
    Preparing,
    Applied,
    RecoveryRequired,
    Restored
}

internal sealed record NativeGraphicsStoredSetting(
    string Section,
    string Key,
    bool Exists,
    string? Value);

internal sealed record NativeGraphicsSettingsManifest(
    int SchemaVersion,
    string TransactionId,
    DateTimeOffset CreatedUtc,
    string InstallPath,
    string SettingsRelativePath,
    NativeGraphicsTransactionState State,
    NativeGraphicsPreset Preset,
    long OriginalLength,
    string OriginalSha256,
    string OriginalBackupRelativePath,
    long AppliedLength,
    string AppliedSha256,
    IReadOnlyList<NativeGraphicsStoredSetting> BaselineSettings,
    IReadOnlyList<NativeGraphicsStoredSetting> AppliedSettings,
    IReadOnlyList<NativeGraphicsStoredSetting>? PreApplySettings = null)
{
    public const int LegacySchemaVersion = 1;
    public const int LightingOnlySchemaVersion = 2;
    public const int CurrentSchemaVersion = 3;
}
