using System.IO.Compression;

namespace SpinTexture.Core.Archives;

public sealed record PfsArchiveReadOptions
{
    public bool SniffContent { get; init; } = true;
    public int ContentSniffBytes { get; init; } = 512;
    public int MaximumEntryCount { get; init; } = 250_000;
    public long MaximumEntryBytes { get; init; } = uint.MaxValue;
    public int MaximumCompressedChunkBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumUncompressedChunkBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumFilenameDirectoryBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (ContentSniffBytes is < 0 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(ContentSniffBytes));
        }

        if (MaximumEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntryCount));
        }

        if (MaximumEntryBytes < 0 || MaximumEntryBytes > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntryBytes));
        }

        if (MaximumCompressedChunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCompressedChunkBytes));
        }

        if (MaximumUncompressedChunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumUncompressedChunkBytes));
        }

        if (MaximumFilenameDirectoryBytes < sizeof(uint))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFilenameDirectoryBytes));
        }
    }
}

public sealed record PfsArchiveWriteOptions
{
    public int ChunkSize { get; init; } = PfsFormat.DefaultChunkSize;
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    internal void Validate()
    {
        if (ChunkSize is <= 0 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize));
        }
    }
}

public sealed record PfsArchiveItem(string Name, ReadOnlyMemory<byte> Data);

public sealed record PfsArchiveReplacement(string Name, ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// Gets the file supplying this replacement, or <see langword="null"/> for an in-memory replacement.
    /// </summary>
    public string? SourceFilePath { get; private init; }

    /// <summary>
    /// Gets whether the replacement payload will be streamed from a file during the archive rebuild.
    /// </summary>
    public bool IsFileBacked => SourceFilePath is not null;

    /// <summary>
    /// Creates a replacement whose payload is streamed from <paramref name="sourceFilePath"/>.
    /// The file is opened and validated by the rebuild before the destination is modified.
    /// </summary>
    public static PfsArchiveReplacement FromFile(string name, string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        return new PfsArchiveReplacement(name, ReadOnlyMemory<byte>.Empty)
        {
            SourceFilePath = Path.GetFullPath(sourceFilePath)
        };
    }
}
