using System.Windows;
using SpinTexture.App.ViewModels;

namespace SpinTexture.App;

public partial class PreviewGalleryWindow : Window
{
    private readonly PreviewGalleryViewModel viewModel;

    public PreviewGalleryWindow(string previewManifestPath)
    {
        InitializeComponent();

        viewModel = new PreviewGalleryViewModel(previewManifestPath);
        ManifestPath = viewModel.ManifestPath;
        DataContext = viewModel;
        Closed += OnWindowClosed;
    }

    public string ManifestPath { get; }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Closed -= OnWindowClosed;
        viewModel.Dispose();
    }
}
