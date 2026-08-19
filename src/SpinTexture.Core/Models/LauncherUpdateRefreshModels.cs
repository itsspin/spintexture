using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.Core.Models;

public enum LauncherUpdateRefreshState
{
    NotApplicable,
    Ready,
    ResumeRequired,
    LauncherIncomplete,
    UnverifiedChanges,
    FreshBuildRequired
}

public sealed record LauncherUpdateRefreshAssessment(
    LauncherUpdateRefreshState State,
    string Summary,
    string? ActiveInstallManifestPath,
    string? ActiveBuildManifestPath,
    int UpdatedArtifactCount,
    IReadOnlyList<string> UpdatedRelativePaths)
{
    public bool CanRefresh => State is
        LauncherUpdateRefreshState.Ready or
        LauncherUpdateRefreshState.ResumeRequired;

    public bool CanReconcileForFreshBuild =>
        State == LauncherUpdateRefreshState.FreshBuildRequired;
}

public sealed record LauncherUpdateRefreshResult(
    IReadOnlyList<TexturePackBuildResult> RebuiltPacks,
    string RefreshedBuildManifestPath,
    ApplyResult ApplyResult,
    int UpdatedArtifactCount,
    int ReusedPackCount);

/// <summary>
/// A staged-pack install was intentionally stopped before any selection was
/// inspected or composed because the active client first requires a verified
/// launcher-update action on the main Build screen.
/// </summary>
public sealed class LauncherUpdateActionRequiredException : InvalidOperationException
{
    public LauncherUpdateActionRequiredException(
        LauncherUpdateRefreshAssessment assessment)
        : base(CreateMessage(assessment))
    {
        Assessment = assessment
            ?? throw new ArgumentNullException(nameof(assessment));
    }

    public LauncherUpdateRefreshAssessment Assessment { get; }

    private static string CreateMessage(LauncherUpdateRefreshAssessment? assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var action = assessment.State switch
        {
            LauncherUpdateRefreshState.Ready =>
                "Return to the main Build screen and choose Refresh + Reinstall After Update.",
            LauncherUpdateRefreshState.ResumeRequired =>
                "Return to the main Build screen and choose Resume Refresh + Reinstall.",
            LauncherUpdateRefreshState.FreshBuildRequired =>
                "Return to the main Build screen and choose Accept Update + Build Fresh.",
            _ =>
                "Return to the main Build screen and resolve the game update before installing packs."
        };
        return $"{assessment.Summary} {action}";
    }
}
