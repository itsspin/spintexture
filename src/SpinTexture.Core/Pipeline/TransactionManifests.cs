using SpinTexture.Core.Models;

namespace SpinTexture.Core.Pipeline;

public sealed record BuildManifest(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedUtc,
    string InstallPath,
    UpscaleOptions Options,
    IReadOnlyList<BuildManifestEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record BuildManifestEntry(
    string RelativeInstallPath,
    long SourceLength,
    string SourceSha256,
    long StagedLength,
    string StagedSha256);

public enum InstallTransactionState
{
    Preparing,
    Applied,
    RolledBack,
    RecoveryRequired,
    Restored
}

public sealed record InstallManifest(
    int SchemaVersion,
    string ApplyId,
    DateTimeOffset AppliedUtc,
    string InstallPath,
    string BuildId,
    string BuildManifestPath,
    InstallTransactionState State,
    IReadOnlyList<InstalledArtifact> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record InstalledArtifact(
    string RelativeInstallPath,
    bool OriginalExisted,
    long OriginalLength,
    string? OriginalSha256,
    string? BackupRelativePath,
    long InstalledLength,
    string InstalledSha256,
    long? InstalledLastWriteTimeUtcTicks = null);

public sealed record ApplyResult(
    string ApplyId,
    string BackupDirectory,
    string InstallManifestPath,
    InstallManifest Manifest);

public sealed record RestoreResult(
    string ApplyId,
    int RestoredArtifacts,
    DateTimeOffset RestoredUtc);
