namespace SpinTexture.Core;

public sealed class ProjectPaths
{
    public ProjectPaths(string installPath, string workspacePath)
    {
        InstallPath = Path.GetFullPath(installPath);
        WorkspacePath = Path.GetFullPath(workspacePath);
    }

    public string InstallPath { get; }
    public string WorkspacePath { get; }
    public string StagingPath => Path.Combine(WorkspacePath, "Staging");
    public string BackupPath => Path.Combine(WorkspacePath, "Backups");
    public string CachePath => Path.Combine(WorkspacePath, "Cache");
    public string LogsPath => Path.Combine(WorkspacePath, "Logs");
    public string ToolsPath => Path.Combine(WorkspacePath, "Tools");
    public string ClientSettingsPath => Path.Combine(WorkspacePath, "ClientSettings");
    public string ClientSettingsTransactionsPath => Path.Combine(ClientSettingsPath, "Transactions");

    public void EnsureWorkspaceDirectories()
    {
        Directory.CreateDirectory(WorkspacePath);
        Directory.CreateDirectory(StagingPath);
        Directory.CreateDirectory(BackupPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(ToolsPath);
        Directory.CreateDirectory(ClientSettingsPath);
        Directory.CreateDirectory(ClientSettingsTransactionsPath);
    }
}
