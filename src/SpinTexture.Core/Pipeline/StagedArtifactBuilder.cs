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
    string? ExpectedStagedSha256 = null);

public sealed record StagedBuildRequest(
    ProjectPaths Paths,
    UpscaleOptions Options,
    IReadOnlyList<StagedBuildItem> Items,
    string? BuildId = null,
    bool RequireAllItems = false);

public sealed record StagedBuildResult(
    string BuildId,
    string BuildDirectory,
    string ManifestPath,
    BuildManifest Manifest);
