using System.Buffers.Binary;
using System.Text;

namespace SpinTexture.Core.Archives;

/// <summary>
/// Constants and helpers for EverQuest PFS 2 archives.
/// </summary>
public static class PfsFormat
{
    public const uint Version2 = 0x0002_0000;
    public const uint FilenameDirectoryCrc = 0x6158_0AC9;
    public const int HeaderSize = 12;
    public const int DirectoryRecordSize = 12;
    public const int DefaultChunkSize = 8 * 1024;

    internal static ReadOnlySpan<byte> Magic => "PFS "u8;
    internal static Encoding NameEncoding { get; } = Encoding.Latin1;

    /// <summary>
    /// Computes the CRC stored in a PFS directory for a logical filename.
    /// PFS uses a forward CRC-32 (polynomial 0x04C11DB7), starts at zero,
    /// lowercases ASCII A-Z, and includes the trailing null byte.
    /// </summary>
    public static uint ComputeFilenameCrc(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        if (filename.Length == 0)
        {
            throw new ArgumentException("A PFS filename cannot be empty.", nameof(filename));
        }

        uint crc = 0;
        foreach (var character in filename)
        {
            if (character == '\0')
            {
                throw new ArgumentException("A PFS filename cannot contain a null character.", nameof(filename));
            }

            if (character > byte.MaxValue)
            {
                throw new ArgumentException(
                    $"PFS filenames must be single-byte Latin-1 text; U+{(int)character:X4} is not supported.",
                    nameof(filename));
            }

            var value = (byte)character;
            if (value is >= (byte)'A' and <= (byte)'Z')
            {
                value = (byte)(value + ('a' - 'A'));
            }

            crc = UpdateForwardCrc(crc, value);
        }

        return UpdateForwardCrc(crc, 0);
    }

    internal static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    internal static void WriteUInt32(Span<byte> bytes, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);

    private static uint UpdateForwardCrc(uint crc, byte value)
    {
        var index = (byte)((crc >> 24) ^ value);
        return (crc << 8) ^ ForwardCrcTable[index];
    }

    private static readonly uint[] ForwardCrcTable = CreateForwardCrcTable();

    private static uint[] CreateForwardCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var crc = index << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000_0000) != 0
                    ? (crc << 1) ^ 0x04C1_1DB7
                    : crc << 1;
            }

            table[index] = crc;
        }

        return table;
    }
}
