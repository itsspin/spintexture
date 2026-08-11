using System.Collections.Specialized;
using System.Windows;
using SpinTexture.App.Services;
using SpinTexture.App.ViewModels;

namespace SpinTexture.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private PreviewGalleryWindow? _previewGalleryWindow;
    private StagedPackLibraryWindow? _packLibraryWindow;
    private NativeGraphicsWindow? _nativeGraphicsWindow;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel(
            new FolderPickerService(),
            new TextureWorkflowService(),
            new UserDialogService(),
            new EnhancedLauncherService());

        DataContext = _viewModel;
        _viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
        _viewModel.PreviewGalleryRequested += OnPreviewGalleryRequested;
        _viewModel.PackLibraryRequested += OnPackLibraryRequested;
        _viewModel.NativeGraphicsRequested += OnNativeGraphicsRequested;
        Closed += OnWindowClosed;
    }

    private void OnNativeGraphicsRequested(string installPath)
    {
        if (_nativeGraphicsWindow is { IsVisible: true })
        {
            if (_nativeGraphicsWindow.WindowState == WindowState.Minimized)
            {
                _nativeGraphicsWindow.WindowState = WindowState.Normal;
            }

            _nativeGraphicsWindow.Activate();
            return;
        }

        var window = new NativeGraphicsWindow(
            installPath,
            new NativeGraphicsServiceAdapter())
        {
            Owner = this
        };
        _nativeGraphicsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_nativeGraphicsWindow, window))
            {
                _nativeGraphicsWindow = null;
            }
        };
        _ = window.ShowDialog();
    }

    private async void OnPackLibraryRequested(string installPath)
    {
        if (_packLibraryWindow is { IsVisible: true })
        {
            if (_packLibraryWindow.WindowState == WindowState.Minimized)
            {
                _packLibraryWindow.WindowState = WindowState.Normal;
            }

            _packLibraryWindow.Activate();
            return;
        }

        var window = new StagedPackLibraryWindow(installPath) { Owner = this };
        _packLibraryWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_packLibraryWindow, window))
            {
                _packLibraryWindow = null;
            }
        };
        // Pack switching is a restore-plus-apply workflow. Keep the owner
        // disabled for the entire selection/repair/install session so a main
        // window Install or Restore command cannot interleave between those
        // two verified transactions.
        _ = window.ShowDialog();
        await _viewModel.RefreshPackLibraryStateAsync().ConfigureAwait(true);
    }

    private void OnPreviewGalleryRequested(string manifestPath)
    {
        try
        {
            if (_previewGalleryWindow is { IsVisible: true }
                && string.Equals(
                    _previewGalleryWindow.ManifestPath,
                    Path.GetFullPath(manifestPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_previewGalleryWindow.WindowState == WindowState.Minimized)
                {
                    _previewGalleryWindow.WindowState = WindowState.Normal;
                }

                _previewGalleryWindow.Activate();
                return;
            }

            _previewGalleryWindow?.Close();
            var galleryWindow = new PreviewGalleryWindow(manifestPath)
            {
                Owner = this
            };
            _previewGalleryWindow = galleryWindow;
            galleryWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_previewGalleryWindow, galleryWindow))
                {
                    _previewGalleryWindow = null;
                }
            };
            galleryWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"The preview gallery could not be opened.\n\n{exception.Message}",
                "Preview gallery",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        _viewModel.PreviewGalleryRequested -= OnPreviewGalleryRequested;
        _viewModel.PackLibraryRequested -= OnPackLibraryRequested;
        _viewModel.NativeGraphicsRequested -= OnNativeGraphicsRequested;
        _packLibraryWindow?.Close();
        _nativeGraphicsWindow?.Close();
        _viewModel.Dispose();
    }
}
