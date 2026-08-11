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
