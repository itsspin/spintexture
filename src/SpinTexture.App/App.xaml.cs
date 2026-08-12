using System.Diagnostics;
using System.Windows;
using SpinTexture.App.Services;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.App;

public partial class App : Application
{
    private const string StartupSmokeArgument = "--startup-smoke";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 0)
        {
            try
            {
                new MainWindow().Show();
            }
            catch (Exception exception)
            {
                var logPath = TryWriteStartupFailure(exception);
                MessageBox.Show(
                    "SpinTexture could not open its main window. No game files were changed."
                    + (logPath is null
                        ? string.Empty
                        : $"\n\nDiagnostic details were saved to:\n{logPath}"),
                    "SpinTexture startup failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }

            return;
        }

        if (e.Args.Length == 1
            && e.Args[0].Equals(StartupSmokeArgument, StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var window = new MainWindow();
                window.ApplyTemplate();
                window.Measure(new Size(1440, 900));
                window.Arrange(new Rect(0, 0, 1440, 900));
                window.UpdateLayout();
                window.Close();

                using var graphics = new NativeGraphicsWindow(
                    Path.GetTempPath(),
                    new StartupNativeGraphicsService());
                await graphics.RunLayoutSmokeAsync(new Size(1368, 700)).ConfigureAwait(true);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                TryWriteStartupFailure(exception);
                Shutdown(1);
            }

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

    private static string? TryWriteStartupFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpinTexture",
                "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "startup-error.log");
            File.WriteAllText(
                path,
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}");
            return path;
        }
        catch
        {
            return null;
        }
    }

    private sealed class StartupNativeGraphicsService : INativeGraphicsService
    {
        private static readonly IReadOnlyList<NativeGraphicsUiSetting> Settings =
        [
            new("Defaults", "Shadows", "TRUE"),
            new("Defaults", "MultiPassLighting", "TRUE"),
            new("Defaults", "PostEffects", "TRUE"),
            new("Defaults", "Bloom", "TRUE"),
            new("Options", "ShadowClipPlane", "100")
        ];

        public Task<NativeGraphicsUiStatus> InspectAsync(
            string installPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NativeGraphicsUiStatus(
                "Cinematic native preset active",
                "MANAGED",
                "The Cinematic native preset is active.",
                Path.Combine(installPath, "eqclient.ini"),
                null,
                Settings,
                NeedsAttention: false,
                CanApply: true,
                CanRestore: true));

        public Task<NativeGraphicsUiPlan> PlanAsync(
            string installPath,
            NativeGraphicsUiPreset preset,
            CancellationToken cancellationToken)
        {
            var changes = preset == NativeGraphicsUiPreset.Balanced
                ? CreateChanges("FALSE", "FALSE", "FALSE", "50")
                : CreateChanges("TRUE", "TRUE", "TRUE", "100");
            return Task.FromResult(new NativeGraphicsUiPlan(
                preset,
                preset == NativeGraphicsUiPreset.Balanced ? "Balanced" : "Cinematic",
                preset == NativeGraphicsUiPreset.Balanced ? "NATIVE STENCIL SHADOWS" : "FULL NATIVE LIGHTING",
                preset == NativeGraphicsUiPreset.Balanced
                    ? "Enables built-in shadows while preserving your other lighting values."
                    : "Enables every verified built-in lighting feature and maximum shadow range.",
                preset == NativeGraphicsUiPreset.Balanced
                    ? "Moderate GPU cost; the recommended starting point."
                    : "Very high GPU cost in dense zones.",
                changes,
                HasChanges: true,
                CanApply: true));
        }

        public Task ApplyAsync(
            string installPath,
            NativeGraphicsUiPreset preset,
            IProgress<string>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestoreAsync(
            string installPath,
            IProgress<string>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        private static IReadOnlyList<NativeGraphicsUiChange> CreateChanges(
            string lighting,
            string postEffects,
            string bloom,
            string shadowDistance) =>
        [
            new("Defaults", "MultiPassLighting", "TRUE", lighting, false, false),
            new("Defaults", "PostEffects", "TRUE", postEffects, false, false),
            new("Defaults", "Bloom", "TRUE", bloom, false, false),
            new("Options", "ShadowClipPlane", "100", shadowDistance, false, false)
        ];
    }
}
