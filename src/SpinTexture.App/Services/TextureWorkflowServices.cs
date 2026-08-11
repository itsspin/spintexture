using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.App.Services;

public interface ITextureWorkflowService
{
    string? LastBuildDirectory { get; }

    Task<ScanSummary> AnalyzeAsync(
        string installPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken);

    Task<TexturePackBuildResult> BuildAsync(
        string installPath,
        UpscaleOptions options,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken);

    Task<InstallHealthReport> AuditInstallHealthAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<InstallHealthReport> AuditInstallHealthFastAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<ApplyResult> ApplyLatestStagedPackAsync(
        string installPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        string installPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken);

    bool HasRestorableBackup(string installPath);
    bool HasStagedPack(string installPath);
}

public sealed class TextureWorkflowService : ITextureWorkflowService
{
    private readonly TexturePackWorkflow workflow;
    private readonly string? workspaceRoot;

    public TextureWorkflowService(TexturePackWorkflow? workflow = null, string? workspaceRoot = null)
    {
        this.workflow = workflow ?? new TexturePackWorkflow();
        this.workspaceRoot = workspaceRoot;
    }

    public string? LastBuildDirectory { get; private set; }

    public Task<ScanSummary> AnalyzeAsync(
        string installPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken) =>
        workflow.AnalyzeAsync(installPath, progress, cancellationToken);

    public async Task<TexturePackBuildResult> BuildAsync(
        string installPath,
        UpscaleOptions options,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var paths = WorkspaceLocator.ForInstall(installPath, workspaceRoot);
        var result = await workflow.BuildAsync(paths, options, progress, cancellationToken);
        LastBuildDirectory = result.StagedBuild.BuildDirectory;
        return result;
    }

    public async Task RestoreAsync(
        string installPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var paths = WorkspaceLocator.ForInstall(installPath, workspaceRoot);
        await workflow.RestoreLatestAsync(paths, progress, cancellationToken);
    }

    public Task<InstallHealthReport> AuditInstallHealthAsync(
        string installPath,
        CancellationToken cancellationToken) =>
        workflow.AuditInstallHealthAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            cancellationToken);

    public Task<InstallHealthReport> AuditInstallHealthFastAsync(
        string installPath,
        CancellationToken cancellationToken) =>
        workflow.AuditInstallHealthFastAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            cancellationToken);

    public Task<ApplyResult> ApplyLatestStagedPackAsync(
        string installPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken) =>
        workflow.ApplyLatestStagedPackAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            progress,
            cancellationToken);

    public bool HasRestorableBackup(string installPath)
    {
        try
        {
            return workflow.HasRestorableBackup(WorkspaceLocator.ForInstall(installPath, workspaceRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool HasStagedPack(string installPath)
    {
        try
        {
            return workflow.FindLatestBuildManifest(
                WorkspaceLocator.ForInstall(installPath, workspaceRoot)) is not null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }
}
