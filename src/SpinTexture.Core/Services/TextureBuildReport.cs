using SpinTexture.Core.Models;

namespace SpinTexture.Core.Services;

/// <summary>
/// Identifies the texture-processing behavior used by a build independently
/// of the JSON report schema. Revision 2 bounded alpha-tested mip chains at
/// 4x4. Revision 3 retains only the enhanced top level for alpha-tested
/// cutouts because the legacy renderer can discard those generated soft-alpha
/// levels based on view angle. Older packs can be upgraded without rerunning
/// their successfully enhanced opaque textures.
/// </summary>
public static class TextureProcessingPipeline
{
    public const int CurrentRevision = 3;

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope)
    {
        if (scope is not (AssetScope.CharactersAndEquipmentOnly
            or AssetScope.WorldCharactersAndEquipment
            or AssetScope.WorldOnly
            or AssetScope.SelectedZone))
        {
            return false;
        }

        return report is null
            || report.TexturePipelineRevision < CurrentRevision;
    }

    /// <summary>
    /// Distinguishes the narrow cutout-policy upgrade from the original
    /// character/equipment coverage repair. World-bearing PFS packs always use
    /// the narrow path when stale. Character-bearing packs use it after revision
    /// 1, or after a recorded incremental coverage repair. Fresh revision-0 or
    /// no-report character/combined packs still receive the legacy coverage pass
    /// (which also writes the current revision).
    /// </summary>
    public static bool RequiresCutoutMipUpgrade(
        TextureBuildReport? report,
        AssetScope scope)
    {
        if (!RequiresRepair(report, scope))
        {
            return false;
        }

        if (scope is AssetScope.WorldOnly or AssetScope.SelectedZone)
        {
            return true;
        }

        // Revision 0 character-bearing packs still need the broader missing
        // race/equipment coverage pass. That pass also applies the current mip
        // validator. Revision 1 proved coverage and can use cutout-only repair.
        return report is not null
            && (report.TexturePipelineRevision >= 1
                || (report.IsIncrementalRepair
                    && !report.IsSourceMismatchRepair));
    }
}

/// <summary>
/// Compatibility facade for callers compiled against the original
/// character/equipment-only revision helper.
/// </summary>
public static class CharacterEquipmentTexturePipeline
{
    public const int CurrentRevision = TextureProcessingPipeline.CurrentRevision;

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope) =>
        TextureProcessingPipeline.RequiresRepair(report, scope);
}

public sealed record TextureBuildStatistics(
    int DiscoveredTextures,
    int EnhancedTextures,
    int PreservedTextures,
    long SourceTextureBytes,
    long EnhancedTextureBytes,
    IReadOnlyDictionary<string, int> PreservedReasons,
    IReadOnlyList<string> Warnings)
{
    public int ReusedTextures { get; init; }
}

public sealed record TextureBuildReport(
    int SchemaVersion,
    string BuildId,
    DateTimeOffset CompletedUtc,
    string InstallPath,
    string StagingPath,
    int SelectedArchives,
    TextureBuildStatistics Statistics)
{
    public const int CurrentSchemaVersion = 3;
    public DateTimeOffset? StartedUtc { get; init; }
    public double? DurationSeconds { get; init; }
    public bool IsIncrementalRepair { get; init; }
    public bool IsSourceMismatchRepair { get; init; }
    public bool IsCutoutMipRepair { get; init; }
    public bool IsManualTextureRevision { get; init; }
    public string? BaselineBuildId { get; init; }
    public int BaselineTexturePipelineRevision { get; init; }
    public int ReusedArtifacts { get; init; }
    public int RebuiltArtifacts { get; init; }
    public int TexturePipelineRevision { get; init; }
}

internal sealed class TextureBuildCounter
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> reasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> warnings = [];
    private int discovered;
    private int enhanced;
    private int preserved;
    private int reused;
    private long sourceBytes;
    private long enhancedBytes;

    public int EnhancedCount
    {
        get
        {
            lock (gate)
            {
                return enhanced;
            }
        }
    }

    public void Discover(long bytes)
    {
        lock (gate)
        {
            discovered++;
            sourceBytes += bytes;
        }
    }

    public void Enhanced(long bytes)
    {
        lock (gate)
        {
            enhanced++;
            enhancedBytes += bytes;
        }
    }

    public void Reused()
    {
        lock (gate)
        {
            reused++;
        }
    }

    public void Preserve(string reason)
    {
        lock (gate)
        {
            preserved++;
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }
    }

    public void Warn(string warning)
    {
        lock (gate)
        {
            if (warnings.Count < 100)
            {
                warnings.Add(warning);
            }
        }
    }

    public TextureBuildStatistics Snapshot()
    {
        lock (gate)
        {
            return new TextureBuildStatistics(
                discovered,
                enhanced,
                preserved,
                sourceBytes,
                enhancedBytes,
                new Dictionary<string, int>(reasons, StringComparer.OrdinalIgnoreCase),
                warnings.ToArray())
            {
                ReusedTextures = reused
            };
        }
    }
}
