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
    /// <summary>
    /// Opaque digest binding a user's confirmation to this exact active
    /// transaction, outcome, launcher evidence, and set of adopted byte
    /// snapshots. It is present only for actionable assessments.
    /// </summary>
    public string? ConfirmationToken { get; init; }

    public bool CanRefresh => State is
        LauncherUpdateRefreshState.Ready or
        LauncherUpdateRefreshState.ResumeRequired;

    public bool CanReconcileForFreshBuild =>
        State == LauncherUpdateRefreshState.FreshBuildRequired;
}

/// <summary>
/// The exact launcher-update state changed after the user was shown a
/// confirmation. No rebuild, reconciliation, or live install write has begun;
/// callers should display <see cref="CurrentAssessment"/> and ask again.
/// </summary>
public sealed class LauncherUpdateAssessmentStaleException : InvalidOperationException
{
    public LauncherUpdateAssessmentStaleException(
        LauncherUpdateRefreshAssessment currentAssessment)
        : base(CreateMessage(currentAssessment))
    {
        CurrentAssessment = currentAssessment
            ?? throw new ArgumentNullException(nameof(currentAssessment));
    }

    public LauncherUpdateRefreshAssessment CurrentAssessment { get; }

    private static string CreateMessage(
        LauncherUpdateRefreshAssessment? currentAssessment)
    {
        ArgumentNullException.ThrowIfNull(currentAssessment);
        return "The verified game-update state changed after confirmation. "
               + "No files were changed; review the refreshed update details and confirm again. "
               + currentAssessment.Summary;
    }
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
