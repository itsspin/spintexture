using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

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
/// checkpoints so older painted artifacts cannot mix with the new art pass.
/// Revision 8 enforces the palette color key on bitmaps referenced by masked
/// WLD materials (weapon blades, cutout props) so their transparent regions
/// can never re-encode as opaque color. Revision 9 widens classic coverage:
/// pre-shader 8-bit bitmaps are no longer misread as modern normal/mask/UI
/// assets ("metal1" doors, "window2" walls), tall trim is no longer skipped
/// as a sprite strip, and thin or low-color indexed bitmaps become
/// enhanceable — a repair re-attempts previously preserved textures under
/// the widened rules. Revision 10 corrects the legacy WLD material render
/// method from a bit field to its actual enum, recovering diffuse 0x14
/// character/world art that revision 6 mistakenly protected as translucent
/// and regenerating diffuse 0x12/0x31/0x553 textures that revision 8 could
/// mistakenly encode with a palette color key. It also lets painted presets
/// stylize safe textures that already equal the selected output cap instead
/// of copying them unchanged. Older packs can advance through these
/// independent safety rules without rerunning unaffected successfully
/// enhanced textures.
/// Revision 11 retains complete WLD reference context so static, fully opaque
/// wall atlases shared by ordinary diffuse and 0x07 passable materials can be
/// reconstructed without weakening protection for animated water, glass, or
/// other blended-only resources. It also carries proven classic diffuse
/// context into semantic classification for logical BMP names stored as DDS.
/// </summary>
public static class TextureProcessingPipeline
{
    public const int CurrentRevision = 11;
    // Preservation reason recorded when a repair retried a previously
    // preserved member and safely kept its original bytes; shared so repair
    // summaries in the workflow and app can count these outcomes.
    public const string RetriedPreservedOriginalReason =
        "Previously preserved texture stayed original after retry";
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
    public const string MaskedMaterialColorKeySafetyRuleId =
        "masked-material-color-key-v8";
    public const string ExpandedClassicCoverageRuleId =
        "expanded-classic-coverage-v9";
    public const string LegacyMaterialClassificationRuleId =
        "legacy-material-classification-v10";
    public const string PaintedAtCapRepaintRuleId =
        "painted-at-cap-repaint-v10";
    public const string ClassicWldVisibleSurfaceCoverageRuleId =
        "classic-wld-visible-surface-coverage-v11";

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope)
        => RequiresRepair(report, scope, artifactPaths: null);

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths)
        => RequiresRepair(report, scope, artifactPaths, preset: null);

    public static bool RequiresRepair(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths,
        TexturePreset? preset)
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
            || GetMissingRepairRuleIds(report, scope, artifactPaths, preset).Count != 0;
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
        => RequiresTargetedSafetyRepair(report, scope, artifactPaths, preset: null);

    public static bool RequiresTargetedSafetyRepair(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths,
        TexturePreset? preset)
    {
        if (!RequiresRepair(report, scope, artifactPaths, preset))
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
        GetRepairRuleIdsThroughRevision(
            scope,
            CurrentRevision,
            artifactPaths: null,
            includePaintedAtCap: true);

    public static IReadOnlyList<string> GetCurrentRepairRuleIds(
        AssetScope scope,
        IEnumerable<string>? artifactPaths) =>
        GetRepairRuleIdsThroughRevision(
            scope,
            CurrentRevision,
            artifactPaths,
            includePaintedAtCap: true);

    public static IReadOnlyList<string> GetCurrentRepairRuleIds(
        AssetScope scope,
        IEnumerable<string>? artifactPaths,
        TexturePreset preset) =>
        GetRepairRuleIdsThroughRevision(
            scope,
            CurrentRevision,
            artifactPaths,
            includePaintedAtCap: IsPaintedPreset(preset));

    public static IReadOnlyList<string> GetMissingRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope)
        => GetMissingRepairRuleIds(report, scope, artifactPaths: null);

    public static IReadOnlyList<string> GetMissingRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths)
        => GetMissingRepairRuleIds(report, scope, artifactPaths, preset: null);

    public static IReadOnlyList<string> GetMissingRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope,
        IEnumerable<string>? artifactPaths,
        TexturePreset? preset)
    {
        var includePaintedAtCap = preset is { } explicitPreset
            ? IsPaintedPreset(explicitPreset)
            : (report?.PaintedProfileRevision ?? 0) > 0;
        var recorded = GetRecordedRepairRuleIds(
                report,
                scope,
                includePaintedAtCap)
            .ToHashSet(StringComparer.Ordinal);
        return GetRepairRuleIdsThroughRevision(
                scope,
                CurrentRevision,
                artifactPaths,
                includePaintedAtCap)
            .Where(ruleId => !recorded.Contains(ruleId))
            .ToArray();
    }

    public static IReadOnlyList<string> GetRecordedRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope)
        => GetRecordedRepairRuleIds(
            report,
            scope,
            includePaintedAtCap: (report?.PaintedProfileRevision ?? 0) > 0);

    private static IReadOnlyList<string> GetRecordedRepairRuleIds(
        TextureBuildReport? report,
        AssetScope scope,
        bool includePaintedAtCap)
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
            artifactPaths: null,
            includePaintedAtCap);
    }

    private static IReadOnlyList<string> GetRepairRuleIdsThroughRevision(
        AssetScope scope,
        int revision,
        IEnumerable<string>? artifactPaths,
        bool includePaintedAtCap)
    {
        var rules = new List<string>(9);
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

        if (revision >= 8
            && scope is AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment
                or AssetScope.WorldOnly
                or AssetScope.SelectedZone)
        {
            rules.Add(MaskedMaterialColorKeySafetyRuleId);
        }

        if (revision >= 9
            && scope is AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment
                or AssetScope.WorldOnly
                or AssetScope.SelectedZone)
        {
            rules.Add(ExpandedClassicCoverageRuleId);
        }

        if (revision >= 10
            && scope is AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment
                or AssetScope.WorldOnly
                or AssetScope.SelectedZone)
        {
            rules.Add(LegacyMaterialClassificationRuleId);
            if (includePaintedAtCap)
            {
                rules.Add(PaintedAtCapRepaintRuleId);
            }
        }

        if (revision >= 11
            && scope is AssetScope.WorldCharactersAndEquipment
                or AssetScope.WorldOnly
                or AssetScope.SelectedZone)
        {
            rules.Add(ClassicWldVisibleSurfaceCoverageRuleId);
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

    private static bool IsPaintedPreset(TexturePreset preset) =>
        preset is TexturePreset.Illustrated or TexturePreset.RusticPainted;
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
    public int ExternalArtisticTextures { get; init; }
    public int BuiltInPaintedTextures { get; init; }
}

public enum PaintedRendererOutcome
{
    Unknown = 0,
    BuiltInOnly = 1,
    ExternalOnly = 2,
    Mixed = 3
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
    public const int MinimumSupportedSchemaVersion = 1;
    // Schema 4 makes PaintedRendererOutcome and the external worker identity
    // actual completed-route provenance. In schemas 1-3 the nullable worker
    // flag only described configured availability and is unsafe for repair.
    public const int CurrentSchemaVersion = 4;
    // Revision 8 restored logical texture names to material-aware diffusion
    // prompts. Revision 9 keeps oversized full-resolution requests on a
    // bounded single-pass diffusion route when safe instead of silently
    // mixing in built-in-painted members. Resume/repair must not combine those
    // route recipes under one claimed Graphic Painted profile.
    public const int CurrentIllustratedProfileRevision = 9;
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
    // Graphic Painted Fantasy renders through the external diffusion worker
    // when it is set up and through the built-in painterly stylization when it
    // is not. Null means the report predates this provenance marker.
    public bool? UsedExternalArtisticWorker { get; init; }
    // SHA-256 identity of the exact worker executable/script plus adjacent
    // worker-config.json. Null means either the built-in route was used or
    // the report predates exact external-renderer provenance.
    public string? ArtisticWorkerFingerprint { get; init; }
    public string? ArtisticWorkerPreset { get; init; }
    public PaintedRendererOutcome PaintedRendererOutcome { get; init; }
    public IReadOnlyList<string> AppliedRepairRuleIds { get; init; } = [];
}

/// <summary>
/// Validates optional report metadata before it is allowed to influence a
/// staged pack's UI or repair decisions. A report is not pack identity; a
/// missing, foreign, future, or malformed report must be treated as absent.
/// </summary>
public static class TextureBuildReportValidation
{
    public static bool IsUsableForStagedPack(
        TextureBuildReport? report,
        string expectedBuildId,
        string? expectedInstallPath,
        string expectedBuildDirectory)
    {
        if (report is null
            || string.IsNullOrWhiteSpace(expectedBuildId)
            || string.IsNullOrWhiteSpace(expectedBuildDirectory)
            || report.SchemaVersion is < TextureBuildReport.MinimumSupportedSchemaVersion
                or > TextureBuildReport.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(report.BuildId)
            || string.IsNullOrWhiteSpace(report.InstallPath)
            || string.IsNullOrWhiteSpace(report.StagingPath)
            || !report.BuildId.Equals(expectedBuildId, StringComparison.Ordinal)
            || report.CompletedUtc == default
            || report.SelectedArchives < 0
            || report.ResumedArtifacts < 0
            || report.BaselineTexturePipelineRevision < 0
            || report.ReusedArtifacts < 0
            || report.RebuiltArtifacts < 0
            || report.SafetyUpgradedArtifacts < 0
            || report.TexturePipelineRevision < 0
            || report.PaintedProfileRevision < 0
            || report.DurationSeconds is { } durationSeconds
                && (!double.IsFinite(durationSeconds) || durationSeconds < 0)
            || report.StartedUtc is { } startedUtc
                && (startedUtc == default || startedUtc > report.CompletedUtc)
            || !Enum.IsDefined(report.PaintedRendererOutcome)
            || report.AppliedRepairRuleIds is null
            || report.AppliedRepairRuleIds.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var statistics = report.Statistics;
        if (statistics is null
            || statistics.PreservedReasons is null
            || statistics.Warnings is null
            || statistics.DiscoveredTextures < 0
            || statistics.EnhancedTextures < 0
            || statistics.PreservedTextures < 0
            || statistics.ReusedTextures < 0
            || statistics.FallbackTextures < 0
            || statistics.ExternalArtisticTextures < 0
            || statistics.BuiltInPaintedTextures < 0
            || statistics.SourceTextureBytes < 0
            || statistics.EnhancedTextureBytes < 0
            || statistics.EnhancedTextures
                > int.MaxValue - statistics.ReusedTextures
            || statistics.PreservedReasons.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0)
            || statistics.Warnings.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        long preservedReasonTotal = 0;
        foreach (var reason in statistics.PreservedReasons)
        {
            preservedReasonTotal += reason.Value;
            if (preservedReasonTotal > statistics.PreservedTextures)
            {
                return false;
            }
        }

        try
        {
            if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(expectedBuildDirectory))
                    .Equals(expectedBuildId, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(expectedInstallPath)
                    && !PathGuard.SamePath(report.InstallPath, expectedInstallPath))
                || (!PathGuard.SamePath(report.StagingPath, expectedBuildDirectory)
                    && !Path.GetFileName(Path.TrimEndingDirectorySeparator(report.StagingPath))
                        .Equals(expectedBuildId, StringComparison.Ordinal)))
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }

        return true;
    }
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
    private int externalArtistic;
    private int builtInPainted;
    private int suppressedWarnings;
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

    public void Enhanced(long bytes, NativeTextureProcessResult? result = null)
    {
        lock (gate)
        {
            enhanced++;
            enhancedBytes += bytes;
            if (result?.UsedExternalArtisticWorker == true)
            {
                externalArtistic++;
            }

            if (result?.UsedBuiltInPaintedRenderer == true)
            {
                builtInPainted++;
            }
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
            else
            {
                suppressedWarnings++;
            }
        }
    }

    public TextureBuildStatistics Snapshot()
    {
        lock (gate)
        {
            var reportedWarnings = suppressedWarnings > 0
                ? warnings.Append(
                        $"{suppressedWarnings:N0} additional warning(s) were suppressed after the first 100; "
                        + "per-reason preservation counts above remain complete.")
                    .ToArray()
                : warnings.ToArray();
            return new TextureBuildStatistics(
                discovered,
                enhanced,
                preserved,
                sourceBytes,
                enhancedBytes,
                new Dictionary<string, int>(reasons, StringComparer.OrdinalIgnoreCase),
                reportedWarnings)
            {
                ReusedTextures = reused,
                FallbackTextures = fallback,
                ExternalArtisticTextures = externalArtistic,
                BuiltInPaintedTextures = builtInPainted
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
                warnings.ToArray())
            {
                ExternalArtisticTextures = externalArtistic,
                BuiltInPaintedTextures = builtInPainted
            };
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
            appendedWarnings)
        {
            ExternalArtisticTextures = checked(
                after.ExternalArtisticTextures - before.ExternalArtisticTextures),
            BuiltInPaintedTextures = checked(
                after.BuiltInPaintedTextures - before.BuiltInPaintedTextures)
        };
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
            externalArtistic = checked(
                externalArtistic + contribution.ExternalArtisticTextures);
            builtInPainted = checked(
                builtInPainted + contribution.BuiltInPaintedTextures);
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
            || contribution.ExternalArtisticTextures < 0
            || contribution.BuiltInPaintedTextures < 0
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
    IReadOnlyList<string> Warnings)
{
    public int ExternalArtisticTextures { get; init; }
    public int BuiltInPaintedTextures { get; init; }
}
