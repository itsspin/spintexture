using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.Core.Pipeline;

internal sealed record StagedBuildCheckpoint(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CreatedUtc,
    double ActiveDurationSeconds,
    string InstallPath,
    UpscaleOptions Options,
    bool RequireAllItems,
    string ResumeOperationKey,
    string PlanSha256,
    IReadOnlyList<StagedBuildCheckpointItem> Items,
    IReadOnlyList<StagedBuildCheckpointArtifact> CompletedArtifacts)
{
    // Schema 3 records the optional World-expansion build identity. Schema-2
    // checkpoints remain readable; null selections omit their JSON property,
    // preserving the exact legacy plan hash for interrupted existing builds.
    public const int MinimumSupportedSchemaVersion = 2;
    public const int CurrentSchemaVersion = 3;

    public static bool IsSupportedSchemaVersion(int schemaVersion) =>
        schemaVersion is >= MinimumSupportedSchemaVersion and <= CurrentSchemaVersion;
}

internal sealed record StagedBuildCheckpointItem(
    string RelativeInstallPath,
    long SourceLength,
    string SourceSha256,
    long? ExpectedStagedLength,
    string? ExpectedStagedSha256,
    bool AllowSourceIdenticalOmission);

internal sealed record StagedBuildCheckpointArtifact(
    string RelativeInstallPath,
    long SourceLength,
    string SourceSha256,
    long StagedLength,
    string StagedSha256,
    bool Omitted,
    StagedBuildCheckpointContribution? Contribution = null);

internal sealed record StagedArtifactCheckpointCapture(
    TextureBuildCounterCheckpoint Statistics,
    TexturePreviewCollectorCheckpoint Previews);

internal sealed record StagedBuildCheckpointContribution(
    TextureBuildCounterCheckpoint Statistics,
    IReadOnlyList<StagedBuildCheckpointPreview> Previews,
    IReadOnlyList<TextureReviewEntry> ReviewEntries);

internal sealed record StagedBuildCheckpointPreview(
    int Slot,
    TexturePreviewEntry Entry,
    long OriginalLength,
    string OriginalSha256,
    long EnhancedLength,
    string EnhancedSha256);
