namespace SpinTexture.Core.Models;

public sealed record ScanSummary(
    string InstallPath,
    int ArchiveCount,
    int PackedTextureCount,
    int LooseTextureCount,
    long SourceBytes,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Warnings);
