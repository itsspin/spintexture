using System.Windows;
using System.Windows.Controls;
using SpinTexture.App.ViewModels;

namespace SpinTexture.App;

public partial class PreviewGalleryWindow : UserControl, IDisposable
{
    private readonly PreviewGalleryViewModel viewModel;
    private readonly Func<IReadOnlyList<SpinTexture.Core.Models.TextureOverride>, Task>? applyChoices;

    public PreviewGalleryWindow(
        string previewManifestPath,
        Func<IReadOnlyList<SpinTexture.Core.Models.TextureOverride>, Task>? applyChoices = null)
    {
        InitializeComponent();

        this.applyChoices = applyChoices;
        viewModel = new PreviewGalleryViewModel(previewManifestPath, applyChoices is not null);
        ManifestPath = viewModel.ManifestPath;
        DataContext = viewModel;
    }

    public string ManifestPath { get; }
    public event EventHandler? CloseRequested;

    private async void ApplyTextureChoices_Click(object sender, RoutedEventArgs e)
    {
        if (applyChoices is null)
        {
            return;
        }

        var choices = viewModel.GetTextureOverrides();
        if (choices.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Create a new pack with {choices.Count:N0} reviewed texture change(s)?\n\n"
            + "Every unselected texture will be reused from the verified staged pack. The existing pack is never modified.",
            "Revise selected textures",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        ApplyTextureChoicesButton.IsEnabled = false;
        try
        {
            await applyChoices(choices).ConfigureAwait(true);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Texture revision", MessageBoxButton.OK, MessageBoxImage.Warning);
            ApplyTextureChoicesButton.IsEnabled = true;
        }
    }

    public void Dispose()
    {
        viewModel.Dispose();
    }
}
