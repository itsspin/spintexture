using System.ComponentModel;
using System.Windows;
using SpinTexture.App.Services;
using SpinTexture.App.ViewModels;

namespace SpinTexture.App;

public partial class NativeGraphicsWindow : Window
{
    private readonly NativeGraphicsWindowViewModel viewModel;
    private bool closeRequested;

    public NativeGraphicsWindow(string installPath, INativeGraphicsService service)
    {
        InitializeComponent();
        viewModel = new NativeGraphicsWindowViewModel(installPath, service);
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (viewModel.RefreshCommand.CanExecute(null))
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!viewModel.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        closeRequested = true;
        viewModel.CancelCommand.Execute(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (closeRequested
            && e.PropertyName == nameof(NativeGraphicsWindowViewModel.IsBusy)
            && !viewModel.IsBusy)
        {
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        Closed -= OnClosed;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Dispose();
    }
}
