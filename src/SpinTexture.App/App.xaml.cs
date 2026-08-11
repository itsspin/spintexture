using System.Diagnostics;
using System.Windows;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 0)
        {
            new MainWindow().Show();
            return;
        }

        // Keep the command-line launcher alive while its asynchronous health
        // check runs even though this process intentionally has no main window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!EnhancedGameLaunch.TryParseArguments(e.Args, out var installPath))
        {
            ShowLaunchFailure(
                "SpinTexture received an unsupported launch command. Open SpinTexture normally and use Play Enhanced EQ.");
            Shutdown(2);
            return;
        }

        try
        {
            await LaunchEnhancedClientAsync(installPath).ConfigureAwait(true);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            ShowLaunchFailure(exception.Message);
            Shutdown(1);
        }
    }

    private static async Task LaunchEnhancedClientAsync(string installPath)
    {
        EnsureClientsClosed();
        var startInfo = EnhancedGameLaunch.CreateStartInfo(installPath);
        var paths = WorkspaceLocator.ForInstall(startInfo.WorkingDirectory);
        var health = await new InstallHealthService()
            .AuditLatestFastAsync(paths)
            .ConfigureAwait(true);

        if (health.State != InstallHealthState.EnhancedActive)
        {
            throw new InvalidOperationException(health.State switch
            {
                InstallHealthState.RevertedToOriginal =>
                    "LaunchPad restored the original game archives. Open SpinTexture and choose Install Staged Pack; the AI upscaler will not run again.",
                InstallHealthState.MixedOrModified when health.Entries.Any(entry =>
                    entry.State == InstalledArtifactHealthState.ModifiedOrMissing) =>
                    "One or more game archives changed outside SpinTexture. Do not force an old staged pack over a game update. Finish updating through LaunchPad, then Analyze and rebuild the affected pack.",
                InstallHealthState.MixedOrModified =>
                    "The installed pack is partly enhanced and partly original. Open SpinTexture and use its verified Restore action before installing again.",
                InstallHealthState.RecoveryRequired =>
                    "The previous install needs recovery. Open SpinTexture and use Restore before playing.",
                _ =>
                    "No active enhanced pack was found. Open SpinTexture, build or select a staged pack, and install it once before playing."
            });
        }

        // Recheck immediately before process creation. This closes the normal
        // race where LaunchPad starts while a large installed pack is audited.
        EnsureClientsClosed();
        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start eqgame.exe.");
        }
    }

    private static void EnsureClientsClosed()
    {
        if (EverQuestInstall.IsGameRunning())
        {
            throw new InvalidOperationException("EverQuest is already running.");
        }

        if (EverQuestInstall.IsLauncherRunning())
        {
            throw new InvalidOperationException(
                "Close LaunchPad before playing. LaunchPad validates and replaces custom texture archives.");
        }
    }

    private static void ShowLaunchFailure(string message) =>
        MessageBox.Show(
            message + "\n\nNo game files were changed and SpinTexture did not read or store login credentials.",
            "SpinTexture could not start Enhanced EQ",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
}
