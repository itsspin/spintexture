namespace SpinTexture.Core.Archives;

public enum PfsContentKind
{
    Unknown,
    Dds,
    Bitmap,
    Targa,
    Png,
    Jpeg
}

public sealed record PfsContentMetadata(
    PfsContentKind Kind,
    int Width = 0,
    int Height = 0,
    int MipCount = 0,
    string PixelFormat = "",
    bool HasAlpha = false)
{
    public bool IsTexture => Kind != PfsContentKind.Unknown;
}

public sealed class PfsArchiveEntry
{
    internal PfsArchiveEntry(string name, PfsStoredEntry storedEntry, PfsContentMetadata content)
    {
        Name = name;
        StoredEntry = storedEntry;
        Content = content;
    }

    public string Name { get; }
    public uint FilenameCrc => StoredEntry.Crc;
    public long UncompressedSize => StoredEntry.UncompressedSize;
    public long StoredOffset => StoredEntry.Offset;
    public long StoredLength => StoredEntry.StoredLength;
    public string DeclaredExtension => Path.GetExtension(Name);
    public PfsContentMetadata Content { get; }
    public bool IsTexture => Content.IsTexture;

    public bool DeclaredExtensionMatchesContent
    {
        get
        {
            var extension = DeclaredExtension.ToLowerInvariant();
            return Content.Kind switch
            {
                PfsContentKind.Dds => extension == ".dds",
                PfsContentKind.Bitmap => extension == ".bmp",
                PfsContentKind.Targa => extension == ".tga",
                PfsContentKind.Png => extension == ".png",
                PfsContentKind.Jpeg => extension is ".jpg" or ".jpeg",
                _ => true
            };
        }
    }

    internal PfsStoredEntry StoredEntry { get; }
}

internal sealed class PfsStoredEntry
{
    public required int DirectoryIndex { get; init; }
    public required uint Crc { get; init; }
    public required long Offset { get; init; }
    public required long UncompressedSize { get; init; }
    public required IReadOnlyList<PfsChunkDescriptor> Chunks { get; init; }
    public required long StoredLength { get; init; }
    public bool IsFilenameDirectory => Crc == PfsFormat.FilenameDirectoryCrc;
}

internal readonly record struct PfsChunkDescriptor(
    long HeaderOffset,
    int CompressedLength,
    int UncompressedLength)
{
    public long DataOffset => HeaderOffset + (sizeof(uint) * 2L);
    public long StoredLength => (sizeof(uint) * 2L) + CompressedLength;
}
