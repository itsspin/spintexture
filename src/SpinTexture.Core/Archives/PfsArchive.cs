using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace SpinTexture.Core.Archives;

/// <summary>
/// A validated, read-only view of an EverQuest PFS 2 archive. Rebuild operations
/// write to a separate destination and never mutate the source archive.
/// </summary>
public sealed class PfsArchive : IDisposable, IAsyncDisposable
{
    private readonly Stream source;
    private readonly bool leaveOpen;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private readonly IReadOnlyList<PfsStoredEntry> storedEntries;
    private readonly Dictionary<string, PfsArchiveEntry> entriesByName;
    private int disposed;

    private PfsArchive(
        Stream source,
        bool leaveOpen,
        string displayName,
        string? archivePath,
        uint version,
        IReadOnlyList<PfsStoredEntry> storedEntries,
        IReadOnlyList<PfsArchiveEntry> entries)
    {
        this.source = source;
        this.leaveOpen = leaveOpen;
        DisplayName = displayName;
        ArchivePath = archivePath;
        Version = version;
        this.storedEntries = storedEntries;
        Entries = new ReadOnlyCollection<PfsArchiveEntry>(entries.ToArray());
        entriesByName = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string DisplayName { get; }
    public string? ArchivePath { get; }
    public uint Version { get; }
    public IReadOnlyList<PfsArchiveEntry> Entries { get; }

    public static bool IsSupportedExtension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path);
        if (extension.Length == 0 && path.StartsWith('.'))
        {
            extension = path;
        }

        return extension.ToLowerInvariant() is ".s3d" or ".eqg" or ".pfs" or ".pak";
    }

    public static async Task<PfsArchive> OpenAsync(
        string archivePath,
        PfsArchiveReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 128 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess
        });

        try
        {
            return await OpenCoreAsync(
                stream,
                leaveOpen: false,
                Path.GetFileName(fullPath),
                fullPath,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<PfsArchive> OpenAsync(
        Stream source,
        string displayName = "memory.pfs",
        bool leaveOpen = false,
        PfsArchiveReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        try
        {
            return await OpenCoreAsync(
                source,
                leaveOpen,
                displayName,
                null,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!leaveOpen)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public bool TryGetEntry(string name, out PfsArchiveEntry? entry)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(name);
        return entriesByName.TryGetValue(name, out entry);
    }

    public PfsArchiveEntry GetEntry(string name)
    {
        if (!TryGetEntry(name, out var entry) || entry is null)
        {
            throw new KeyNotFoundException($"The PFS archive does not contain '{name}'.");
        }

        return entry;
    }

    public async Task<byte[]> ReadEntryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(name);
        if (entry.UncompressedSize > Array.MaxLength)
        {
            throw new PfsArchiveException(
                $"The entry '{entry.Name}' is too large to return as one byte array. " +
                "Use ExtractEntryAsync with a destination stream instead.");
        }

        using var output = new MemoryStream(checked((int)entry.UncompressedSize));
        await ExtractEntryAsync(entry, output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public Task ExtractEntryAsync(
        string name,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        ExtractEntryAsync(GetEntry(name), destination, cancellationToken);

    public async Task ExtractEntryAsync(
        PfsArchiveEntry entry,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The extraction destination must be writable.", nameof(destination));
        }

        if (ReferenceEquals(source, destination))
        {
            throw new ArgumentException("The source archive cannot also be the extraction destination.", nameof(destination));
        }

        if (!entriesByName.TryGetValue(entry.Name, out var ownedEntry) || !ReferenceEquals(entry, ownedEntry))
        {
            throw new ArgumentException("The supplied entry does not belong to this PFS archive.", nameof(entry));
        }

        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            await PfsStreamIo.DecompressEntryToAsync(
                source,
                entry.StoredEntry,
                destination,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ioGate.Release();
        }
    }

    internal async Task<byte[]> ComputeStoredEntrySha256Async(
        PfsArchiveEntry entry,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entriesByName.TryGetValue(entry.Name, out var ownedEntry)
            || !ReferenceEquals(entry, ownedEntry))
        {
            throw new ArgumentException(
                "The supplied entry does not belong to this PFS archive.",
                nameof(entry));
        }

        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            using var algorithm = SHA256.Create();
            await using (var hashingStream = new CryptoStream(
                Stream.Null,
                algorithm,
                CryptoStreamMode.Write,
                leaveOpen: true))
            {
                await PfsStreamIo.CopyRangeAsync(
                    source,
                    entry.StoredEntry.Offset,
                    entry.StoredEntry.StoredLength,
                    hashingStream,
                    cancellationToken).ConfigureAwait(false);
                hashingStream.FlushFinalBlock();
            }

            return algorithm.Hash
                ?? throw new InvalidOperationException(
                    "SHA-256 did not produce a stored-entry hash.");
        }
        finally
        {
            ioGate.Release();
        }
    }

    public async Task ExtractAllAsync(
        string outputDirectory,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        foreach (var entry in Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = GetSafeExtractionPath(root, entry.Name);
            var parent = Path.GetDirectoryName(destinationPath)
                ?? throw new PfsArchiveException($"Unable to resolve the extraction path for '{entry.Name}'.");
            Directory.CreateDirectory(parent);

            await using var destination = new FileStream(destinationPath, new FileStreamOptions
            {
                Mode = overwrite ? FileMode.Create : FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 128 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            await ExtractEntryAsync(entry, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RebuildAsync(
        Stream destination,
        IEnumerable<PfsArchiveReplacement>? replacements = null,
        PfsArchiveWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException("A PFS rebuild destination must be writable and seekable.", nameof(destination));
        }

        if (ReferenceEquals(source, destination))
        {
            throw new ArgumentException("A PFS archive cannot be rebuilt over its source stream.", nameof(destination));
        }

        options ??= new PfsArchiveWriteOptions();
        options.Validate();
        var replacementMap = ValidateReplacements(replacements ?? []);

        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            destination.SetLength(0);
            destination.Position = 0;
            await PfsArchiveWriter.WriteHeaderAsync(destination, 0, cancellationToken).ConfigureAwait(false);

            var recordsByIndex = new PfsArchiveWriter.WriteRecord[storedEntries.Count];
            // PfsStoredEntry has reference identity, so the default comparer is
            // both exact and O(1); the old linear Entries.FirstOrDefault lookup
            // made large archive rebuilds quadratic.
            var logicalEntriesByStoredEntry = Entries.ToDictionary(entry => entry.StoredEntry);
            var physicalEntries = storedEntries
                .OrderBy(entry => entry.Offset)
                .ThenBy(entry => entry.StoredLength == 0 ? 0 : 1)
                .ThenBy(entry => entry.DirectoryIndex)
                .ToArray();

            foreach (var storedEntry in physicalEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = PfsArchiveWriter.CheckedUInt32(destination.Position, "rebuilt entry offset");
                uint size;
                string? name = null;

                if (!storedEntry.IsFilenameDirectory
                    && logicalEntriesByStoredEntry.TryGetValue(storedEntry, out var logicalEntry)
                    && replacementMap.TryGetValue(logicalEntry.Name, out var replacement))
                {
                    if (replacement.SourceFilePath is not null)
                    {
                        await using var replacementStream = OpenReplacementFile(replacement);
                        await PfsStreamIo.WriteCompressedPayloadAsync(
                            destination,
                            replacementStream,
                            replacement.Length,
                            options,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await PfsStreamIo.WriteCompressedPayloadAsync(
                            destination,
                            replacement.Replacement.Data,
                            options,
                            cancellationToken).ConfigureAwait(false);
                    }

                    size = PfsArchiveWriter.CheckedUInt32(replacement.Length, "replacement entry size");
                    name = logicalEntry.Name;
                }
                else
                {
                    await PfsStreamIo.CopyRangeAsync(
                        source,
                        storedEntry.Offset,
                        storedEntry.StoredLength,
                        destination,
                        cancellationToken).ConfigureAwait(false);
                    size = PfsArchiveWriter.CheckedUInt32(storedEntry.UncompressedSize, "preserved entry size");
                    if (logicalEntriesByStoredEntry.TryGetValue(storedEntry, out logicalEntry))
                    {
                        name = logicalEntry.Name;
                    }
                }

                recordsByIndex[storedEntry.DirectoryIndex] = new PfsArchiveWriter.WriteRecord(
                    storedEntry.Crc,
                    offset,
                    size,
                    name);
            }

            var directoryOffset = PfsArchiveWriter.CheckedUInt32(destination.Position, "rebuilt directory offset");
            await PfsArchiveWriter.WriteDirectoryAsync(
                destination,
                recordsByIndex,
                cancellationToken).ConfigureAwait(false);

            var end = destination.Position;
            destination.Position = 0;
            await PfsArchiveWriter.WriteHeaderAsync(
                destination,
                directoryOffset,
                cancellationToken).ConfigureAwait(false);
            destination.Position = end;
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ioGate.Release();
        }
    }

    public async Task RebuildToFileAsync(
        string destinationPath,
        IEnumerable<PfsArchiveReplacement>? replacements = null,
        bool overwrite = false,
        PfsArchiveWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (ArchivePath is not null
            && fullDestinationPath.Equals(ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SpinTexture rebuilds must target staging; rebuilding over the source archive is not allowed.");
        }

        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("The destination path has no parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        if (!overwrite && File.Exists(fullDestinationPath))
        {
            throw new IOException($"The destination archive already exists: {fullDestinationPath}");
        }

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 128 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                await RebuildAsync(output, replacements, options, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullDestinationPath, overwrite);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!leaveOpen)
        {
            source.Dispose();
        }

        ioGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!leaveOpen)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        ioGate.Dispose();
    }

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private static async Task<PfsArchive> OpenCoreAsync(
        Stream source,
        bool leaveOpen,
        string displayName,
        string? archivePath,
        PfsArchiveReadOptions? options,
        CancellationToken cancellationToken)
    {
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("A PFS source must be readable and seekable.", nameof(source));
        }

        options ??= new PfsArchiveReadOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        if (source.Length < PfsFormat.HeaderSize + sizeof(uint))
        {
            throw new PfsArchiveException("The stream is too short to contain a PFS archive.");
        }

        var header = new byte[PfsFormat.HeaderSize];
        await PfsStreamIo.ReadExactlyAsync(source, header, cancellationToken).ConfigureAwait(false);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (!header.AsSpan(sizeof(uint), PfsFormat.Magic.Length).SequenceEqual(PfsFormat.Magic))
        {
            throw new PfsArchiveException("The stream does not contain the PFS magic signature.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sizeof(uint) * 2));
        if (version != PfsFormat.Version2)
        {
            throw new PfsArchiveException($"Unsupported PFS version 0x{version:X8}; only PFS 2 is supported.");
        }

        if (directoryOffset < PfsFormat.HeaderSize || directoryOffset > source.Length - sizeof(uint))
        {
            throw new PfsArchiveException("The PFS directory offset is outside the archive.");
        }

        source.Position = directoryOffset;
        var entryCountValue = await PfsStreamIo.ReadUInt32Async(source, cancellationToken).ConfigureAwait(false);
        if (entryCountValue is 0 || entryCountValue > options.MaximumEntryCount)
        {
            throw new PfsArchiveException($"The PFS directory count {entryCountValue} is invalid.");
        }

        var entryCount = checked((int)entryCountValue);
        var directoryByteCount = checked(entryCount * PfsFormat.DirectoryRecordSize);
        if (directoryByteCount > source.Length - source.Position)
        {
            throw new PfsArchiveException("The PFS directory extends beyond the archive.");
        }

        var directoryBytes = new byte[directoryByteCount];
        await PfsStreamIo.ReadExactlyAsync(source, directoryBytes, cancellationToken).ConfigureAwait(false);
        var rawRecords = new RawDirectoryRecord[entryCount];
        for (var index = 0; index < entryCount; index++)
        {
            var record = directoryBytes.AsSpan(index * PfsFormat.DirectoryRecordSize);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[sizeof(uint)..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[(sizeof(uint) * 2)..]);
            if (size > options.MaximumEntryBytes)
            {
                throw new PfsArchiveException(
                    $"PFS directory record {index} exceeds the configured entry-size limit.");
            }

            rawRecords[index] = new RawDirectoryRecord(index, crc, offset, size);
        }

        var storedEntries = new List<PfsStoredEntry>(entryCount);
        foreach (var record in rawRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            storedEntries.Add(await ReadStoredEntryAsync(
                source,
                directoryOffset,
                record,
                options,
                cancellationToken).ConfigureAwait(false));
        }

        ValidateStoredRanges(storedEntries, directoryOffset);
        var filenameDirectories = storedEntries.Where(entry => entry.IsFilenameDirectory).ToArray();
        PfsStoredEntry filenameDirectory;
        var usesLegacyFilenameDirectoryCrc = false;
        if (filenameDirectories.Length == 1)
        {
            filenameDirectory = filenameDirectories[0];
        }
        else if (filenameDirectories.Length == 0)
        {
            // A small number of live legacy archives label their otherwise-valid
            // filename directory with 0xFFFFFFFF. Accept only a unique candidate;
            // its payload and mapping must still prove a complete bijection below.
            var legacyCandidates = storedEntries
                .Where(entry => entry.Crc == uint.MaxValue)
                .ToArray();
            if (legacyCandidates.Length != 1)
            {
                throw new PfsArchiveException(
                    "The PFS archive has no canonical filename directory and does not "
                    + "contain one unique legacy candidate.");
            }

            filenameDirectory = legacyCandidates[0];
            usesLegacyFilenameDirectoryCrc = true;
        }
        else
        {
            throw new PfsArchiveException(
                $"The PFS archive must contain exactly one filename directory; found {filenameDirectories.Length}.");
        }

        if (filenameDirectory.UncompressedSize > options.MaximumFilenameDirectoryBytes
            || filenameDirectory.UncompressedSize > Array.MaxLength)
        {
            throw new PfsArchiveException("The PFS filename directory exceeds the configured safety limit.");
        }

        using var filenameBytes = new MemoryStream(checked((int)filenameDirectory.UncompressedSize));
        await PfsStreamIo.DecompressEntryToAsync(
            source,
            filenameDirectory,
            filenameBytes,
            cancellationToken).ConfigureAwait(false);
        var filenames = ParseFilenameDirectory(
            filenameBytes.GetBuffer().AsSpan(0, checked((int)filenameBytes.Length)),
            options.MaximumEntryCount);

        var dataEntryCount = entryCount - 1;
        if (usesLegacyFilenameDirectoryCrc && filenames.Count != dataEntryCount)
        {
            throw new PfsArchiveException(
                $"The legacy PFS filename directory declares {filenames.Count} names; "
                + $"{dataEntryCount} were required for a bijective fallback.");
        }

        if (!usesLegacyFilenameDirectoryCrc && filenames.Count < dataEntryCount)
        {
            throw new PfsArchiveException(
                $"The PFS filename directory declares {filenames.Count} names; "
                + $"at least {dataEntryCount} were required.");
        }

        var logicalMappings = MapFilenamesToEntries(
            filenames,
            storedEntries,
            filenameDirectory,
            allowSurplusNames: !usesLegacyFilenameDirectoryCrc);
        var logicalEntries = new PfsArchiveEntry[logicalMappings.Count];
        for (var index = 0; index < logicalMappings.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapping = logicalMappings[index];
            var metadata = new PfsContentMetadata(PfsContentKind.Unknown);
            if (options.SniffContent && options.ContentSniffBytes > 0)
            {
                var prefix = await PfsStreamIo.ReadPrefixAsync(
                    source,
                    mapping.StoredEntry,
                    options.ContentSniffBytes,
                    cancellationToken).ConfigureAwait(false);
                metadata = PfsContentSniffer.Sniff(prefix, mapping.Name);
            }

            logicalEntries[index] = new PfsArchiveEntry(mapping.Name, mapping.StoredEntry, metadata);
        }

        return new PfsArchive(
            source,
            leaveOpen,
            displayName,
            archivePath,
            version,
            new ReadOnlyCollection<PfsStoredEntry>(storedEntries),
            logicalEntries);
    }

    private static async ValueTask<PfsStoredEntry> ReadStoredEntryAsync(
        Stream source,
        long directoryOffset,
        RawDirectoryRecord record,
        PfsArchiveReadOptions options,
        CancellationToken cancellationToken)
    {
        var entryOffset = (long)record.Offset;
        var entrySize = (long)record.Size;
        if (entryOffset < PfsFormat.HeaderSize || entryOffset > directoryOffset)
        {
            throw new PfsArchiveException(
                $"PFS directory record {record.Index} has an invalid data offset {entryOffset}.");
        }

        if (entrySize == 0)
        {
            return new PfsStoredEntry
            {
                DirectoryIndex = record.Index,
                Crc = record.Crc,
                Offset = entryOffset,
                UncompressedSize = 0,
                Chunks = Array.Empty<PfsChunkDescriptor>(),
                StoredLength = 0
            };
        }

        if (entryOffset > directoryOffset - (sizeof(uint) * 2L))
        {
            throw new PfsArchiveException(
                $"PFS directory record {record.Index} has no room for a chunk header.");
        }

        source.Position = entryOffset;
        var chunks = new List<PfsChunkDescriptor>();
        var produced = 0L;
        while (produced < entrySize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerOffset = source.Position;
            if (headerOffset > directoryOffset - (sizeof(uint) * 2L))
            {
                throw new PfsArchiveException(
                    $"PFS directory record {record.Index} has a truncated chunk header.");
            }

            var chunkHeader = new byte[sizeof(uint) * 2];
            await PfsStreamIo.ReadExactlyAsync(source, chunkHeader, cancellationToken).ConfigureAwait(false);
            var compressedLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            var uncompressedLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(sizeof(uint)));
            if (compressedLengthValue is 0 or > int.MaxValue
                || compressedLengthValue > options.MaximumCompressedChunkBytes)
            {
                throw new PfsArchiveException(
                    $"PFS directory record {record.Index} has invalid compressed chunk length " +
                    $"{compressedLengthValue}.");
            }

            if (uncompressedLengthValue is 0 or > int.MaxValue
                || uncompressedLengthValue > options.MaximumUncompressedChunkBytes)
            {
                throw new PfsArchiveException(
                    $"PFS directory record {record.Index} has invalid uncompressed chunk length " +
                    $"{uncompressedLengthValue}.");
            }

            var compressedLength = checked((int)compressedLengthValue);
            var uncompressedLength = checked((int)uncompressedLengthValue);
            if (uncompressedLength > entrySize - produced)
            {
                throw new PfsArchiveException(
                    $"PFS directory record {record.Index} chunks exceed its declared size.");
            }

            if (source.Position > directoryOffset - compressedLength)
            {
                throw new PfsArchiveException(
                    $"PFS directory record {record.Index} has a compressed chunk outside the data area.");
            }

            chunks.Add(new PfsChunkDescriptor(headerOffset, compressedLength, uncompressedLength));
            source.Position += compressedLength;
            produced += uncompressedLength;
        }

        return new PfsStoredEntry
        {
            DirectoryIndex = record.Index,
            Crc = record.Crc,
            Offset = entryOffset,
            UncompressedSize = entrySize,
            Chunks = new ReadOnlyCollection<PfsChunkDescriptor>(chunks),
            StoredLength = source.Position - entryOffset
        };
    }

    private static IReadOnlyList<string> ParseFilenameDirectory(
        ReadOnlySpan<byte> bytes,
        int maximumEntryCount)
    {
        if (bytes.Length < sizeof(uint))
        {
            throw new PfsArchiveException("The PFS filename directory is truncated.");
        }

        var filenameCountValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (filenameCountValue > maximumEntryCount)
        {
            throw new PfsArchiveException(
                $"The PFS filename directory declares {filenameCountValue} names, "
                + $"exceeding the configured maximum of {maximumEntryCount}.");
        }

        var filenameCount = checked((int)filenameCountValue);
        var filenames = new string[filenameCount];
        var position = sizeof(uint);
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < filenameCount; index++)
        {
            if (position > bytes.Length - sizeof(uint))
            {
                throw new PfsArchiveException("The PFS filename directory is truncated before a name length.");
            }

            var nameLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
            position += sizeof(uint);
            if (nameLengthValue is 0 or > int.MaxValue || nameLengthValue > bytes.Length - position)
            {
                throw new PfsArchiveException($"PFS filename {index} has an invalid encoded length.");
            }

            var nameLength = checked((int)nameLengthValue);
            var encodedName = bytes.Slice(position, nameLength);
            position += nameLength;
            if (encodedName[^1] != 0 || encodedName[..^1].Contains((byte)0))
            {
                throw new PfsArchiveException($"PFS filename {index} is not correctly null terminated.");
            }

            var name = PfsFormat.NameEncoding.GetString(encodedName[..^1]);
            if (name.Length == 0 || !uniqueNames.Add(name))
            {
                throw new PfsArchiveException($"The PFS filename '{name}' is empty or duplicated.");
            }

            filenames[index] = name;
        }

        if (position != bytes.Length)
        {
            throw new PfsArchiveException("The PFS filename directory contains unexpected trailing bytes.");
        }

        return filenames;
    }

    private static IReadOnlyList<LogicalMapping> MapFilenamesToEntries(
        IReadOnlyList<string> filenames,
        IReadOnlyList<PfsStoredEntry> storedEntries,
        PfsStoredEntry filenameDirectory,
        bool allowSurplusNames)
    {
        var entriesByCrc = storedEntries
            .Where(entry => entry.DirectoryIndex != filenameDirectory.DirectoryIndex)
            .GroupBy(entry => entry.Crc)
            .ToDictionary(
                group => group.Key,
                group => new Queue<PfsStoredEntry>(group.OrderBy(entry => entry.DirectoryIndex)));
        var dataRecordCrcs = entriesByCrc.Keys.ToHashSet();

        var mappings = new List<LogicalMapping>(entriesByCrc.Values.Sum(queue => queue.Count));
        foreach (var name in filenames)
        {
            var crc = PfsFormat.ComputeFilenameCrc(name);
            if (!entriesByCrc.TryGetValue(crc, out var candidates) || candidates.Count == 0)
            {
                if (allowSurplusNames && !dataRecordCrcs.Contains(crc))
                {
                    continue;
                }

                throw new PfsArchiveException(
                    $"PFS filename '{name}' has CRC 0x{crc:X8}, but no matching directory record exists.");
            }

            mappings.Add(new LogicalMapping(name, candidates.Dequeue()));
        }

        var unmatched = entriesByCrc.Values.Sum(queue => queue.Count);
        if (unmatched != 0)
        {
            throw new PfsArchiveException($"The PFS archive has {unmatched} directory records without filenames.");
        }

        return mappings.ToArray();
    }

    private static void ValidateStoredRanges(IReadOnlyList<PfsStoredEntry> entries, long directoryOffset)
    {
        PfsStoredEntry? previous = null;
        foreach (var entry in entries
            .Where(entry => entry.StoredLength > 0)
            .OrderBy(entry => entry.Offset)
            .ThenBy(entry => entry.StoredLength))
        {
            if (entry.Offset + entry.StoredLength > directoryOffset)
            {
                throw new PfsArchiveException(
                    $"PFS directory record {entry.DirectoryIndex} crosses into the directory table.");
            }

            if (previous is not null && entry.Offset < previous.Offset + previous.StoredLength)
            {
                var exactAlias = entry.Offset == previous.Offset
                    && entry.StoredLength == previous.StoredLength
                    && entry.UncompressedSize == previous.UncompressedSize;
                if (!exactAlias)
                {
                    throw new PfsArchiveException(
                        $"PFS directory records {previous.DirectoryIndex} and {entry.DirectoryIndex} overlap.");
                }
            }

            previous = entry;
        }
    }

    private Dictionary<string, ValidatedReplacement> ValidateReplacements(
        IEnumerable<PfsArchiveReplacement> replacements)
    {
        var replacementMap = new Dictionary<string, ValidatedReplacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in replacements)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            if (!entriesByName.ContainsKey(replacement.Name))
            {
                throw new KeyNotFoundException(
                    $"The replacement '{replacement.Name}' does not exist in the source PFS archive.");
            }

            var validated = ValidateReplacementSource(replacement);
            if (!replacementMap.TryAdd(replacement.Name, validated))
            {
                throw new ArgumentException(
                    $"The PFS replacement '{replacement.Name}' was supplied more than once.",
                    nameof(replacements));
            }
        }

        return replacementMap;
    }

    private static ValidatedReplacement ValidateReplacementSource(PfsArchiveReplacement replacement)
    {
        if (replacement.SourceFilePath is null)
        {
            return new ValidatedReplacement(replacement, null, replacement.Data.Length);
        }

        using var sourceFile = OpenReplacementFile(replacement.SourceFilePath);
        _ = PfsArchiveWriter.CheckedUInt32(sourceFile.Length, "replacement entry size");
        return new ValidatedReplacement(replacement, replacement.SourceFilePath, sourceFile.Length);
    }

    private static FileStream OpenReplacementFile(ValidatedReplacement replacement)
    {
        var stream = OpenReplacementFile(
            replacement.SourceFilePath
            ?? throw new InvalidOperationException("The replacement is not file-backed."));
        if (stream.Length != replacement.Length)
        {
            stream.Dispose();
            throw new IOException(
                $"The PFS replacement source changed size before it could be archived: " +
                $"{replacement.SourceFilePath}");
        }

        return stream;
    }

    private static FileStream OpenReplacementFile(string path)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 128 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
    }

    private static string GetSafeExtractionPath(string root, string logicalName)
    {
        if (Path.IsPathRooted(logicalName))
        {
            throw new PfsArchiveException($"The rooted archive path '{logicalName}' cannot be extracted.");
        }

        var relativePath = logicalName
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PfsArchiveException(
                $"The archive path '{logicalName}' attempts to escape the extraction directory.");
        }

        return candidate;
    }

    private readonly record struct RawDirectoryRecord(int Index, uint Crc, uint Offset, uint Size);
    private readonly record struct LogicalMapping(string Name, PfsStoredEntry StoredEntry);
    private sealed record ValidatedReplacement(
        PfsArchiveReplacement Replacement,
        string? SourceFilePath,
        long Length);
}
