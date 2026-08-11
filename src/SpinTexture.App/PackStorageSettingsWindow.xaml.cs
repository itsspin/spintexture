using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;

namespace SpinTexture.App;

public partial class PackStorageSettingsWindow : UserControl
{
    private readonly string installPath;
    private readonly StagedPackStorageService storageService = new();
    private CancellationTokenSource? cancellation;
    private StagedPackStoragePlan? plan;
    private bool isBusy;

    public PackStorageSettingsWindow(string installPath)
    {
        this.installPath = Path.GetFullPath(installPath);
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public bool StorageLocationChanged { get; private set; }
    public bool CanNavigateAway => !isBusy;
    public event EventHandler? CloseRequested;
    public event EventHandler? StorageMoved;

    private async Task RefreshAsync()
    {
        try
        {
            var paths = WorkspaceLocator.ForInstall(installPath);
            var status = await storageService.InspectAsync(paths).ConfigureAwait(true);
            CurrentPathBox.Text = status.CurrentPath;
            CurrentSummaryText.Text = status.Summary
                + (status.UsesDefaultLocation ? "  •  Default location" : "  •  Custom location");
            DefaultButton.IsEnabled = !status.UsesDefaultLocation && !isBusy;
            if (plan is null)
            {
                StatusText.Text = "Choose a destination when you are ready. Nothing moves until you confirm.";
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            StatusText.Text = $"Pack storage could not be inspected: {exception.Message}";
            MoveButton.IsEnabled = false;
        }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a parent folder for SpinTexture staged packs",
            Multiselect = false,
            InitialDirectory = Directory.Exists(CurrentPathBox.Text)
                ? Path.GetDirectoryName(CurrentPathBox.Text)
                : null
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        await PreparePlanAsync(dialog.FolderName, useDefault: false).ConfigureAwait(true);
    }

    private async void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        var paths = WorkspaceLocator.ForInstall(installPath);
        await PreparePlanAsync(paths.DefaultStagingPath, useDefault: true).ConfigureAwait(true);
    }

    private async Task PreparePlanAsync(string selectedPath, bool useDefault)
    {
        try
        {
            SetBusy(true, planning: true);
            var paths = WorkspaceLocator.ForInstall(installPath);
            plan = await storageService.PlanAsync(paths, selectedPath, useDefault)
                .ConfigureAwait(true);
            DestinationText.Text = plan.DestinationPath;
            PlanSummaryText.Text = plan.Summary;
            StatusText.Text = plan.IsNoOp
                ? "This location is already active; no transfer is needed."
                : "Review the destination, then choose Move + Verify Pack Library.";
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            plan = null;
            DestinationText.Text = "No safe destination selected.";
            PlanSummaryText.Text = exception.Message;
            StatusText.Text = "Nothing was moved and the current library remains active.";
        }
        finally
        {
            SetBusy(false, planning: false);
        }
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (plan is null || plan.IsNoOp)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"Move the staged-pack library to:\n\n{plan.DestinationPath}\n\n"
            + "SpinTexture will copy and SHA-256 verify the library before changing the saved location. "
            + "No game files, installed textures, or original backups will be changed.",
            "Move staged-pack library",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        cancellation = new CancellationTokenSource();
        var progress = new Progress<ProgressUpdate>(update =>
        {
            MoveProgress.IsIndeterminate = update.Total <= 0;
            if (update.Total > 0)
            {
                MoveProgress.Maximum = update.Total;
                MoveProgress.Value = Math.Min(update.Total, update.Completed);
            }

            StatusText.Text = string.IsNullOrWhiteSpace(update.CurrentItem)
                ? update.Message
                : $"{update.Message}\n{update.CurrentItem}";
        });

        try
        {
            SetBusy(true, planning: false);
            var paths = WorkspaceLocator.ForInstall(installPath);
            var result = await storageService.MoveAsync(
                    paths,
                    plan,
                    progress,
                    cancellation.Token)
                .ConfigureAwait(true);
            StorageLocationChanged = true;
            StorageMoved?.Invoke(this, EventArgs.Empty);
            plan = null;
            DestinationText.Text = "Choose a folder to preview the managed destination.";
            PlanSummaryText.Text = string.Empty;
            StatusText.Text = result.CleanupWarning is null
                ? "Move complete. The new verified pack-library location is active."
                : result.CleanupWarning;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Transfer canceled safely before the saved location changed.";
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            StatusText.Text = $"The move was not completed. The previous location remains active.\n{exception.Message}";
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            SetBusy(false, planning: false);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(CurrentPathBox.Text))
            {
                Directory.CreateDirectory(CurrentPathBox.Text);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = CurrentPathBox.Text,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Open pack folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        StatusText.Text = "Cancel requested. SpinTexture will stop before switching locations.";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            StatusText.Text = "Cancel the transfer first. SpinTexture will stop before switching locations.";
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetBusy(bool busy, bool planning)
    {
        isBusy = busy;
        MoveButton.IsEnabled = !busy && plan is { IsNoOp: false };
        CloseButton.IsEnabled = !busy;
        CancelButton.Visibility = busy && !planning ? Visibility.Visible : Visibility.Collapsed;
        DefaultButton.IsEnabled = !busy && !PathEquals(CurrentPathBox.Text, WorkspaceLocator.ForInstall(installPath).DefaultStagingPath);
        MoveProgress.IsIndeterminate = busy && planning;
        if (!busy)
        {
            MoveProgress.IsIndeterminate = false;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        ArgumentException or
        NotSupportedException;
}
