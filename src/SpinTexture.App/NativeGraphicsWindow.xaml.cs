using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SpinTexture.App.Services;
using SpinTexture.App.ViewModels;

namespace SpinTexture.App;

public partial class NativeGraphicsWindow : UserControl, IDisposable
{
    private readonly NativeGraphicsWindowViewModel viewModel;
    private bool closeRequested;

    public NativeGraphicsWindow(string installPath, INativeGraphicsService service)
    {
        InitializeComponent();
        viewModel = new NativeGraphicsWindowViewModel(installPath, service);
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (viewModel.RefreshCommand.CanExecute(null))
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }

    public bool CanNavigateAway => !viewModel.IsBusy;
    public event EventHandler? CloseRequested;

    internal async Task RunLayoutSmokeAsync(Size viewport)
    {
        await viewModel.RefreshForLayoutSmokeAsync().ConfigureAwait(true);
        TechnicalDetailsToggle.IsChecked = true;
        ApplyTemplate();
        Measure(viewport);
        Arrange(new Rect(new Point(), viewport));
        UpdateLayout();

        if (PresetList.ActualWidth > viewport.Width + 0.5)
        {
            throw new InvalidOperationException(
                $"Native graphics presets overflowed the {viewport.Width:N0}px viewport.");
        }

        for (var index = 0; index < PresetList.Items.Count; index++)
        {
            if (PresetList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item
                && item.ActualWidth > (PresetList.ActualWidth / 2d) + 0.5)
            {
                throw new InvalidOperationException(
                    $"Native graphics preset {index + 1} did not fit in its two-column layout.");
            }
        }

        TechnicalDetailsToggle.IsChecked = false;
        UpdateLayout();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            closeRequested = true;
            viewModel.CancelCommand.Execute(null);
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (closeRequested
            && e.PropertyName == nameof(NativeGraphicsWindowViewModel.IsBusy)
            && !viewModel.IsBusy)
        {
            closeRequested = false;
            _ = Dispatcher.BeginInvoke(new Action(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        closeRequested = false;
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Dispose();
    }
}
