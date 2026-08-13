using System.Buffers.Binary;

namespace SpinTexture.Core.Textures;

/// <summary>
/// Finds bitmap payloads owned by semi-transparent legacy WLD materials.
/// Their animation frames, dimensions, and material flags form one renderer
/// contract; enhancing individual bitmaps can make water, glass, or similar
/// blended surfaces become visually opaque.
/// </summary>
public static class LegacyTranslucentMaterialSafetyPolicy
{
    public const string PreservedReason =
        "Protected legacy translucent material texture";

    private const uint WldMagic = 0x54503D02;
    private const uint OldWldVersion = 0x00015500;
    private const uint BitmapNameFragment = 0x03;
    private const uint BitmapInfoFragment = 0x04;
    private const uint BitmapInfoReferenceFragment = 0x05;
    private const uint MaterialFragment = 0x30;
    private const uint SemiTransparentMaterialBits = 0x0C;

    private static readonly byte[] StringKey =
        [0x95, 0x3A, 0xC5, 0x2A, 0x95, 0x7A, 0x95, 0x6A];

    /// <summary>
    /// Returns every bitmap filename referenced by a WLD material whose flags
    /// request semi-transparent rendering. Invalid or unsupported data fails
    /// closed by returning an empty set; it never guesses from broad names.
    /// </summary>
    public static IReadOnlySet<string> FindProtectedTextureNames(
        ReadOnlySpan<byte> wldPayload)
    {
        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wldPayload.Length < 28
            || BinaryPrimitives.ReadUInt32LittleEndian(wldPayload) != WldMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(wldPayload.Slice(4, 4))
                != OldWldVersion)
        {
            return protectedNames;
        }

        var stringTableLength = BinaryPrimitives.ReadUInt32LittleEndian(
            wldPayload.Slice(20, sizeof(uint)));
        if (stringTableLength > int.MaxValue
            || 28L + stringTableLength > wldPayload.Length)
        {
            return protectedNames;
        }

        var fragments = ParseFragments(
            wldPayload,
            checked(28 + (int)stringTableLength));
        if (fragments.Count == 0)
        {
            return protectedNames;
        }

        foreach (var material in fragments.Where(fragment =>
                     fragment.Type == MaterialFragment
                     && fragment.Data.Length >= 8))
        {
            var parameters = BinaryPrimitives.ReadUInt32LittleEndian(
                material.Data.Span.Slice(4, sizeof(uint)));
            if ((parameters & SemiTransparentMaterialBits) == 0)
            {
                continue;
            }

            // The material's texture-info reference is the sixth DWORD after
            // the fragment header in every supported old-format zone WLD.
            // Do not scan arbitrary material fields as references: colors and
            // floats can coincidentally equal valid fragment indices.
            if (material.Data.Length < 24
                || !TryResolve(
                    fragments,
                    BinaryPrimitives.ReadUInt32LittleEndian(material.Data.Span.Slice(20, 4)),
                    BitmapInfoReferenceFragment,
                    out var infoReference)
                || infoReference.Data.Length < 4
                || !TryResolve(
                    fragments,
                    BinaryPrimitives.ReadUInt32LittleEndian(infoReference.Data.Span),
                    BitmapInfoFragment,
                    out var bitmapInfo)
                || bitmapInfo.Data.Length < 8)
            {
                continue;
            }

            var bitmapCount = BinaryPrimitives.ReadUInt32LittleEndian(
                bitmapInfo.Data.Span.Slice(4, 4));
            if (bitmapCount == 0
                || bitmapCount > int.MaxValue
                || bitmapCount * sizeof(uint) > bitmapInfo.Data.Length - 8)
            {
                continue;
            }

            // Optional animation timing fields precede the reference list.
            // Taking the final Count DWORDs is format-stable for static and
            // animated 0x04 fragments and avoids interpreting the timer as a
            // fragment reference.
            var referencesOffset = checked(
                bitmapInfo.Data.Length - (int)bitmapCount * sizeof(uint));
            for (var bitmapIndex = 0; bitmapIndex < (int)bitmapCount; bitmapIndex++)
            {
                var bitmapReference = BinaryPrimitives.ReadUInt32LittleEndian(
                    bitmapInfo.Data.Span.Slice(
                        referencesOffset + bitmapIndex * sizeof(uint),
                        sizeof(uint)));
                if (!TryResolve(fragments, bitmapReference, BitmapNameFragment, out var bitmapName))
                {
                    continue;
                }

                foreach (var name in ReadBitmapNames(bitmapName.Data.Span))
                {
                    protectedNames.Add(name);
                }
            }
        }

        return protectedNames;
    }

    private static List<WldFragment> ParseFragments(
        ReadOnlySpan<byte> payload,
        int offset)
    {
        var fragments = new List<WldFragment>();
        while (offset <= payload.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(offset, sizeof(uint)));
            if (size < 4 || size > int.MaxValue || offset + 8L + size > payload.Length)
            {
                return [];
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(offset + 4, sizeof(uint)));
            var dataLength = checked((int)size - 4);
            fragments.Add(new WldFragment(
                type,
                payload.Slice(offset + 12, dataLength).ToArray()));
            offset = checked(offset + 8 + (int)size);
        }

        // Real client WLDs can end with one DWORD of zero padding or the
        // 0xFFFFFFFF end sentinel after the declared fragment table. Never
        // ignore a larger or arbitrary suffix that could indicate bad layout.
        var suffix = payload.Slice(offset);
        return offset == payload.Length
            || (suffix.Length == sizeof(uint)
                && (suffix.IndexOfAnyExcept((byte)0) < 0
                    || suffix.IndexOfAnyExcept((byte)0xFF) < 0))
            ? fragments
            : [];
    }

    private static bool TryResolve(
        IReadOnlyList<WldFragment> fragments,
        uint reference,
        uint expectedType,
        out WldFragment fragment)
    {
        if (reference == 0 || reference > fragments.Count)
        {
            fragment = default;
            return false;
        }

        fragment = fragments[checked((int)reference - 1)];
        return fragment.Type == expectedType;
    }

    private static IReadOnlyList<string> ReadBitmapNames(ReadOnlySpan<byte> data)
    {
        var names = new List<string>();
        if (data.Length < 6)
        {
            return names;
        }

        // Old client WLD 0x03 data starts with a reserved DWORD, followed by
        // one or more length-prefixed XOR-encoded filenames.
        var offset = sizeof(uint);
        while (offset < data.Length)
        {
            if (offset > data.Length - sizeof(ushort))
            {
                return names;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                data.Slice(offset, sizeof(ushort)));
            offset += sizeof(ushort);
            if (length == 0 || offset > data.Length - length)
            {
                return names;
            }

            var encoded = data.Slice(offset, length);
            var decoded = new byte[length];
            for (var byteIndex = 0; byteIndex < decoded.Length; byteIndex++)
            {
                decoded[byteIndex] = (byte)(encoded[byteIndex]
                    ^ StringKey[byteIndex % StringKey.Length]);
            }

            var terminator = Array.IndexOf(decoded, (byte)0);
            var nameLength = terminator >= 0 ? terminator : decoded.Length;
            if (nameLength > 0)
            {
                var name = System.Text.Encoding.ASCII.GetString(decoded, 0, nameLength);
                if (!Path.IsPathRooted(name)
                    && !name.Contains(Path.DirectorySeparatorChar)
                    && !name.Contains(Path.AltDirectorySeparatorChar)
                    && name is not "." and not "..")
                {
                    names.Add(name);
                }
            }

            offset += length;
        }

        return names;
    }

    private readonly record struct WldFragment(uint Type, ReadOnlyMemory<byte> Data);
}
