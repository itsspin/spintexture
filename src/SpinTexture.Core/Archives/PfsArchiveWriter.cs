using System.Buffers.Binary;

namespace SpinTexture.Core.Archives;

public static class PfsArchiveWriter
{
    public static async Task WriteAsync(
        Stream destination,
        IEnumerable<PfsArchiveItem> items,
        PfsArchiveWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(items);

        options ??= new PfsArchiveWriteOptions();
        options.Validate();
        ValidateDestination(destination);

        var materializedItems = items.ToArray();
        ValidateItems(materializedItems);
        var orderedItems = materializedItems
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        destination.SetLength(0);
        destination.Position = 0;
        await WriteHeaderAsync(destination, directoryOffset: 0, cancellationToken).ConfigureAwait(false);

        var records = new List<WriteRecord>(orderedItems.Length + 1);
        foreach (var item in orderedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = CheckedUInt32(destination.Position, "PFS entry offset");
            await PfsStreamIo.WriteCompressedPayloadAsync(
                destination,
                item.Data,
                options,
                cancellationToken).ConfigureAwait(false);
            records.Add(new WriteRecord(
                PfsFormat.ComputeFilenameCrc(item.Name),
                offset,
                CheckedUInt32(item.Data.Length, "PFS entry size"),
                item.Name));
        }

        var filenameDirectory = BuildFilenameDirectory(orderedItems);
        var filenameDirectoryOffset = CheckedUInt32(destination.Position, "filename directory offset");
        await PfsStreamIo.WriteCompressedPayloadAsync(
            destination,
            filenameDirectory,
            options,
            cancellationToken).ConfigureAwait(false);
        records.Add(new WriteRecord(
            PfsFormat.FilenameDirectoryCrc,
            filenameDirectoryOffset,
            CheckedUInt32(filenameDirectory.Length, "filename directory size"),
            null));

        var directoryOffset = CheckedUInt32(destination.Position, "PFS directory offset");
        await WriteDirectoryAsync(
            destination,
            records
                .OrderBy(record => record.Crc)
                .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);

        var end = destination.Position;
        destination.Position = 0;
        await WriteHeaderAsync(destination, directoryOffset, cancellationToken).ConfigureAwait(false);
        destination.Position = end;
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask WriteHeaderAsync(
        Stream destination,
        uint directoryOffset,
        CancellationToken cancellationToken)
    {
        var header = new byte[PfsFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, directoryOffset);
        PfsFormat.Magic.CopyTo(header.AsSpan(sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(uint) * 2), PfsFormat.Version2);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask WriteDirectoryAsync(
        Stream destination,
        IReadOnlyList<WriteRecord> records,
        CancellationToken cancellationToken)
    {
        await PfsStreamIo.WriteUInt32Async(
            destination,
            CheckedUInt32(records.Count, "PFS directory record count"),
            cancellationToken).ConfigureAwait(false);

        var bytes = new byte[records.Count * PfsFormat.DirectoryRecordSize];
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var destinationRecord = bytes.AsSpan(index * PfsFormat.DirectoryRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(destinationRecord, record.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(destinationRecord[sizeof(uint)..], record.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(destinationRecord[(sizeof(uint) * 2)..], record.Size);
        }

        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static uint CheckedUInt32(long value, string description)
    {
        if (value is < 0 or > uint.MaxValue)
        {
            throw new PfsArchiveException($"The {description} exceeds the PFS 32-bit format limit.");
        }

        return (uint)value;
    }

    internal sealed record WriteRecord(uint Crc, uint Offset, uint Size, string? Name);

    private static byte[] BuildFilenameDirectory(IReadOnlyList<PfsArchiveItem> items)
    {
        using var output = new MemoryStream();
        Span<byte> integer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(integer, CheckedUInt32(items.Count, "filename count"));
        output.Write(integer);

        foreach (var item in items)
        {
            var encoded = PfsFormat.NameEncoding.GetBytes(item.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(
                integer,
                CheckedUInt32(encoded.Length + 1L, "encoded filename length"));
            output.Write(integer);
            output.Write(encoded);
            output.WriteByte(0);
        }

        return output.ToArray();
    }

    private static void ValidateDestination(Stream destination)
    {
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException("A PFS destination must be writable and seekable.", nameof(destination));
        }
    }

    private static void ValidateItems(IReadOnlyList<PfsArchiveItem> items)
    {
        if (items.Count >= 250_000)
        {
            throw new ArgumentException("The requested PFS archive contains too many entries.", nameof(items));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!names.Add(item.Name))
            {
                throw new ArgumentException($"The PFS filename '{item.Name}' is duplicated.", nameof(items));
            }

            _ = PfsFormat.ComputeFilenameCrc(item.Name);
        }
    }
}
