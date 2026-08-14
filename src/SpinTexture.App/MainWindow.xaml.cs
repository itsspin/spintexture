using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpinTexture.App.Services;
using SpinTexture.App.ViewModels;
using SpinTexture.Core.Services;

namespace SpinTexture.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private PreviewGalleryWindow? _previewGalleryView;
    private StagedPackLibraryWindow? _packLibraryView;
    private PackStorageSettingsWindow? _packStorageView;
    private NativeGraphicsWindow? _nativeGraphicsView;
    private bool _previewReturnsToPacks;
    private string? _viewInstallPath;
    private bool _shutdownDrainComplete;
    private bool _shutdownDrainRunning;
    private bool _closedCleanupComplete;

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

        double expectedRevealWidth = LiveComparisonSurface.ActualWidth * 0.35d;
        if (Math.Abs(EnhancedRevealLayer.ActualWidth - expectedRevealWidth) > 2d)
        {
            throw new InvalidDataException("The live preview enhanced reveal width binding is invalid.");
        }

        double dividerPosition = Canvas.GetLeft(RevealDivider);
        double expectedDividerPosition = LiveComparisonSurface.ActualWidth * 0.65d;
        if (double.IsNaN(dividerPosition)
            || Math.Abs(dividerPosition - expectedDividerPosition) > 2d)
        {
            throw new InvalidDataException("The live preview reveal divider binding is invalid.");
        }

        if (BuildPage.ExtentWidth > BuildPage.ViewportWidth + 2d)
        {
            throw new InvalidDataException("The expanded live preview introduced horizontal overflow at minimum width.");
        }
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
        EnsureViewsMatchInstall(installPath);
        if (_packLibraryView is null)
        {
            _packLibraryView = new StagedPackLibraryWindow(installPath);
            _packLibraryView.CloseRequested += async (_, _) =>
            {
                ShowBuildSection();
                await _viewModel.RefreshPackLibraryStateAsync().ConfigureAwait(true);
            };
            _packLibraryView.PreviewRequested += OnPackPreviewRequested;
        }

        ShowSection(_packLibraryView, "STAGED PACKS");
    }

    private void OnPackPreviewRequested(object? sender, PackPreviewRequestedEventArgs e) =>
        ShowPreview(e.ManifestPath, e.ApplyChoices, returnsToPacks: true);

    private async void OnPreviewGalleryRequested(string manifestPath)
    {
        await _viewModel.SuspendOptionPreviewForExternalWorkflowAsync().ConfigureAwait(true);
        ShowPreview(manifestPath, applyChoices: null, returnsToPacks: false);
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
                _previewGalleryView = new PreviewGalleryWindow(fullManifestPath, applyChoices);
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

    private bool CanLeaveCurrentSection() => SectionContent.Content switch
    {
        StagedPackLibraryWindow packs => packs.CanNavigateAway,
        PackStorageSettingsWindow storage => storage.CanNavigateAway,
        NativeGraphicsWindow graphics => graphics.CanNavigateAway,
        _ => true
    };

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

        _packLibraryView?.Dispose();
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
        if (_shutdownDrainRunning)
        {
            return;
        }

        _shutdownDrainRunning = true;
        try
        {
            await _viewModel.DrainOptionPreviewForShutdownAsync().ConfigureAwait(true);
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
            MessageBox.Show(
                this,
                $"SpinTexture is still stopping preview work safely. Try closing again after it finishes.\n\n{exception.Message}",
                "Preview is still stopping",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

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
