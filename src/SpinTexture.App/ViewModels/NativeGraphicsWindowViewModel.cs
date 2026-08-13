using System.Collections.ObjectModel;
using SpinTexture.App.Services;

namespace SpinTexture.App.ViewModels;

public sealed class NativeGraphicsWindowViewModel : ObservableObject, IDisposable
{
    private readonly string installPath;
    private readonly INativeGraphicsService service;
    private NativeGraphicsUiPlan? selectedPreset;
    private bool isBusy;
    private string currentStateTitle = "Reading native graphics state...";
    private string currentStateBadge = "CHECKING";
    private string currentStateSummary = "SpinTexture is inspecting eqclient.ini without changing it.";
    private string settingsPath = string.Empty;
    private string? activeTransactionPath;
    private bool canRestore;
    private IReadOnlyList<NativeGraphicsUiSetting> currentValues = [];
    private bool currentStateNeedsAttention;
    private string statusText = "No changes have been made.";
    private bool isError;
    private bool isBloomEnabled;
    private bool isThirdPersonPitchLockEnabled;

    public NativeGraphicsWindowViewModel(string installPath, INativeGraphicsService service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        this.installPath = Path.GetFullPath(installPath);
        this.service = service ?? throw new ArgumentNullException(nameof(service));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && CanRestore);
        BloomChangedCommand = new AsyncRelayCommand(UpdateBloomChoiceAsync, () => !IsBusy && IsCinematicSelected);
        CameraChangedCommand = new AsyncRelayCommand(UpdateCameraChoiceAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
    }

    public ObservableCollection<NativeGraphicsUiPlan> Presets { get; } = [];

    public NativeGraphicsUiPlan? SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (SetProperty(ref selectedPreset, value))
            {
                OnPropertyChanged(nameof(PlannedChanges));
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(ApplyButtonText));
                OnPropertyChanged(nameof(IsCinematicSelected));
                BloomChangedCommand.RaiseCanExecuteChanged();
                RefreshCommands();
            }
        }
    }

    public IReadOnlyList<NativeGraphicsUiChange> PlannedChanges =>
        SelectedPreset?.Changes ?? [];

    public string PlanSummary => SelectedPreset is null
        ? "Select a native preset to inspect its exact eqclient.ini changes."
        : SelectedPreset.HasChanges
            ? $"{SelectedPreset.Changes.Count:N0} exact setting change(s) are planned."
            : "This preset already matches the settings on disk; Apply will make no change.";

    public string ApplyButtonText => SelectedPreset is null
        ? "Apply Native Preset"
        : $"Apply {SelectedPreset.Name}";

    public bool IsCinematicSelected => SelectedPreset?.IsCinematic == true;

    public bool IsBloomEnabled
    {
        get => isBloomEnabled;
        set
        {
            if (SetProperty(ref isBloomEnabled, value))
            {
                OnPropertyChanged(nameof(BloomChoiceText));
            }
        }
    }

    public string BloomChoiceText => IsBloomEnabled
        ? "Bloom on. Brighter glow, with a higher chance of halos around sun, moon, and star sprites."
        : "Bloom off. Recommended for clean skies while keeping Cinematic lighting and maximum shadows.";

    public bool IsThirdPersonPitchLockEnabled
    {
        get => isThirdPersonPitchLockEnabled;
        set
        {
            if (SetProperty(ref isThirdPersonPitchLockEnabled, value))
            {
                OnPropertyChanged(nameof(CameraChoiceText));
            }
        }
    }

    public string CameraChoiceText => IsThirdPersonPitchLockEnabled
        ? "On. Third-person character pitch changes only while Shift is held, then returns level when Shift is released."
        : "Off. EQL keeps the camera and levitation pitch behavior that existed before SpinTexture.";

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RefreshCommands();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string CurrentStateTitle
    {
        get => currentStateTitle;
        private set => SetProperty(ref currentStateTitle, value);
    }

    public string CurrentStateBadge
    {
        get => currentStateBadge;
        private set => SetProperty(ref currentStateBadge, value);
    }

    public string CurrentStateSummary
    {
        get => currentStateSummary;
        private set => SetProperty(ref currentStateSummary, value);
    }

    public string SettingsPath
    {
        get => settingsPath;
        private set => SetProperty(ref settingsPath, value);
    }

    public string? ActiveTransactionPath
    {
        get => activeTransactionPath;
        private set
        {
            if (SetProperty(ref activeTransactionPath, value))
            {
                OnPropertyChanged(nameof(HasActiveTransaction));
            }
        }
    }

    public bool HasActiveTransaction => !string.IsNullOrWhiteSpace(ActiveTransactionPath);

    public IReadOnlyList<NativeGraphicsUiSetting> CurrentValues
    {
        get => currentValues;
        private set => SetProperty(ref currentValues, value);
    }

    public bool CurrentStateNeedsAttention
    {
        get => currentStateNeedsAttention;
        private set => SetProperty(ref currentStateNeedsAttention, value);
    }

    public bool CanRestore
    {
        get => canRestore;
        private set
        {
            if (SetProperty(ref canRestore, value))
            {
                OnPropertyChanged(nameof(RestoreAvailabilityText));
                RefreshCommands();
            }
        }
    }

    public string RestoreAvailabilityText => CanRestore
        ? "Verified pre-preset managed values are available for one-click restore."
        : "No managed native-graphics change currently needs restoring.";

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool IsError
    {
        get => isError;
        private set => SetProperty(ref isError, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand BloomChangedCommand { get; }
    public AsyncRelayCommand CameraChangedCommand { get; }
    public RelayCommand CancelCommand { get; }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IsError = false;
        StatusText = "Inspecting native EQL graphics settings. No files are being changed.";
        var previousPreset = SelectedPreset?.Preset ?? NativeGraphicsUiPreset.Balanced;
        try
        {
            var status = await service.InspectAsync(installPath, cancellationToken)
                .ConfigureAwait(true);
            UpdateStatus(status);
            if (!status.CanApply)
            {
                Presets.Clear();
                SelectedPreset = null;
                StatusText = status.CanRestore
                    ? "Restore the verified original settings before selecting another preset."
                    : status.Summary;
                return;
            }

            var cinematicPreset = status.AppliedPreset is NativeGraphicsUiPreset.Cinematic
                or NativeGraphicsUiPreset.CinematicNoBloom
                    ? status.AppliedPreset.Value
                    : previousPreset is NativeGraphicsUiPreset.Cinematic
                        or NativeGraphicsUiPreset.CinematicNoBloom
                            ? previousPreset
                            : NativeGraphicsUiPreset.CinematicNoBloom;
            IsBloomEnabled = cinematicPreset == NativeGraphicsUiPreset.Cinematic;
            var balancedTask = service.PlanAsync(
                installPath,
                NativeGraphicsUiPreset.Balanced,
                IsThirdPersonPitchLockEnabled,
                cancellationToken);
            var cinematicTask = service.PlanAsync(
                installPath,
                cinematicPreset,
                IsThirdPersonPitchLockEnabled,
                cancellationToken);
            await Task.WhenAll(balancedTask, cinematicTask).ConfigureAwait(true);

            Presets.Clear();
            Presets.Add(await balancedTask.ConfigureAwait(true));
            Presets.Add(await cinematicTask.ConfigureAwait(true));
            SelectedPreset = Presets.FirstOrDefault(plan => plan.Preset == previousPreset)
                ?? Presets.First(plan => plan.Preset == cinematicPreset);
            StatusText = "Ready. Review the exact setting changes before applying a preset.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Inspection canceled. No native graphics settings were changed.";
            throw;
        }
        catch (Exception exception)
        {
            ShowError("Native graphics settings could not be inspected", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal Task RefreshForLayoutSmokeAsync() => RefreshAsync(CancellationToken.None);

    private async Task UpdateBloomChoiceAsync(CancellationToken cancellationToken)
    {
        if (!IsCinematicSelected)
        {
            return;
        }

        IsBusy = true;
        IsError = false;
        StatusText = "Updating the Cinematic preview. No settings are being changed.";
        try
        {
            var requested = IsBloomEnabled
                ? NativeGraphicsUiPreset.Cinematic
                : NativeGraphicsUiPreset.CinematicNoBloom;
            var plan = await service.PlanAsync(
                    installPath,
                    requested,
                    IsThirdPersonPitchLockEnabled,
                    cancellationToken)
                .ConfigureAwait(true);
            var index = Presets.IndexOf(SelectedPreset!);
            if (index < 0)
            {
                index = Presets.ToList().FindIndex(candidate => candidate.IsCinematic);
            }

            if (index >= 0)
            {
                Presets[index] = plan;
            }

            SelectedPreset = plan;
            StatusText = "Ready. Review the exact Bloom and Cinematic setting changes before applying.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Bloom preview canceled. No native graphics settings were changed.";
            throw;
        }
        catch (Exception exception)
        {
            ShowError("The Bloom choice could not be previewed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UpdateCameraChoiceAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedPreset?.Preset ?? NativeGraphicsUiPreset.Balanced;
        IsBusy = true;
        IsError = false;
        StatusText = "Updating the supported third-person camera preview. No settings are being changed.";
        try
        {
            var plan = await service.PlanAsync(
                    installPath,
                    selected,
                    IsThirdPersonPitchLockEnabled,
                    cancellationToken)
                .ConfigureAwait(true);
            var index = Presets.IndexOf(SelectedPreset!);
            if (index >= 0)
            {
                Presets[index] = plan;
            }

            SelectedPreset = plan;
            StatusText = "Ready. Review the camera comfort and native graphics changes before applying.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Camera preview canceled. No settings were changed.";
            throw;
        }
        catch (Exception exception)
        {
            ShowError("The camera comfort choice could not be previewed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var preset = SelectedPreset;
        if (preset is null)
        {
            return;
        }

        IsBusy = true;
        IsError = false;
        StatusText = $"Applying {preset.Name} through a verified native-settings transaction...";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            await service.ApplyAsync(
                installPath,
                preset.Preset,
                IsThirdPersonPitchLockEnabled,
                progress,
                cancellationToken).ConfigureAwait(true);
            try
            {
                await ReloadAfterOperationAsync(
                    preset.Preset,
                    $"{preset.Name} is active. Texture packs, UI files, fonts, and game archives were not changed.",
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception refreshException)
            {
                ShowError(
                    $"{preset.Name} was applied and verified, but its refreshed state could not be displayed",
                    refreshException);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Apply canceled safely. SpinTexture did not report an unverified state as active.";
            throw;
        }
        catch (Exception exception)
        {
            ShowError("The native preset was not applied", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IsError = false;
        StatusText = "Restoring the verified pre-preset managed graphics values...";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            await service.RestoreAsync(
                installPath,
                progress,
                cancellationToken).ConfigureAwait(true);
            try
            {
                await ReloadAfterOperationAsync(
                    SelectedPreset?.Preset ?? NativeGraphicsUiPreset.Balanced,
                    "Pre-preset managed graphics values restored and verified.",
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception refreshException)
            {
                ShowError(
                    "Original settings were restored and verified, but the refreshed state could not be displayed",
                    refreshException);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Restore canceled safely. Review the current state before launching EverQuest.";
            throw;
        }
        catch (Exception exception)
        {
            ShowError("Pre-preset managed graphics values were not restored", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAfterOperationAsync(
        NativeGraphicsUiPreset selected,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var status = await service.InspectAsync(installPath, cancellationToken).ConfigureAwait(true);
        UpdateStatus(status);
        if (!status.CanApply)
        {
            Presets.Clear();
            SelectedPreset = null;
            StatusText = successMessage;
            return;
        }

        var plans = await Task.WhenAll(
                service.PlanAsync(
                    installPath,
                    NativeGraphicsUiPreset.Balanced,
                    IsThirdPersonPitchLockEnabled,
                    cancellationToken),
                service.PlanAsync(
                    installPath,
                    selected is NativeGraphicsUiPreset.Cinematic
                        or NativeGraphicsUiPreset.CinematicNoBloom
                            ? selected
                            : IsBloomEnabled
                                ? NativeGraphicsUiPreset.Cinematic
                                : NativeGraphicsUiPreset.CinematicNoBloom,
                    IsThirdPersonPitchLockEnabled,
                    cancellationToken))
            .ConfigureAwait(true);
        Presets.Clear();
        foreach (var plan in plans)
        {
            Presets.Add(plan);
        }

        SelectedPreset = Presets.First(plan => plan.Preset == selected);
        IsBloomEnabled = selected == NativeGraphicsUiPreset.Cinematic;
        StatusText = successMessage;
    }

    private void UpdateStatus(NativeGraphicsUiStatus status)
    {
        CurrentStateTitle = status.StateName;
        CurrentStateBadge = status.StateBadge;
        CurrentStateSummary = status.Summary;
        SettingsPath = status.SettingsPath;
        ActiveTransactionPath = status.ActiveTransactionPath;
        CurrentValues = status.ManagedValues;
        CurrentStateNeedsAttention = status.NeedsAttention;
        CanRestore = status.CanRestore;
        IsThirdPersonPitchLockEnabled = status.ThirdPersonPitchLockEnabled;
    }

    private void ShowError(string context, Exception exception)
    {
        IsError = true;
        StatusText = $"{context}: {exception.Message}";
    }

    private bool CanApply() =>
        !IsBusy && SelectedPreset is { HasChanges: true, CanApply: true };

    private void Cancel()
    {
        RefreshCommand.Cancel();
        ApplyCommand.Cancel();
        RestoreCommand.Cancel();
        StatusText = "Canceling safely...";
    }

    private void RefreshCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        BloomChangedCommand.RaiseCanExecuteChanged();
        CameraChangedCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        RefreshCommand.Dispose();
        ApplyCommand.Dispose();
        RestoreCommand.Dispose();
        BloomChangedCommand.Dispose();
    }
}
