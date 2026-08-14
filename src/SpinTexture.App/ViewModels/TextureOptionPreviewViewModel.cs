using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using SpinTexture.App.Services;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.App.ViewModels;

/// <summary>
/// Owns the opt-in, cancellable live preview lifecycle. The preview never
/// starts while collapsed and every option change invalidates older imagery
/// before a debounced replacement is generated.
/// </summary>
public sealed class TextureOptionPreviewViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan OptionChangeDebounce = TimeSpan.FromMilliseconds(400);
    private readonly ITextureWorkflowService workflow;
    private readonly ObservableCollection<TextureOptionPreviewSampleViewModel> samples = [];
    private readonly ReadOnlyObservableCollection<TextureOptionPreviewSampleViewModel> readonlySamples;
    private CancellationTokenSource? refreshCancellation;
    private PreviewRequestState? request;
    private TextureOptionPreviewSampleViewModel? selectedSample;
    private long requestVersion;
    private bool isExpanded;
    private bool isReady;
    private bool isLoading;
    private bool isSuspended;
    private bool hasGenerated;
    private bool isDisposed;
    private Task activeRefresh = Task.CompletedTask;
    private string statusText = "Analyze a valid EverQuest client to generate a real preview.";
    private string progressText = "Preview generation is idle";
    private string errorText = string.Empty;
    private double revealPercent = 50d;
    private ulong sampleSeed = CreateSampleSeed();

    public TextureOptionPreviewViewModel(ITextureWorkflowService workflow)
    {
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        readonlySamples = new ReadOnlyObservableCollection<TextureOptionPreviewSampleViewModel>(samples);
        ToggleExpandedCommand = new RelayCommand(
            _ => ToggleExpanded(),
            _ => IsExpanded || IsReady);
        RefreshCommand = new RelayCommand(
            _ => ScheduleRefresh(TimeSpan.Zero),
            _ => IsExpanded && IsReady && !IsSuspended && !IsLoading);
        NewSamplesCommand = new RelayCommand(
            _ => SelectNewSamples(),
            _ => IsExpanded && IsReady && !IsSuspended && !IsLoading);
        ShowOriginalCommand = new RelayCommand(_ => RevealPercent = 0d);
        ShowSplitCommand = new RelayCommand(_ => RevealPercent = 50d);
        ShowEnhancedCommand = new RelayCommand(_ => RevealPercent = 100d);
    }

    public ReadOnlyObservableCollection<TextureOptionPreviewSampleViewModel> Samples => readonlySamples;

    public RelayCommand ToggleExpandedCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand NewSamplesCommand { get; }
    public RelayCommand ShowOriginalCommand { get; }
    public RelayCommand ShowSplitCommand { get; }
    public RelayCommand ShowEnhancedCommand { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        private set
        {
            if (!SetProperty(ref isExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(ToggleButtonText));
            OnPropertyChanged(nameof(ShowExpandedHint));
            ToggleExpandedCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            NewSamplesCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsCollapsed => !IsExpanded;

    public bool IsReady
    {
        get => isReady;
        private set
        {
            if (!SetProperty(ref isReady, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ToggleButtonText));
            ToggleExpandedCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            NewSamplesCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (!SetProperty(ref isLoading, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowExpandedHint));
            RefreshCommand.RaiseCanExecuteChanged();
            NewSamplesCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSuspended
    {
        get => isSuspended;
        private set
        {
            if (SetProperty(ref isSuspended, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                NewSamplesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasContent => !IsLoading && !HasError && samples.Count > 0;
    public bool HasError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorText);
    public bool ShowEmptyState => !IsLoading && !HasError && hasGenerated && samples.Count == 0;
    public bool ShowExpandedHint => IsExpanded && !IsLoading && !HasError && !hasGenerated && samples.Count == 0;

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string ProgressText
    {
        get => progressText;
        private set => SetProperty(ref progressText, value);
    }

    public string ErrorText
    {
        get => errorText;
        private set
        {
            if (SetProperty(ref errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasContent));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowExpandedHint));
            }
        }
    }

    public string ToggleButtonText => IsExpanded
        ? "Hide live preview"
        : IsReady
            ? "Preview these settings"
            : "Analyze to enable preview";

    public string ProfileChipText => request is null
        ? "Current profile"
        : request.Options.Preset == TexturePreset.Illustrated
            ? $"{PresetDisplayName(request.Options.Preset)} / {ThemeDisplayName(request.Options.PaintedTheme)}"
            : PresetDisplayName(request.Options.Preset);

    public string CapChipText => request is null
        ? "Output ceiling"
        : request.Options.MaximumDimension >= 4096
            ? "4K ceiling"
            : request.Options.MaximumDimension >= 2048
                ? "2K ceiling"
                : "1K ceiling";

    public string MipChipText => request?.Options.GenerateMipMaps == true
        ? "Mip chains on"
        : "Top level only";

    public TextureOptionPreviewSampleViewModel? SelectedSample
    {
        get => selectedSample;
        set
        {
            if (!SetProperty(ref selectedSample, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedFallback));
            OnPropertyChanged(nameof(SizingNote));
            OnPropertyChanged(nameof(MipNote));
        }
    }

    public bool HasSelectedFallback => SelectedSample?.UsedFallback == true;

    public double RevealPercent
    {
        get => revealPercent;
        set
        {
            double normalized = Math.Clamp(value, 0d, 100d);
            if (!SetProperty(ref revealPercent, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsOriginalView));
            OnPropertyChanged(nameof(IsSplitView));
            OnPropertyChanged(nameof(IsEnhancedView));
            OnPropertyChanged(nameof(RevealAccessibleText));
        }
    }

    public bool IsOriginalView => RevealPercent <= 0.5d;
    public bool IsEnhancedView => RevealPercent >= 99.5d;
    public bool IsSplitView => !IsOriginalView && !IsEnhancedView;
    public string RevealAccessibleText => IsOriginalView
        ? "Original only"
        : IsEnhancedView
            ? "Enhanced only"
            : $"Enhanced reveal {RevealPercent:0} percent";

    public string SizingNote
    {
        get
        {
            if (request is null || SelectedSample is null)
            {
                return "The selected size is a ceiling, not forced enlargement.";
            }

            if (request.Options.MaximumDimension >= 4096 && SelectedSample.IsSameAs2048)
            {
                return "This source reaches the same top level at 2K and 4K. A 4K ceiling does not enlarge texture data that has already reached its meaningful up-to-4x result.";
            }

            return $"{CapChipText} is a limit, not a target. This source produces {SelectedSample.ResultDimensionsText} while preserving its aspect ratio.";
        }
    }

    public string MipNote
    {
        get
        {
            if (request is null || SelectedSample is null)
            {
                return "Mip settings do not alter the top-level image shown here.";
            }

            if (!request.Options.GenerateMipMaps)
            {
                return "Mip generation is off. The top-level preview is unchanged; only smaller in-game distance levels are omitted.";
            }

            return SelectedSample.ResultMipCount > 1
                ? $"This result has {SelectedSample.ResultMipCount:N0} mip levels. The preview shows only the top level; smaller levels reduce distance shimmer in game."
                : "The preview shows the top level. Legacy safety kept this texture top-level only even though mip generation is enabled.";
        }
    }

    /// <summary>
    /// Supplies the latest selection. Calling this never starts work while the
    /// panel is collapsed or suspended.
    /// </summary>
    public void UpdateSelection(
        string installPath,
        UpscaleOptions options,
        ScanSummary? analysis,
        bool hasValidClient,
        bool hasAnalysis)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        var next = new PreviewRequestState(installPath, options, analysis);
        bool selectionChanged = request is null || !request.Equals(next);
        request = next;

        IsReady = hasValidClient && hasAnalysis && analysis is not null;
        OnPropertyChanged(nameof(ProfileChipText));
        OnPropertyChanged(nameof(CapChipText));
        OnPropertyChanged(nameof(MipChipText));
        OnPropertyChanged(nameof(SizingNote));
        OnPropertyChanged(nameof(MipNote));

        if (!IsReady)
        {
            CancelRefresh();
            ClearResults();
            StatusText = hasValidClient
                ? "Run the read-only analysis first so SpinTexture can choose real, representative source textures."
                : "Choose a valid EverQuest Legends directory to preview its actual textures.";
            return;
        }

        StatusText = IsExpanded
            ? "Preview follows the currently selected profile, ceiling, and mip setting."
            : "Generate three representative comparisons from this client without building a pack.";

        if (!selectionChanged || !IsExpanded)
        {
            return;
        }

        ClearResults();
        if (IsSuspended)
        {
            StatusText = "Live preview is paused while another workflow is running.";
            return;
        }

        ScheduleRefresh(OptionChangeDebounce);
    }

    public void SetSuspended(bool suspended, string? reason = null)
    {
        if (suspended)
        {
            IsSuspended = true;
            CancelRefresh();
            IsLoading = false;
            if (IsExpanded)
            {
                StatusText = string.IsNullOrWhiteSpace(reason)
                    ? "Live preview is paused while the active workflow finishes."
                    : reason;
            }

            return;
        }

        if (!IsSuspended)
        {
            return;
        }

        IsSuspended = false;

        if (IsExpanded && IsReady)
        {
            StatusText = "Refreshing the preview for the current settings.";
            ScheduleRefresh(OptionChangeDebounce);
        }
    }

    internal void PrepareLayoutSmokeState()
    {
        CancelRefresh();
        IsExpanded = true;
        IsLoading = false;
        hasGenerated = true;
        ErrorText = string.Empty;
        samples.Clear();
        samples.Add(new TextureOptionPreviewSampleViewModel(new TextureOptionPreviewSample(
            "layout-smoke",
            "WORLD SURFACE",
            "Stone wall",
            "zone.s3d",
            "wall01.dds",
            string.Empty,
            string.Empty,
            TexturePreset.ClassicHd,
            TexturePreset.ClassicHd,
            PaintedTheme.ClassicPainted,
            PaintedTheme.ClassicPainted,
            "Layout smoke route",
            UsedFallback: false,
            SourceWidth: 256,
            SourceHeight: 256,
            ResultWidth: 1024,
            ResultHeight: 1024,
            ResultMipCount: 11,
            IsSameAs2048: true,
            TextureOptionPreviewSourceProvenance.ClientSource)));
        SelectedSample = samples[0];
        RevealPercent = 35d;
        OnStateChanged();
    }

    private void ToggleExpanded()
    {
        if (IsExpanded)
        {
            IsExpanded = false;
            CancelRefresh();
            IsLoading = false;
            StatusText = "Live preview is closed; option changes will not start preview work.";
            return;
        }

        if (!IsReady)
        {
            return;
        }

        IsExpanded = true;
        StatusText = "Generating real comparisons from the selected client.";
        ScheduleRefresh(TimeSpan.Zero);
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        if (!IsExpanded || !IsReady || IsSuspended || request is null)
        {
            return;
        }

        CancelRefresh();
        ClearResults();
        IsLoading = true;
        ProgressText = delay > TimeSpan.Zero
            ? "Updating after the settings settle..."
            : "Selecting representative source textures...";

        long version = ++requestVersion;
        ulong pendingSampleSeed = sampleSeed;
        var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        Task nextRefresh = RefreshAsync(
            request,
            pendingSampleSeed,
            delay,
            version,
            cancellation);
        activeRefresh = Task.WhenAll(activeRefresh, nextRefresh);
    }

    public async Task WaitForIdleAsync()
    {
        Task pending = activeRefresh;
        try
        {
            await pending.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected when a full workflow takes priority.
        }
    }

    private async Task RefreshAsync(
        PreviewRequestState pendingRequest,
        ulong pendingSampleSeed,
        TimeSpan delay,
        long version,
        CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var progress = new Progress<ProgressUpdate>(update =>
            {
                if (version == requestVersion && !cancellationToken.IsCancellationRequested)
                {
                    ProgressText = string.IsNullOrWhiteSpace(update.CurrentItem)
                        ? update.Message
                        : update.CurrentItem;
                }
            });

            TextureOptionPreviewResult result = await workflow.GenerateOptionPreviewAsync(
                pendingRequest.InstallPath,
                pendingRequest.Options,
                pendingRequest.Analysis,
                pendingSampleSeed,
                progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            TextureOptionPreviewSampleViewModel[] loadedSamples = await Task.Run(
                () => result.Samples
                    .Take(3)
                    .Select(sample => new TextureOptionPreviewSampleViewModel(sample))
                    .ToArray(),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (version != requestVersion || !IsExpanded || IsSuspended)
            {
                return;
            }

            samples.Clear();
            foreach (TextureOptionPreviewSampleViewModel sample in loadedSamples)
            {
                samples.Add(sample);
            }

            SelectedSample = samples.FirstOrDefault();
            hasGenerated = true;
            ErrorText = string.Empty;
            ProgressText = samples.Count == 0
                ? "No safe representative images were found"
                : $"{samples.Count:N0} real comparison{(samples.Count == 1 ? string.Empty : "s")} ready";
            StatusText = samples.Count == 0
                ? "No color-safe samples matched this scope. Try another asset set after analysis."
                : $"Generated for {PresetDisplayName(result.RequestedPreset)} from this client; no staged pack was built.";
            IsLoading = false;
            OnStateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (version == requestVersion)
            {
                IsLoading = false;
            }
        }
        catch (Exception exception)
        {
            if (version != requestVersion)
            {
                return;
            }

            hasGenerated = false;
            IsLoading = false;
            ErrorText = exception.Message;
            StatusText = "The preview could not be generated. The client and staged packs were not changed.";
            ProgressText = "Preview unavailable";
            OnStateChanged();
        }
        finally
        {
            if (ReferenceEquals(refreshCancellation, cancellation))
            {
                refreshCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ClearResults()
    {
        samples.Clear();
        SelectedSample = null;
        hasGenerated = false;
        ErrorText = string.Empty;
        OnStateChanged();
    }

    private void CancelRefresh()
    {
        ++requestVersion;
        CancellationTokenSource? previous = refreshCancellation;
        refreshCancellation = null;
        previous?.Cancel();
    }

    private void SelectNewSamples()
    {
        sampleSeed = sampleSeed == ulong.MaxValue ? 0UL : sampleSeed + 1UL;
        StatusText = "Choosing another varied set of safe textures from this asset selection.";
        ScheduleRefresh(TimeSpan.Zero);
    }

    private static ulong CreateSampleSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowExpandedHint));
    }

    private static string PresetDisplayName(TexturePreset preset) => preset switch
    {
        TexturePreset.Faithful => "Original Clarity",
        TexturePreset.ClassicHd => "Texture HD",
        TexturePreset.MaximumDetail => "Material Detail",
        TexturePreset.Illustrated => "Graphic Painted Fantasy",
        TexturePreset.RusticPainted => "Rustic Painted Fantasy",
        _ => preset.ToString()
    };

    internal static string ThemeDisplayName(PaintedTheme theme) => theme switch
    {
        PaintedTheme.ZoneAware => "Follow each zone",
        PaintedTheme.ClassicPainted => "Classic painted",
        PaintedTheme.LightStorybook => "Light storybook",
        PaintedTheme.DarkGothic => "Dark gothic",
        PaintedTheme.ComicInk => "Comic ink",
        _ => theme.ToString()
    };

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        CancelRefresh();
    }

    private sealed record PreviewRequestState(
        string InstallPath,
        UpscaleOptions Options,
        ScanSummary? Analysis);
}

public sealed class TextureOptionPreviewSampleViewModel
{
    public TextureOptionPreviewSampleViewModel(TextureOptionPreviewSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Title = string.IsNullOrWhiteSpace(sample.Title) ? sample.LogicalName : sample.Title;
        Category = string.IsNullOrWhiteSpace(sample.Category) ? "Texture sample" : sample.Category;
        LogicalName = sample.LogicalName;
        ContainerRelativePath = sample.ContainerRelativePath;
        ProcessingRoute = sample.ProcessingRoute;
        RequestedPreset = sample.RequestedPreset;
        ResolvedPreset = sample.ResolvedPreset;
        ActualPreset = sample.ActualPreset;
        RequestedTheme = sample.RequestedTheme;
        ResolvedTheme = sample.ResolvedTheme;
        UsedFallback = sample.UsedFallback;
        SourceWidth = sample.SourceWidth;
        SourceHeight = sample.SourceHeight;
        ResultWidth = sample.ResultWidth;
        ResultHeight = sample.ResultHeight;
        ResultMipCount = sample.ResultMipCount;
        IsSameAs2048 = sample.IsSameAs2048;
        SourceProvenance = sample.SourceProvenance;
        OriginalImage = PreviewGalleryViewModel.LoadBitmap(sample.OriginalImagePath, 1200);
        EnhancedImage = PreviewGalleryViewModel.LoadBitmap(sample.EnhancedImagePath, 1200);
        Thumbnail = PreviewGalleryViewModel.LoadBitmap(sample.EnhancedImagePath, 180)
                    ?? PreviewGalleryViewModel.LoadBitmap(sample.OriginalImagePath, 180);
    }

    public string Title { get; }
    public string Category { get; }
    public string LogicalName { get; }
    public string ContainerRelativePath { get; }
    public string ProcessingRoute { get; }
    public TexturePreset RequestedPreset { get; }
    public TexturePreset ResolvedPreset { get; }
    public TexturePreset ActualPreset { get; }
    public PaintedTheme RequestedTheme { get; }
    public PaintedTheme ResolvedTheme { get; }
    public bool UsedFallback { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int ResultWidth { get; }
    public int ResultHeight { get; }
    public int ResultMipCount { get; }
    public bool IsSameAs2048 { get; }
    public TextureOptionPreviewSourceProvenance SourceProvenance { get; }
    public BitmapSource? OriginalImage { get; }
    public BitmapSource? EnhancedImage { get; }
    public BitmapSource? Thumbnail { get; }

    public string SourceDimensionsText => $"{SourceWidth:N0} x {SourceHeight:N0}";
    public string ResultDimensionsText => $"{ResultWidth:N0} x {ResultHeight:N0}";
    public string ProvenanceText
    {
        get
        {
            string source = SourceProvenance == TextureOptionPreviewSourceProvenance.ManagedVerifiedOriginal
                ? "verified managed original"
                : "selected client";
            string location = string.IsNullOrWhiteSpace(ContainerRelativePath)
                ? LogicalName
                : $"{ContainerRelativePath}  /  {LogicalName}";
            return $"{source}  /  {location}";
        }
    }
    public string EffectiveRouteText => UsedFallback
        ? $"Fallback: {PresetDisplayName(ActualPreset)} - {ProcessingRoute}"
        : ProcessingRoute;
    public string EffectiveProfileText => RequestedPreset == ActualPreset
        ? PresetDisplayName(ActualPreset)
        : $"Requested {PresetDisplayName(RequestedPreset)} / actual pixels {PresetDisplayName(ActualPreset)}";
    public string ThemeText => DescribeTheme(
        RequestedPreset,
        ActualPreset,
        RequestedTheme,
        ResolvedTheme,
        UsedFallback);

    internal static string DescribeTheme(
        TexturePreset requestedPreset,
        TexturePreset actualPreset,
        PaintedTheme requestedTheme,
        PaintedTheme resolvedTheme,
        bool usedFallback) => actualPreset switch
    {
        TexturePreset.Illustrated =>
            requestedTheme == PaintedTheme.ZoneAware
                ? $"Theme: Follow each zone -> {TextureOptionPreviewViewModel.ThemeDisplayName(resolvedTheme)}"
                : $"Theme: {TextureOptionPreviewViewModel.ThemeDisplayName(resolvedTheme)}",
        TexturePreset.RusticPainted =>
            "Rustic painted grading applied",
        _ when usedFallback
               && requestedPreset is TexturePreset.Illustrated or TexturePreset.RusticPainted =>
            "Painted theme not applied - source palette retained",
        _ => "Source palette retained"
    };

    internal static void ValidateThemeDescriptionSmoke()
    {
        if (!DescribeTheme(
                TexturePreset.Illustrated,
                TexturePreset.Illustrated,
                PaintedTheme.DarkGothic,
                PaintedTheme.DarkGothic,
                usedFallback: false)
            .Equals("Theme: Dark gothic", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The live preview did not report the illustrated theme that produced its pixels.");
        }

        if (!DescribeTheme(
                TexturePreset.RusticPainted,
                TexturePreset.RusticPainted,
                PaintedTheme.ClassicPainted,
                PaintedTheme.ClassicPainted,
                usedFallback: false)
            .Equals("Rustic painted grading applied", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The live preview mislabeled the rustic painted route.");
        }

        if (!DescribeTheme(
                TexturePreset.Illustrated,
                TexturePreset.Faithful,
                PaintedTheme.DarkGothic,
                PaintedTheme.DarkGothic,
                usedFallback: true)
            .Equals("Painted theme not applied - source palette retained", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The live preview claimed a painted theme after a faithful fallback.");
        }

        if (!DescribeTheme(
                TexturePreset.Illustrated,
                TexturePreset.Illustrated,
                PaintedTheme.ZoneAware,
                PaintedTheme.LightStorybook,
                usedFallback: false)
            .Equals("Theme: Follow each zone -> Light storybook", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The live preview hid the concrete theme selected by Follow each zone.");
        }
    }

    public string TabStatusText => UsedFallback
        ? "Original Clarity fallback"
        : ActualPreset == TexturePreset.Illustrated
            ? RequestedTheme == PaintedTheme.ZoneAware
                ? $"Follow zone -> {TextureOptionPreviewViewModel.ThemeDisplayName(ResolvedTheme)}"
                : TextureOptionPreviewViewModel.ThemeDisplayName(ResolvedTheme)
            : PresetDisplayName(ActualPreset);

    private static string PresetDisplayName(TexturePreset preset) => preset switch
    {
        TexturePreset.Faithful => "Original Clarity",
        TexturePreset.ClassicHd => "Texture HD",
        TexturePreset.MaximumDetail => "Material Detail",
        TexturePreset.Illustrated => "Graphic Painted Fantasy",
        TexturePreset.RusticPainted => "Rustic Painted Fantasy",
        _ => preset.ToString()
    };
}
