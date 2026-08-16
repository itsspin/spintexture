namespace SpinTexture.Core.Models;

public sealed record StagedPackStorageStatus(
    string CurrentPath,
    string DefaultPath,
    long StoredBytes,
    int FileCount,
    int PackCount,
    bool UsesDefaultLocation,
    long CompletedPackBytes,
    int ResumableBuildCount,
    long ResumableBuildBytes,
    int LeftoverDirectoryCount,
    long LeftoverBytes)
{
    public string Summary => FileCount == 0
        ? "No staged pack files are stored yet."
        : $"{FormatBytes(StoredBytes)} logical file size · {PackCount:N0} completed pack(s) ({FormatBytes(CompletedPackBytes)} logical)"
          + (ResumableBuildCount == 0
              ? string.Empty
              : $" · {ResumableBuildCount:N0} resumable build(s) ({FormatBytes(ResumableBuildBytes)})")
          + (LeftoverDirectoryCount == 0
              ? string.Empty
              : $" · {LeftoverDirectoryCount:N0} leftover folder(s) ({FormatBytes(LeftoverBytes)})");

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }
}

public sealed record StagedPackStoragePlan(
    string SourcePath,
    string DestinationPath,
    long BytesToTransfer,
    int FileCount,
    bool UsesDefaultDestination,
    bool DestinationIsVerifiedMirror,
    bool IsNoOp,
    string Summary);

public sealed record StagedPackStorageMoveResult(
    StagedPackStorageStatus Status,
    bool OldLibraryRemoved,
    string? CleanupWarning);
