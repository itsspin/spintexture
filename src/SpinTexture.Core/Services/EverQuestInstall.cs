namespace SpinTexture.Core.Services;

public sealed record InstallValidationResult(
    bool IsValid,
    string NormalizedPath,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class EverQuestInstall
{
    private static readonly string[] RequiredFiles = ["eqgame.exe"];

    public static InstallValidationResult Validate(string installPath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return new InstallValidationResult(false, string.Empty, ["Choose the EverQuest Legends directory."], []);
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(installPath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new InstallValidationResult(false, installPath, [$"The selected path is invalid: {exception.Message}"], []);
        }

        if (!Directory.Exists(normalized))
        {
            errors.Add("The selected directory does not exist.");
            return new InstallValidationResult(false, normalized, errors, warnings);
        }

        foreach (string requiredFile in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(normalized, requiredFile)))
            {
                errors.Add($"{requiredFile} was not found in the selected directory.");
            }
        }

        int archiveCount = Directory.EnumerateFiles(normalized, "*", SearchOption.TopDirectoryOnly)
            .Count(path => IsPfsArchiveExtension(Path.GetExtension(path)));
        if (archiveCount == 0)
        {
            errors.Add("No S3D, EQG, PFS, or PAK asset archives were found.");
        }
        else if (archiveCount < 20)
        {
            warnings.Add("The directory contains unusually few EverQuest asset archives.");
        }

        if (IsGameOrLauncherRunning())
        {
            warnings.Add("EverQuest or LaunchPad is running. Analyze and staging are safe, but Install and Restore remain blocked.");
        }

        return new InstallValidationResult(errors.Count == 0, normalized, errors, warnings);
    }

    public static bool IsGameRunning() => IsProcessRunning("eqgame");

    public static bool IsLauncherRunning() => IsProcessRunning("LaunchPad");

    public static bool IsGameOrLauncherRunning() => IsGameRunning() || IsLauncherRunning();

    public static bool IsPfsArchiveExtension(string extension) =>
        extension.Equals(".s3d", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".eqg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pfs", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pak", StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length != 0;
        }
        catch
        {
            return true;
        }
    }
}
