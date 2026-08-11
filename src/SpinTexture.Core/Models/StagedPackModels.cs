using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Models;

public enum StagedPackVerificationMode
{
    Metadata,
    Exact
}

public enum StagedPackValidationState
{
    MetadataValid,
    Ready,
    InvalidManifest,
    DifferentInstall,
    PayloadMissing,
    PayloadInvalid
}

public enum StagedPackArtifactValidationState
{
    MetadataValid,
    HashVerified,
    Missing,
    LengthMismatch,
    HashMismatch,
    Invalid
}

public sealed record StagedPackFileFingerprint(long Length, string Sha256);

public sealed record StagedPackArtifactInfo(
    BuildManifestEntry Entry,
    string CanonicalRelativeInstallPath,
    string PayloadPath,
    StagedPackArtifactValidationState State,
    long? ObservedLength,
    string? ObservedSha256,
    string Summary,
    long? VerifiedLastWriteTimeUtcTicks = null)
{
    public bool IsValid => State is
        StagedPackArtifactValidationState.MetadataValid or
        StagedPackArtifactValidationState.HashVerified;
}

public sealed record StagedPackInfo(
    string ManifestPath,
    string BuildDirectory,
    string CandidateBuildId,
    StagedPackFileFingerprint? ManifestFingerprint,
    BuildManifest? Manifest,
    StagedPackValidationState State,
    string Summary,
    IReadOnlyList<StagedPackArtifactInfo> Artifacts)
{
    public bool IsReady => State == StagedPackValidationState.Ready;
    public bool IsMetadataValid => State is
        StagedPackValidationState.MetadataValid or
        StagedPackValidationState.Ready;
    public int ArtifactCount => Artifacts.Count;
    public long StagedBytes => Artifacts
        .Where(artifact => artifact.IsValid)
        .Sum(artifact => artifact.Entry.StagedLength);
}

public enum StagedPackCompositionConflictKind
{
    SourceMismatch,
    OutputMismatch
}

public sealed record StagedPackCompositionOrigin(
    string BuildId,
    string ManifestPath,
    string PayloadPath);

public sealed record StagedPackCompositionEntry(
    BuildManifestEntry Entry,
    IReadOnlyList<StagedPackCompositionOrigin> Origins)
{
    public string SourcePayloadPath => Origins[0].PayloadPath;
}

public sealed record StagedPackCompositionConflict(
    string RelativeInstallPath,
    StagedPackCompositionConflictKind Kind,
    string ExistingBuildId,
    string ConflictingBuildId,
    string Summary);

public sealed record StagedPackCompositionPlan(
    IReadOnlyList<StagedPackInfo> Components,
    IReadOnlyList<StagedPackCompositionEntry> Entries,
    IReadOnlyList<StagedPackCompositionConflict> Conflicts)
{
    public bool CanCompose => Components.Count > 0 && Entries.Count > 0 && Conflicts.Count == 0;
    public long StagedBytes => Entries.Sum(entry => entry.Entry.StagedLength);
}

public enum StagedPackMaterializationKind
{
    HardLink,
    VerifiedCopy
}

public sealed record StagedPackCompositionComponent(
    string BuildId,
    string ManifestRelativePath,
    long ManifestLength,
    string ManifestSha256,
    DateTimeOffset CreatedUtc,
    UpscaleOptions Options,
    int ArtifactCount);

public sealed record StagedPackCompositionEntryProvenance(
    string RelativeInstallPath,
    IReadOnlyList<string> BuildIds);

public sealed record StagedPackCompositionDocument(
    int SchemaVersion,
    string CompositionId,
    DateTimeOffset CreatedUtc,
    string InstallPath,
    UpscaleOptions CompatibilityOptions,
    IReadOnlyList<StagedPackCompositionComponent> Components,
    IReadOnlyList<StagedPackCompositionEntryProvenance> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record StagedPackCompositionResult(
    string CompositionId,
    string BuildDirectory,
    string ManifestPath,
    string CompositionDocumentPath,
    BuildManifest Manifest,
    StagedPackCompositionDocument Composition,
    int HardLinkedArtifacts,
    int CopiedArtifacts);

public sealed class StagedPackCompositionException : InvalidOperationException
{
    public StagedPackCompositionException(
        string message,
        IReadOnlyList<StagedPackCompositionConflict> conflicts)
        : base(message)
    {
        Conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
    }

    public IReadOnlyList<StagedPackCompositionConflict> Conflicts { get; }
}
