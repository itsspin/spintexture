using System.Security.Cryptography;
using System.Text;

namespace SpinTexture.Core.Services;

public static class WorkspaceLocator
{
    public static ProjectPaths ForInstall(string installPath, string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var normalizedInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpinTexture",
                "Profiles")
            : Path.GetFullPath(workspaceRoot);

        var installName = SanitizeName(Path.GetFileName(normalizedInstall));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedInstall.ToUpperInvariant())))[..12];
        var workspacePath = Path.Combine(root, $"{installName}-{hash}");
        var defaultStagingPath = Path.Combine(workspacePath, "Staging");
        var stagingPath = StagedPackStorageSettingsStore.ResolveStagingPath(
            normalizedInstall,
            workspacePath,
            defaultStagingPath);
        return new ProjectPaths(normalizedInstall, workspacePath, stagingPath);
    }

    public static ProjectPaths ForInstallDefault(string installPath, string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var normalizedInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpinTexture",
                "Profiles")
            : Path.GetFullPath(workspaceRoot);
        var installName = SanitizeName(Path.GetFileName(normalizedInstall));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedInstall.ToUpperInvariant())))[..12];
        return new ProjectPaths(normalizedInstall, Path.Combine(root, $"{installName}-{hash}"));
    }

    private static string SanitizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray();
        var result = new string(characters).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "EverQuest" : result;
    }
}
