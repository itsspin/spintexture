using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.App.Services;

public sealed class NativeGraphicsServiceAdapter : INativeGraphicsService
{
    private readonly NativeGraphicsSettingsService service;
    private readonly string? workspaceRoot;

    public NativeGraphicsServiceAdapter(
        NativeGraphicsSettingsService? service = null,
        string? workspaceRoot = null)
    {
        this.service = service ?? new NativeGraphicsSettingsService();
        this.workspaceRoot = workspaceRoot;
    }

    public async Task<NativeGraphicsUiStatus> InspectAsync(
        string installPath,
        CancellationToken cancellationToken)
    {
        var status = await service.InspectAsync(
                ResolvePaths(installPath),
                cancellationToken)
            .ConfigureAwait(false);
        return MapStatus(status);
    }

    public async Task<NativeGraphicsUiPlan> PlanAsync(
        string installPath,
        NativeGraphicsUiPreset preset,
        bool thirdPersonPitchLockEnabled,
        CancellationToken cancellationToken)
    {
        var corePreset = MapPreset(preset);
        var plan = await service.PlanAsync(
                ResolvePaths(installPath),
                corePreset,
                thirdPersonPitchLockEnabled,
                cancellationToken)
            .ConfigureAwait(false);
        var changes = plan.Changes
            .Select(change => new NativeGraphicsUiChange(
                change.Section,
                change.Key,
                change.CurrentExists
                    ? change.CurrentValue ?? "(empty)"
                    : "(not set)",
                change.PlannedValue,
                change.WillAdd,
                change.WillRemove))
            .ToArray();
        var presentation = GetPresentation(preset);
        return new NativeGraphicsUiPlan(
            preset,
            presentation.Name,
            presentation.Eyebrow,
            presentation.Description,
            presentation.PerformanceNote,
            changes,
            plan.HasChanges,
            plan.Status.CanApply && plan.HasChanges);
    }

    public async Task ApplyAsync(
        string installPath,
        NativeGraphicsUiPreset preset,
        bool thirdPersonPitchLockEnabled,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var coreProgress = progress is null
            ? null
            : new Progress<ProgressUpdate>(update =>
                progress.Report(string.IsNullOrWhiteSpace(update.CurrentItem)
                    ? update.Message
                    : $"{update.Message}  {update.CurrentItem}"));
        await service.ApplyAsync(
                ResolvePaths(installPath),
                MapPreset(preset),
                thirdPersonPitchLockEnabled,
                coreProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RestoreAsync(
        string installPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var coreProgress = progress is null
            ? null
            : new Progress<ProgressUpdate>(update =>
                progress.Report(string.IsNullOrWhiteSpace(update.CurrentItem)
                    ? update.Message
                    : $"{update.Message}  {update.CurrentItem}"));
        await service.RestoreAsync(
                ResolvePaths(installPath),
                coreProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ProjectPaths ResolvePaths(string installPath) =>
        WorkspaceLocator.ForInstall(installPath, workspaceRoot);

    private static NativeGraphicsPreset MapPreset(NativeGraphicsUiPreset preset) => preset switch
    {
        NativeGraphicsUiPreset.Balanced => NativeGraphicsPreset.Balanced,
        NativeGraphicsUiPreset.Cinematic => NativeGraphicsPreset.Cinematic,
        NativeGraphicsUiPreset.CinematicNoBloom => NativeGraphicsPreset.CinematicNoBloom,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    private static NativeGraphicsUiStatus MapStatus(NativeGraphicsSettingsStatus status)
    {
        var (name, badge) = status.State switch
        {
            NativeGraphicsSettingsState.Unavailable => ("Native graphics unavailable", "UNAVAILABLE"),
            NativeGraphicsSettingsState.Original => ("Current EQL graphics settings", "UNMANAGED"),
            NativeGraphicsSettingsState.Applied => (
                status.AppliedPreset is { } preset
                    ? $"{GetPresetDisplayName(preset)} native preset active"
                    : "Managed native preset active",
                "MANAGED"),
            NativeGraphicsSettingsState.Modified => ("Managed settings changed externally", "REVIEW"),
            NativeGraphicsSettingsState.RecoveryRequired => ("Native settings recovery required", "RECOVERY"),
            _ => ("Unknown native graphics state", "UNKNOWN")
        };
        return new NativeGraphicsUiStatus(
            name,
            badge,
            status.Summary,
            status.SettingsPath,
            status.ActiveTransactionManifestPath,
            status.ManagedValues
                .Select(value => new NativeGraphicsUiSetting(
                    value.Section,
                    value.Key,
                    value.Exists ? value.Value ?? "(empty)" : "(not set)"))
                .ToArray(),
            status.State is NativeGraphicsSettingsState.Unavailable
                or NativeGraphicsSettingsState.Modified
                or NativeGraphicsSettingsState.RecoveryRequired,
            status.CanApply,
            status.CanRestore,
            status.AppliedPreset switch
            {
                NativeGraphicsPreset.Balanced => NativeGraphicsUiPreset.Balanced,
                NativeGraphicsPreset.Cinematic => NativeGraphicsUiPreset.Cinematic,
                NativeGraphicsPreset.CinematicNoBloom => NativeGraphicsUiPreset.CinematicNoBloom,
                _ => null
            },
            status.ThirdPersonPitchLockEnabled);
    }

    private static NativeGraphicsPresentation GetPresentation(NativeGraphicsUiPreset preset) => preset switch
    {
        NativeGraphicsUiPreset.Balanced => new NativeGraphicsPresentation(
            "Balanced",
            "NATIVE STENCIL SHADOWS",
            "Enables EQL's built-in stencil shadows only. Advanced lighting, post effects, bloom, and the current shadow distance remain at their original values.",
            "Moderate GPU cost; the best starting point for most systems."),
        NativeGraphicsUiPreset.Cinematic => new NativeGraphicsPresentation(
            "Cinematic",
            "MAX SHADOW RANGE + LIGHTING + BLOOM",
            "Enables the verified built-in stencil-shadow, multipass-lighting, post-effects, and bloom-lighting settings, and raises EQL's native Shadow Clip Plane to its maximum setting.",
            "Very high GPU cost. Native bloom can enlarge or halo bright sun, moon, and star sprites; choose Balanced if you see that effect."),
        NativeGraphicsUiPreset.CinematicNoBloom => new NativeGraphicsPresentation(
            "Cinematic",
            "MAX SHADOW RANGE + LIGHTING",
            "Enables the verified built-in stencil-shadow, multipass-lighting, and post-effects settings, raises EQL's native Shadow Clip Plane to maximum, and explicitly disables Bloom.",
            "High GPU cost. Bloom is off to reduce halos around bright sky sprites while retaining Cinematic lighting and shadows."),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    private sealed record NativeGraphicsPresentation(
        string Name,
        string Eyebrow,
        string Description,
        string PerformanceNote);

    private static string GetPresetDisplayName(NativeGraphicsPreset preset) => preset switch
    {
        NativeGraphicsPreset.CinematicNoBloom => "Cinematic (Bloom off)",
        _ => preset.ToString()
    };
}
