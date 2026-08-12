using SpinTexture.Core.Models;

namespace SpinTexture.Core.Pipeline;

public sealed record StagedArtifactBuildContext(
    string RelativeInstallPath,
    string SourcePath,
    string DestinationPath,
    string WorkingDirectory,
    string PreviewDirectory,
    UpscaleOptions Options);

/// <summary>
/// Builds one complete install artifact into DestinationPath. SourcePath is a read-only workspace snapshot,
/// never a path into the live EverQuest install.
/// </summary>
public interface IStagedArtifactBuilder
{
    Task BuildAsync(StagedArtifactBuildContext context, CancellationToken cancellationToken = default);
}

internal interface IStagedArtifactCheckpointParticipant
{
    StagedArtifactCheckpointCapture CaptureCheckpointState();
    StagedBuildCheckpointContribution CompleteCheckpointCapture(
        StagedArtifactCheckpointCapture before);
    void RestoreCheckpointContribution(StagedBuildCheckpointContribution contribution);
}

public sealed class DelegateStagedArtifactBuilder : IStagedArtifactBuilder
{
    private readonly Func<StagedArtifactBuildContext, CancellationToken, Task> _builder;

    public DelegateStagedArtifactBuilder(Func<StagedArtifactBuildContext, CancellationToken, Task> builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public Task BuildAsync(StagedArtifactBuildContext context, CancellationToken cancellationToken = default) =>
        _builder(context, cancellationToken);
}

public sealed record StagedBuildItem(
    string RelativeInstallPath,
    IStagedArtifactBuilder Builder,
    string? VerifiedSourcePath = null,
    long? ExpectedSourceLength = null,
    string? ExpectedSourceSha256 = null,
    long? ExpectedStagedLength = null,
    string? ExpectedStagedSha256 = null,
    // A targeted safety rule can deliberately restore an artifact to the
    // verified original. Omitting that source-identical artifact from the new
    // immutable replacement manifest makes a pack switch remove the older
    // enhanced payload. This is opt-in per item so RequireAllItems remains
    // strict for every ordinary build and reuse operation.
    bool AllowSourceIdenticalOmission = false);

public sealed record StagedBuildRequest(
    ProjectPaths Paths,
    UpscaleOptions Options,
    IReadOnlyList<StagedBuildItem> Items,
    string? BuildId = null,
    bool RequireAllItems = false,
    // A stable workflow-owned discriminator. Automatic crash resume is enabled
    // only when this is non-empty so repair/composition builders never inherit
    // outputs from a semantically different operation by accident.
    string? ResumeOperationKey = null);

public sealed record StagedBuildResult(
    string BuildId,
    string BuildDirectory,
    string ManifestPath,
    BuildManifest Manifest)
{
    public int ResumedArtifactCount { get; init; }
    public double ActiveDurationSeconds { get; init; }
    public DateTimeOffset CheckpointCreatedUtc { get; init; }
}

public sealed record RecoverableStagedBuild(
    string BuildId,
    DateTimeOffset CreatedUtc,
    UpscaleOptions Options,
    int VerifiedArtifacts,
    int TotalArtifacts);
