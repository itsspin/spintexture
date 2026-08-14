using SpinTexture.Core.Models;
using SpinTexture.Core.Textures;

namespace SpinTexture.Core.Services;

/// <summary>
/// Identifies the texture-processing behavior used by a build independently
/// of the JSON report schema. Revision 2 bounded alpha-tested mip chains at
/// 4x4. Revision 3 retains only the enhanced top level for alpha-tested
/// cutouts because the legacy renderer can discard generated soft-alpha levels
/// based on view angle. Revision 4 adds the legacy celestial/sky safety policy.
/// Revision 5 closes the native Resources/sky atlas boundary. Revision 6
/// preserves the exact bitmap sets referenced by legacy semi-transparent WLD
/// materials so water and glass retain their authored blending. Revision 7
/// introduces the asset-aware graphic-painted route and fences crash-resume
/// checkpoints so older painted artifacts cannot mix with the new art pass. Older
/// packs can advance through these independent safety rules without rerunning
/// unaffected successfully enhanced textures.
/// </summary>
public static class TextureProcessingPipeline
{
    public const int CurrentRevision = 7;
    public const string CharacterEquipmentCoverageRuleId =
        "character-equipment-coverage-v1";
    public const string CutoutMipSafetyRuleId =
        "cutout-single-level-mips-v3";
    public const string CelestialSkySafetyRuleId =
        "celestial-sky-originals-v4";
    public const string NativeSkyResourceSafetyRuleId =
        "native-sky-resources-originals-v5";
    public const string LegacyTranslucentMaterialSafetyRuleId =
        "legacy-translucent-materials-originals-v6";

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope)
        => RequiresRepair(report, scope, artifactPaths: null);

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths)
    {
        if (scope is not (AssetScope.CharactersAndEquipmentOnly
            or AssetScope.WorldCharactersAndEquipment
            or AssetScope.WorldOnly
            or AssetScope.SelectedZone
            or AssetScope.SpellEffectsOnly
            or AssetScope.AllSafeTextures))
        {
            return false;
        }

        // Revisions are only a compatibility hint. Repair requirements are
        // scope-specific: for example, a valid revision-3 character pack does
        // not contain environmental sky/celestial assets and therefore has no
        // revision-4 work to do.
        return report is null
            || GetMissingRepairRuleIds(report, scope, artifactPaths).Count != 0;
    }

    /// <summary>
    /// Distinguishes a targeted safety-policy upgrade from the original broad
    /// character/equipment coverage repair. Every stale revision-1-or-newer PFS
    /// pack has sufficient provenance for targeted reuse. The two older special
    /// cases remain supported so already-released World and repaired character
    /// packs do not lose their safe incremental upgrade path.
    /// </summary>
    public static bool RequiresTargetedSafetyRepair(
        TextureBuildReport? report,
        AssetScope scope)
        => RequiresTargetedSafetyRepair(report, scope, artifactPaths: null);

    public static bool RequiresTargetedSafetyRepair(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths)
    {
        if (!RequiresRepair(report, scope, artifactPaths))
        {
            return false;
        }

        if (report is not null && report.TexturePipelineRevision >= 1)
        {
            return true;
        }

        if (scope is AssetScope.WorldOnly or AssetScope.SelectedZone)
        {
            return true;
        }

        if (scope == AssetScope.SpellEffectsOnly)
        {
            return report is not null;
        }

        if (scope == AssetScope.AllSafeTextures)
        {
            // Mixed packs remain ineligible for broad reconstruction. A
            // recorded pack containing a now-protected native sky resource can
            // still use the exact whole-artifact safety path.
            return report is not null;
        }

        // Revision 0 character-bearing packs still need the broader missing
        // race/equipment coverage pass. That pass also applies the current mip
        // validator. Revision 1 proved coverage and can use cutout-only repair.
        return report is not null
            && (report.TexturePipelineRevision >= 1
                || (report.IsIncrementalRepair
                    && !report.IsSourceMismatchRepair));
    }

    /// <summary>
    /// Compatibility name retained for older UI and test callers. The targeted
    /// route now applies every missing safety rule, not only the cutout rule.
    /// </summary>
    public static bool RequiresCutoutMipUpgrade(
        TextureBuildReport? report,
        AssetScope scope) =>
        RequiresTargetedSafetyRepair(report, scope);

    public static IReadOnlyList<string> GetCurrentRepairRuleIds(AssetScope scope) =>
        GetRepairRuleIdsThroughRevision(scope, CurrentRevision, artifactPaths: null);

    public static IReadOnlyList<string> GetCurrentRepairRuleIds(
        AssetScope scope,
        IEnumerable<string>? artifactPaths) =>
        GetRepairRuleIdsThroughRevision(scope, CurrentRevision, artifactPaths);

    public static IReadOnlyList<string> GetMissingRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope)
        => GetMissingRepairRuleIds(report, scope, artifactPaths: null);

    public static IReadOnlyList<string> GetMissingRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths)
    {
        var recorded = GetRecordedRepairRuleIds(report, scope)
            .ToHashSet(StringComparer.Ordinal);
        return GetCurrentRepairRuleIds(scope, artifactPaths)
            .Where(ruleId => !recorded.Contains(ruleId))
            .ToArray();
    }

    public static IReadOnlyList<string> GetRecordedRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope)
    {
        if (report?.AppliedRepairRuleIds is { Count: > 0 } recorded)
        {
            return recorded
                .Where(ruleId => !string.IsNullOrWhiteSpace(ruleId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return GetRepairRuleIdsThroughRevision(
            scope,
            report?.TexturePipelineRevision ?? 0,
            artifactPaths: null);
    }

    private static IReadOnlyList<string> GetRepairRuleIdsThroughRevision(
        AssetScope scope,
        int revision,
        IEnumerable<string>? artifactPaths)
    {
        var rules = new List<string>(5);
        if (revision >= 1
            && scope is AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment)
        {
            rules.Add(CharacterEquipmentCoverageRuleId);
        }

        if (revision >= 3
            && scope is AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment
                or AssetScope.WorldOnly
                or AssetScope.SelectedZone)
        {
            rules.Add(CutoutMipSafetyRuleId);
        }

        var paths = artifactPaths?.ToArray();
        var hasTopLevelSkyArchive = paths?.Any(path =>
            Path.GetFileName(path).Equals("sky.s3d", StringComparison.OrdinalIgnoreCase)
            && !path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Contains(Path.DirectorySeparatorChar)) == true;
        var hasProtectedLooseCelestial = paths?.Any(path =>
            CelestialTextureSafetyPolicy.GetPreservedReason(
                path,
                Path.GetFileName(path)) is not null) == true;
        var hasProtectedNativeSkyResource = paths?.Any(path =>
            CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(path) is not null) == true;
        var celestialApplies = scope switch
        {
            AssetScope.WorldCharactersAndEquipment or AssetScope.WorldOnly => true,
            AssetScope.SelectedZone => hasTopLevelSkyArchive,
            AssetScope.SpellEffectsOnly => paths is null || hasProtectedLooseCelestial,
            AssetScope.AllSafeTextures => hasTopLevelSkyArchive || hasProtectedLooseCelestial,
            _ => false
        };
        if (revision >= 4 && celestialApplies)
        {
            rules.Add(CelestialSkySafetyRuleId);
        }

        if (revision >= 5 && hasProtectedNativeSkyResource)
        {
            // Fresh packs never stage Resources/sky. This rule therefore
            // applies only to an older manifest that actually contains one of
            // those renderer-owned files, regardless of the broad scope that
            // originally discovered it.
            rules.Add(NativeSkyResourceSafetyRuleId);
        }

        if (revision >= 6
            && scope is AssetScope.WorldCharactersAndEquipment
                or AssetScope.WorldOnly
                or AssetScope.SelectedZone)
        {
            rules.Add(LegacyTranslucentMaterialSafetyRuleId);
        }

        return rules.ToArray();
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
    public int FallbackTextures { get; init; }
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
    public const int CurrentIllustratedProfileRevision = 2;
    public const int CurrentRusticPaintedProfileRevision = 1;

    public static int GetCurrentPaintedProfileRevision(TexturePreset preset) => preset switch
    {
        TexturePreset.Illustrated => CurrentIllustratedProfileRevision,
        TexturePreset.RusticPainted => CurrentRusticPaintedProfileRevision,
        _ => 0
    };
    public DateTimeOffset? StartedUtc { get; init; }
    public double? DurationSeconds { get; init; }
    public bool WasResumed { get; init; }
    public int ResumedArtifacts { get; init; }
    public bool IsIncrementalRepair { get; init; }
    public bool IsSourceMismatchRepair { get; init; }
    public bool IsSafetyRepair { get; init; }
    // Retained so reports written before the generic safety-repair terminology
    // and older app versions continue to recognize the cutout upgrade.
    public bool IsCutoutMipRepair { get; init; }
    public bool IsManualTextureRevision { get; init; }
    public string? BaselineBuildId { get; init; }
    public int BaselineTexturePipelineRevision { get; init; }
    public int ReusedArtifacts { get; init; }
    public int RebuiltArtifacts { get; init; }
    public int SafetyUpgradedArtifacts { get; init; }
    public int TexturePipelineRevision { get; init; }
    // Zero is the backward-compatible value for reports written before the
    // Graphic Painted/Rustic profile provenance marker existed.
    public int PaintedProfileRevision { get; init; }
    public IReadOnlyList<string> AppliedRepairRuleIds { get; init; } = [];
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
    private int fallback;
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

    public void Fallback()
    {
        lock (gate)
        {
            fallback++;
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
                ReusedTextures = reused,
                FallbackTextures = fallback
            };
        }
    }

    internal TextureBuildCounterCheckpoint CaptureCheckpoint()
    {
        lock (gate)
        {
            return new TextureBuildCounterCheckpoint(
                discovered,
                enhanced,
                preserved,
                reused,
                fallback,
                sourceBytes,
                enhancedBytes,
                new Dictionary<string, int>(reasons, StringComparer.OrdinalIgnoreCase),
                warnings.ToArray());
        }
    }

    internal TextureBuildCounterCheckpoint CaptureDelta(TextureBuildCounterCheckpoint before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var after = CaptureCheckpoint();
        var reasonDelta = after.PreservedReasons.ToDictionary(
            pair => pair.Key,
            pair => checked(pair.Value - before.PreservedReasons.GetValueOrDefault(pair.Key)),
            StringComparer.OrdinalIgnoreCase);
        var appendedWarnings = after.Warnings
            .Skip(Math.Min(before.Warnings.Count, after.Warnings.Count))
            .ToArray();
        return new TextureBuildCounterCheckpoint(
            checked(after.DiscoveredTextures - before.DiscoveredTextures),
            checked(after.EnhancedTextures - before.EnhancedTextures),
            checked(after.PreservedTextures - before.PreservedTextures),
            checked(after.ReusedTextures - before.ReusedTextures),
            checked(after.FallbackTextures - before.FallbackTextures),
            checked(after.SourceTextureBytes - before.SourceTextureBytes),
            checked(after.EnhancedTextureBytes - before.EnhancedTextureBytes),
            reasonDelta,
            appendedWarnings);
    }

    internal void RestoreCheckpoint(TextureBuildCounterCheckpoint contribution)
    {
        ValidateCheckpoint(contribution);
        lock (gate)
        {
            discovered = checked(discovered + contribution.DiscoveredTextures);
            enhanced = checked(enhanced + contribution.EnhancedTextures);
            preserved = checked(preserved + contribution.PreservedTextures);
            reused = checked(reused + contribution.ReusedTextures);
            fallback = checked(fallback + contribution.FallbackTextures);
            sourceBytes = checked(sourceBytes + contribution.SourceTextureBytes);
            enhancedBytes = checked(enhancedBytes + contribution.EnhancedTextureBytes);
            foreach (var pair in contribution.PreservedReasons)
            {
                reasons[pair.Key] = checked(reasons.GetValueOrDefault(pair.Key) + pair.Value);
            }

            foreach (var warning in contribution.Warnings)
            {
                if (warnings.Count < 100)
                {
                    warnings.Add(warning);
                }
            }
        }
    }

    private static void ValidateCheckpoint(TextureBuildCounterCheckpoint contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution.DiscoveredTextures < 0
            || contribution.EnhancedTextures < 0
            || contribution.PreservedTextures < 0
            || contribution.ReusedTextures < 0
            || contribution.FallbackTextures < 0
            || contribution.SourceTextureBytes < 0
            || contribution.EnhancedTextureBytes < 0
            || contribution.PreservedReasons.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0)
            || contribution.Warnings.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("A staged-build statistics checkpoint is invalid.");
        }
    }
}

internal sealed record TextureBuildCounterCheckpoint(
    int DiscoveredTextures,
    int EnhancedTextures,
    int PreservedTextures,
    int ReusedTextures,
    int FallbackTextures,
    long SourceTextureBytes,
    long EnhancedTextureBytes,
    IReadOnlyDictionary<string, int> PreservedReasons,
    IReadOnlyList<string> Warnings);
