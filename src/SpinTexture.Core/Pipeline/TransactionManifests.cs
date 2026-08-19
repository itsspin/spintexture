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
    // Schema 2 adds an explicit World-expansion subset to UpscaleOptions.
    // Readers retain schema-1 support for every staged pack already built,
    // while older SpinTexture releases reject schema-2 packs instead of
    // silently forgetting their World selection during repair.
    public const int MinimumSupportedSchemaVersion = 1;
    // Schema 2 added worldExpansions; schema 3 added paintedStyle.
    public const int CurrentSchemaVersion = 3;

    public static bool IsSupportedSchemaVersion(int schemaVersion) =>
        schemaVersion is >= MinimumSupportedSchemaVersion and <= CurrentSchemaVersion;
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

/// <summary>
/// An exact live-client snapshot that the caller has independently authorized as
/// the result of a completed launcher update. Missing files are never inferred:
/// changed files that disappeared must be represented explicitly with
/// <see cref="Exists"/> set to false. Do not authorize an unchanged missing path
/// whose managed original already did not exist; it is an ordinary managed
/// original state rather than a launcher change.
/// </summary>
public sealed record AdoptedOriginalArtifact(
    string RelativeInstallPath,
    bool Exists,
    long Length,
    string? Sha256);

public enum LauncherUpdateReconciliationState
{
    Preparing,
    Completed,
    RolledBack
}

public enum LauncherUpdateOriginalDisposition
{
    AlreadyManagedOriginal,
    RestoredManagedOriginal,
    AdoptedUpdatedFile,
    AdoptedRemovedFile
}

/// <summary>
/// The complete post-reconciliation original-client state for one artifact.
/// This is intentionally broader than the caller's changed-file authorization:
/// a completed receipt accounts for every artifact in the retired install.
/// </summary>
public sealed record LauncherUpdateOriginalArtifact(
    string RelativeInstallPath,
    bool Exists,
    long Length,
    string? Sha256,
    LauncherUpdateOriginalDisposition Disposition);

/// <summary>
/// Durable journal and completion receipt for retiring an install after a
/// verified launcher update. Preparing receipts make an interrupted restore of
/// still-enhanced files resumable; Completed receipts are written before the
/// source install manifest is retired.
/// </summary>
public sealed record LauncherUpdateReconciliationReceipt(
    int SchemaVersion,
    string ApplyId,
    DateTimeOffset AppliedUtc,
    DateTimeOffset StartedUtc,
    DateTimeOffset? ReconciledUtc,
    string InstallPath,
    LauncherUpdateReconciliationState State,
    string? SafetyDirectoryName,
    IReadOnlyList<LauncherUpdateOriginalArtifact> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record LauncherUpdateReconciliationResult(
    string ApplyId,
    string ReceiptPath,
    int ReconciledArtifacts,
    int RestoredEnhancedArtifacts,
    int AdoptedArtifacts,
    DateTimeOffset ReconciledUtc);
