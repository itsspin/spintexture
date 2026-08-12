using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using SpinTexture.App.Services;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IFolderPickerService _folderPicker;
    private readonly ITextureWorkflowService _workflow;
    private readonly IUserDialogService _dialogs;
    private readonly IEnhancedLauncherService _enhancedLauncher;
    private readonly AppPreferencesStore _preferences;
    private string _installPath = string.Empty;
    private string _statusText = "Choose an EverQuest Legends directory to begin";
    private string _currentStage = "READY";
    private string _currentItem = "No operation running";
    private double _progressValue;
    private bool _isBusy;
    private bool _hasAnalysis;
    private bool _hasManagedBackup;
    private bool _hasStagedPack;
    private bool _isLegalAcknowledged;
    private bool _generateMipMaps = true;
    private ScanSummary? _scanSummary;
    private PresetOptionViewModel _selectedPresetOption;
    private ScopeOptionViewModel _selectedScopeOption;
    private string? _selectedZone;
    private int _selectedMaximumDimension = 2048;
    private string _estimatedOutputText = "Analyze to estimate";
    private string _estimatedArchivesText = "—";
    private string _estimatedTimeText = "—";
    private string _estimateBasisText = "Analyze the client for a hardware-aware forecast.";
    private string _workspaceText = "Staged workspace • live client remains unchanged";
    private string _installHealthText = "No enhanced pack is active";
    private string? _previewManifestPath;
    private InstallHealthReport? _installHealth;
    private readonly DispatcherTimer _buildProgressTimer;
    private readonly Stopwatch _buildStopwatch = new();
    private bool _isStagedBuildOperation;
    private bool _hasEnteredArchiveBuild;
    private int _buildCompletedArtifacts;
    private int _buildTotalArtifacts;
    private double _currentArtifactFraction;
    private TimeSpan _archiveBuildStartedAt;
    private TimeSpan _lastBuildActivityAt;
    private TimeSpan _freshArtifactStartedAt;
    private bool _isMeasuringFreshArtifact;
    private bool _resumedBuildNeedsFreshEtaSample;
    private double? _smoothedSecondsPerArtifact;
    private string _currentBuildPhase = "archive preparation";
    private bool _stagedBuildSucceeded;
    private string _overallProgressText = "No overall job is running";
    private string _progressEtaText = "Choose an asset set to see its estimate";
    private string _progressCountText = string.Empty;

    public MainWindowViewModel(
        IFolderPickerService folderPicker,
        ITextureWorkflowService workflow,
        IUserDialogService dialogs,
        IEnhancedLauncherService enhancedLauncher,
        AppPreferencesStore? preferences = null)
    {
        _folderPicker = folderPicker;
        _workflow = workflow;
        _dialogs = dialogs;
        _enhancedLauncher = enhancedLauncher;
        _preferences = preferences ?? new AppPreferencesStore();
        _buildProgressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _buildProgressTimer.Tick += OnBuildProgressTimerTick;

        PresetOptions =
        [
            new PresetOptionViewModel(
                TexturePreset.Faithful,
                "Original Clarity",
                "FAITHFUL / LOW VARIANCE",
                "Cleans compression noise and sharpens forms without inventing surface detail.",
                "Best match to the original art",
                "Real-ESRNet x4plus",
                "Original palette, forms, and material character",
                "Fastest neural route"),
            new PresetOptionViewModel(
                TexturePreset.ClassicHd,
                "Texture HD",
                "NATURAL / RECOMMENDED",
                "Uses a game-texture-trained model to reconstruct clearly visible stone, wood, cloth, and terrain detail.",
                "Natural detail without changing the art direction",
                "PBRify SPAN V4 + faithful safety fallback",
                "Crisper surfaces while preserving the classic look",
                "Fast balanced route",
                IsRecommended: true),
            new PresetOptionViewModel(
                TexturePreset.MaximumDetail,
                "Material Detail",
                "STRONG / GENERATED DETAIL",
                "Pushes rock, metal, cloth, and hero assets toward stronger microdetail. It does not add true PBR materials.",
                "Most aggressive result; review a small zone first",
                "Real-ESRGAN x4plus with TTA",
                "Sharper generated material texture and contrast",
                "Slowest route; eight-view TTA performs substantially more GPU work"),
            new PresetOptionViewModel(
                TexturePreset.Illustrated,
                "Illustrated / Clean Painted",
                "STYLIZED / CLEAN SHAPES",
                "Uses the official illustrated Real-ESRGAN model for flatter painted detail and cleaner shapes.",
                "Stylizes texture art; it does not add toon outlines to 3D geometry",
                "Real-ESRGAN x4plus-anime",
                "Cleaner painted forms and less photographic surface noise",
                "Moderate route; faithful fallback protected"),
            new PresetOptionViewModel(
                TexturePreset.RusticPainted,
                "Rustic Painted Fantasy",
                "WATERCOLOR-INSPIRED / EARTHY",
                "Builds clean illustrated detail, then applies bounded earthy palette shaping for a darker hand-painted fantasy character.",
                "Muted stone, wood, foliage, cloth, and armor without replacing the original composition",
                "Real-ESRGAN x4plus-anime + SpinTexture painted grade",
                "Warm shadows, olive greens, restrained saturation, and subtle painted tone planes",
                "Moderate route; stylized fidelity gate and faithful fallback")
        ];

        ScopeOptions =
        [
            new ScopeOptionViewModel(AssetScope.SelectedZone, "Selected zone", "Build and validate one zone at a time."),
            new ScopeOptionViewModel(AssetScope.WorldOnly, "World only", "Terrain, architecture, and safe world objects."),
            new ScopeOptionViewModel(AssetScope.CharactersAndEquipmentOnly, "Characters + equipment only", "Race models, armor, weapons, and dedicated wearable-item packs without the world."),
            new ScopeOptionViewModel(
                AssetScope.WorldCharactersAndEquipment,
                "World + characters + equipment",
                "World textures plus playable races, mobs, armor, weapons, and verified equipment materials."),
            new ScopeOptionViewModel(
                AssetScope.SpellEffectsOnly,
                "Spell effects",
                "Preview-first loose effect art. Soft-translucent particles keep original alpha and use Original Clarity instead of stylization."),
            new ScopeOptionViewModel(AssetScope.AllSafeTextures, "All safe textures", "Every classified texture that is safe to transform.")
        ];

        TextureCaps = [1024, 2048, 4096];
        _selectedPresetOption = PresetOptions.Single(option => option.Value == TexturePreset.ClassicHd);
        _selectedScopeOption = ScopeOptions.Single(option => option.Value == AssetScope.WorldOnly);
        var rememberedInstall = _preferences.Read().LastInstallPath;
        if (!string.IsNullOrWhiteSpace(rememberedInstall)
            && Directory.Exists(rememberedInstall)
            && File.Exists(Path.Combine(rememberedInstall, "eqgame.exe")))
        {
            _installPath = rememberedInstall;
            _statusText = "Remembered EverQuest directory • ready to analyze";
        }

        BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsBusy);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, CanAnalyze);
        BuildStagedPackCommand = new AsyncRelayCommand(
            BuildAsync,
            CanBuildStaged);
        InstallLatestPackCommand = new AsyncRelayCommand(InstallLatestPackAsync, CanInstallLatestPack);
        PlayEnhancedCommand = new AsyncRelayCommand(PlayEnhancedAsync, CanPlayEnhanced);
        OpenPreviewGalleryCommand = new RelayCommand(_ => OpenPreviewGallery(), _ => CanOpenPreviewGallery());
        OpenPackLibraryCommand = new RelayCommand(
            _ => PackLibraryRequested?.Invoke(InstallPath),
            _ => !IsBusy && IsClientSignaturePresent);
        OpenPackStorageCommand = new RelayCommand(
            _ => PackStorageRequested?.Invoke(InstallPath),
            _ => !IsBusy && IsClientSignaturePresent);
        OpenNativeGraphicsCommand = new RelayCommand(
            _ => NativeGraphicsRequested?.Invoke(InstallPath),
            _ => !IsBusy && IsClientSignaturePresent);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, CanRestore);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);

        AddLog("INFO", "SpinTexture initialized. Analyze and staged builds never write to the selected client.");
        AddLog("SAFE", "Default profile: Texture HD • World only • 2,048 px • staged output.");
    }

    public IReadOnlyList<PresetOptionViewModel> PresetOptions { get; }
    public IReadOnlyList<ScopeOptionViewModel> ScopeOptions { get; }
    public IReadOnlyList<int> TextureCaps { get; }
    public ObservableCollection<string> Zones { get; } = [];
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand BuildStagedPackCommand { get; }
    public AsyncRelayCommand InstallLatestPackCommand { get; }
    public AsyncRelayCommand PlayEnhancedCommand { get; }
    public RelayCommand OpenPreviewGalleryCommand { get; }
    public RelayCommand OpenPackLibraryCommand { get; }
    public RelayCommand OpenPackStorageCommand { get; }
    public RelayCommand OpenNativeGraphicsCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action<string>? PreviewGalleryRequested;
    public event Action<string>? PackLibraryRequested;
    public event Action<string>? PackStorageRequested;
    public event Action<string>? NativeGraphicsRequested;

    public async Task RefreshAfterPackStorageMoveAsync()
    {
        PreviewManifestPath = null;
        await RefreshPackLibraryStateAsync().ConfigureAwait(true);
        AddLog("SAFE", "Staged-pack storage location updated; future builds and Pack Library actions use the new location.");
    }

    public async Task RefreshPackLibraryStateAsync()
    {
        if (string.IsNullOrWhiteSpace(InstallPath) || !Directory.Exists(InstallPath))
        {
            return;
        }

        try
        {
            HasManagedBackup = _workflow.HasRestorableBackup(InstallPath);
            HasStagedPack = _workflow.HasStagedPack(InstallPath);
            await RefreshInstallHealthAsync(CancellationToken.None, fast: true)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            AddLog("WARN", $"Pack Library state could not be refreshed: {exception.Message}");
        }
    }

    public string InstallPath
    {
        get => _installPath;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _installPath, normalized))
            {
                return;
            }

            ClearAnalysis();
            OnPropertyChanged(nameof(PathStateText));
            OnPropertyChanged(nameof(IsClientSignaturePresent));
            RefreshCommands();
            if (IsClientSignaturePresent)
            {
                _ = RememberInstallPathAsync(normalized);
            }
        }
    }

    public string PathStateText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallPath))
            {
                return "No client selected";
            }

            if (!Directory.Exists(InstallPath))
            {
                return "Folder not found";
            }

            return IsClientSignaturePresent
                ? "EverQuest client signature found"
                : "Folder exists • verify this is the EQL client";
        }
    }

    public bool IsClientSignaturePresent =>
        Directory.Exists(InstallPath) && File.Exists(Path.Combine(InstallPath, "eqgame.exe"));

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetProperty(ref _currentStage, value);
    }

    public string CurrentItem
    {
        get => _currentItem;
        private set => SetProperty(ref _currentItem, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string OverallProgressText
    {
        get => _overallProgressText;
        private set => SetProperty(ref _overallProgressText, value);
    }

    public string ProgressEtaText
    {
        get => _progressEtaText;
        private set => SetProperty(ref _progressEtaText, value);
    }

    public string ProgressCountText
    {
        get => _progressCountText;
        private set => SetProperty(ref _progressCountText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool HasAnalysis
    {
        get => _hasAnalysis;
        private set
        {
            if (SetProperty(ref _hasAnalysis, value))
            {
                OnPropertyChanged(nameof(AnalysisStateText));
                OnPropertyChanged(nameof(IsZoneSelectionEnabled));
                OnPropertyChanged(nameof(ZoneSelectionStatusText));
                RefreshCommands();
            }
        }
    }

    public string AnalysisStateText => HasAnalysis ? "ANALYZED" : "NOT ANALYZED";

    public PresetOptionViewModel SelectedPresetOption
    {
        get => _selectedPresetOption;
        set
        {
            if (value is not null && SetProperty(ref _selectedPresetOption, value))
            {
                UpdateEstimate();
            }
        }
    }

    public ScopeOptionViewModel SelectedScopeOption
    {
        get => _selectedScopeOption;
        set
        {
            if (value is not null && SetProperty(ref _selectedScopeOption, value))
            {
                if (!IsSelectedZoneScope && _selectedZone is not null)
                {
                    _selectedZone = null;
                    OnPropertyChanged(nameof(SelectedZone));
                }

                OnPropertyChanged(nameof(IsSelectedZoneScope));
                OnPropertyChanged(nameof(IsZoneSelectionEnabled));
                OnPropertyChanged(nameof(ZoneSelectionStatusText));
                UpdateEstimate();
                RefreshCommands();
            }
        }
    }

    public bool IsSelectedZoneScope => SelectedScopeOption.Value == AssetScope.SelectedZone;

    // The zone picker doubles as a shortcut: after analysis, selecting any zone
    // switches the scope to Selected zone. This avoids presenting a seemingly
    // broken disabled field when the default scope is World only.
    public bool IsZoneSelectionEnabled => HasAnalysis && Zones.Count > 0;

    public string ZoneSelectionStatusText
    {
        get
        {
            if (!HasAnalysis)
            {
                return "Analyze first to load the available zones.";
            }

            if (Zones.Count == 0)
            {
                return "No likely zone archives were found in this client.";
            }

            if (!IsSelectedZoneScope)
            {
                return "Optional: choosing a zone switches Asset Set to Selected zone.";
            }

            return string.IsNullOrWhiteSpace(SelectedZone)
                ? "Selected zone scope is active - choose one zone to continue."
                : $"Only {SelectedZone} will be included in this build.";
        }
    }

    public string? SelectedZone
    {
        get => _selectedZone;
        set
        {
            if (SetProperty(ref _selectedZone, value))
            {
                if (HasAnalysis && !string.IsNullOrWhiteSpace(value) && !IsSelectedZoneScope)
                {
                    SelectedScopeOption = ScopeOptions.Single(option => option.Value == AssetScope.SelectedZone);
                }
                else
                {
                    UpdateEstimate();
                }

                OnPropertyChanged(nameof(ZoneSelectionStatusText));
                RefreshCommands();
            }
        }
    }

    public int SelectedMaximumDimension
    {
        get => _selectedMaximumDimension;
        set
        {
            if (SetProperty(ref _selectedMaximumDimension, value))
            {
                UpdateEstimate();
            }
        }
    }

    public bool GenerateMipMaps
    {
        get => _generateMipMaps;
        set
        {
            if (SetProperty(ref _generateMipMaps, value))
            {
                UpdateEstimate();
            }
        }
    }

    public bool IsLegalAcknowledged
    {
        get => _isLegalAcknowledged;
        set
        {
            if (SetProperty(ref _isLegalAcknowledged, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool HasManagedBackup
    {
        get => _hasManagedBackup;
        private set
        {
            if (SetProperty(ref _hasManagedBackup, value))
            {
                OnPropertyChanged(nameof(RestoreStateText));
                RefreshCommands();
            }
        }
    }

    public bool HasStagedPack
    {
        get => _hasStagedPack;
        private set
        {
            if (SetProperty(ref _hasStagedPack, value))
            {
                RefreshCommands();
            }
        }
    }

    public string RestoreStateText => HasManagedBackup ? "Verified backup available" : "No managed backup detected";

    public string InstallHealthText
    {
        get => _installHealthText;
        private set => SetProperty(ref _installHealthText, value);
    }

    public string ArchiveCountText => _scanSummary is null ? "—" : _scanSummary.ArchiveCount.ToString("N0");
    public string PackedTextureCountText => _scanSummary is null ? "—" : _scanSummary.PackedTextureCount.ToString("N0");
    public string LooseTextureCountText => _scanSummary is null ? "—" : _scanSummary.LooseTextureCount.ToString("N0");
    public string SourceFootprintText => _scanSummary is null ? "—" : FormatBytes(_scanSummary.SourceBytes);

    public string EstimatedOutputText
    {
        get => _estimatedOutputText;
        private set => SetProperty(ref _estimatedOutputText, value);
    }

    public string EstimatedArchivesText
    {
        get => _estimatedArchivesText;
        private set => SetProperty(ref _estimatedArchivesText, value);
    }

    public string EstimatedTimeText
    {
        get => _estimatedTimeText;
        private set => SetProperty(ref _estimatedTimeText, value);
    }

    public string EstimateBasisText
    {
        get => _estimateBasisText;
        private set => SetProperty(ref _estimateBasisText, value);
    }

    public string HardwarePerformanceText =>
        $"{Environment.ProcessorCount:N0} logical CPU threads detected · up to {Math.Clamp(Environment.ProcessorCount / 4, 1, 4):N0} bounded CPU lanes · one stable Vulkan GPU queue · up to 64 textures per neural batch";

    public string WorkspaceText
    {
        get => _workspaceText;
        private set => SetProperty(ref _workspaceText, value);
    }

    public string? PreviewManifestPath
    {
        get => _previewManifestPath;
        private set
        {
            if (!SetProperty(ref _previewManifestPath, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPreviewGallery));
            OnPropertyChanged(nameof(PreviewStateText));
            RefreshCommands();
        }
    }

    public bool HasPreviewGallery =>
        !string.IsNullOrWhiteSpace(PreviewManifestPath) && File.Exists(PreviewManifestPath);

    public string PreviewStateText => HasPreviewGallery
        ? "Before/after previews ready for review"
        : "Available after a successful staged build";

    private void Browse()
    {
        string? selectedPath = _folderPicker.PickFolder(InstallPath);
        if (selectedPath is null)
        {
            return;
        }

        InstallPath = selectedPath;
        AddLog("INFO", $"Selected client directory: {selectedPath}");
        StatusText = "Directory selected • ready for read-only analysis";
    }

    private async Task RememberInstallPathAsync(string installPath)
    {
        try
        {
            await _preferences.WriteLastInstallPathAsync(installPath).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AddLog("WARN", $"The client path could not be remembered: {exception.Message}");
        }
    }

    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        BeginOperation("ANALYZE", "Reading archive and loose-texture inventory");
        AddLog("SCAN", "Started read-only client analysis.");

        try
        {
            var progress = new Progress<ProgressUpdate>(HandleProgress);
            ScanSummary summary = await _workflow.AnalyzeAsync(InstallPath, progress, cancellationToken);
            await _preferences.WriteLastInstallPathAsync(InstallPath, cancellationToken).ConfigureAwait(true);

            _scanSummary = summary;
            Zones.Clear();
            foreach (string zone in summary.Zones)
            {
                Zones.Add(zone);
            }

            // Leave the quick selector empty until the user intentionally chooses
            // a zone. Selecting one will switch the asset scope automatically.
            SelectedZone = null;
            HasAnalysis = true;
            HasManagedBackup = _workflow.HasRestorableBackup(InstallPath);
            HasStagedPack = _workflow.HasStagedPack(InstallPath);
            await RefreshInstallHealthAsync(cancellationToken, fast: true).ConfigureAwait(true);
            RecoverableStagedBuild? recovery = await _workflow
                .FindRecoverableBuildAsync(InstallPath, cancellationToken)
                .ConfigureAwait(true);
            if (recovery is not null)
            {
                SelectedPresetOption = PresetOptions.Single(option =>
                    option.Value == recovery.Options.Preset);
                SelectedScopeOption = ScopeOptions.Single(option =>
                    option.Value == recovery.Options.Scope);
                SelectedMaximumDimension = recovery.Options.MaximumDimension;
                GenerateMipMaps = recovery.Options.GenerateMipMaps;
                if (recovery.Options.Scope == AssetScope.SelectedZone
                    && !string.IsNullOrWhiteSpace(recovery.Options.SelectedZone))
                {
                    var matchingZone = Zones.FirstOrDefault(zone => zone.Equals(
                        recovery.Options.SelectedZone,
                        StringComparison.OrdinalIgnoreCase));
                    if (matchingZone is not null)
                    {
                        SelectedZone = matchingZone;
                    }
                }

                var recoveryMessage =
                    $"Saved crash recovery found: {recovery.VerifiedArtifacts:N0} of {recovery.TotalArtifacts:N0} artifacts are intact. Build Staged Pack will verify current sources and resume this selection.";
                AddLog("SAFE", recoveryMessage);
                StatusText = recoveryMessage;
                CurrentItem = "Saved staged-build work is ready to verify and resume";
            }
            OnPropertyChanged(nameof(ArchiveCountText));
            OnPropertyChanged(nameof(PackedTextureCountText));
            OnPropertyChanged(nameof(LooseTextureCountText));
            OnPropertyChanged(nameof(SourceFootprintText));
            UpdateEstimate();

            AddLog("DONE", $"Analysis complete: {summary.ArchiveCount:N0} archives, {summary.LooseTextureCount:N0} loose textures, {summary.Zones.Count:N0} likely zones.");
            foreach (string warning in summary.Warnings)
            {
                AddLog("WARN", warning);
            }

            StatusText = "Analysis complete • review scope and output estimate";
            CurrentStage = "ANALYZED";
            CurrentItem = "Ready to build a staged pack";
            if (recovery is not null)
            {
                StatusText =
                    $"Saved crash recovery found: {recovery.VerifiedArtifacts:N0} of {recovery.TotalArtifacts:N0} artifacts are intact. Build Staged Pack will verify current sources and resume this selection.";
                CurrentItem = "Saved staged-build work is ready to verify and resume";
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("WARN", "Analysis canceled. No files were changed.");
            StatusText = "Analysis canceled";
            CurrentStage = "CANCELED";
            throw;
        }
        catch (Exception exception)
        {
            AddLog("ERROR", exception.Message);
            StatusText = "Analysis failed • see activity log";
            CurrentStage = "ERROR";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task BuildAsync(CancellationToken cancellationToken)
    {
        const string operation = "STAGED BUILD";
        BeginOperation(operation, "Preparing immutable staged output");
        BeginStagedBuildProgress();
        AddLog("SAFE", "Started staged-pack workflow. The live client remains immutable.");

        string? previewToOpenAfterBuild = null;

        try
        {
            var options = new UpscaleOptions(
                SelectedPresetOption.Value,
                SelectedScopeOption.Value,
                SelectedMaximumDimension,
                GenerateMipMaps,
                InstallAfterBuild: false,
                IsSelectedZoneScope ? SelectedZone : null);

            TexturePackBuildResult result = await _workflow.BuildAsync(
                InstallPath,
                options,
                new Progress<ProgressUpdate>(HandleProgress),
                cancellationToken);

            AddLog("DONE", $"Staged {result.Report.Statistics.EnhancedTextures:N0} enhanced textures without changing the live client.");
            if (result.Report.Statistics.FallbackTextures > 0)
            {
                AddLog(
                    "SAFE",
                    $"{result.Report.Statistics.FallbackTextures:N0} texture(s) used a validated fallback route because the requested model output did not meet its safety gate.");
            }
            foreach (string warning in result.Report.Statistics.Warnings.Take(12))
            {
                AddLog("WARN", warning);
            }
            StatusText = "Staged pack complete • review previews, then install once";
            WorkspaceText = _workflow.LastBuildDirectory is null
                ? "Managed workspace ready"
                : $"Output: {_workflow.LastBuildDirectory}";
            PreviewManifestPath = !string.IsNullOrWhiteSpace(result.PreviewManifestPath)
                                  && File.Exists(result.PreviewManifestPath)
                ? Path.GetFullPath(result.PreviewManifestPath)
                : null;
            if (HasPreviewGallery)
            {
                AddLog("PREVIEW", "Before/after review images are ready in the staged workspace.");
                previewToOpenAfterBuild = PreviewManifestPath;
            }
            else
            {
                AddLog("WARN", "This build did not produce preview images. The staged texture pack is still available.");
            }

            HasManagedBackup = _workflow.HasRestorableBackup(InstallPath);
            HasStagedPack = _workflow.HasStagedPack(InstallPath);
            await RefreshInstallHealthAsync(cancellationToken).ConfigureAwait(true);
            AddLog("NEXT", "Review the previews, close EverQuest and LaunchPad, then install the staged pack. Installation reuses this output without rerunning AI.");
            _stagedBuildSucceeded = true;
        }
        catch (OperationCanceledException)
        {
            AddLog("WARN", $"{operation} canceled. No live files were changed.");
            StatusText = "Operation canceled safely";
            throw;
        }
        catch (InstallTransactionException exception)
        {
            AddLog("ERROR", exception.Message);
            foreach (Exception rollbackFailure in exception.RollbackFailures.Take(5))
            {
                AddLog("ERROR", $"Recovery detail: {rollbackFailure.Message}");
            }

            HasManagedBackup = _workflow.HasRestorableBackup(InstallPath);
            StatusText = "Recovery required • do not launch EverQuest • use Restore";
            CurrentStage = "RECOVERY REQUIRED";
            CurrentItem = "A verified backup is available; review the activity log";
            WorkspaceText = "Install did not complete cleanly • managed backup retained";
        }
        catch (Exception exception)
        {
            AddLog("ERROR", exception.Message);
            StatusText = "Build failed • the live client remains unchanged";
        }
        finally
        {
            EndStagedBuildProgress();
            EndOperation();
            if (previewToOpenAfterBuild is not null)
            {
                OpenPreviewGallery(previewToOpenAfterBuild);
            }
        }
    }

    private async Task InstallLatestPackAsync(CancellationToken cancellationToken)
    {
        if (!_dialogs.ConfirmApplyLatest())
        {
            AddLog("INFO", "Install Staged Pack canceled at the safety gate.");
            return;
        }

        BeginOperation("INSTALL PACK", "Verifying the staged pack and original client files");
        try
        {
            await RefreshInstallHealthAsync(cancellationToken).ConfigureAwait(true);
            if (_installHealth?.State == InstallHealthState.RevertedToOriginal)
            {
                AddLog("REPAIR", "LaunchPad restored the previous pack. Reusing the verified staged output without rerunning the AI upscaler.");
            }

            await _workflow.ApplyLatestStagedPackAsync(
                InstallPath,
                new Progress<ProgressUpdate>(HandleProgress),
                cancellationToken).ConfigureAwait(true);
            await RefreshInstallHealthAsync(cancellationToken).ConfigureAwait(true);
            if (_installHealth?.State != InstallHealthState.EnhancedActive)
            {
                throw new InvalidOperationException(
                    "The apply transaction completed, but the live enhanced hashes could not be verified.");
            }

            HasManagedBackup = _workflow.HasRestorableBackup(InstallPath);
            try
            {
                string shortcutPath = _enhancedLauncher.CreateDesktopShortcut(InstallPath);
                AddLog("SHORTCUT", $"Created the no-patch play shortcut: {shortcutPath}");
            }
            catch (Exception shortcutFailure)
            {
                AddLog("WARN", $"The pack is installed, but the desktop shortcut could not be created: {shortcutFailure.Message}");
            }

            AddLog("DONE", "Enhanced archives are active and SHA-256 verified. The AI upscaler does not need to run again.");
            AddLog("SAFE", "For normal play, use Play Enhanced EQ or the SpinTexture desktop shortcut. LaunchPad is only needed for real game updates.");
            StatusText = "Enhanced textures ACTIVE • use the SpinTexture play shortcut";
            CurrentStage = "INSTALLED";
            CurrentItem = "One-time install complete • LaunchPad bypass shortcut ready";
        }
        catch (OperationCanceledException)
        {
            AddLog("WARN", "Install Staged Pack canceled safely.");
            StatusText = "Install canceled safely";
            throw;
        }
        catch (Exception exception)
        {
            AddLog("ERROR", exception.Message);
            StatusText = "Pack not installed • close EverQuest and LaunchPad, then try again";
            CurrentStage = "NOT APPLIED";
            CurrentItem = "No unverified pack will be reported as active";
            await RefreshInstallHealthAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PlayEnhancedAsync(CancellationToken cancellationToken)
    {
        BeginOperation("VERIFY + PLAY", "Verifying that the enhanced archives are still active");
        try
        {
            await RefreshInstallHealthAsync(cancellationToken, fast: true).ConfigureAwait(true);
            if (_installHealth?.State != InstallHealthState.EnhancedActive)
            {
                throw new InvalidOperationException(_installHealth?.State switch
                {
                    InstallHealthState.RevertedToOriginal =>
                        "LaunchPad restored the original archives. Close LaunchPad and reinstall the staged pack; the AI upscaler will not run again.",
                    InstallHealthState.MixedOrModified when _installHealth.Entries.Any(entry =>
                        entry.State == InstalledArtifactHealthState.ModifiedOrMissing) =>
                        "Game archives changed outside SpinTexture. Finish the LaunchPad update, then Analyze and rebuild against the patched client.",
                    InstallHealthState.MixedOrModified =>
                        "The pack is partly enhanced and partly original. Use SpinTexture's verified Restore action before installing again.",
                    InstallHealthState.RecoveryRequired =>
                        "The previous install needs recovery. Use Restore before playing.",
                    _ =>
                        "No active enhanced pack was found. Install the staged pack before playing."
                });
            }

            await _enhancedLauncher.LaunchAsync(InstallPath, cancellationToken).ConfigureAwait(true);
            AddLog("PLAY", "Started eqgame.exe with the no-patch option. The verified enhanced archives remain installed.");
            StatusText = "EverQuest started with SpinTexture enhancements active";
            CurrentStage = "PLAYING";
            CurrentItem = "LaunchPad was not started for this session; Direct Play does not check for updates";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddLog("ERROR", exception.Message);
            StatusText = "Enhanced client was not started • see activity log";
            CurrentStage = "NOT STARTED";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!_dialogs.ConfirmRestore())
        {
            AddLog("INFO", "Restore canceled at the confirmation gate.");
            return;
        }

        BeginOperation("RESTORE", "Verifying managed backup");
        try
        {
            await _workflow.RestoreAsync(InstallPath, new Progress<ProgressUpdate>(HandleProgress), cancellationToken);
            HasManagedBackup = _workflow.HasRestorableBackup(InstallPath);
            await RefreshInstallHealthAsync(cancellationToken).ConfigureAwait(true);
            AddLog("DONE", "Original EverQuest archives restored and verified.");
            StatusText = "Restore complete • original files verified";
            CurrentStage = "RESTORED";
            CurrentItem = "Vanilla client files are active";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddLog("ERROR", exception.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    private void BeginOperation(string stage, string message)
    {
        IsBusy = true;
        CurrentStage = stage;
        CurrentItem = message;
        ProgressValue = 0;
        StatusText = message;
        OverallProgressText = "Overall progress";
        ProgressEtaText = string.Empty;
        ProgressCountText = string.Empty;
    }

    private void EndOperation()
    {
        IsBusy = false;
        RefreshCommands();
    }

    private void HandleProgress(ProgressUpdate update)
    {
        CurrentStage = update.Stage.ToUpperInvariant();
        CurrentItem = update.CurrentItem ?? update.Message;
        if (_isStagedBuildOperation)
        {
            HandleStagedBuildProgress(update);
        }
        else
        {
            ProgressValue = update.Percent;
            OverallProgressText = update.Stage;
            ProgressCountText = update.Total > 0
                ? $"{Math.Min(update.Completed, update.Total):N0} of {update.Total:N0}"
                : string.Empty;
        }
        StatusText = update.Message;
        if (!update.IsHeartbeat)
        {
            AddLog("STEP", $"{update.Stage}: {update.Message}");
        }
    }

    private void BeginStagedBuildProgress()
    {
        _isStagedBuildOperation = true;
        _hasEnteredArchiveBuild = false;
        _buildCompletedArtifacts = 0;
        _buildTotalArtifacts = 0;
        _currentArtifactFraction = 0;
        _archiveBuildStartedAt = TimeSpan.Zero;
        _lastBuildActivityAt = TimeSpan.Zero;
        _freshArtifactStartedAt = TimeSpan.Zero;
        _isMeasuringFreshArtifact = false;
        _resumedBuildNeedsFreshEtaSample = false;
        _smoothedSecondsPerArtifact = null;
        _currentBuildPhase = "archive preparation";
        _stagedBuildSucceeded = false;
        _buildStopwatch.Restart();
        _buildProgressTimer.Start();
        ProgressValue = 0;
        OverallProgressText = $"Overall · {SelectedScopeOption.Name}";
        ProgressCountText = "Planning selected archives";
        ProgressEtaText = $"Pre-build estimate: {EstimatedTimeText}";
    }

    private void EndStagedBuildProgress()
    {
        _buildProgressTimer.Stop();
        _buildStopwatch.Stop();
        if (_isStagedBuildOperation && _stagedBuildSucceeded)
        {
            ProgressValue = 100;
            ProgressCountText = _buildTotalArtifacts > 0
                ? $"{_buildTotalArtifacts:N0} of {_buildTotalArtifacts:N0} artifacts"
                : "Complete";
            ProgressEtaText = $"Finished in {FormatDuration(_buildStopwatch.Elapsed)}";
        }
        else if (_isStagedBuildOperation)
        {
            ProgressEtaText = $"Stopped after {FormatDuration(_buildStopwatch.Elapsed)}";
        }

        _isStagedBuildOperation = false;
    }

    private void HandleStagedBuildProgress(ProgressUpdate update)
    {
        _lastBuildActivityAt = _buildStopwatch.Elapsed;
        UpdateCurrentBuildPhase(update);
        if (update.Stage.Equals("Plan", StringComparison.OrdinalIgnoreCase))
        {
            if (!_hasEnteredArchiveBuild)
            {
                ProgressValue = Math.Max(
                    ProgressValue,
                    update.Total <= 0
                        ? 0
                        : Math.Clamp(update.Percent * 0.10, 0, 10));
                ProgressCountText = update.Total > 0
                    ? $"Planning {Math.Min(update.Completed, update.Total):N0} of {update.Total:N0} candidate archives"
                    : "Planning selected archives";
            }

            return;
        }

        if (update.Stage.Equals("Resume", StringComparison.OrdinalIgnoreCase))
        {
            _resumedBuildNeedsFreshEtaSample = update.Completed > 0;
            ProgressCountText = update.Total > 0
                ? $"Recovering {Math.Min(update.Completed, update.Total):N0} of {update.Total:N0} verified artifacts"
                : "Recovering verified artifacts";
            return;
        }

        if (update.Stage.Equals("Build", StringComparison.OrdinalIgnoreCase))
        {
            if (!_hasEnteredArchiveBuild)
            {
                _archiveBuildStartedAt = _buildStopwatch.Elapsed;
            }

            _hasEnteredArchiveBuild = true;
            _buildTotalArtifacts = Math.Max(_buildTotalArtifacts, update.Total);
            var previouslyCompleted = _buildCompletedArtifacts;
            _buildCompletedArtifacts = Math.Max(
                _buildCompletedArtifacts,
                Math.Clamp(update.Completed, 0, Math.Max(0, _buildTotalArtifacts)));
            var resumedArtifact = update.Message.Contains(
                "skipped duplicate upscaling",
                StringComparison.OrdinalIgnoreCase);
            var startingFreshArtifact = update.Message.StartsWith(
                "Rebuilding complete install artifact",
                StringComparison.OrdinalIgnoreCase);
            if (startingFreshArtifact && !_isMeasuringFreshArtifact)
            {
                _freshArtifactStartedAt = _buildStopwatch.Elapsed;
                _isMeasuringFreshArtifact = true;
            }

            if (_buildCompletedArtifacts > previouslyCompleted
                && !resumedArtifact
                && _isMeasuringFreshArtifact)
            {
                var observed = (_buildStopwatch.Elapsed - _freshArtifactStartedAt).TotalSeconds;
                _smoothedSecondsPerArtifact = _smoothedSecondsPerArtifact is null
                    ? observed
                    : (_smoothedSecondsPerArtifact.Value * 0.80) + (observed * 0.20);
                _isMeasuringFreshArtifact = false;
                _resumedBuildNeedsFreshEtaSample = false;
            }
            _currentArtifactFraction = 0;
            UpdateOverallBuildProgress();
            return;
        }

        if (!_hasEnteredArchiveBuild || _buildTotalArtifacts <= 0)
        {
            return;
        }

        if (update.Stage.Equals("Upscale", StringComparison.OrdinalIgnoreCase))
        {
            double nestedFraction = update.Total <= 0
                ? 0
                : Math.Clamp((double)update.Completed / update.Total, 0, 1);
            bool validating = update.Message.StartsWith(
                "Validating enhanced textures",
                StringComparison.OrdinalIgnoreCase);
            var reportedFraction = validating
                ? 0.62 + nestedFraction * 0.33
                : nestedFraction * 0.58;
            // CPU/GPU/encoding sub-batches use different local totals. Their
            // heartbeat must never make the overall artifact bar move back.
            _currentArtifactFraction = Math.Max(
                _currentArtifactFraction,
                reportedFraction);
            UpdateOverallBuildProgress();
        }
    }

    private void UpdateCurrentBuildPhase(ProgressUpdate update)
    {
        if (update.Message.StartsWith("CPU preparation", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "CPU texture preparation";
        }
        else if (update.Message.Contains("Material Detail", StringComparison.OrdinalIgnoreCase)
                 && update.Message.Contains("GPU", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "GPU Material Detail/TTA";
        }
        else if (update.Message.StartsWith("GPU enhancement", StringComparison.OrdinalIgnoreCase)
                 || update.Message.StartsWith("GPU worker", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "GPU enhancement";
        }
        else if (update.Message.Contains("encoding and validating", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "texture encoding and validation";
        }
        else if (update.Message.StartsWith("Validating enhanced textures", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "archive validation";
        }
        else if (update.Message.StartsWith("Preparing safe textures", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "archive texture scan";
        }
        else if (update.Stage.Equals("Resume", StringComparison.OrdinalIgnoreCase))
        {
            _currentBuildPhase = "checkpoint recovery";
        }
    }

    private void UpdateOverallBuildProgress()
    {
        if (_buildTotalArtifacts <= 0)
        {
            ProgressValue = Math.Max(ProgressValue, 10);
            return;
        }

        double artifactProgress = Math.Clamp(
            _buildCompletedArtifacts + _currentArtifactFraction,
            0,
            _buildTotalArtifacts);
        ProgressValue = 10 + artifactProgress / _buildTotalArtifacts * 90;
        int displayArtifact = Math.Min(
            _buildTotalArtifacts,
            _buildCompletedArtifacts + (_buildCompletedArtifacts < _buildTotalArtifacts ? 1 : 0));
        ProgressCountText = _buildCompletedArtifacts >= _buildTotalArtifacts
            ? $"{_buildTotalArtifacts:N0} of {_buildTotalArtifacts:N0} artifacts"
            : $"Artifact {displayArtifact:N0} of {_buildTotalArtifacts:N0}";
        UpdateBuildEtaText();
    }

    private void OnBuildProgressTimerTick(object? sender, EventArgs e)
    {
        if (_isStagedBuildOperation)
        {
            UpdateBuildEtaText();
        }
    }

    private void UpdateBuildEtaText()
    {
        if (!_isStagedBuildOperation)
        {
            return;
        }

        if (!_hasEnteredArchiveBuild || _buildTotalArtifacts <= 0)
        {
            ProgressEtaText = $"Pre-build estimate: {EstimatedTimeText} · elapsed {FormatDuration(_buildStopwatch.Elapsed)}";
            return;
        }

        double completedWork = _buildCompletedArtifacts + _currentArtifactFraction;
        var archiveElapsed = _buildStopwatch.Elapsed - _archiveBuildStartedAt;
        if (_resumedBuildNeedsFreshEtaSample && _smoothedSecondsPerArtifact is null)
        {
            ProgressEtaText = $"Recovered {_buildCompletedArtifacts:N0} verified artifacts · measuring the first remaining artifact · elapsed {FormatDuration(_buildStopwatch.Elapsed)}";
            return;
        }
        if (completedWork < 1 || archiveElapsed < TimeSpan.FromSeconds(8))
        {
            ProgressEtaText = $"Measuring this PC's speed… · elapsed {FormatDuration(_buildStopwatch.Elapsed)}";
            return;
        }

        double remainingWork = Math.Max(0, _buildTotalArtifacts - completedWork);
        var rawSecondsPerArtifact = archiveElapsed.TotalSeconds / completedWork;
        var secondsPerArtifact = _smoothedSecondsPerArtifact is null
            ? rawSecondsPerArtifact
            : _smoothedSecondsPerArtifact.Value;
        var remaining = TimeSpan.FromSeconds(secondsPerArtifact * remainingWork);
        var quietFor = _buildStopwatch.Elapsed - _lastBuildActivityAt;
        var activitySuffix = quietFor >= TimeSpan.FromSeconds(15)
            ? $" · still working in {_currentBuildPhase}; last worker activity {FormatDuration(quietFor)} ago"
            : string.Empty;
        var materialSuffix = SelectedPresetOption.Value == TexturePreset.MaximumDetail
            ? " · Material Detail/TTA"
            : string.Empty;
        if (remaining > TimeSpan.FromSeconds(2))
        {
            ProgressEtaText = $"About {FormatDuration(remaining)} remaining · elapsed {FormatDuration(_buildStopwatch.Elapsed)}{materialSuffix}{activitySuffix}";
            return;
        }
        ProgressEtaText = remaining <= TimeSpan.FromSeconds(2)
            ? $"Finishing… · elapsed {FormatDuration(_buildStopwatch.Elapsed)}"
            : $"About {FormatDuration(remaining)} remaining · elapsed {FormatDuration(_buildStopwatch.Elapsed)}";
    }

    private void Cancel()
    {
        AnalyzeCommand.Cancel();
        BuildStagedPackCommand.Cancel();
        InstallLatestPackCommand.Cancel();
        PlayEnhancedCommand.Cancel();
        RestoreCommand.Cancel();
        StatusText = "Canceling safely…";
    }

    private bool CanAnalyze() => !IsBusy && Directory.Exists(InstallPath);

    private bool CanBuildStaged() =>
        !IsBusy && HasAnalysis && (!IsSelectedZoneScope || !string.IsNullOrWhiteSpace(SelectedZone));

    private bool CanInstallLatestPack() =>
        !IsBusy && HasStagedPack && IsLegalAcknowledged && Directory.Exists(InstallPath);

    private bool CanPlayEnhanced() =>
        !IsBusy
        && _installHealth?.State == InstallHealthState.EnhancedActive
        && Directory.Exists(InstallPath);

    private bool CanOpenPreviewGallery() => !IsBusy && HasPreviewGallery;

    private bool CanRestore() => !IsBusy && HasManagedBackup && Directory.Exists(InstallPath);

    private void OpenPreviewGallery(string? manifestPath = null)
    {
        string? path = manifestPath ?? PreviewManifestPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewManifestPath = null;
            AddLog("WARN", "The preview manifest is no longer available. Build a new staged pack to regenerate it.");
            return;
        }

        PreviewGalleryRequested?.Invoke(path);
    }

    private async Task RefreshInstallHealthAsync(
        CancellationToken cancellationToken,
        bool fast = false)
    {
        InstallHealthState? previousState = _installHealth?.State;
        try
        {
            _installHealth = await (fast
                    ? _workflow.AuditInstallHealthFastAsync(InstallPath, cancellationToken)
                    : _workflow.AuditInstallHealthAsync(InstallPath, cancellationToken))
                .ConfigureAwait(true);
            InstallHealthText = _installHealth.State switch
            {
                InstallHealthState.None => HasStagedPack
                    ? "Staged pack ready • not installed yet"
                    : "No enhanced pack is active",
                InstallHealthState.EnhancedActive => _installHealth.Entries.All(entry => entry.ObservedSha256 is not null)
                    ? $"Enhanced pack ACTIVE • {_installHealth.Entries.Count:N0} archive(s) hash-verified"
                    : $"Enhanced pack ACTIVE • {_installHealth.Entries.Count:N0} archive(s) fast-checked",
                InstallHealthState.RevertedToOriginal =>
                    $"LaunchPad restored {_installHealth.Entries.Count:N0} archive(s) • reinstall staged pack (no AI rebuild)",
                InstallHealthState.MixedOrModified when _installHealth.Entries.Any(entry =>
                    entry.State == InstalledArtifactHealthState.ModifiedOrMissing) =>
                    "Game archives changed outside SpinTexture - finish updating, then Analyze and rebuild",
                InstallHealthState.MixedOrModified =>
                    "Pack is partly enhanced and partly original - use verified Restore",
                InstallHealthState.RecoveryRequired =>
                    "Install recovery is required before playing",
                _ => _installHealth.Summary
            };

            if (previousState != _installHealth.State
                && _installHealth.State == InstallHealthState.RevertedToOriginal)
            {
                AddLog(
                    "WARN",
                    $"LaunchPad replaced {_installHealth.Entries.Count:N0} enhanced archive(s) with originals. The staged pack is still safe and can be reapplied without rebuilding.");
            }

            RefreshCommands();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _installHealth = null;
            InstallHealthText = "Install health could not be verified";
            AddLog("WARN", $"Could not verify the installed pack: {exception.Message}");
            RefreshCommands();
        }
    }

    private void ClearAnalysis()
    {
        _scanSummary = null;
        Zones.Clear();
        _selectedZone = null;
        HasAnalysis = false;
        HasManagedBackup = false;
        HasStagedPack = false;
        _installHealth = null;
        InstallHealthText = "No enhanced pack is active";
        PreviewManifestPath = null;
        EstimatedOutputText = "Analyze to estimate";
        EstimatedArchivesText = "—";
        EstimatedTimeText = "—";
        EstimateBasisText = "Analyze the client for a hardware-aware forecast.";
        WorkspaceText = "Staged workspace • live client remains unchanged";
        OnPropertyChanged(nameof(SelectedZone));
        OnPropertyChanged(nameof(IsZoneSelectionEnabled));
        OnPropertyChanged(nameof(ZoneSelectionStatusText));
        OnPropertyChanged(nameof(ArchiveCountText));
        OnPropertyChanged(nameof(PackedTextureCountText));
        OnPropertyChanged(nameof(LooseTextureCountText));
        OnPropertyChanged(nameof(SourceFootprintText));
    }

    private void UpdateEstimate()
    {
        if (_scanSummary is null)
        {
            return;
        }

        var estimateOptions = new UpscaleOptions(
            SelectedPresetOption.Value,
            SelectedScopeOption.Value,
            SelectedMaximumDimension,
            GenerateMipMaps,
            InstallAfterBuild: false,
            IsSelectedZoneScope ? SelectedZone : null);
        if (TryUpdateEstimateFromHistory(estimateOptions))
        {
            return;
        }

        double byteScopeFactor = SelectedScopeOption.Value switch
        {
            AssetScope.SelectedZone => 0.018,
            AssetScope.WorldOnly => 0.34,
            AssetScope.CharactersAndEquipmentOnly => 0.60,
            AssetScope.WorldCharactersAndEquipment => 0.78,
            AssetScope.AllSafeTextures => 1.0,
            AssetScope.SpellEffectsOnly => 0.035,
            _ => 0.34
        };

        double presetFactor = SelectedPresetOption.Value switch
        {
            TexturePreset.Faithful => 1.35,
            TexturePreset.ClassicHd => 1.55,
            TexturePreset.MaximumDetail => 2.55,
            TexturePreset.Illustrated => 1.70,
            TexturePreset.RusticPainted => 1.82,
            _ => 1.55
        };

        double capFactor = SelectedMaximumDimension switch
        {
            <= 1024 => 0.58,
            <= 2048 => 1.0,
            _ => 2.85
        };

        double mipFactor = GenerateMipMaps ? 1.18 : 1.0;
        long estimatedBytes = (long)Math.Max(
            24 * 1024 * 1024,
            _scanSummary.SourceBytes * byteScopeFactor * presetFactor * capFactor * mipFactor);
        int archiveCount;
        try
        {
            archiveCount = Math.Max(
                1,
                TexturePackWorkflow.ResolveSelectedArchives(
                    InstallPath,
                    estimateOptions).Count);
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            archiveCount = Math.Max(
                1,
                (int)Math.Round(_scanSummary.ArchiveCount * byteScopeFactor));
        }
        double minutesPerArchive = SelectedPresetOption.Value switch
        {
            TexturePreset.Faithful => 0.22,
            TexturePreset.ClassicHd => 0.18,
            // Material Detail enables eight-view TTA. Field evidence from a
            // 1,872-artifact combined build measured roughly 1.8 minutes per
            // artifact, so the no-history forecast must not resemble a normal
            // one-pass model estimate.
            TexturePreset.MaximumDetail => 1.45,
            TexturePreset.Illustrated => 0.26,
            TexturePreset.RusticPainted => 0.29,
            _ => 0.18
        };
        double minutes = Math.Max(1, archiveCount * minutesPerArchive);

        EstimatedOutputText = $"≈ {FormatBytes(estimatedBytes)}";
        EstimatedArchivesText = $"≈ {archiveCount:N0} archives";
        EstimatedTimeText = minutes < 60
            ? $"~{Math.Ceiling(minutes):N0}–{Math.Ceiling(minutes * 1.8):N0} min"
            : $"~{minutes / 60:0.0}–{minutes * 1.8 / 60:0.0} hr";
        EstimateBasisText = "First-run planning range from the selected archive count and preset cost; actual GPU speed and texture dimensions can move it.";
    }

    private bool TryUpdateEstimateFromHistory(UpscaleOptions options)
    {
        HistoricalBuildEstimate? estimate;
        try
        {
            estimate = StagedBuildEstimateHistory.FindLatest(
                WorkspaceLocator.ForInstall(InstallPath),
                options);
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }

        if (estimate is null)
        {
            return false;
        }

        EstimatedOutputText = $"~ {FormatBytes(estimate.StagedBytes)} (prior build)";
        EstimatedArchivesText = $"{estimate.SelectedArchives:N0} archives (prior build)";

        double minutes;
        string basis;
        if (estimate.Duration is { } measuredDuration)
        {
            minutes = Math.Max(1, measuredDuration.TotalMinutes);
            basis = "measured";
        }
        else
        {
            double minutesPerArchive = options.Preset switch
            {
                TexturePreset.Faithful => 0.22,
                TexturePreset.ClassicHd => 0.18,
            TexturePreset.MaximumDetail => 1.45,
                TexturePreset.Illustrated => 0.26,
                TexturePreset.RusticPainted => 0.29,
                _ => 0.18
            };
            minutes = Math.Max(1, estimate.SelectedArchives * minutesPerArchive);
            basis = "prior pack size";
        }

        double lowerMinutes = minutes * 0.9;
        double upperMinutes = minutes * 1.2;
        EstimatedTimeText = upperMinutes < 60
            ? $"~{Math.Ceiling(lowerMinutes):N0}-{Math.Ceiling(upperMinutes):N0} min ({basis})"
            : $"~{lowerMinutes / 60:0.0}-{upperMinutes / 60:0.0} hr ({basis})";
        EstimateBasisText = estimate.Duration is not null
            ? "Calibrated from your latest completed build with this exact scope, style, cap, and mip setting."
            : "Calibrated from the output size and archive count of your latest matching staged pack.";
        return true;
    }

    private void AddLog(string level, string message)
    {
        LogEntries.Add(new LogEntryViewModel(
            DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            level,
            message));

        while (LogEntries.Count > 250)
        {
            LogEntries.RemoveAt(0);
        }
    }

    private void RefreshCommands()
    {
        BrowseCommand.RaiseCanExecuteChanged();
        AnalyzeCommand.RaiseCanExecuteChanged();
        BuildStagedPackCommand.RaiseCanExecuteChanged();
        InstallLatestPackCommand.RaiseCanExecuteChanged();
        PlayEnhancedCommand.RaiseCanExecuteChanged();
        OpenPreviewGalleryCommand.RaiseCanExecuteChanged();
        OpenPackLibraryCommand.RaiseCanExecuteChanged();
        OpenPackStorageCommand.RaiseCanExecuteChanged();
        OpenNativeGraphicsCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:N0}h {duration.Minutes:D2}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes:N0}m {duration.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds):N0}s";
    }

    public void Dispose()
    {
        _buildProgressTimer.Stop();
        _buildProgressTimer.Tick -= OnBuildProgressTimerTick;
        AnalyzeCommand.Dispose();
        BuildStagedPackCommand.Dispose();
        InstallLatestPackCommand.Dispose();
        PlayEnhancedCommand.Dispose();
        RestoreCommand.Dispose();
    }
}
