using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace SpinTexture.Core.Archives;

internal static class PfsStreamIo
{
    private const int CopyBufferSize = 128 * 1024;

    public static async ValueTask<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(uint)];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static async ValueTask WriteUInt32Async(
        Stream stream,
        uint value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new PfsArchiveException("The PFS archive ended before the requested data was available.", exception);
        }
    }

    public static async ValueTask<byte[]> ReadPrefixAsync(
        Stream source,
        PfsStoredEntry entry,
        int requestedBytes,
        CancellationToken cancellationToken)
    {
        if (requestedBytes <= 0 || entry.UncompressedSize == 0 || entry.Chunks.Count == 0)
        {
            return [];
        }

        var chunk = entry.Chunks[0];
        var prefixLength = Math.Min(requestedBytes, chunk.UncompressedLength);
        var prefix = new byte[prefixLength];
        var compressed = ArrayPool<byte>.Shared.Rent(chunk.CompressedLength);
        try
        {
            source.Position = chunk.DataOffset;
            await ReadExactlyAsync(
                source,
                compressed.AsMemory(0, chunk.CompressedLength),
                cancellationToken).ConfigureAwait(false);

            using var compressedStream = new MemoryStream(
                compressed,
                0,
                chunk.CompressedLength,
                writable: false,
                publiclyVisible: true);
            await using var decompressor = new ZLibStream(
                compressedStream,
                CompressionMode.Decompress,
                leaveOpen: false);
            await ReadExactlyAsync(decompressor, prefix, cancellationToken).ConfigureAwait(false);
            return prefix;
        }
        catch (InvalidDataException exception)
        {
            throw new PfsArchiveException(
                $"The compressed data for PFS directory record {entry.DirectoryIndex} is invalid.",
                exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    public static async ValueTask DecompressEntryToAsync(
        Stream source,
        PfsStoredEntry entry,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var totalWritten = 0L;
        foreach (var chunk in entry.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DecompressChunkToAsync(source, chunk, destination, cancellationToken).ConfigureAwait(false);
            totalWritten = checked(totalWritten + chunk.UncompressedLength);
        }

        if (totalWritten != entry.UncompressedSize)
        {
            throw new PfsArchiveException(
                $"PFS directory record {entry.DirectoryIndex} declared {entry.UncompressedSize} bytes " +
                $"but its chunks produced {totalWritten} bytes.");
        }
    }

    public static async ValueTask CopyRangeAsync(
        Stream source,
        long offset,
        long length,
        Stream destination,
        CancellationToken cancellationToken)
    {
        source.Position = offset;
        var remaining = length;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, remaining);
                await ReadExactlyAsync(source, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<long> WriteCompressedPayloadAsync(
        Stream destination,
        ReadOnlyMemory<byte> payload,
        PfsArchiveWriteOptions options,
        CancellationToken cancellationToken)
    {
        var start = destination.Position;
        var offset = 0;
        while (offset < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(options.ChunkSize, payload.Length - offset);
            await WriteCompressedChunkAsync(
                destination,
                payload.Slice(offset, chunkLength),
                options,
                cancellationToken).ConfigureAwait(false);
            offset = checked(offset + chunkLength);
        }

        return destination.Position - start;
    }

    public static async ValueTask<long> WriteCompressedPayloadAsync(
        Stream destination,
        Stream payload,
        long payloadLength,
        PfsArchiveWriteOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.CanRead)
        {
            throw new ArgumentException("A streamed PFS replacement must be readable.", nameof(payload));
        }

        if (payloadLength is < 0 or > uint.MaxValue)
        {
            throw new PfsArchiveException("A replacement entry exceeded the PFS 32-bit format limit.");
        }

        var start = destination.Position;
        var remaining = payloadLength;
        if (remaining == 0)
        {
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(options.ChunkSize, remaining));
        try
        {
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkLength = (int)Math.Min(options.ChunkSize, remaining);
                await ReadExactlyAsync(
                    payload,
                    buffer.AsMemory(0, chunkLength),
                    cancellationToken).ConfigureAwait(false);
                await WriteCompressedChunkAsync(
                    destination,
                    buffer.AsMemory(0, chunkLength),
                    options,
                    cancellationToken).ConfigureAwait(false);
                remaining -= chunkLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return destination.Position - start;
    }

    private static async ValueTask WriteCompressedChunkAsync(
        Stream destination,
        ReadOnlyMemory<byte> chunk,
        PfsArchiveWriteOptions options,
        CancellationToken cancellationToken)
    {
        using var compressed = new MemoryStream();
        await using (var compressor = new ZLibStream(
            compressed,
            options.CompressionLevel,
            leaveOpen: true))
        {
            await compressor.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        if (compressed.Length > uint.MaxValue)
        {
            throw new PfsArchiveException("A compressed PFS chunk exceeded the 32-bit format limit.");
        }

        await WriteUInt32Async(destination, (uint)compressed.Length, cancellationToken).ConfigureAwait(false);
        await WriteUInt32Async(destination, (uint)chunk.Length, cancellationToken).ConfigureAwait(false);

        if (!compressed.TryGetBuffer(out var segment) || segment.Array is null)
        {
            throw new InvalidOperationException("The in-memory zlib buffer was unexpectedly unavailable.");
        }

        await destination.WriteAsync(
            segment.Array.AsMemory(segment.Offset, checked((int)compressed.Length)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask DecompressChunkToAsync(
        Stream source,
        PfsChunkDescriptor chunk,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var compressed = ArrayPool<byte>.Shared.Rent(chunk.CompressedLength);
        var output = ArrayPool<byte>.Shared.Rent(Math.Min(CopyBufferSize, chunk.UncompressedLength));
        try
        {
            source.Position = chunk.DataOffset;
            await ReadExactlyAsync(
                source,
                compressed.AsMemory(0, chunk.CompressedLength),
                cancellationToken).ConfigureAwait(false);

            using var compressedStream = new MemoryStream(
                compressed,
                0,
                chunk.CompressedLength,
                writable: false,
                publiclyVisible: true);
            await using var decompressor = new ZLibStream(
                compressedStream,
                CompressionMode.Decompress,
                leaveOpen: false);

            var remaining = chunk.UncompressedLength;
            while (remaining > 0)
            {
                var count = Math.Min(output.Length, remaining);
                var read = await decompressor.ReadAsync(
                    output.AsMemory(0, count),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new PfsArchiveException(
                        $"A zlib chunk produced {chunk.UncompressedLength - remaining} bytes; " +
                        $"{chunk.UncompressedLength} were declared.");
                }

                await destination.WriteAsync(output.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }

            var extra = await decompressor.ReadAsync(output.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (extra != 0)
            {
                throw new PfsArchiveException(
                    $"A zlib chunk produced more than its declared {chunk.UncompressedLength} bytes.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new PfsArchiveException("A PFS zlib chunk failed validation.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }
}
