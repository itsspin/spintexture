using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpinTexture.App.Services;
using SpinTexture.App.ViewModels;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private PreviewGalleryWindow? _previewGalleryView;
    private StagedPackLibraryWindow? _packLibraryView;
    private PackStorageSettingsWindow? _packStorageView;
    private NativeGraphicsWindow? _nativeGraphicsView;
    private Task _packLibraryCloseRefreshTask = Task.CompletedTask;
    private bool _previewReturnsToPacks;
    private string? _viewInstallPath;
    private bool _shutdownDrainComplete;
    private bool _shutdownDrainRunning;
    private bool _closedCleanupComplete;
    private bool _isDraggingLiveComparison;

    internal bool CanStartUpdate => !_viewModel.IsBusy && CanLeaveCurrentSection();

    internal void RunLivePreviewLayoutSmoke(Size viewport)
    {
        TextureOptionPreviewSampleViewModel.ValidateThemeDescriptionSmoke();
        _viewModel.OptionPreview.PrepareLayoutSmokeState();
        _viewModel.OptionPreview.ShowOriginalCommand.Execute(null);
        if (!_viewModel.OptionPreview.IsOriginalView)
        {
            throw new InvalidDataException("The live preview Original shortcut did not select the original image.");
        }

        _viewModel.OptionPreview.ShowEnhancedCommand.Execute(null);
        if (!_viewModel.OptionPreview.IsEnhancedView)
        {
            throw new InvalidDataException("The live preview Enhanced shortcut did not select the enhanced image.");
        }

        _viewModel.OptionPreview.RevealPercent = 35d;
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Measure(viewport);
        Arrange(new Rect(new Point(0, 0), viewport));
        UpdateLayout();

        LivePreviewCard.BringIntoView();
        BuildPage.ScrollToVerticalOffset(Math.Min(
            BuildPage.ScrollableHeight,
            Math.Max(0d, BuildPage.VerticalOffset)));
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        UpdateLayout();

        if (LiveComparisonSurface.ActualWidth < 240d)
        {
            // A headless single-file startup can defer layout for content below
            // the current ScrollViewer viewport. Measure the named card at the
            // real center-column width so the binding smoke stays deterministic.
            double cardWidth = Math.Max(
                400d,
                viewport.Width - 48d - 310d - 338d - 28d);
            LivePreviewCard.Measure(new Size(cardWidth, double.PositiveInfinity));
            LivePreviewCard.Arrange(new Rect(
                0d,
                0d,
                cardWidth,
                LivePreviewCard.DesiredSize.Height));
            LivePreviewCard.UpdateLayout();
        }

        if (LiveComparisonSurface.ActualWidth < 240d)
        {
            throw new InvalidDataException("The live preview comparison surface collapsed below its usable minimum width.");
        }

        if (LiveComparisonSurface.ActualHeight < 400d)
        {
            throw new InvalidDataException("The live preview comparison surface did not retain its larger inspection area.");
        }

        if (ComparisonViewbox.ActualWidth <= 0d
            || ComparisonViewbox.ActualHeight <= 0d
            || ComparisonImageStage.ActualWidth <= 0d
            || ComparisonImageStage.ActualHeight <= 0d
            || !ReferenceEquals(VisualTreeHelper.GetParent(OriginalPreviewImage), ComparisonImageStage)
            || !ReferenceEquals(VisualTreeHelper.GetParent(EnhancedPreviewImage), ComparisonImageStage)
            || OriginalPreviewImage.Stretch != Stretch.Fill
            || EnhancedPreviewImage.Stretch != Stretch.Fill
            || !double.IsNaN(OriginalPreviewImage.Width)
            || !double.IsNaN(EnhancedPreviewImage.Width))
        {
            throw new InvalidDataException("The original and enhanced preview images do not share one layout slot.");
        }

        if (EnhancedPreviewImage.Clip is not RectangleGeometry revealClip)
        {
            throw new InvalidDataException("The live preview enhanced image is missing its reveal clip.");
        }

        double expectedRevealWidth = ComparisonImageStage.ActualWidth * 0.35d;
        if (Math.Abs(revealClip.Rect.Width - expectedRevealWidth) > 2d
            || Math.Abs(revealClip.Rect.Height - ComparisonImageStage.ActualHeight) > 2d)
        {
            throw new InvalidDataException("The live preview enhanced overlay clip binding is invalid.");
        }

        double dividerPosition = Canvas.GetLeft(RevealDivider);
        double expectedDividerPosition = ComparisonInteractionSurface.ActualWidth * 0.35d;
        if (double.IsNaN(dividerPosition)
            || Math.Abs(dividerPosition - expectedDividerPosition) > 2d)
        {
            throw new InvalidDataException("The live preview reveal divider binding is invalid.");
        }

        RevealSlider.Value = 73d;
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (Math.Abs(_viewModel.OptionPreview.RevealPercent - 73d) > 0.1d)
        {
            throw new InvalidDataException("The live preview slider did not update its reveal state.");
        }

        _viewModel.OptionPreview.RevealPercent = 27d;
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (Math.Abs(RevealSlider.Value - 27d) > 0.1d
            || RevealPercentFromPointer(-10d, 100d) != 0d
            || RevealPercentFromPointer(50d, 100d) != 50d
            || RevealPercentFromPointer(110d, 100d) != 100d)
        {
            throw new InvalidDataException("The live preview reveal controls did not round-trip or clamp correctly.");
        }

        if (WorldExpansionCard.Visibility != Visibility.Visible
            || WorldExpansionItems.Items.Count != WorldExpansionCatalog.OrderedGroups.Count
            || !_viewModel.WorldExpansionOptions.Single(option =>
                    option.Value == WorldExpansion.CurrentEql).IsSelected)
        {
            throw new InvalidDataException(
                "The World scope did not show all expansion groups with Current EQL selected by default.");
        }

        var currentExpansion = _viewModel.WorldExpansionOptions.Single(option =>
            option.Value == WorldExpansion.CurrentEql);
        var missingExpansion = _viewModel.WorldExpansionOptions.Single(option =>
            option.Value == WorldExpansion.Kunark);
        currentExpansion.UpdateDetectedZoneCount(1);
        missingExpansion.SetSelectedSilently(true);
        if (_viewModel.HasCompleteWorldExpansionSelection)
        {
            throw new InvalidDataException(
                "A recovered World selection incorrectly accepted a selected expansion missing from the client.");
        }
        missingExpansion.SetSelectedSilently(false);
        currentExpansion.UpdateDetectedZoneCount(0);

        ScopeOptionViewModel originalScope = _viewModel.SelectedScopeOption;
        _viewModel.SelectedScopeOption = _viewModel.ScopeOptions.Single(option =>
            option.Value == AssetScope.WorldCharactersAndEquipment);
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (WorldExpansionCard.Visibility != Visibility.Visible)
        {
            throw new InvalidDataException(
                "The World expansion checklist was hidden for the combined World scope.");
        }

        _viewModel.SelectedScopeOption = _viewModel.ScopeOptions.Single(option =>
            option.Value == AssetScope.CharactersAndEquipmentOnly);
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (WorldExpansionCard.Visibility != Visibility.Collapsed)
        {
            throw new InvalidDataException(
                "The World expansion checklist remained visible for a character-only scope.");
        }

        _viewModel.SelectedScopeOption = originalScope;
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        if (WorldExpansionCard.Visibility != Visibility.Visible)
        {
            throw new InvalidDataException(
                "The World expansion checklist did not return with the World scope.");
        }

        if (BuildPage.ExtentWidth > BuildPage.ViewportWidth + 2d)
        {
            throw new InvalidDataException("The expanded live preview introduced horizontal overflow at minimum width.");
        }
    }

    private void OnLiveComparisonMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _isDraggingLiveComparison = true;
        ComparisonInteractionSurface.CaptureMouse();
        UpdateLiveComparisonReveal(e.GetPosition(ComparisonInteractionSurface));
        e.Handled = true;
    }

    private void OnLiveComparisonMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingLiveComparison || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateLiveComparisonReveal(e.GetPosition(ComparisonInteractionSurface));
        e.Handled = true;
    }

    private void OnLiveComparisonMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isDraggingLiveComparison)
        {
            return;
        }

        UpdateLiveComparisonReveal(e.GetPosition(ComparisonInteractionSurface));
        _isDraggingLiveComparison = false;
        ComparisonInteractionSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnLiveComparisonLostMouseCapture(object sender, MouseEventArgs e) =>
        _isDraggingLiveComparison = false;

    private void UpdateLiveComparisonReveal(Point position)
    {
        _viewModel.OptionPreview.RevealPercent = RevealPercentFromPointer(
            position.X,
            ComparisonInteractionSurface.ActualWidth);
    }

    internal static double RevealPercentFromPointer(double pointerX, double width)
    {
        if (!double.IsFinite(width) || width <= 0d || double.IsNaN(pointerX))
        {
            return 0d;
        }

        if (double.IsPositiveInfinity(pointerX))
        {
            return 100d;
        }

        if (double.IsNegativeInfinity(pointerX))
        {
            return 0d;
        }

        return Math.Clamp(pointerX / width * 100d, 0d, 100d);
    }

    public MainWindow()
    {
        InitializeComponent();
        InstalledVersionText.Text = ApplicationUpdateService.GetCurrentVersionDisplay();

        _viewModel = new MainWindowViewModel(
            new FolderPickerService(),
            new TextureWorkflowService(),
            new UserDialogService(),
            new EnhancedLauncherService());

        DataContext = _viewModel;
        _viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
        _viewModel.PreviewGalleryRequested += OnPreviewGalleryRequested;
        _viewModel.PackLibraryRequested += OnPackLibraryRequested;
        _viewModel.PackStorageRequested += OnPackStorageRequested;
        _viewModel.NativeGraphicsRequested += OnNativeGraphicsRequested;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private async void OnPackStorageRequested(string installPath)
    {
        await _viewModel.SuspendOptionPreviewForExternalWorkflowAsync().ConfigureAwait(true);
        if (IsClosingOrClosed)
        {
            return;
        }

        EnsureViewsMatchInstall(installPath);
        if (_packStorageView is null)
        {
            _packStorageView = new PackStorageSettingsWindow(installPath);
            _packStorageView.CloseRequested += (_, _) => ShowBuildSection();
            _packStorageView.StorageMoved += OnStorageMoved;
        }

        ShowSection(_packStorageView, "PACK STORAGE");
    }

    private async void OnStorageMoved(object? sender, EventArgs e)
    {
        DisposePackAndPreviewViews();
        await _viewModel.RefreshAfterPackStorageMoveAsync().ConfigureAwait(true);
    }

    private async void OnNativeGraphicsRequested(string installPath)
    {
        await _viewModel.SuspendOptionPreviewForExternalWorkflowAsync().ConfigureAwait(true);
        if (IsClosingOrClosed)
        {
            return;
        }

        EnsureViewsMatchInstall(installPath);
        if (_nativeGraphicsView is null)
        {
            _nativeGraphicsView = new NativeGraphicsWindow(
                installPath,
                new NativeGraphicsServiceAdapter());
            _nativeGraphicsView.CloseRequested += (_, _) => ShowBuildSection();
        }

        ShowSection(_nativeGraphicsView, "NATIVE GRAPHICS");
    }

    private async void OnPackLibraryRequested(string installPath)
    {
        await _viewModel.SuspendOptionPreviewForExternalWorkflowAsync().ConfigureAwait(true);
        if (IsClosingOrClosed)
        {
            return;
        }

        EnsureViewsMatchInstall(installPath);
        if (_packLibraryView is null)
        {
            _packLibraryView = new StagedPackLibraryWindow(installPath);
            _packLibraryView.CloseRequested += OnPackLibraryCloseRequested;
            _packLibraryView.FreshBuildPreparationCompleted += (_, _) =>
            {
                _viewModel.CompleteFreshBuildPreparationAfterLauncherUpdate();
                ShowBuildSection();
            };
            _packLibraryView.PreviewRequested += OnPackPreviewRequested;
        }

        ShowSection(_packLibraryView, "STAGED PACKS");
    }

    private void OnPackLibraryCloseRequested(object? sender, EventArgs e)
    {
        if (IsClosingOrClosed
            || sender is not StagedPackLibraryWindow packLibraryView
            || !ReferenceEquals(_packLibraryView, packLibraryView)
            || !_packLibraryCloseRefreshTask.IsCompleted)
        {
            return;
        }

        var requestedInstallPath = Path.GetFullPath(_viewModel.InstallPath);
        _packLibraryCloseRefreshTask = RefreshPackStateAndReturnToBuildAsync(
            packLibraryView,
            requestedInstallPath);
    }

    private async Task RefreshPackStateAndReturnToBuildAsync(
        StagedPackLibraryWindow packLibraryView,
        string requestedInstallPath)
    {
        packLibraryView.BeginReturnToBuildRefresh();
        try
        {
            await _viewModel.RefreshPackLibraryStateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (!IsClosingOrClosed && ReferenceEquals(_packLibraryView, packLibraryView))
            {
                MessageBox.Show(
                    this,
                    $"SpinTexture could not refresh the installed-pack status. No game files were changed; verify the install status again before installing or playing.\n\n{exception.Message}",
                    "Pack status refresh",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            packLibraryView.CompleteReturnToBuildRefresh();
        }

        if (!IsClosingOrClosed
            && ReferenceEquals(_packLibraryView, packLibraryView)
            && string.Equals(
                Path.GetFullPath(_viewModel.InstallPath),
                requestedInstallPath,
                StringComparison.OrdinalIgnoreCase))
        {
            ShowBuildSection();
        }
    }

    private void OnPackPreviewRequested(object? sender, PackPreviewRequestedEventArgs e) =>
        ShowPreview(e.ManifestPath, e.ApplyChoices, returnsToPacks: true);

    private async void OnPreviewGalleryRequested(string manifestPath)
    {
        await _viewModel.SuspendOptionPreviewForExternalWorkflowAsync().ConfigureAwait(true);
        if (IsClosingOrClosed)
        {
            return;
        }

        ShowPreview(manifestPath, applyChoices: null, returnsToPacks: false);
    }

    private bool IsClosingOrClosed =>
        _shutdownDrainRunning || _shutdownDrainComplete || _closedCleanupComplete;

    /// <summary>
    /// Builds the gallery's fallback renderer for textures without a captured
    /// preview pair: the enhanced side decodes from the staged pack payload
    /// and the original side from verified original bytes (live client or
    /// exact managed backups). Null when no valid install is configured.
    /// </summary>
    private OnDemandPreviewLoader? CreateOnDemandPreviewLoader(string previewManifestPath)
    {
        var installPath = _viewModel.InstallPath;
        if (string.IsNullOrWhiteSpace(installPath) || !System.IO.Directory.Exists(installPath))
        {
            return null;
        }

        try
        {
            var previewPaths = WorkspaceLocator.ForInstall(installPath);
            var previewWorkflow = new TexturePackWorkflow();
            return async (archivePath, logicalName, cancellationToken) => new OnDemandPreviewPanes(
                await previewWorkflow.LoadOriginalTexturePreviewAsync(
                        previewPaths,
                        previewManifestPath,
                        archivePath,
                        logicalName,
                        cancellationToken)
                    .ConfigureAwait(false),
                await previewWorkflow.LoadStagedTexturePreviewAsync(
                        previewPaths,
                        previewManifestPath,
                        archivePath,
                        logicalName,
                        cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or InvalidDataException)
        {
            return null;
        }
    }

    private void ShowPreview(
        string manifestPath,
        Func<IReadOnlyList<SpinTexture.Core.Models.TextureOverride>, Task>? applyChoices,
        bool returnsToPacks)
    {
        try
        {
            var fullManifestPath = Path.GetFullPath(manifestPath);
            if (_previewGalleryView is null
                || !string.Equals(
                    _previewGalleryView.ManifestPath,
                    fullManifestPath,
                    StringComparison.OrdinalIgnoreCase)
                || applyChoices is not null)
            {
                if (ReferenceEquals(SectionContent.Content, _previewGalleryView))
                {
                    SectionContent.Content = null;
                }

                _previewGalleryView?.Dispose();
                _previewGalleryView = new PreviewGalleryWindow(
                    fullManifestPath,
                    applyChoices,
                    CreateOnDemandPreviewLoader(fullManifestPath));
                _previewGalleryView.CloseRequested += (_, _) =>
                {
                    if (_previewReturnsToPacks && _packLibraryView is not null)
                    {
                        ShowSection(_packLibraryView, "STAGED PACKS");
                    }
                    else
                    {
                        ShowBuildSection();
                    }
                };
            }

            _previewReturnsToPacks = returnsToPacks;
            ShowSection(_previewGalleryView, "TEXTURE REVIEW");
        }
        catch (Exception exception)
        {
            if (!returnsToPacks)
            {
                _viewModel.ResumeOptionPreviewForBuildSection();
            }

            MessageBox.Show(
                this,
                $"The texture review could not be opened.\n\n{exception.Message}",
                "Texture review",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowSection(UserControl view, string title)
    {
        SectionContent.Content = view;
        SectionTitleText.Text = title;
        SectionOverlay.Visibility = Visibility.Visible;
    }

    private void ShowBuildSection()
    {
        if (!CanLeaveCurrentSection())
        {
            return;
        }

        SectionContent.Content = null;
        SectionOverlay.Visibility = Visibility.Collapsed;
        _viewModel.ResumeOptionPreviewForBuildSection();
    }

    private bool CanLeaveCurrentSection() =>
        (_packLibraryView?.CanNavigateAway ?? true)
        && (_packStorageView?.CanNavigateAway ?? true)
        && (_nativeGraphicsView?.CanNavigateAway ?? true);

    private void EnsureViewsMatchInstall(string installPath)
    {
        var fullInstallPath = Path.GetFullPath(installPath);
        if (_viewInstallPath is not null
            && string.Equals(_viewInstallPath, fullInstallPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SectionContent.Content = null;
        DisposeAllSectionViews();
        _viewInstallPath = fullInstallPath;
    }

    private void DisposePackAndPreviewViews()
    {
        if (ReferenceEquals(SectionContent.Content, _packLibraryView)
            || ReferenceEquals(SectionContent.Content, _previewGalleryView))
        {
            SectionContent.Content = null;
        }

        if (_packLibraryView is not null)
        {
            _packLibraryView.CloseRequested -= OnPackLibraryCloseRequested;
            _packLibraryView.Dispose();
        }
        _packLibraryView = null;
        _previewGalleryView?.Dispose();
        _previewGalleryView = null;
    }

    private void DisposeAllSectionViews()
    {
        DisposePackAndPreviewViews();
        _packStorageView = null;
        _nativeGraphicsView?.Dispose();
        _nativeGraphicsView = null;
    }

    private void BuildNav_Click(object sender, RoutedEventArgs e) => ShowBuildSection();
    private void BackToBuild_Click(object sender, RoutedEventArgs e) => ShowBuildSection();

    private void PacksNav_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenPackLibraryCommand.CanExecute(null))
        {
            _viewModel.OpenPackLibraryCommand.Execute(null);
        }
    }

    private void ReviewNav_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenPreviewGalleryCommand.CanExecute(null))
        {
            _viewModel.OpenPreviewGalleryCommand.Execute(null);
        }
    }

    private void GraphicsNav_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenNativeGraphicsCommand.CanExecute(null))
        {
            _viewModel.OpenNativeGraphicsCommand.Execute(null);
        }
    }

    private void StorageNav_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenPackStorageCommand.CanExecute(null))
        {
            _viewModel.OpenPackStorageCommand.Execute(null);
        }
    }

    private async void UpdatesNav_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || !CanLeaveCurrentSection())
        {
            MessageBox.Show(
                this,
                "Finish or cancel the current operation before updating SpinTexture.",
                "Update paused",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await App.CheckForUpdatesAsync(this, notifyWhenCurrent: true).ConfigureAwait(true);
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.LogEntries.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
            ActivityLogList.ScrollIntoView(_viewModel.LogEntries[^1])));
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDrainComplete)
        {
            return;
        }

        e.Cancel = true;
        if (!CanDrainCurrentSectionForShutdown())
        {
            MessageBox.Show(
                this,
                "The current Packs, Storage, or Graphics operation must finish safely before SpinTexture can close. Use its Cancel button if available, wait for the operation to stop, then close again.",
                "Operation still running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_shutdownDrainRunning)
        {
            return;
        }

        _shutdownDrainRunning = true;
        IsEnabled = false;
        try
        {
            await Task.WhenAll(
                    _viewModel.DrainForShutdownAsync(),
                    _packLibraryCloseRefreshTask)
                .ConfigureAwait(true);
            await Dispatcher.InvokeAsync(
                () =>
                {
                    _shutdownDrainComplete = true;
                    Close();
                },
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception exception)
        {
            _shutdownDrainRunning = false;
            IsEnabled = true;
            MessageBox.Show(
                this,
                $"SpinTexture is still stopping active work safely. Try closing again after it finishes.\n\n{exception.Message}",
                "Work is still stopping",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private bool CanDrainCurrentSectionForShutdown() =>
        ((_packLibraryView?.CanNavigateAway ?? true)
         || !_packLibraryCloseRefreshTask.IsCompleted)
        && (_packStorageView?.CanNavigateAway ?? true)
        && (_nativeGraphicsView?.CanNavigateAway ?? true);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_closedCleanupComplete)
        {
            return;
        }

        _closedCleanupComplete = true;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
        _viewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        _viewModel.PreviewGalleryRequested -= OnPreviewGalleryRequested;
        _viewModel.PackLibraryRequested -= OnPackLibraryRequested;
        _viewModel.PackStorageRequested -= OnPackStorageRequested;
        _viewModel.NativeGraphicsRequested -= OnNativeGraphicsRequested;
        SectionContent.Content = null;
        DisposeAllSectionViews();
        _viewModel.Dispose();
    }
}
