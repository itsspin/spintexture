using System.Buffers.Binary;

namespace SpinTexture.Core.Textures;

/// <summary>
/// Describes every classic WLD material route that references one logical
/// texture. Keeping this context intact lets the archive builder distinguish
/// true water/glass animation contracts from a static opaque wall atlas that
/// is deliberately shared with a passable material variant.
/// </summary>
public sealed record LegacyMaterialTextureUsage(
    string LogicalName,
    IReadOnlyList<uint> MaterialTypes,
    bool HasAnimatedReference)
{
    public bool HasBlendedReference => MaterialTypes.Any(
        LegacyTranslucentMaterialSafetyPolicy.IsBlendedMaterialType);

    public bool HasMaskedReference => MaterialTypes.Contains(
        LegacyTranslucentMaterialSafetyPolicy.MaskedMaterialTypeValue);

    public bool HasClassicDiffuseReference => MaterialTypes.Any(
        LegacyTranslucentMaterialSafetyPolicy.IsClassicDiffuseMaterialType);

    public bool IsStaticDiffusePassableDualUseCandidate =>
        !HasAnimatedReference
        && MaterialTypes.Contains(
            LegacyTranslucentMaterialSafetyPolicy.TransparentMaskedPassableMaterialType)
        && HasClassicDiffuseReference
        && MaterialTypes.All(type =>
            type == LegacyTranslucentMaterialSafetyPolicy
                .TransparentMaskedPassableMaterialType
            || LegacyTranslucentMaterialSafetyPolicy
                .IsClassicDiffuseMaterialType(type));

    internal bool WasLegacyBitRuleMisclassified => MaterialTypes.Any(
        LegacyTranslucentMaterialSafetyPolicy.WasLegacyBitRuleMisclassified);
}

/// <summary>
/// Immutable per-texture material-reference analysis for one or more WLDs.
/// </summary>
public sealed class LegacyMaterialReferenceContext
{
    private readonly IReadOnlyDictionary<string, LegacyMaterialTextureUsage> usages;

    internal LegacyMaterialReferenceContext(
        IReadOnlyDictionary<string, LegacyMaterialTextureUsage> usages,
        bool isComplete)
    {
        this.usages = usages;
        IsComplete = isComplete;
    }

    public IReadOnlyDictionary<string, LegacyMaterialTextureUsage> Usages => usages;

    /// <summary>
    /// True only when every material/bitmap reference in every contributing
    /// WLD was structurally resolved. Coverage exceptions must fail closed
    /// when a graph is truncated or otherwise ambiguous.
    /// </summary>
    public bool IsComplete { get; }

    public bool TryGetUsage(
        string logicalName,
        out LegacyMaterialTextureUsage? usage) =>
        usages.TryGetValue(logicalName, out usage);
}

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
    private const uint MaterialTypeMask = 0x7FFF_FFFF;
    internal const uint MaskedMaterialTypeValue = 0x13;
    internal const uint TransparentMaskedPassableMaterialType = 0x07;

    private static readonly byte[] StringKey =
        [0x95, 0x3A, 0xC5, 0x2A, 0x95, 0x7A, 0x95, 0x6A];

    /// <summary>
    /// Returns every bitmap filename referenced by a WLD material whose flags
    /// request semi-transparent rendering. Invalid or unsupported data fails
    /// closed by returning an empty set; it never guesses from broad names.
    /// </summary>
    public static IReadOnlySet<string> FindProtectedTextureNames(
        ReadOnlySpan<byte> wldPayload)
        => Analyze(wldPayload).Usages.Values
            .Where(usage => usage.HasBlendedReference)
            .Select(usage => usage.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns every bitmap filename referenced by a WLD material rendered
    /// with palette-masked (color-key) transparency and no alpha blending.
    /// Those bitmaps must keep their keyed palette index exactly, or the
    /// client draws their transparent regions as opaque color.
    /// </summary>
    public static IReadOnlySet<string> FindMaskedTextureNames(
        ReadOnlySpan<byte> wldPayload)
        => Analyze(wldPayload).Usages.Values
            .Where(usage => usage.HasMaskedReference)
            .Select(usage => usage.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds textures whose treatment changed when the legacy material value
    /// was corrected from a bit field to its documented enum. Pipeline
    /// revisions before 10 interpreted values such as 0x14 (diffuse) as
    /// blended and 0x12/0x31/0x553 (diffuse variants) as palette-masked. A
    /// targeted repair must retry original members and regenerate any changed
    /// output that was encoded with the false color-key contract.
    /// </summary>
    internal static IReadOnlySet<string> FindLegacyBitRuleMisclassifiedTextureNames(
        ReadOnlySpan<byte> wldPayload)
        => Analyze(wldPayload).Usages.Values
            .Where(usage => usage.WasLegacyBitRuleMisclassified)
            .Select(usage => usage.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the complete material graph once and retains usage type and
    /// animation context per logical texture name.
    /// </summary>
    public static LegacyMaterialReferenceContext Analyze(
        ReadOnlySpan<byte> wldPayload)
    {
        var empty = new LegacyMaterialReferenceContext(
            new Dictionary<string, LegacyMaterialTextureUsage>(
                StringComparer.OrdinalIgnoreCase),
            isComplete: false);
        if (wldPayload.Length < 28
            || BinaryPrimitives.ReadUInt32LittleEndian(wldPayload) != WldMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(wldPayload.Slice(4, 4))
                != OldWldVersion)
        {
            return empty;
        }

        var stringTableLength = BinaryPrimitives.ReadUInt32LittleEndian(
            wldPayload.Slice(20, sizeof(uint)));
        if (stringTableLength > int.MaxValue
            || 28L + stringTableLength > wldPayload.Length)
        {
            return empty;
        }

        var expectedFragmentCount = BinaryPrimitives.ReadUInt32LittleEndian(
            wldPayload.Slice(8, sizeof(uint)));
        if (expectedFragmentCount > int.MaxValue)
        {
            return empty;
        }

        var fragments = ParseFragments(
            wldPayload,
            checked(28 + (int)stringTableLength));
        if (fragments is null
            || fragments.Count != (int)expectedFragmentCount)
        {
            return empty;
        }

        var isComplete = true;
        var mutable = new Dictionary<string, MutableTextureUsage>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var material in fragments.Where(fragment =>
                     fragment.Type == MaterialFragment))
        {
            if (material.Data.Length < 24)
            {
                isComplete = false;
                continue;
            }

            var materialType = NormalizeMaterialType(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    material.Data.Span.Slice(4, sizeof(uint))));
            if (!TryResolve(
                    fragments,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        material.Data.Span.Slice(20, sizeof(uint))),
                    BitmapInfoReferenceFragment,
                    out var infoReference)
                || infoReference.Data.Length < sizeof(uint)
                || !TryResolve(
                    fragments,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        infoReference.Data.Span),
                    BitmapInfoFragment,
                    out var bitmapInfo)
                || bitmapInfo.Data.Length < 8)
            {
                isComplete = false;
                continue;
            }

            var bitmapCount = BinaryPrimitives.ReadUInt32LittleEndian(
                bitmapInfo.Data.Span.Slice(4, sizeof(uint)));
            if (bitmapCount == 0
                || bitmapCount > int.MaxValue
                || bitmapCount * sizeof(uint) > bitmapInfo.Data.Length - 8)
            {
                isComplete = false;
                continue;
            }

            var referencesOffset = checked(
                bitmapInfo.Data.Length - (int)bitmapCount * sizeof(uint));
            for (var bitmapIndex = 0; bitmapIndex < (int)bitmapCount; bitmapIndex++)
            {
                var bitmapReference = BinaryPrimitives.ReadUInt32LittleEndian(
                    bitmapInfo.Data.Span.Slice(
                        referencesOffset + bitmapIndex * sizeof(uint),
                        sizeof(uint)));
                if (!TryResolve(
                        fragments,
                        bitmapReference,
                        BitmapNameFragment,
                        out var bitmapName))
                {
                    isComplete = false;
                    continue;
                }

                if (!TryReadBitmapNames(bitmapName.Data.Span, out var names)
                    || names.Count == 0)
                {
                    isComplete = false;
                    continue;
                }
                var animatedReference = bitmapCount > 1 || names.Count > 1;
                foreach (var name in names)
                {
                    if (!mutable.TryGetValue(name, out var usage))
                    {
                        usage = new MutableTextureUsage(name);
                        mutable.Add(name, usage);
                    }

                    usage.MaterialTypes.Add(materialType);
                    usage.HasAnimatedReference |= animatedReference;
                }
            }
        }

        return new LegacyMaterialReferenceContext(
            mutable.ToDictionary(
                pair => pair.Key,
                pair => new LegacyMaterialTextureUsage(
                    pair.Value.LogicalName,
                    pair.Value.MaterialTypes.Order().ToArray(),
                    pair.Value.HasAnimatedReference),
                StringComparer.OrdinalIgnoreCase),
            isComplete);
    }

    /// <summary>
    /// Combines contexts from every WLD member in one archive without losing
    /// a stricter material type or animation reference from another graph.
    /// </summary>
    public static LegacyMaterialReferenceContext Combine(
        IEnumerable<LegacyMaterialReferenceContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        var mutable = new Dictionary<string, MutableTextureUsage>(
            StringComparer.OrdinalIgnoreCase);
        var isComplete = true;
        foreach (var context in contexts)
        {
            ArgumentNullException.ThrowIfNull(context);
            isComplete &= context.IsComplete;
            foreach (var item in context.Usages.Values)
            {
                if (!mutable.TryGetValue(item.LogicalName, out var usage))
                {
                    usage = new MutableTextureUsage(item.LogicalName);
                    mutable.Add(item.LogicalName, usage);
                }

                usage.MaterialTypes.UnionWith(item.MaterialTypes);
                usage.HasAnimatedReference |= item.HasAnimatedReference;
            }
        }

        return new LegacyMaterialReferenceContext(
            mutable.ToDictionary(
                pair => pair.Key,
                pair => new LegacyMaterialTextureUsage(
                    pair.Value.LogicalName,
                    pair.Value.MaterialTypes.Order().ToArray(),
                    pair.Value.HasAnimatedReference),
                StringComparer.OrdinalIgnoreCase),
            isComplete);
    }

    private static uint NormalizeMaterialType(uint parameters) =>
        parameters & MaterialTypeMask;

    internal static bool IsBlendedMaterialType(uint materialType) =>
        materialType switch
        {
            // Transparent 50%, masked/passable, 25%, 75%, and additive.
            0x05 or 0x07 or 0x09 or 0x0A or 0x0B or 0x17 => true,
            // Transparent and additive-unlit skydome variants. sky.s3d is
            // preserved as a whole, but recognizing the values here keeps the
            // parser correct for any renderer-owned copy in another WLD.
            0x0F or 0x10 => true,
            _ => false
        };

    internal static bool IsClassicDiffuseMaterialType(uint materialType) =>
        materialType is 0x01 or 0x02 or 0x0D or 0x12 or 0x14 or 0x15
            or 0x19 or 0x31 or 0x553;

    internal static bool WasLegacyBitRuleMisclassified(uint materialType)
    {
        var legacyBlended = (materialType & 0x0C) != 0;
        var legacyMasked = (materialType & 0x10) != 0 && !legacyBlended;
        return legacyBlended != IsBlendedMaterialType(materialType)
            || legacyMasked != (materialType == MaskedMaterialTypeValue);
    }

    private static List<WldFragment>? ParseFragments(
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
                return null;
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
            : null;
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

    private static bool TryReadBitmapNames(
        ReadOnlySpan<byte> data,
        out IReadOnlyList<string> names)
    {
        var parsed = new List<string>();
        names = parsed;
        if (data.Length < 6)
        {
            return false;
        }

        // Old client WLD 0x03 data starts with a reserved DWORD, followed by
        // one or more length-prefixed XOR-encoded filenames.
        var offset = sizeof(uint);
        while (offset < data.Length)
        {
            if (offset > data.Length - sizeof(ushort))
            {
                return data.Slice(offset).IndexOfAnyExcept((byte)0) < 0;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                data.Slice(offset, sizeof(ushort)));
            offset += sizeof(ushort);
            if (length == 0)
            {
                // Real 0x03 fragments are DWORD-aligned and use a zero length
                // followed only by zero bytes as padding/termination. A zero
                // record before any nonzero tail is ambiguous and therefore
                // cannot contribute static-reference proof.
                return data.Slice(offset).IndexOfAnyExcept((byte)0) < 0;
            }

            if (offset > data.Length - length)
            {
                return false;
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
            if (nameLength == 0)
            {
                return false;
            }

            var name = System.Text.Encoding.ASCII.GetString(decoded, 0, nameLength);
            if (Path.IsPathRooted(name)
                || name.Contains(Path.DirectorySeparatorChar)
                || name.Contains(Path.AltDirectorySeparatorChar)
                || name is "." or "..")
            {
                return false;
            }

            parsed.Add(name);
            offset += length;
        }

        return true;
    }

    private sealed class MutableTextureUsage(string logicalName)
    {
        public string LogicalName { get; } = logicalName;

        public HashSet<uint> MaterialTypes { get; } = [];

        public bool HasAnimatedReference { get; set; }
    }

    private readonly record struct WldFragment(uint Type, ReadOnlyMemory<byte> Data);
}
