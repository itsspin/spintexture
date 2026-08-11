using System.Diagnostics;

namespace SpinTexture.Core.Services;

/// <summary>
/// Parses SpinTexture's credential-free enhanced-play command and creates the
/// fixed EverQuest launch plan. This helper never starts a process.
/// </summary>
public static class EnhancedGameLaunch
{
    public const string CommandArgument = "--play-enhanced";

    public static bool TryParseArguments(
        IReadOnlyList<string>? arguments,
        out string installPath)
    {
        installPath = string.Empty;
        if (arguments is not { Count: 2 }
            || !string.Equals(arguments[0], CommandArgument, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[1])
            || IsTicketArgument(arguments[1]))
        {
            return false;
        }

        installPath = arguments[1];
        return true;
    }

    public static ProcessStartInfo CreateStartInfo(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var normalizedInstall = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installPath));
        if (!Directory.Exists(normalizedInstall))
        {
            throw new DirectoryNotFoundException(
                $"The EverQuest install directory was not found: {normalizedInstall}");
        }

        var executablePath = Path.Combine(normalizedInstall, "eqgame.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "eqgame.exe was not found in the selected client directory.",
                executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = normalizedInstall,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("patchme");
        return startInfo;
    }

    private static bool IsTicketArgument(string argument)
    {
        var trimmed = argument.TrimStart();
        return trimmed.Equals("/ticket", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/ticket:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/ticket=", StringComparison.OrdinalIgnoreCase);
    }
}
