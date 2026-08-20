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

    Task<TextureOptionPreviewResult> GenerateOptionPreviewAsync(
        string installPath,
        UpscaleOptions options,
        ScanSummary? analysis,
        ulong sampleSeed,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken);

    Task<RecoverableStagedBuild?> FindRecoverableBuildAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<ArtisticWorkerRoute?> ResolveArtisticWorkerRouteAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<InstallHealthReport> AuditInstallHealthAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<InstallHealthReport> AuditInstallHealthFastAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<InstallHealthReport> AuditInstallHealthForLauncherUpdateDetectionAsync(
        string installPath,
        CancellationToken cancellationToken);

    Task<LauncherUpdateRefreshAssessment> AssessLauncherUpdateRefreshAsync(
        string installPath,
        InstallHealthReport? verifiedHealth,
        CancellationToken cancellationToken);

    Task<LauncherUpdateRefreshResult> RefreshAndApplyActivePackAfterLauncherUpdateAsync(
        string installPath,
        string expectedConfirmationToken,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken);

    Task<LauncherUpdateReconciliationResult>
        ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
            string installPath,
            string expectedConfirmationToken,
            IProgress<ProgressUpdate> progress,
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
    private readonly TextureOptionPreviewService optionPreviewService;
    private readonly string? workspaceRoot;

    public TextureWorkflowService(
        TexturePackWorkflow? workflow = null,
        string? workspaceRoot = null,
        TextureOptionPreviewService? optionPreviewService = null)
    {
        this.workflow = workflow ?? new TexturePackWorkflow();
        this.optionPreviewService = optionPreviewService ?? new TextureOptionPreviewService();
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

    public Task<TextureOptionPreviewResult> GenerateOptionPreviewAsync(
        string installPath,
        UpscaleOptions options,
        ScanSummary? analysis,
        ulong sampleSeed,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        optionPreviewService.GenerateAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            options,
            analysis,
            progress,
            cancellationToken,
            sampleSeed);

    public Task<RecoverableStagedBuild?> FindRecoverableBuildAsync(
        string installPath,
        CancellationToken cancellationToken) =>
        new StagedBuildService().FindRecoverableBuildAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            (UpscaleOptions options) =>
                TexturePackWorkflow.GetFreshBuildResumeOperationKey(options.Preset),
            cancellationToken);

    public Task<ArtisticWorkerRoute?> ResolveArtisticWorkerRouteAsync(
        string installPath,
        CancellationToken cancellationToken) =>
        workflow.ResolveArtisticWorkerRouteAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            cancellationToken);

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

    public Task<InstallHealthReport> AuditInstallHealthForLauncherUpdateDetectionAsync(
        string installPath,
        CancellationToken cancellationToken) =>
        workflow.AuditInstallHealthForLauncherUpdateDetectionAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            cancellationToken);

    public Task<LauncherUpdateRefreshAssessment> AssessLauncherUpdateRefreshAsync(
        string installPath,
        InstallHealthReport? verifiedHealth,
        CancellationToken cancellationToken) =>
        workflow.AssessLauncherUpdateRefreshAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            verifiedHealth,
            cancellationToken);

    public async Task<LauncherUpdateRefreshResult>
        RefreshAndApplyActivePackAfterLauncherUpdateAsync(
            string installPath,
            string expectedConfirmationToken,
            IProgress<ProgressUpdate> progress,
            CancellationToken cancellationToken)
    {
        var result = await workflow
            .RefreshAndApplyActivePackAfterLauncherUpdateAsync(
                WorkspaceLocator.ForInstall(installPath, workspaceRoot),
                expectedConfirmationToken,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        LastBuildDirectory = result.RebuiltPacks.LastOrDefault()?.StagedBuild.BuildDirectory;
        return result;
    }

    public Task<LauncherUpdateReconciliationResult>
        ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
            string installPath,
            string expectedConfirmationToken,
            IProgress<ProgressUpdate> progress,
            CancellationToken cancellationToken) =>
        workflow.ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
            WorkspaceLocator.ForInstall(installPath, workspaceRoot),
            expectedConfirmationToken,
            progress,
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
