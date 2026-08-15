using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using SpinTexture.Core.Services;

namespace SpinTexture.App.Services;

public interface IFolderPickerService
{
    string? PickFolder(string? initialPath);
}

public interface IUserDialogService
{
    bool ConfirmApplyLatest();
    bool ConfirmRestore();
    bool ConfirmArtisticWorkerSetup(long totalDownloadBytes, IReadOnlyList<string> componentSummaries);
    bool ConfirmArtisticWorkerRemove();
    void ShowArtisticWorkerNotice(string message, bool isError);
}

public interface IEnhancedLauncherService
{
    string CreateDesktopShortcut(string installPath);
    Task LaunchAsync(string installPath, CancellationToken cancellationToken);
}

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string? initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the EverQuest Legends game directory",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed class EnhancedLauncherService : IEnhancedLauncherService
{
    private const string ShortcutDescriptionPrefix = "Managed by SpinTexture - enhanced EverQuest client";

    public string CreateDesktopShortcut(string installPath)
    {
        var gameStartInfo = EnhancedGameLaunch.CreateStartInfo(installPath);
        var normalizedInstall = gameStartInfo.WorkingDirectory;
        var gameExecutablePath = gameStartInfo.FileName;
        var spinTexturePath = ResolveSpinTextureExecutable();
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
        {
            throw new DirectoryNotFoundException("Windows did not report an available Desktop directory.");
        }

        var installKey = CreateInstallKey(normalizedInstall);
        var shortcutDescription = $"{ShortcutDescriptionPrefix} [{installKey}]";
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new NotSupportedException("Windows Script Host is unavailable, so the desktop shortcut could not be created.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows did not create the shortcut service.");
            dynamic dynamicShell = shell;
            var shortcutPath = SelectShortcutPath(
                dynamicShell,
                desktopPath,
                normalizedInstall,
                installKey,
                shortcutDescription,
                BuildShortcutArguments(normalizedInstall));
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = spinTexturePath;
            dynamicShortcut.Arguments = BuildShortcutArguments(normalizedInstall);
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(spinTexturePath)!;
            dynamicShortcut.IconLocation = $"{gameExecutablePath},0";
            dynamicShortcut.Description = shortcutDescription;
            dynamicShortcut.Save();

            if (!File.Exists(shortcutPath))
            {
                throw new IOException("Windows did not save the SpinTexture desktop shortcut.");
            }

            return shortcutPath;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    public async Task LaunchAsync(string installPath, CancellationToken cancellationToken)
    {
        var gameStartInfo = EnhancedGameLaunch.CreateStartInfo(installPath);
        using var process = Process.Start(CreateSpinTextureStartInfo(gameStartInfo.WorkingDirectory));
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start SpinTexture's verified game launcher.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The verification wrapper exited between HasExited and Kill.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "SpinTexture's verified game launcher did not start EverQuest. Review the launcher message and try again.");
        }
    }

    private static ProcessStartInfo CreateSpinTextureStartInfo(string normalizedInstall)
    {
        var executablePath = ResolveSpinTextureExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(EnhancedGameLaunch.CommandArgument);
        startInfo.ArgumentList.Add(normalizedInstall);
        return startInfo;
    }

    private static string ResolveSpinTextureExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            throw new FileNotFoundException(
                "SpinTexture could not identify its executable, so the verified play shortcut could not be created.",
                processPath);
        }

        return Path.GetFullPath(processPath);
    }

    private static string BuildShortcutArguments(string normalizedInstall)
    {
        if (normalizedInstall.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The EverQuest install path contains an unsupported quote character.");
        }

        return $"{EnhancedGameLaunch.CommandArgument} {QuoteWindowsArgument(normalizedInstall)}";
    }

    private static string SelectShortcutPath(
        dynamic shell,
        string desktopPath,
        string normalizedInstall,
        string installKey,
        string expectedDescription,
        string expectedArguments)
    {
        var installName = Path.GetFileName(normalizedInstall);
        if (string.IsNullOrWhiteSpace(installName))
        {
            installName = "EverQuest";
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            installName = installName.Replace(invalidCharacter, '-');
        }

        var baseName = $"Play EverQuest with SpinTexture - {installName} {installKey}";
        for (var index = 1; index <= 100; index++)
        {
            var suffix = index == 1 ? string.Empty : $" ({index})";
            var candidate = Path.Combine(desktopPath, $"{baseName}{suffix}.lnk");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            object? existingShortcut = null;
            try
            {
                existingShortcut = shell.CreateShortcut(candidate);
                dynamic existing = existingShortcut;
                var description = Convert.ToString(existing.Description, System.Globalization.CultureInfo.InvariantCulture);
                var arguments = Convert.ToString(existing.Arguments, System.Globalization.CultureInfo.InvariantCulture);
                if (string.Equals(description, expectedDescription, StringComparison.Ordinal)
                    && string.Equals(arguments, expectedArguments, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            finally
            {
                ReleaseComObject(existingShortcut);
            }
        }

        throw new IOException("SpinTexture could not choose a safe, unused desktop shortcut name.");
    }

    private static string CreateInstallKey(string normalizedInstall)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedInstall.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0
            && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        // Backslashes immediately before the closing quote must be doubled so
        // a drive or UNC root cannot escape that quote.
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

public sealed class UserDialogService : IUserDialogService
{
    public bool ConfirmApplyLatest()
    {
        const string message =
            "SpinTexture will install the latest staged pack without running the AI upscaler again.\n\n"
            + "EverQuest and LaunchPad must both be closed. SpinTexture will create and verify original backups, install the enhanced archives once, and create an install-specific desktop shortcut. That shortcut performs a quick pack-health check and then starts eqgame.exe with the standard patchme option; it does not rebuild or reinstall textures.\n\n"
            + "Use that shortcut for normal play. If this Legends client presents its manual login screen, sign in there; server support for manual patchme login can vary and should be tested first. SpinTexture never reads, stores, or forwards account credentials or a LaunchPad ticket. Starting LaunchPad will validate and restore the original archives, so only use it when the game actually needs an update. After an update, SpinTexture will reuse the staged pack only when its original archive hashes are still compatible; otherwise it will require a safe rebuild.\n\n"
            + "Install the staged pack now?";

        return MessageBox.Show(
            message,
            "Install staged textures",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmRestore()
    {
        const string message =
            "Restore the original archives from SpinTexture's verified backup?\n\n"
            + "EverQuest and LaunchPad must be closed. Any currently installed enhanced archives in this managed transaction will be replaced with their originals.";

        return MessageBox.Show(
            message,
            "Restore original textures",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmArtisticWorkerSetup(long totalDownloadBytes, IReadOnlyList<string> componentSummaries)
    {
        var componentList = string.Join(
            "\n",
            componentSummaries.Select(summary => $"  \u2022 {summary}"));
        var message =
            $"Set up the experimental diffusion repaint worker (~{totalDownloadBytes / (1024.0 * 1024 * 1024):N1} GB download)?\n\n"
            + "SpinTexture will download pinned, SHA-256-verified components:\n"
            + componentList
            + "\n\n"
            + "After download, SpinTexture verifies the worker on this PC by repainting a test image twice and checking the results are identical and exactly 4x. Only a verified worker is enabled.\n\n"
            + "When enabled, Graphic Painted Fantasy builds repaint each texture with Stable Diffusion. Expect builds to take several times longer than today (try one zone first), and around 8 GB of GPU memory to be used while building. Alpha, tiling seams, fidelity gates, themes, and all backup/restore guarantees still apply, and any worker problem falls back automatically to the built-in painterly stylization.\n\n"
            + "If the download is interrupted, run setup again \u2014 verified components are kept and the rest resumes.";

        return MessageBox.Show(
            message,
            "Set up artistic worker (experimental)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmArtisticWorkerRemove()
    {
        return MessageBox.Show(
            "Remove the artistic worker and its downloaded models?\n\nGraphic Painted Fantasy returns to the built-in painterly stylization. Completed packs are not affected.",
            "Remove artistic worker",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public void ShowArtisticWorkerNotice(string message, bool isError)
    {
        MessageBox.Show(
            message,
            "Artistic worker",
            MessageBoxButton.OK,
            isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
