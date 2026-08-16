using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Tooling;
using SpinTexture.Core.Textures;

namespace SpinTexture.Core.Services;

public sealed record TexturePackBuildResult(
    StagedBuildResult StagedBuild,
    TextureBuildReport Report,
    string ReportPath,
    ApplyResult? ApplyResult,
    string? PreviewManifestPath);

public sealed class TexturePackWorkflow
{
    public static string FreshBuildResumeOperationKey =>
        $"fresh-texture-pack-pipeline-{TextureProcessingPipeline.CurrentRevision}";

    public static string IllustratedFreshBuildResumeOperationKey =>
        $"{FreshBuildResumeOperationKey}-illustrated-{TextureBuildReport.CurrentIllustratedProfileRevision}";

    public static string GetFreshBuildResumeOperationKey(TexturePreset preset) =>
        preset == TexturePreset.Illustrated
            ? IllustratedFreshBuildResumeOperationKey
            : FreshBuildResumeOperationKey;

    internal static int GetFreshPaintedProfileRevision(TexturePreset preset) =>
        TextureBuildReport.GetCurrentPaintedProfileRevision(preset);

    private static readonly JsonSerializerOptions CompositionJsonOptions =
        CreateCompositionJsonOptions();

    private readonly EverQuestTextureScanner scanner;
    private readonly ToolchainDiscovery toolchainDiscovery;
    private readonly StagedBuildService stagedBuildService;
    private readonly InstallTransactionService installTransactionService;
    private readonly ManifestStore manifestStore;
    private readonly InstallHealthService installHealthService;
    private readonly StagedPackCatalogService stagedPackCatalogService;
    private readonly StagedPackComposer stagedPackComposer;
    private readonly Action clientClosedGuard;

    public TexturePackWorkflow(
        EverQuestTextureScanner? scanner = null,
        ToolchainDiscovery? toolchainDiscovery = null,
        StagedBuildService? stagedBuildService = null,
        InstallTransactionService? installTransactionService = null,
        ManifestStore? manifestStore = null,
        InstallHealthService? installHealthService = null,
        StagedPackCatalogService? stagedPackCatalogService = null,
        StagedPackComposer? stagedPackComposer = null)
    {
        this.scanner = scanner ?? new EverQuestTextureScanner();
        this.toolchainDiscovery = toolchainDiscovery ?? new ToolchainDiscovery();
        this.stagedBuildService = stagedBuildService ?? new StagedBuildService();
        this.installTransactionService = installTransactionService ?? new InstallTransactionService();
        this.manifestStore = manifestStore ?? new ManifestStore();
        this.installHealthService = installHealthService ?? new InstallHealthService(this.manifestStore);
        this.stagedPackCatalogService = stagedPackCatalogService
            ?? new StagedPackCatalogService(this.manifestStore);
        this.stagedPackComposer = stagedPackComposer
            ?? new StagedPackComposer(this.stagedPackCatalogService, this.manifestStore);
        clientClosedGuard = EnsureClientClosed;
    }

    internal TexturePackWorkflow(
        Action clientClosedGuard,
        EverQuestTextureScanner? scanner = null,
        ToolchainDiscovery? toolchainDiscovery = null,
        StagedBuildService? stagedBuildService = null,
        InstallTransactionService? installTransactionService = null,
        ManifestStore? manifestStore = null,
        InstallHealthService? installHealthService = null,
        StagedPackCatalogService? stagedPackCatalogService = null,
        StagedPackComposer? stagedPackComposer = null)
        : this(
            scanner,
            toolchainDiscovery,
            stagedBuildService,
            installTransactionService,
            manifestStore,
            installHealthService,
            stagedPackCatalogService,
            stagedPackComposer)
    {
        this.clientClosedGuard = clientClosedGuard
            ?? throw new ArgumentNullException(nameof(clientClosedGuard));
    }

    public Task<ScanSummary> AnalyzeAsync(
        string installPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        scanner.AnalyzeAsync(installPath, progress, cancellationToken);

    public async Task<TexturePackBuildResult> BuildAsync(
        ProjectPaths paths,
        UpscaleOptions options,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        var buildStartedUtc = DateTimeOffset.UtcNow;
        var validation = EverQuestInstall.Validate(paths.InstallPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }

        if (options.MaximumDimension is not (1024 or 2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The texture cap must be 1024, 2048, or 4096 pixels.");
        }
        if (!Enum.IsDefined(options.Preset) || !Enum.IsDefined(options.Scope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The selected texture preset or asset scope is not supported.");
        }
        if (!Enum.IsDefined(options.PaintedTheme)
            || (options.Preset != TexturePreset.Illustrated
                && options.PaintedTheme != PaintedTheme.ClassicPainted))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A painted theme may be selected only with Graphic Painted Fantasy.");
        }
        TextureOverridePolicy.ValidateAll(options.TextureOverrides);
        WorldExpansionSelectionPolicy.Validate(options);

        var originalSources = await PrepareBuildOriginalSourcesAsync(
                paths,
                cancellationToken)
            .ConfigureAwait(false);

        var tools = toolchainDiscovery.Discover(paths);
        if (!tools.IsReady)
        {
            throw new FileNotFoundException(
                "SpinTexture's bundled processing tools are incomplete. "
                + string.Join(" ", tools.Diagnostics));
        }

        var archiveScopes = DiscoverArchiveScopes(paths.InstallPath);
        var selectedArchives = SelectArchives(archiveScopes, options);
        if (selectedArchives.Count == 0 && options.Scope != AssetScope.SpellEffectsOnly)
        {
            throw new InvalidOperationException("The selected scope did not resolve to any EverQuest archives.");
        }

        var counter = new TextureBuildCounter();
        var processor = new NativeTextureProcessor(tools);
        var previewCollector = new TexturePreviewCollector(maximumEntries: 24);
        var archiveBuilder = new PfsTextureArchiveBuilder(
            processor,
            counter,
            progress,
            previewCollector,
            clampArchivePaths: SelectCharacterAndEquipmentArchives(
                archiveScopes,
                options),
            filterCharacterEquipmentEntries:
                options.Scope is AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment);
        var items = await PlanArchiveItemsAsync(
            paths,
            options,
            selectedArchives,
            archiveBuilder,
            counter,
            originalSources,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (options.Scope is AssetScope.AllSafeTextures or AssetScope.SpellEffectsOnly)
        {
            await AddLooseTextureItemsAsync(
                paths,
                options,
                items,
                processor,
                counter,
                previewCollector,
                originalSources,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "No safe textures below the selected resolution cap were found in this scope.");
        }

        TextureBuildReport? report = null;
        string? reportPath = null;
        string? previewManifestPath = null;
        var staged = await stagedBuildService.BuildAsync(
            new StagedBuildRequest(
                paths,
                options,
                items,
                ResumeOperationKey: GetFreshBuildResumeOperationKey(options.Preset)),
            progress,
            cancellationToken,
            async (finalizing, metadataCancellationToken) =>
            {
                var statistics = counter.Snapshot();
                if (statistics.EnhancedTextures == 0)
                {
                    throw new InvalidOperationException(
                        "The build completed without an eligible texture. No client files were installed.");
                }

                var buildCompletedUtc = DateTimeOffset.UtcNow;
                report = new TextureBuildReport(
                    TextureBuildReport.CurrentSchemaVersion,
                    finalizing.BuildId,
                    buildCompletedUtc,
                    paths.InstallPath,
                    finalizing.BuildDirectory,
                    selectedArchives.Count,
                    statistics)
                {
                    StartedUtc = finalizing.CheckpointCreatedUtc == default
                        ? buildStartedUtc
                        : finalizing.CheckpointCreatedUtc,
                    DurationSeconds = finalizing.ActiveDurationSeconds,
                    WasResumed = finalizing.ResumedArtifactCount > 0,
                    ResumedArtifacts = finalizing.ResumedArtifactCount,
                    TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision,
                    PaintedProfileRevision = GetFreshPaintedProfileRevision(options.Preset),
                    UsedExternalArtisticWorker = options.Preset == TexturePreset.Illustrated
                        ? tools.HasArtisticWorker
                        : null,
                    AppliedRepairRuleIds = TextureProcessingPipeline
                        .GetCurrentRepairRuleIds(
                            options.Scope,
                            finalizing.Manifest.Entries.Select(entry => entry.RelativeInstallPath))
                };
                reportPath = Path.Combine(finalizing.BuildDirectory, "texture-report.json");
                await WriteReportAsync(reportPath, report, metadataCancellationToken).ConfigureAwait(false);

                var previewEntries = previewCollector.Snapshot();
                var reviewEntries = previewCollector.ReviewSnapshot();
                if (previewEntries.Count > 0 || reviewEntries.Count > 0)
                {
                    var previewManifest = new TexturePreviewManifest(
                        TexturePreviewManifest.CurrentSchemaVersion,
                        finalizing.BuildId,
                        DateTimeOffset.UtcNow,
                        previewEntries)
                    {
                        ReviewEntries = reviewEntries
                    };
                    previewManifestPath = Path.Combine(
                        finalizing.BuildDirectory,
                        "previews",
                        "preview-manifest.json");
                    await WritePreviewManifestAsync(
                        previewManifestPath,
                        previewManifest,
                        metadataCancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        if (report is null || reportPath is null)
        {
            throw new InvalidDataException("The staged build metadata finalizer did not complete.");
        }

        ApplyResult? applyResult = null;
        if (options.InstallAfterBuild)
        {
            clientClosedGuard();
            applyResult = await installTransactionService.ApplyAsync(
                paths,
                staged.ManifestPath,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        return new TexturePackBuildResult(
            staged,
            report,
            reportPath,
            applyResult,
            previewManifestPath);
    }

    internal async Task<BuildOriginalSourceResolver> PrepareBuildOriginalSourcesAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var health = await installHealthService
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        if (health.State == InstallHealthState.RevertedToOriginal)
        {
            // The fast audit can identify a restored file by length alone. A
            // build is a new trust boundary, so exact-hash every reported live
            // original before allowing it to become a source.
            health = await installHealthService
                .AuditLatestAsync(paths, cancellationToken)
                .ConfigureAwait(false);
        }

        if (health.State is InstallHealthState.None or InstallHealthState.RevertedToOriginal)
        {
            return BuildOriginalSourceResolver.Empty;
        }

        if (health.State == InstallHealthState.MixedOrModified)
        {
            throw new InvalidOperationException(
                "SpinTexture will not build from a client that is partly enhanced, partly original, or externally modified. Restore the active pack or finish the LaunchPad update first.");
        }

        if (health.State == InstallHealthState.RecoveryRequired)
        {
            throw new InvalidOperationException(
                $"SpinTexture will not build while an install transaction requires recovery. {health.Summary}");
        }

        if (health.State != InstallHealthState.EnhancedActive
            || string.IsNullOrWhiteSpace(health.InstallManifestPath))
        {
            throw new InvalidDataException(
                "The active enhanced install did not identify a verified transaction manifest.");
        }

        var safeManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.BackupPath,
            health.InstallManifestPath);
        var manifest = await manifestStore
            .ReadInstallManifestAsync(safeManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (!PathGuard.SamePath(manifest.InstallPath, paths.InstallPath)
            || manifest.State != InstallTransactionState.Applied
            || manifest.Entries is null
            || manifest.Entries.Count == 0)
        {
            throw new InvalidDataException(
                "The active install transaction cannot provide trustworthy original build sources.");
        }

        var backupDirectory = Path.GetDirectoryName(safeManifestPath)!;
        var references = new Dictionary<string, ManagedOriginalSource>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!artifact.OriginalExisted
                || artifact.OriginalLength < 0
                || artifact.OriginalSha256 is not { Length: 64 }
                || artifact.OriginalSha256.Any(character => !Uri.IsHexDigit(character))
                || string.IsNullOrWhiteSpace(artifact.BackupRelativePath))
            {
                throw new InvalidDataException(
                    $"The active install is missing a verified original source for {artifact.RelativeInstallPath}.");
            }

            var livePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                artifact.RelativeInstallPath);
            var relativePath = Path.GetRelativePath(paths.InstallPath, livePath);
            var backupPath = PathGuard.ResolveUnderRoot(
                backupDirectory,
                artifact.BackupRelativePath);
            if (!PathGuard.IsPathUnderRoot(paths.BackupPath, backupPath)
                || !references.TryAdd(
                    relativePath,
                    new ManagedOriginalSource(
                        backupPath,
                        artifact.OriginalLength,
                        artifact.OriginalSha256)))
            {
                throw new InvalidDataException(
                    $"The active install contains an unsafe or duplicate original source for {relativePath}.");
            }
        }

        return new BuildOriginalSourceResolver(references);
    }

    /// <summary>
    /// Creates a new full replacement pack from an existing completed pack without
    /// rerunning textures that were already enhanced successfully. Changed members
    /// are reused byte-for-byte; only unchanged entries that are eligible under the
    /// current safety rules are retried.
    /// </summary>
    public async Task<TexturePackBuildResult> RepairStagedPackAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        TexturePreset retryPreset = TexturePreset.Faithful,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<TextureOverride>? textureOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineManifestPath);
        TextureOverridePolicy.ValidateAll(textureOverrides);
        _ = retryPreset; // Retained for binary/source compatibility; repair never changes pack identity.
        var isManualTextureRevision = textureOverrides is { Count: > 0 };
        var repairStartedUtc = DateTimeOffset.UtcNow;
        var validation = EverQuestInstall.Validate(paths.InstallPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }

        var tools = toolchainDiscovery.Discover(paths);
        if (!tools.IsReady)
        {
            throw new FileNotFoundException(
                "SpinTexture's bundled processing tools are incomplete. "
                + string.Join(" ", tools.Diagnostics));
        }

        var safeBaselineManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.StagingPath,
            baselineManifestPath);
        var baselineInfo = await stagedPackCatalogService
            .InspectAsync(
                paths,
                safeBaselineManifestPath,
                StagedPackVerificationMode.Exact,
                cancellationToken)
            .ConfigureAwait(false);
        if (!baselineInfo.IsReady || baselineInfo.Manifest is null)
        {
            throw new InvalidDataException(
                $"The selected staged pack cannot be repaired safely: {baselineInfo.Summary}");
        }

        var baseline = baselineInfo.Manifest;
        var baselineDirectory = Path.GetDirectoryName(safeBaselineManifestPath)!;
        if (File.Exists(Path.Combine(baselineDirectory, "composition.json")))
        {
            throw new InvalidOperationException(
                "A combined pack cannot be repaired as one build. Repair its original character/equipment constituent, then select the repaired pack with the zone packs you want active.");
        }

        var baselineReport = await TryReadTextureBuildReportAsync(
                baselineInfo.BuildDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        var currentPaintedProfileRevision =
            GetFreshPaintedProfileRevision(baseline.Options.Preset);
        if (isManualTextureRevision
            && currentPaintedProfileRevision > 0
            && (baselineReport?.PaintedProfileRevision ?? 0) != currentPaintedProfileRevision
            && textureOverrides!.Any(choice =>
                choice.Action == TextureOverrideAction.Reprocess))
        {
            throw new InvalidOperationException(
                "This painted pack uses an older or unknown art-profile revision. SpinTexture will not mix newly processed textures into it; build a fresh painted pack instead. Preserve Original choices remain safe.");
        }
        var baselineArtifactPaths = baselineInfo.Artifacts
            .Select(artifact => artifact.CanonicalRelativeInstallPath)
            .ToArray();
        var isPipelineRepairScope = baseline.Options.Scope is
            AssetScope.CharactersAndEquipmentOnly
            or AssetScope.WorldOnly
            or AssetScope.WorldCharactersAndEquipment
            or AssetScope.SelectedZone
            or AssetScope.SpellEffectsOnly
            or AssetScope.AllSafeTextures;
        var requiresPipelineRepair = TextureProcessingPipeline.RequiresRepair(
            baselineReport,
            baseline.Options.Scope,
            baselineArtifactPaths);
        if (isManualTextureRevision && requiresPipelineRepair)
        {
            throw new InvalidOperationException(
                "Upgrade this staged pack to the current texture pipeline before applying individual texture choices. The baseline was left unchanged.");
        }
        var isTargetedSafetyRepair = !isManualTextureRevision
            && TextureProcessingPipeline.RequiresTargetedSafetyRepair(
                baselineReport,
                baseline.Options.Scope,
                baselineArtifactPaths);
        if (!isManualTextureRevision && isPipelineRepairScope && !requiresPipelineRepair)
        {
            throw new InvalidOperationException(
                "This staged pack already uses the current texture pipeline. The completed pack was left unchanged.");
        }

        var missingRepairRules = TextureProcessingPipeline.GetMissingRepairRuleIds(
            baselineReport,
            baseline.Options.Scope,
            baselineArtifactPaths);
        var missingExactOriginalRules = missingRepairRules
            .Where(ruleId =>
                ruleId.Equals(
                    TextureProcessingPipeline.CelestialSkySafetyRuleId,
                    StringComparison.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.NativeSkyResourceSafetyRuleId,
                    StringComparison.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.LegacyTranslucentMaterialSafetyRuleId,
                    StringComparison.Ordinal))
            .ToArray();
        // The masked color-key rule regenerates textures, so it can only run
        // on the full targeted repair path. A pack that is also missing
        // exact-original safety rules takes the lighter exact-original path
        // first (recording only the rules it actually applied); the masked
        // rule stays missing and is offered as its own follow-up repair.
        var isExactOriginalSafetyRepair = !isManualTextureRevision
            && isTargetedSafetyRepair
            && missingExactOriginalRules.Length != 0
            && missingRepairRules.All(ruleId =>
                missingExactOriginalRules.Contains(ruleId, StringComparer.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.MaskedMaterialColorKeySafetyRuleId,
                    StringComparison.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.ExpandedClassicCoverageRuleId,
                    StringComparison.Ordinal));

        if (baseline.Options.Scope == AssetScope.AllSafeTextures
            && !isExactOriginalSafetyRepair)
        {
            throw new InvalidOperationException(
                "Legacy All Safe packs can mix PFS and loose files and may have been built from managed enhanced sources. Their source provenance must be repaired before a safe mixed-artifact upgrade can be offered; the selected pack was left unchanged.");
        }

        if (baseline.Options.Scope != AssetScope.SpellEffectsOnly
            && !isExactOriginalSafetyRepair
            && baseline.Entries.Any(entry =>
                !EverQuestInstall.IsPfsArchiveExtension(
                    Path.GetExtension(entry.RelativeInstallPath))))
        {
            throw new InvalidOperationException(
                "This pack contains loose or non-archive artifacts that the missing-texture repair cannot safely reconstruct. Build that scope again instead of risking an incomplete replacement.");
        }

        var baselineEntries = baselineInfo.Artifacts.ToDictionary(
            artifact => artifact.CanonicalRelativeInstallPath,
            artifact => artifact.Entry with
            {
                RelativeInstallPath = artifact.CanonicalRelativeInstallPath
            },
            StringComparer.OrdinalIgnoreCase);
        var reuseArchivePaths = baselineInfo.Artifacts.ToDictionary(
            artifact => artifact.CanonicalRelativeInstallPath,
            artifact => artifact.PayloadPath,
            StringComparer.OrdinalIgnoreCase);
        var reuseArchiveFingerprints = baselineInfo.Artifacts.ToDictionary(
            artifact => artifact.CanonicalRelativeInstallPath,
            artifact => new StagedPackFileFingerprint(
                artifact.Entry.StagedLength,
                artifact.Entry.StagedSha256),
            StringComparer.OrdinalIgnoreCase);
        if (isManualTextureRevision)
        {
            foreach (var choice in textureOverrides!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonicalArchive = choice.ArchivePath
                    .Trim()
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var artifact = baselineInfo.Artifacts.FirstOrDefault(candidate =>
                    candidate.CanonicalRelativeInstallPath.Equals(
                        canonicalArchive,
                        StringComparison.OrdinalIgnoreCase));
                if (artifact is null
                    || !EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(canonicalArchive)))
                {
                    throw new InvalidDataException(
                        $"The reviewed archive is not part of this verified PFS pack: {choice.ArchivePath}.");
                }

                await using var reviewedArchive = await PfsArchive.OpenAsync(
                    artifact.PayloadPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!reviewedArchive.TryGetEntry(choice.LogicalName, out var reviewedEntry)
                    || reviewedEntry is null
                    || !reviewedEntry.IsTexture)
                {
                    throw new InvalidDataException(
                        $"The reviewed texture no longer exists in {choice.ArchivePath}: {choice.LogicalName}.");
                }
            }
        }

        InstallManifest? activeInstall = null;
        string? activeInstallDirectory = null;
        var activeInstallPath = FindLatestInstallManifest(paths);
        if (activeInstallPath is not null)
        {
            activeInstall = await manifestStore
                .ReadInstallManifestAsync(activeInstallPath, cancellationToken)
                .ConfigureAwait(false);
            activeInstallDirectory = Path.GetDirectoryName(activeInstallPath)!;
        }

        if (isExactOriginalSafetyRepair)
        {
            return await RepairCelestialSafetyPackAsync(
                    paths,
                    baselineInfo,
                    baselineReport,
                    tools,
                    missingExactOriginalRules,
                    activeInstall,
                    activeInstallDirectory,
                    repairStartedUtc,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var repairOptions = baseline.Options with
        {
            // Every repair preserves the completed pack's recorded visual
            // profile. A repair must never silently turn Painted work into
            // Faithful, Texture HD, or original pixels.
            InstallAfterBuild = false,
            TextureOverrides = textureOverrides
        };
        var requireRequestedVisualProfile = baseline.Options.Preset is
            TexturePreset.Illustrated or TexturePreset.RusticPainted;
        var hasReproduciblePaintedProfile = !requireRequestedVisualProfile
            || (baselineReport?.PaintedProfileRevision ?? 0)
                == currentPaintedProfileRevision;
        var counter = new TextureBuildCounter();
        // Graphic Painted Fantasy has two renderers: the external diffusion
        // worker and the built-in painterly stylization. A repair regenerates
        // retried textures with whichever renderer is configured today, so a
        // mismatch with the pack's recorded renderer would quietly mix two
        // different art styles into one pack.
        var recordedArtisticWorker = baselineReport?.UsedExternalArtisticWorker;
        if (baseline.Options.Preset == TexturePreset.Illustrated
            && hasReproduciblePaintedProfile
            && !isManualTextureRevision)
        {
            if (recordedArtisticWorker is { } recordedWorker)
            {
                if (recordedWorker != tools.HasArtisticWorker)
                {
                    throw new InvalidOperationException(recordedWorker
                        ? "This Graphic Painted Fantasy pack was created with the diffusion repaint worker, but that worker is not currently set up. Run Set Up Diffusion Repaint again (or rebuild the pack) before repairing, so the repair keeps one consistent art style."
                        : "This Graphic Painted Fantasy pack was created with the built-in painterly stylization, but the diffusion repaint worker is currently enabled. Disable the worker (or rebuild the pack with it) before repairing, so the repair keeps one consistent art style.");
                }
            }
            else
            {
                counter.Warn(
                    "This pack predates painted-renderer provenance; retried textures use the currently configured Graphic Painted Fantasy renderer.");
            }
        }
        var archiveScopes = DiscoverArchiveScopes(paths.InstallPath);
        var selectedArchives = SelectArchives(archiveScopes, repairOptions);
        var selectedRelativePaths = selectedArchives
            .Select(path => Path.GetRelativePath(paths.InstallPath, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncoveredBaselineArtifacts = baselineEntries.Keys
            .Where(relativePath => !selectedRelativePaths.Contains(relativePath))
            .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (uncoveredBaselineArtifacts.Length != 0)
        {
            var examples = string.Join(
                ", ",
                uncoveredBaselineArtifacts.Take(5).Select(Path.GetFileName));
            throw new InvalidOperationException(
                $"Repair coverage no longer includes {uncoveredBaselineArtifacts.Length:N0} archive(s) from the original pack ({examples}). The original staged pack remains intact; rebuild instead of creating an incomplete replacement.");
        }

        var repairArchives = (isTargetedSafetyRepair || isManualTextureRevision)
            ? selectedArchives
                .Where(path => baselineEntries.ContainsKey(
                    Path.GetRelativePath(paths.InstallPath, path)))
                .ToArray()
            : selectedArchives;

        var processor = new NativeTextureProcessor(tools);
        var previewCollector = new TexturePreviewCollector(maximumEntries: 24);
        var archiveBuilder = new PfsTextureArchiveBuilder(
            processor,
            counter,
            progress,
            previewCollector,
            clampArchivePaths: SelectCharacterAndEquipmentArchives(
                archiveScopes,
                repairOptions),
            filterCharacterEquipmentEntries:
                repairOptions.Scope is AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment,
            reuseArchivePaths: reuseArchivePaths,
            reuseArchiveFingerprints: reuseArchiveFingerprints,
            // A painted repair always starts from the exact verified staged
            // archive. Rebuilding from the original would silently drop any
            // prior painted member that no longer matches today's allowlist.
            rebuildFromReuseArchive: isTargetedSafetyRepair
                || isManualTextureRevision
                || requireRequestedVisualProfile,
            // Targeted repairs normally leave source-identical (previously
            // preserved) members untouched. The expanded-coverage rule exists
            // precisely to re-attempt them under today's widened eligibility,
            // so it opts those members back in.
            retryUnchangedEntries: !isManualTextureRevision
                && (!isTargetedSafetyRepair
                    || missingRepairRules.Contains(
                        TextureProcessingPipeline.ExpandedClassicCoverageRuleId,
                        StringComparer.Ordinal)),
            requireRequestedVisualProfile: requireRequestedVisualProfile,
            // Only the explicitly versioned current painted pipeline can
            // reproduce a missing member. Legacy painted pixels can still be
            // reused byte-for-byte, but mixing them with today's algorithm is
            // refused so users receive a rebuild instruction instead.
            allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile);

        var items = new List<StagedBuildItem>();
        for (var index = 0; index < repairArchives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveArchivePath = repairArchives[index];
            var relativePath = Path.GetRelativePath(paths.InstallPath, liveArchivePath);
            progress?.Report(new ProgressUpdate(
                "Repair plan",
                isManualTextureRevision
                    ? "Verifying prior output and locating only user-selected texture changes."
                    : isTargetedSafetyRepair
                    ? "Verifying prior output and locating only textures affected by newer safety rules."
                    : "Verifying prior successes and locating only missing texture work.",
                index,
                repairArchives.Count,
                relativePath));

            var sourcePath = liveArchivePath;
            if (baselineEntries.TryGetValue(relativePath, out var baselineEntry))
            {
                sourcePath = await ResolveRepairSourceAsync(
                    paths,
                    liveArchivePath,
                    baselineEntry,
                    activeInstall,
                    activeInstallDirectory,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await using var archive = await PfsArchive.OpenAsync(
                    sourcePath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (archive.Entries.Any(entry =>
                        PfsTextureArchiveBuilder.IsPotentialCandidate(
                            entry,
                            repairOptions.MaximumDimension)
                        && (repairOptions.Scope is not (
                                AssetScope.CharactersAndEquipmentOnly
                                or AssetScope.WorldCharactersAndEquipment)
                            || CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                                relativePath,
                                entry.Name))))
                {
                    items.Add(new StagedBuildItem(
                        relativePath,
                        archiveBuilder,
                        PathGuard.SamePath(sourcePath, liveArchivePath) ? null : sourcePath,
                        baselineEntry?.SourceLength,
                        baselineEntry?.SourceSha256,
                        AllowSourceIdenticalOmission: isTargetedSafetyRepair
                            && CanOmitSourceIdenticalSafetyArtifact(relativePath)));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                if (baselineEntries.ContainsKey(relativePath))
                {
                    throw new InvalidDataException(
                        $"A previously staged archive could not be read during repair: {relativePath}. The original staged pack remains intact and no incomplete replacement was created.",
                        exception);
                }

                counter.Warn($"Skipped repair archive {Path.GetFileName(relativePath)}: {exception.Message}");
            }
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected staged pack has no reusable or retryable texture archives.");
        }


        if ((isTargetedSafetyRepair || isManualTextureRevision)
            && items.Count != baselineEntries.Count)
        {
            throw new InvalidOperationException(
                "The targeted safety repair could not account for every archive in the verified baseline. The original staged pack remains intact; no incomplete replacement was created.");
        }

        var staged = await stagedBuildService.BuildAsync(
            new StagedBuildRequest(
                paths,
                repairOptions,
                items,
                RequireAllItems: isTargetedSafetyRepair || isManualTextureRevision),
            progress,
            cancellationToken).ConfigureAwait(false);
        var preliminaryStatistics = counter.Snapshot();
        counter.Warn(isManualTextureRevision
            ? $"Texture revision reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and changed only explicitly reviewed entries."
            : isTargetedSafetyRepair
                ? $"Safety repair reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and changed only outputs affected by newer safety rules; source-identical entries were not retried."
                : $"Repair reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and retried only unchanged eligible entries.");
        var retriedKeptOriginal = preliminaryStatistics.PreservedReasons.GetValueOrDefault(
            PfsTextureArchiveBuilder.RetriedPreservedOriginalReason);
        counter.Warn(
            $"Repair outcome: {preliminaryStatistics.EnhancedTextures:N0} texture(s) were newly enhanced by this repair"
            + (retriedKeptOriginal > 0
                ? $"; {retriedKeptOriginal:N0} retried texture(s) could not be regenerated and kept their verified originals."
                : "."));
        var statistics = counter.Snapshot();
        if (statistics.EnhancedTextures == 0 && statistics.ReusedTextures == 0)
        {
            throw new InvalidOperationException(
                "The repair completed without a reusable or newly enhanced texture.");
        }

        var repairCompletedUtc = DateTimeOffset.UtcNow;
        var report = new TextureBuildReport(
            TextureBuildReport.CurrentSchemaVersion,
            staged.BuildId,
            repairCompletedUtc,
            paths.InstallPath,
            staged.BuildDirectory,
            repairArchives.Count,
            statistics)
        {
            StartedUtc = repairStartedUtc,
            DurationSeconds = (repairCompletedUtc - repairStartedUtc).TotalSeconds,
            IsIncrementalRepair = true,
            IsSafetyRepair = isTargetedSafetyRepair,
            // Compatibility flag: true only when the baseline predates the
            // cutout rule, even though the targeted route may apply newer rules.
            IsCutoutMipRepair = isTargetedSafetyRepair
                && (baselineReport?.TexturePipelineRevision ?? 0) < 3,
            IsManualTextureRevision = isManualTextureRevision,
            BaselineBuildId = baseline.BuildId,
            BaselineTexturePipelineRevision = baselineReport?.TexturePipelineRevision ?? 0,
            TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision,
            PaintedProfileRevision = baselineReport?.PaintedProfileRevision ?? 0,
            // A pack that predates renderer provenance regenerated its retried
            // textures with the currently configured renderer just now, so the
            // replacement records today's renderer as its identity.
            UsedExternalArtisticWorker = baseline.Options.Preset == TexturePreset.Illustrated
                ? recordedArtisticWorker ?? tools.HasArtisticWorker
                : recordedArtisticWorker,
            AppliedRepairRuleIds = TextureProcessingPipeline
                .GetCurrentRepairRuleIds(
                    baseline.Options.Scope,
                    baselineArtifactPaths)
        };
        var reportPath = Path.Combine(staged.BuildDirectory, "texture-report.json");
        await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

        string? previewManifestPath = null;
        var previewEntries = previewCollector.Snapshot();
        var reviewEntries = previewCollector.ReviewSnapshot();
        if (previewEntries.Count > 0 || reviewEntries.Count > 0)
        {
            var previewManifest = new TexturePreviewManifest(
                TexturePreviewManifest.CurrentSchemaVersion,
                staged.BuildId,
                DateTimeOffset.UtcNow,
                previewEntries)
            {
                ReviewEntries = reviewEntries
            };
            previewManifestPath = Path.Combine(
                staged.BuildDirectory,
                "previews",
                "preview-manifest.json");
            await WritePreviewManifestAsync(
                previewManifestPath,
                previewManifest,
                cancellationToken).ConfigureAwait(false);
        }

        return new TexturePackBuildResult(
            staged,
            report,
            reportPath,
            ApplyResult: null,
            previewManifestPath);
    }

    /// <summary>
    /// Finds PFS-only world packs whose recorded source bytes are known outputs of
    /// an earlier managed SpinTexture install. This is a metadata-only discovery
    /// pass; the repair action exact-verifies every source and staged payload.
    /// </summary>
    public async Task<IReadOnlySet<string>> FindStagedPackSourceRepairCandidatesAsync(
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifestPaths);
        var provenance = await LoadManagedInstallProvenanceAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (provenance.Count == 0)
        {
            return candidates;
        }

        foreach (var requestedPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = PathGuard.EnsurePathUnderRoot(paths.StagingPath, requestedPath);
            var info = await stagedPackCatalogService
                .InspectAsync(
                    paths,
                    safePath,
                    StagedPackVerificationMode.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsSourceMismatchRepairEligible(info))
            {
                continue;
            }

            if (info.Artifacts.Any(artifact =>
                    provenance.TryGetValue(
                        CreateManagedInstalledKey(
                            artifact.CanonicalRelativeInstallPath,
                            artifact.Entry.SourceLength,
                            artifact.Entry.SourceSha256),
                        out var sources)
                    && sources.Any(source =>
                        source.OriginalLength != artifact.Entry.SourceLength
                        || !source.OriginalSha256.Equals(
                            artifact.Entry.SourceSha256,
                            StringComparison.OrdinalIgnoreCase))))
            {
                candidates.Add(info.ManifestPath);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Advances a verified completed pack through an original-content safety
    /// revision without invoking the AI pipeline or recompressing unaffected
    /// PFS members. Ordinary payloads are exact-reused; protected sky and loose
    /// celestial artifacts are restored from verified originals and omitted,
    /// while legacy translucent WLD material frames are restored in-place.
    /// </summary>
    private async Task<TexturePackBuildResult> RepairCelestialSafetyPackAsync(
        ProjectPaths paths,
        StagedPackInfo baselineInfo,
        TextureBuildReport? baselineReport,
        ExternalToolPaths tools,
        IReadOnlyList<string> missingRepairRules,
        InstallManifest? activeInstall,
        string? activeInstallDirectory,
        DateTimeOffset repairStartedUtc,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var baseline = baselineInfo.Manifest
            ?? throw new InvalidDataException("The verified safety-repair baseline has no manifest.");
        var provenance = await LoadManagedInstallProvenanceAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        foreach (var artifact in baselineInfo.Artifacts)
        {
            var entry = artifact.Entry;
            if (provenance.TryGetValue(
                    CreateManagedInstalledKey(
                        artifact.CanonicalRelativeInstallPath,
                        entry.SourceLength,
                        entry.SourceSha256),
                    out var knownSources)
                && knownSources.Any(source =>
                    source.OriginalLength != entry.SourceLength
                    || !source.OriginalSha256.Equals(
                        entry.SourceSha256,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"{artifact.CanonicalRelativeInstallPath} was built from bytes installed by an older managed pack. Run Repair Pack's source-and-safety repair so this artifact is rebuilt from its verified original; the staged baseline was left unchanged.");
            }
        }

        var counter = new TextureBuildCounter();
        var repairLegacyTranslucentMaterials = missingRepairRules.Contains(
            TextureProcessingPipeline.LegacyTranslucentMaterialSafetyRuleId,
            StringComparer.Ordinal);
        var reuseArchivePaths = baselineInfo.Artifacts
            .Where(artifact => EverQuestInstall.IsPfsArchiveExtension(
                Path.GetExtension(artifact.CanonicalRelativeInstallPath)))
            .ToDictionary(
                artifact => artifact.CanonicalRelativeInstallPath,
                artifact => artifact.PayloadPath,
                StringComparer.OrdinalIgnoreCase);
        var reuseArchiveFingerprints = baselineInfo.Artifacts
            .Where(artifact => EverQuestInstall.IsPfsArchiveExtension(
                Path.GetExtension(artifact.CanonicalRelativeInstallPath)))
            .ToDictionary(
                artifact => artifact.CanonicalRelativeInstallPath,
                artifact => new StagedPackFileFingerprint(
                    artifact.Entry.StagedLength,
                    artifact.Entry.StagedSha256),
                StringComparer.OrdinalIgnoreCase);
        var translucentMaterialBuilder = repairLegacyTranslucentMaterials
            ? new PfsTextureArchiveBuilder(
                new NativeTextureProcessor(tools),
                counter,
                progress,
                previewCollector: null,
                filterCharacterEquipmentEntries: false,
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: reuseArchivePaths,
                reuseArchiveFingerprints: reuseArchiveFingerprints,
                requireRequestedVisualProfile: baseline.Options.Preset is
                    TexturePreset.Illustrated or TexturePreset.RusticPainted,
                allowRequestedVisualProfileRegeneration:
                    baseline.Options.Preset is not (TexturePreset.Illustrated
                        or TexturePreset.RusticPainted)
                    || (baselineReport?.PaintedProfileRevision ?? 0)
                        == GetFreshPaintedProfileRevision(baseline.Options.Preset))
            : null;
        var items = new List<StagedBuildItem>(baselineInfo.Artifacts.Count);
        var exactReusedArtifacts = 0;
        var safetyUpgradedArtifacts = 0;
        for (var index = 0; index < baselineInfo.Artifacts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = baselineInfo.Artifacts[index];
            var entry = artifact.Entry;
            var relativePath = artifact.CanonicalRelativeInstallPath;
            var livePath = PathGuard.ResolveUnderRoot(paths.InstallPath, relativePath);
            var sourcePath = await ResolveRepairSourceAsync(
                    paths,
                    livePath,
                    entry with { RelativeInstallPath = relativePath },
                    activeInstall,
                    activeInstallDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            var restoredToOriginal = CanOmitSourceIdenticalSafetyArtifact(relativePath);
            var pfsTranslucentRepair = translucentMaterialBuilder is not null
                && !restoredToOriginal
                && await ContainsLegacyTranslucentMaterialAsync(
                    sourcePath,
                    cancellationToken).ConfigureAwait(false);
            IStagedArtifactBuilder builder = pfsTranslucentRepair
                ? translucentMaterialBuilder!
                : new LooseTextureSafetyRepairBuilder(
                artifact.PayloadPath,
                entry.StagedLength,
                entry.StagedSha256,
                counter);
            items.Add(new StagedBuildItem(
                relativePath,
                builder,
                PathGuard.SamePath(sourcePath, livePath) ? null : sourcePath,
                entry.SourceLength,
                entry.SourceSha256,
                ExpectedStagedLength: restoredToOriginal || pfsTranslucentRepair
                    ? null
                    : entry.StagedLength,
                ExpectedStagedSha256: restoredToOriginal || pfsTranslucentRepair
                    ? null
                    : entry.StagedSha256,
                AllowSourceIdenticalOmission: restoredToOriginal));
            if (restoredToOriginal || pfsTranslucentRepair)
            {
                safetyUpgradedArtifacts++;
            }
            else
            {
                exactReusedArtifacts++;
            }
            progress?.Report(new ProgressUpdate(
                "Safety repair plan",
                restoredToOriginal
                    ? "Restoring protected sky/celestial bytes from the verified original."
                    : pfsTranslucentRepair
                    ? "Restoring legacy water/translucent material frames while exact-reusing unaffected enhanced archive members."
                    : "Exact-reusing an unaffected completed artifact.",
                index + 1,
                baselineInfo.Artifacts.Count,
                relativePath));
        }

        var repairOptions = baseline.Options with
        {
            InstallAfterBuild = false,
            TextureOverrides = null
        };
        StagedBuildResult staged;
        try
        {
            staged = await stagedBuildService.BuildAsync(
                    new StagedBuildRequest(
                        paths,
                        repairOptions,
                        items,
                        RequireAllItems: true),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "no changed install artifacts",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Every artifact in this legacy pack is now protected original content, so there is no enhanced payload left to stage. Restore or delete the old pack instead; no incomplete replacement was created.",
                exception);
        }

        var statistics = counter.Snapshot();
        counter.Warn(
            $"Safety repair exact-reused {exactReusedArtifacts:N0} unaffected artifact(s) and upgraded {safetyUpgradedArtifacts:N0} artifact(s) by restoring protected sky/celestial or legacy translucent-material content from verified originals without AI processing.");
        statistics = counter.Snapshot();
        var completedUtc = DateTimeOffset.UtcNow;
        var artifactPaths = baselineInfo.Artifacts
            .Select(artifact => artifact.CanonicalRelativeInstallPath)
            .ToArray();
        var report = new TextureBuildReport(
            TextureBuildReport.CurrentSchemaVersion,
            staged.BuildId,
            completedUtc,
            paths.InstallPath,
            staged.BuildDirectory,
            baselineInfo.Artifacts.Count,
            statistics)
        {
            StartedUtc = repairStartedUtc,
            DurationSeconds = (completedUtc - repairStartedUtc).TotalSeconds,
            IsIncrementalRepair = true,
            IsSafetyRepair = true,
            IsCutoutMipRepair = false,
            BaselineBuildId = baseline.BuildId,
            BaselineTexturePipelineRevision = baselineReport?.TexturePipelineRevision ?? 0,
            ReusedArtifacts = exactReusedArtifacts,
            SafetyUpgradedArtifacts = safetyUpgradedArtifacts,
            TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision,
            PaintedProfileRevision = baselineReport?.PaintedProfileRevision ?? 0,
            UsedExternalArtisticWorker = baselineReport?.UsedExternalArtisticWorker,
            // Record only what this exact-original pass actually applied on
            // top of the pack's prior provenance. Claiming every current rule
            // here would silently mark repairs (such as the masked color-key
            // regeneration) as done when this path never performs them.
            AppliedRepairRuleIds = TextureProcessingPipeline
                .GetRecordedRepairRuleIds(baselineReport, baseline.Options.Scope)
                .Concat(missingRepairRules)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(ruleId => ruleId, StringComparer.Ordinal)
                .ToArray()
        };
        var reportPath = Path.Combine(staged.BuildDirectory, "texture-report.json");
        await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
        return new TexturePackBuildResult(
            staged,
            report,
            reportPath,
            ApplyResult: null,
            PreviewManifestPath: null);
    }

    private static async Task<bool> ContainsLegacyTranslucentMaterialAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (!EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(archivePath)))
        {
            return false;
        }

        try
        {
            await using var archive = await PfsArchive.OpenAsync(
                archivePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var wldEntry in archive.Entries.Where(entry =>
                         entry.Name.EndsWith(".wld", StringComparison.OrdinalIgnoreCase)))
            {
                var payload = await archive.ReadEntryAsync(wldEntry.Name, cancellationToken)
                    .ConfigureAwait(false);
                if (LegacyTranslucentMaterialSafetyPolicy
                    .FindProtectedTextureNames(payload).Count != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is PfsArchiveException
                                               or InvalidDataException
                                               or IOException)
        {
            // This optimized original-safety path may also carry synthetic or
            // non-PFS fixtures with a legacy extension. Leave them exact rather
            // than guessing at material ownership. Normal build validation still
            // rejects unreadable game archives before enhancement.
            return false;
        }
    }

    /// <summary>
    /// Creates a new immutable replacement for a source-contaminated PFS world
    /// pack. Whole staged artifacts are reused only when the currently verified
    /// original still matches the baseline source. Artifacts built from a known
    /// managed enhanced fingerprint are rebuilt from the corresponding original;
    /// no member-level output from those artifacts is reused.
    /// </summary>
    public Task<TexturePackBuildResult> RepairStagedPackSourceMismatchAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        RepairStagedPackSourceMismatchCoreAsync(
            paths,
            baselineManifestPath,
            rebuildBuilderOverride: null,
            targetedSafetyBuilderOverride: null,
            forceTargetedSafetyRepair: null,
            progress,
            cancellationToken);

    internal Task<TexturePackBuildResult> RepairStagedPackSourceMismatchAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        IStagedArtifactBuilder rebuildBuilder,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        RepairStagedPackSourceMismatchCoreAsync(
            paths,
            baselineManifestPath,
            rebuildBuilder ?? throw new ArgumentNullException(nameof(rebuildBuilder)),
            targetedSafetyBuilderOverride: null,
            forceTargetedSafetyRepair: false,
            progress,
            cancellationToken);

    internal Task<TexturePackBuildResult> RepairStagedPackSourceMismatchAndSafetyAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        IStagedArtifactBuilder rebuildBuilder,
        IStagedArtifactBuilder targetedSafetyBuilder,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        RepairStagedPackSourceMismatchCoreAsync(
            paths,
            baselineManifestPath,
            rebuildBuilder ?? throw new ArgumentNullException(nameof(rebuildBuilder)),
            targetedSafetyBuilder
                ?? throw new ArgumentNullException(nameof(targetedSafetyBuilder)),
            forceTargetedSafetyRepair: true,
            progress,
            cancellationToken);

    private async Task<TexturePackBuildResult> RepairStagedPackSourceMismatchCoreAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        IStagedArtifactBuilder? rebuildBuilderOverride,
        IStagedArtifactBuilder? targetedSafetyBuilderOverride,
        bool? forceTargetedSafetyRepair,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineManifestPath);
        var repairStartedUtc = DateTimeOffset.UtcNow;
        var validation = EverQuestInstall.Validate(paths.InstallPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }

        ExternalToolPaths? tools = null;
        if (rebuildBuilderOverride is null
            || (forceTargetedSafetyRepair == true
                && targetedSafetyBuilderOverride is null))
        {
            tools = toolchainDiscovery.Discover(paths);
            if (!tools.IsReady)
            {
                throw new FileNotFoundException(
                    "SpinTexture's bundled processing tools are incomplete. "
                    + string.Join(" ", tools.Diagnostics));
            }
        }

        var safeBaselineManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.StagingPath,
            baselineManifestPath);
        var baselineInfo = await stagedPackCatalogService
            .InspectAsync(
                paths,
                safeBaselineManifestPath,
                StagedPackVerificationMode.Exact,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsSourceMismatchRepairEligible(baselineInfo))
        {
            throw new InvalidOperationException(
                "Source-mismatch repair is available only for a complete, non-composite PFS world pack. The selected pack was left unchanged.");
        }

        var baseline = baselineInfo.Manifest!;
        var baselineReport = await TryReadTextureBuildReportAsync(
                baselineInfo.BuildDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        var isPaintedBaseline = baseline.Options.Preset is
            TexturePreset.Illustrated or TexturePreset.RusticPainted;
        var currentPaintedProfileRevision =
            GetFreshPaintedProfileRevision(baseline.Options.Preset);
        var hasReproduciblePaintedProfile = !isPaintedBaseline
            || (baselineReport?.PaintedProfileRevision ?? 0)
                == currentPaintedProfileRevision;
        // Production source repair can advance a stale, provenance-capable pack
        // through the current targeted safety rules in the same immutable
        // replacement. Tests may inject a non-PFS rebuild builder, so that seam
        // deliberately retains the original whole-artifact behavior.
        var applyTargetedSafetyRepair = forceTargetedSafetyRepair
            ?? (rebuildBuilderOverride is null
                && TextureProcessingPipeline.RequiresTargetedSafetyRepair(
                    baselineReport,
                    baseline.Options.Scope,
                    baselineInfo.Artifacts.Select(artifact =>
                        artifact.CanonicalRelativeInstallPath)));
        var originalSources = await PrepareBuildOriginalSourcesAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        var provenance = await LoadManagedInstallProvenanceAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        var archiveScopes = DiscoverArchiveScopes(paths.InstallPath);
        var counter = new TextureBuildCounter();
        var processor = tools is null ? null : new NativeTextureProcessor(tools);
        var previewCollector = new TexturePreviewCollector(maximumEntries: 24);
        var rebuildBuilder = rebuildBuilderOverride ?? new PfsTextureArchiveBuilder(
            processor!,
            counter,
            progress,
            previewCollector,
            clampArchivePaths: SelectCharacterAndEquipmentArchives(
                archiveScopes,
                baseline.Options),
            filterCharacterEquipmentEntries:
                baseline.Options.Scope is AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment,
            requireRequestedVisualProfile: isPaintedBaseline,
            allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile);
        var reuseArchivePaths = baselineInfo.Artifacts.ToDictionary(
            artifact => artifact.CanonicalRelativeInstallPath,
            artifact => artifact.PayloadPath,
            StringComparer.OrdinalIgnoreCase);
        var reuseArchiveFingerprints = baselineInfo.Artifacts.ToDictionary(
            artifact => artifact.CanonicalRelativeInstallPath,
            artifact => new StagedPackFileFingerprint(
                artifact.Entry.StagedLength,
                artifact.Entry.StagedSha256),
            StringComparer.OrdinalIgnoreCase);
        var targetedSafetyBuilder = applyTargetedSafetyRepair
            ? targetedSafetyBuilderOverride ?? new PfsTextureArchiveBuilder(
                processor!,
                counter,
                progress,
                previewCollector,
                clampArchivePaths: SelectCharacterAndEquipmentArchives(
                    archiveScopes,
                    baseline.Options),
                filterCharacterEquipmentEntries:
                    baseline.Options.Scope is AssetScope.CharactersAndEquipmentOnly
                        or AssetScope.WorldCharactersAndEquipment,
                retryUnchangedEntries: false,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: reuseArchivePaths,
                reuseArchiveFingerprints: reuseArchiveFingerprints,
                requireRequestedVisualProfile: isPaintedBaseline,
                allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile)
            : null;
        var reusableArtifacts = baselineInfo.Artifacts.ToDictionary(
            artifact => artifact.CanonicalRelativeInstallPath,
            artifact => new ReusableStagedArtifact(
                artifact.PayloadPath,
                artifact.Entry.StagedLength,
                artifact.Entry.StagedSha256),
            StringComparer.OrdinalIgnoreCase);
        var reuseBuilder = new WholeStagedArtifactReuseBuilder(reusableArtifacts);
        var items = new List<StagedBuildItem>(baselineInfo.Artifacts.Count);
        var reusedCount = 0;
        var rebuiltCount = 0;
        var safetyUpgradedCount = 0;

        for (var index = 0; index < baselineInfo.Artifacts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = baselineInfo.Artifacts[index];
            var entry = artifact.Entry;
            var relativePath = artifact.CanonicalRelativeInstallPath;
            var livePath = PathGuard.ResolveUnderRoot(paths.InstallPath, relativePath);
            var resolvedSource = await originalSources
                .ResolveAsync(relativePath, livePath, cancellationToken)
                .ConfigureAwait(false);
            var sourcePath = resolvedSource.Path;
            var currentSource = await FileIntegrity
                .FingerprintAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(
                "Source repair plan",
                "Verifying the current original before deciding whether to reuse or rebuild this archive.",
                index,
                baselineInfo.Artifacts.Count,
                relativePath));

            if (currentSource.Length == entry.SourceLength
                && currentSource.Sha256.Equals(
                    entry.SourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                var safetyBuilder = (IStagedArtifactBuilder?)targetedSafetyBuilder;
                items.Add(new StagedBuildItem(
                    relativePath,
                    safetyBuilder ?? reuseBuilder,
                    PathGuard.SamePath(sourcePath, livePath) ? null : sourcePath,
                    currentSource.Length,
                    currentSource.Sha256,
                    // Whole-artifact reuse must reproduce the exact completed
                    // payload. A targeted safety builder intentionally changes
                    // stale output, so comparing it to the old staged hash
                    // would reject the repair before omission can be applied.
                    ExpectedStagedLength: safetyBuilder is null
                        ? entry.StagedLength
                        : null,
                    ExpectedStagedSha256: safetyBuilder is null
                        ? entry.StagedSha256
                        : null,
                    AllowSourceIdenticalOmission: safetyBuilder is not null
                        && CanOmitSourceIdenticalSafetyArtifact(relativePath)));
                if (targetedSafetyBuilder is null)
                {
                    reusedCount++;
                }
                else
                {
                    safetyUpgradedCount++;
                }

                continue;
            }

            if (!provenance.TryGetValue(
                    CreateManagedInstalledKey(
                        relativePath,
                        entry.SourceLength,
                        entry.SourceSha256),
                    out var knownSources))
            {
                throw new InvalidOperationException(
                    $"The current original for {relativePath} does not match the pack source or a verified original behind that managed enhanced source. This appears to be an unknown client-version change; rebuild the World pack instead. The original staged pack remains intact.");
            }

            var matchingSources = knownSources
                .Where(source =>
                    source.OriginalLength == currentSource.Length
                    && source.OriginalSha256.Equals(
                        currentSource.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingSources.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The current original for {relativePath} does not match the pack source or a verified original behind that managed enhanced source. This appears to be an unknown client-version change; rebuild the World pack instead. The original staged pack remains intact.");
            }

            var verifiedHistoricalBackup = false;
            foreach (var knownSource in matchingSources)
            {
                try
                {
                    await FileIntegrity.EnsureMatchesAsync(
                            knownSource.BackupPath,
                            knownSource.OriginalLength,
                            knownSource.OriginalSha256,
                            "Historical managed original proving source-repair provenance",
                            cancellationToken)
                        .ConfigureAwait(false);
                    verifiedHistoricalBackup = true;
                    break;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    // A second matching transaction may still contain an intact
                    // independently verified original. Fail only after all exact
                    // provenance sources have been exhausted.
                }
            }

            if (!verifiedHistoricalBackup)
            {
                throw new InvalidDataException(
                    $"SpinTexture found managed source-repair provenance for {relativePath}, but every matching historical original backup is missing or corrupt. Rebuild the World pack; the original staged pack remains intact.");
            }

            // This artifact was built from bytes installed by a prior managed
            // pack. Rebuild it from the verified original with no reuse archive:
            // member-level reuse would preserve double-upscaled or stale members.
            items.Add(new StagedBuildItem(
                relativePath,
                rebuildBuilder,
                PathGuard.SamePath(sourcePath, livePath) ? null : sourcePath,
                currentSource.Length,
                currentSource.Sha256,
                AllowSourceIdenticalOmission: applyTargetedSafetyRepair
                    && CanOmitSourceIdenticalSafetyArtifact(relativePath)));
            rebuiltCount++;
        }

        if (rebuiltCount == 0)
        {
            throw new InvalidOperationException(
                "This staged pack has no managed source mismatch to repair. The original staged pack remains intact.");
        }

        var repairOptions = baseline.Options with { InstallAfterBuild = false };
        var staged = await stagedBuildService.BuildAsync(
                new StagedBuildRequest(
                    paths,
                    repairOptions,
                    items,
                    RequireAllItems: true),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        counter.Warn(applyTargetedSafetyRepair
            ? $"Source and safety repair upgraded {safetyUpgradedCount:N0} clean archive(s) from their exact staged baselines and rebuilt {rebuiltCount:N0} contaminated archive(s) from verified originals."
            : $"Source repair reused {reusedCount:N0} complete staged archive(s) and rebuilt {rebuiltCount:N0} contaminated archive(s) from verified originals.");
        var statistics = counter.Snapshot();
        var repairCompletedUtc = DateTimeOffset.UtcNow;
        var report = new TextureBuildReport(
            TextureBuildReport.CurrentSchemaVersion,
            staged.BuildId,
            repairCompletedUtc,
            paths.InstallPath,
            staged.BuildDirectory,
            baseline.Entries.Count,
            statistics)
        {
            StartedUtc = repairStartedUtc,
            DurationSeconds = (repairCompletedUtc - repairStartedUtc).TotalSeconds,
            IsIncrementalRepair = applyTargetedSafetyRepair,
            IsSourceMismatchRepair = true,
            IsSafetyRepair = applyTargetedSafetyRepair,
            IsCutoutMipRepair = applyTargetedSafetyRepair
                && (baselineReport?.TexturePipelineRevision ?? 0) < 3,
            BaselineBuildId = baseline.BuildId,
            BaselineTexturePipelineRevision = baselineReport?.TexturePipelineRevision ?? 0,
            ReusedArtifacts = reusedCount,
            RebuiltArtifacts = rebuiltCount,
            SafetyUpgradedArtifacts = safetyUpgradedCount,
            TexturePipelineRevision = applyTargetedSafetyRepair
                ? TextureProcessingPipeline.CurrentRevision
                : baselineReport?.TexturePipelineRevision ?? 0,
            PaintedProfileRevision = baselineReport?.PaintedProfileRevision ?? 0,
            UsedExternalArtisticWorker = baselineReport?.UsedExternalArtisticWorker,
            AppliedRepairRuleIds = applyTargetedSafetyRepair
                ? TextureProcessingPipeline.GetCurrentRepairRuleIds(
                    baseline.Options.Scope,
                    baselineInfo.Artifacts.Select(
                        artifact => artifact.CanonicalRelativeInstallPath))
                : TextureProcessingPipeline.GetRecordedRepairRuleIds(
                    baselineReport,
                    baseline.Options.Scope)
        };
        var reportPath = Path.Combine(staged.BuildDirectory, "texture-report.json");
        await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

        string? previewManifestPath = null;
        var previewEntries = previewCollector.Snapshot();
        var reviewEntries = previewCollector.ReviewSnapshot();
        if (previewEntries.Count > 0 || reviewEntries.Count > 0)
        {
            var previewManifest = new TexturePreviewManifest(
                TexturePreviewManifest.CurrentSchemaVersion,
                staged.BuildId,
                DateTimeOffset.UtcNow,
                previewEntries)
            {
                ReviewEntries = reviewEntries
            };
            previewManifestPath = Path.Combine(
                staged.BuildDirectory,
                "previews",
                "preview-manifest.json");
            await WritePreviewManifestAsync(
                previewManifestPath,
                previewManifest,
                cancellationToken).ConfigureAwait(false);
        }

        return new TexturePackBuildResult(
            staged,
            report,
            reportPath,
            ApplyResult: null,
            previewManifestPath);
    }

    private static bool IsSourceMismatchRepairEligible(StagedPackInfo info) =>
        info.IsMetadataValid
        && info.Manifest is { Entries.Count: > 0 } manifest
        && manifest.Options.Scope is AssetScope.WorldOnly
            or AssetScope.WorldCharactersAndEquipment
            or AssetScope.SelectedZone
        && !File.Exists(Path.Combine(info.BuildDirectory, "composition.json"))
        && info.Artifacts.Count == manifest.Entries.Count
        && info.Artifacts.All(artifact =>
            EverQuestInstall.IsPfsArchiveExtension(
                Path.GetExtension(artifact.CanonicalRelativeInstallPath)));

    private static bool CanOmitSourceIdenticalSafetyArtifact(string relativePath) =>
        CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(relativePath) is not null
        || CelestialTextureSafetyPolicy.GetPreservedReason(
            relativePath,
            Path.GetFileName(relativePath)) is not null;

    private static async Task<TextureBuildReport?> TryReadTextureBuildReportAsync(
        string buildDirectory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(buildDirectory, "texture-report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TextureBuildReport>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           NotSupportedException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ManagedSourceProvenance>>>
        LoadManagedInstallProvenanceAsync(
            ProjectPaths paths,
            CancellationToken cancellationToken)
    {
        var collected = new Dictionary<string, List<ManagedSourceProvenance>>(
            StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(paths.BackupPath))
        {
            return new Dictionary<string, IReadOnlyList<ManagedSourceProvenance>>(
                StringComparer.OrdinalIgnoreCase);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };
        foreach (var transactionDirectory in Directory.EnumerateDirectories(
                     paths.BackupPath,
                     "*",
                     options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeDirectory = PathGuard.EnsurePathUnderRoot(
                paths.BackupPath,
                transactionDirectory);
            var manifestPath = Path.Combine(safeDirectory, "install-manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            InstallManifest manifest;
            try
            {
                manifest = await manifestStore
                    .ReadInstallManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                                               IOException or
                                               UnauthorizedAccessException or
                                               JsonException or
                                               InvalidDataException or
                                               ArgumentException or
                                               NotSupportedException)
            {
                continue;
            }

            if (manifest.State is not (InstallTransactionState.Applied
                    or InstallTransactionState.Restored)
                || !PathGuard.SamePath(manifest.InstallPath, paths.InstallPath)
                || !manifest.ApplyId.Equals(
                    Path.GetFileName(safeDirectory),
                    StringComparison.OrdinalIgnoreCase)
                || manifest.Entries is null
                || manifest.Entries.Count == 0)
            {
                continue;
            }

            foreach (var artifact in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!artifact.OriginalExisted
                    || artifact.OriginalLength < 0
                    || artifact.InstalledLength < 0
                    || artifact.OriginalSha256 is not { Length: 64 }
                    || artifact.InstalledSha256 is not { Length: 64 }
                    || artifact.OriginalSha256.Any(character => !Uri.IsHexDigit(character))
                    || artifact.InstalledSha256.Any(character => !Uri.IsHexDigit(character))
                    || string.IsNullOrWhiteSpace(artifact.BackupRelativePath))
                {
                    continue;
                }

                string relativePath;
                string backupPath;
                try
                {
                    var livePath = PathGuard.ResolveUnderRoot(
                        paths.InstallPath,
                        artifact.RelativeInstallPath);
                    relativePath = Path.GetRelativePath(paths.InstallPath, livePath);
                    backupPath = PathGuard.ResolveUnderRoot(
                        safeDirectory,
                        artifact.BackupRelativePath);
                    if (!PathGuard.IsPathUnderRoot(paths.BackupPath, backupPath))
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is
                                                   InvalidDataException or
                                                   ArgumentException or
                                                   NotSupportedException)
                {
                    continue;
                }

                var key = CreateManagedInstalledKey(
                    relativePath,
                    artifact.InstalledLength,
                    artifact.InstalledSha256);
                if (!collected.TryGetValue(key, out var sources))
                {
                    sources = [];
                    collected.Add(key, sources);
                }

                var source = new ManagedSourceProvenance(
                    artifact.OriginalLength,
                    artifact.OriginalSha256,
                    backupPath);
                if (!sources.Contains(source))
                {
                    sources.Add(source);
                }
            }
        }

        return collected.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ManagedSourceProvenance>)pair.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateManagedInstalledKey(
        string relativePath,
        long length,
        string sha256) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{relativePath}\0{length}\0{sha256}");

    public bool HasRestorableBackup(ProjectPaths paths) => FindLatestInstallManifest(paths) is not null;

    private static async Task<string> ResolveRepairSourceAsync(
        ProjectPaths paths,
        string livePath,
        BuildManifestEntry baselineEntry,
        InstallManifest? activeInstall,
        string? activeInstallDirectory,
        CancellationToken cancellationToken)
    {
        var liveFingerprint = await FileIntegrity
            .FingerprintAsync(livePath, cancellationToken)
            .ConfigureAwait(false);
        if (liveFingerprint.Length == baselineEntry.SourceLength
            && liveFingerprint.Sha256.Equals(
                baselineEntry.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return livePath;
        }

        var candidates = new List<string>();
        static void AddMatchingCandidate(
            ICollection<string> destinations,
            ProjectPaths candidatePaths,
            InstallManifest manifest,
            string manifestDirectory,
            BuildManifestEntry baseline)
        {
            if (!PathGuard.SamePath(manifest.InstallPath, candidatePaths.InstallPath)
                || manifest.State is not (InstallTransactionState.Applied
                    or InstallTransactionState.Restored))
            {
                return;
            }

            var installed = manifest.Entries.FirstOrDefault(entry =>
                entry.RelativeInstallPath.Equals(
                    baseline.RelativeInstallPath,
                    StringComparison.OrdinalIgnoreCase));
            if (installed is null
                || !installed.OriginalExisted
                || string.IsNullOrWhiteSpace(installed.BackupRelativePath)
                || installed.OriginalLength != baseline.SourceLength
                || !string.Equals(
                    installed.OriginalSha256,
                    baseline.SourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var candidate = PathGuard.ResolveUnderRoot(
                manifestDirectory,
                installed.BackupRelativePath);
            if (!PathGuard.IsPathUnderRoot(candidatePaths.BackupPath, candidate))
            {
                throw new InvalidDataException("A managed repair source escaped the backup root.");
            }

            destinations.Add(candidate);
        }

        if (activeInstall is not null && activeInstallDirectory is not null)
        {
            AddMatchingCandidate(
                candidates,
                paths,
                activeInstall,
                activeInstallDirectory,
                baselineEntry);
        }

        if (Directory.Exists(paths.BackupPath))
        {
            foreach (var manifestPath in Directory.EnumerateFiles(
                         paths.BackupPath,
                         "install-manifest.json",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safeManifestPath = PathGuard.EnsurePathUnderRoot(
                    paths.BackupPath,
                    manifestPath);
                if (activeInstallDirectory is not null
                    && PathGuard.SamePath(
                        Path.GetDirectoryName(safeManifestPath)!,
                        activeInstallDirectory))
                {
                    continue;
                }

                InstallManifest historical;
                try
                {
                    historical = await new ManifestStore()
                        .ReadInstallManifestAsync(safeManifestPath, cancellationToken)
                        .ConfigureAwait(false);
                    AddMatchingCandidate(
                        candidates,
                        paths,
                        historical,
                        Path.GetDirectoryName(safeManifestPath)!,
                        baselineEntry);
                }
                catch (Exception exception) when (exception is
                                                   IOException or
                                                   UnauthorizedAccessException or
                                                   JsonException or
                                                   InvalidDataException or
                                                   ArgumentException or
                                                   NotSupportedException)
                {
                    // A malformed unrelated historical transaction cannot be
                    // trusted, but must not hide another exact managed backup.
                }
            }
        }

        var exactCandidates = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await FileIntegrity.EnsureMatchesAsync(
                        candidate,
                        baselineEntry.SourceLength,
                        baselineEntry.SourceSha256,
                        "Managed original used for repair",
                        cancellationToken)
                    .ConfigureAwait(false);
                exactCandidates.Add(candidate);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // Continue across historical transactions. Every returned
                // candidate is exact-reverified immediately before use.
            }
        }

        if (exactCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"The original source for {baselineEntry.RelativeInstallPath} is not available in the live client or any exact managed backup. "
                + "Finish any LaunchPad update and rebuild this pack against the current client.");
        }

        // Multiple byte-identical candidates are not semantically ambiguous:
        // they prove the same length/SHA source. Deterministic ordering keeps
        // repair results stable while corruption is rejected above.
        return exactCandidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>
    /// Decodes the staged (enhanced or preserved) bytes of one texture inside
    /// a completed staged pack for on-demand review display. Returns null when
    /// the member is missing or uses a container the managed decoder does not
    /// handle (block-compressed DDS).
    /// </summary>
    public async Task<DecodedTexturePreview?> LoadStagedTexturePreviewAsync(
        ProjectPaths paths,
        string previewManifestPath,
        string archiveRelativePath,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var buildDirectory = ResolveBuildDirectoryFromPreviewManifest(paths, previewManifestPath);
        if (buildDirectory is null)
        {
            return null;
        }

        var payloadRoot = Path.Combine(buildDirectory, "payload");
        string payloadPath;
        try
        {
            payloadPath = PathGuard.EnsurePathUnderRoot(
                payloadRoot,
                Path.Combine(payloadRoot, archiveRelativePath));
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return await DecodeArchiveTextureAsync(payloadPath, logicalName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes the verified original bytes of one texture belonging to a
    /// completed staged pack, resolving them from the live client when it is
    /// untouched or from exact managed install backups when the pack (or any
    /// other) is currently installed. Returns null when no exact original can
    /// be located or the container is not managed-decodable.
    /// </summary>
    public async Task<DecodedTexturePreview?> LoadOriginalTexturePreviewAsync(
        ProjectPaths paths,
        string previewManifestPath,
        string archiveRelativePath,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var buildDirectory = ResolveBuildDirectoryFromPreviewManifest(paths, previewManifestPath);
        if (buildDirectory is null)
        {
            return null;
        }

        try
        {
            var manifest = await manifestStore
                .ReadBuildManifestAsync(
                    Path.Combine(buildDirectory, "manifest.json"),
                    cancellationToken)
                .ConfigureAwait(false);
            var entry = manifest.Entries.FirstOrDefault(candidate =>
                candidate.RelativeInstallPath.Equals(
                    archiveRelativePath,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            var livePath = PathGuard.EnsurePathUnderRoot(
                paths.InstallPath,
                Path.Combine(paths.InstallPath, entry.RelativeInstallPath));
            InstallManifest? activeInstall = null;
            string? activeInstallDirectory = null;
            var activeInstallPath = FindLatestInstallManifest(paths);
            if (activeInstallPath is not null)
            {
                activeInstall = await manifestStore
                    .ReadInstallManifestAsync(activeInstallPath, cancellationToken)
                    .ConfigureAwait(false);
                activeInstallDirectory = Path.GetDirectoryName(activeInstallPath)!;
            }

            var sourcePath = await ResolveRepairSourceAsync(
                    paths,
                    livePath,
                    entry,
                    activeInstall,
                    activeInstallDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            return await DecodeArchiveTextureAsync(sourcePath, logicalName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidDataException
                                               or InvalidOperationException
                                               or NotSupportedException)
        {
            // On-demand review rendering is best-effort; the gallery shows its
            // established "unavailable" state instead of failing the window.
            return null;
        }
    }

    private static string? ResolveBuildDirectoryFromPreviewManifest(
        ProjectPaths paths,
        string previewManifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewManifestPath);
        try
        {
            var safeManifestPath = PathGuard.EnsurePathUnderRoot(
                paths.StagingPath,
                previewManifestPath);
            // The preview manifest lives at <build>/previews/preview-manifest.json.
            var previewsDirectory = Path.GetDirectoryName(safeManifestPath);
            var buildDirectory = Path.GetDirectoryName(previewsDirectory);
            return buildDirectory is not null && Directory.Exists(buildDirectory)
                ? buildDirectory
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<DecodedTexturePreview?> DecodeArchiveTextureAsync(
        string archivePath,
        string logicalName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        if (!File.Exists(archivePath))
        {
            return null;
        }

        try
        {
            await using var archive = await PfsArchive.OpenAsync(
                archivePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!archive.TryGetEntry(logicalName, out var entry)
                || entry is null
                || !entry.IsTexture)
            {
                return null;
            }

            var payload = await archive.ReadEntryAsync(logicalName, cancellationToken)
                .ConfigureAwait(false);
            return ClassicTextureDecoder.TryDecode(payload);
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or PfsArchiveException
                                               or NotSupportedException)
        {
            return null;
        }
    }

    public Task<InstallHealthReport> AuditInstallHealthAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default) =>
        installHealthService.AuditLatestAsync(paths, cancellationToken);

    public Task<InstallHealthReport> AuditInstallHealthFastAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default) =>
        installHealthService.AuditLatestFastAsync(paths, cancellationToken);

    public async Task<ApplyResult> ApplyLatestStagedPackAsync(
        ProjectPaths paths,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var buildManifestPath = FindLatestBuildManifest(paths)
            ?? throw new FileNotFoundException(
                "No completed staged pack was found. Build a staged pack before applying it.");
        var health = await installHealthService
            .AuditLatestAsync(paths, cancellationToken)
            .ConfigureAwait(false);

        if (health.State == InstallHealthState.EnhancedActive)
        {
            var activeManifestPath = health.InstallManifestPath
                ?? throw new InvalidDataException("The active install health report did not identify its manifest.");
            var activeManifest = await manifestStore
                .ReadInstallManifestAsync(activeManifestPath, cancellationToken)
                .ConfigureAwait(false);
            if (!PathGuard.SamePath(activeManifest.BuildManifestPath, buildManifestPath))
            {
                throw new InvalidOperationException(
                    "A different enhanced pack is active. Restore it before applying the latest staged pack.");
            }

            return new ApplyResult(
                activeManifest.ApplyId,
                Path.GetDirectoryName(activeManifestPath)!,
                activeManifestPath,
                activeManifest);
        }

        if (health.State == InstallHealthState.RevertedToOriginal)
        {
            await RetireLauncherRevertedInstallAsync(paths, health, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (health.State == InstallHealthState.MixedOrModified)
        {
            throw new InvalidOperationException(health.Entries.Any(entry =>
                    entry.State == InstalledArtifactHealthState.ModifiedOrMissing)
                ? "One or more live archives changed outside SpinTexture. Finish the game update in LaunchPad, "
                  + "then Analyze and rebuild against the patched client; an old staged archive will not be forced over it."
                : "The current install is partly enhanced and partly original. "
                  + "Use Restore before applying another staged pack.");
        }
        else if (health.State == InstallHealthState.RecoveryRequired)
        {
            throw new InvalidOperationException(
                $"The previous install requires recovery before another pack can be applied. {health.Summary}");
        }

        return await installTransactionService.ApplyAsync(
            paths,
            buildManifestPath,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs an explicit checked selection of completed packs. Disjoint packs
    /// are composed without AI work. A strict additive superset is promoted in
    /// place so already-active archives remain untouched; removals or replacements
    /// retain the verified full restore/apply switch path.
    /// </summary>
    public async Task<ApplyResult> ApplySelectedStagedPacksAsync(
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifestPaths);
        if (manifestPaths.Count == 0)
        {
            throw new ArgumentException("Select at least one staged pack to install.", nameof(manifestPaths));
        }

        clientClosedGuard();
        var distinctManifestPaths = manifestPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var health = await installHealthService
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        string? activeManifestPath = null;
        InstallManifest? activeManifest = null;
        if (health.State == InstallHealthState.EnhancedActive)
        {
            activeManifestPath = health.InstallManifestPath
                ?? throw new InvalidDataException("The active install has no transaction manifest.");
            activeManifest = await manifestStore
                .ReadInstallManifestAsync(activeManifestPath, cancellationToken)
                .ConfigureAwait(false);

            if (distinctManifestPaths.Length > 1
                && await SelectionMatchesActiveCompositionAsync(
                        paths,
                        activeManifest,
                        distinctManifestPaths,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                progress?.Report(new ProgressUpdate(
                    "Switch packs",
                    $"All {activeManifest.Entries.Count:N0} selected archives are already active; no staging or client files were changed.",
                    activeManifest.Entries.Count,
                    activeManifest.Entries.Count));
                return new ApplyResult(
                    activeManifest.ApplyId,
                    Path.GetDirectoryName(activeManifestPath)!,
                    activeManifestPath,
                    activeManifest);
            }
        }

        string selectedManifestPath;
        if (distinctManifestPaths.Length == 1)
        {
            var inspected = await stagedPackCatalogService.InspectAsync(
                paths,
                distinctManifestPaths[0],
                StagedPackVerificationMode.Exact,
                cancellationToken).ConfigureAwait(false);
            if (!inspected.IsReady)
            {
                throw new InvalidDataException(
                    $"The selected staged pack is not ready: {inspected.Summary}");
            }

            selectedManifestPath = inspected.ManifestPath;
        }
        else
        {
            var composition = await stagedPackComposer.ComposeAsync(
                paths,
                distinctManifestPaths,
                progress,
                cancellationToken).ConfigureAwait(false);
            selectedManifestPath = composition.ManifestPath;
        }

        if (health.State == InstallHealthState.EnhancedActive)
        {
            activeManifestPath ??= health.InstallManifestPath
                ?? throw new InvalidDataException("The active install has no transaction manifest.");
            activeManifest ??= await manifestStore
                .ReadInstallManifestAsync(activeManifestPath, cancellationToken)
                .ConfigureAwait(false);
            if (PathGuard.SamePath(activeManifest.BuildManifestPath, selectedManifestPath))
            {
                return new ApplyResult(
                    activeManifest.ApplyId,
                    Path.GetDirectoryName(activeManifestPath)!,
                    activeManifestPath,
                    activeManifest);
            }

            var promoted = await installTransactionService.TryPromoteAdditiveAsync(
                    paths,
                    activeManifestPath,
                    selectedManifestPath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (promoted is not null)
            {
                return promoted;
            }

            await EnsurePackSwitchSourcesAreCompatibleAsync(
                    paths,
                    selectedManifestPath,
                    activeManifest,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ProgressUpdate(
                "Switch packs",
                "Restoring verified originals before applying the checked pack selection.",
                0,
                2));
            await RestoreLatestAsync(paths, progress, cancellationToken).ConfigureAwait(false);
        }
        else if (health.State == InstallHealthState.RevertedToOriginal)
        {
            await RetireLauncherRevertedInstallAsync(paths, health, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (health.State == InstallHealthState.MixedOrModified)
        {
            throw new InvalidOperationException(
                health.Entries.Any(entry =>
                    entry.State == InstalledArtifactHealthState.ModifiedOrMissing)
                    ? "One or more client archives changed outside SpinTexture. Finish the LaunchPad update, then Analyze and rebuild affected packs."
                    : "The current installation is partly enhanced and partly original. Use Restore before switching pack selections.");
        }
        else if (health.State == InstallHealthState.RecoveryRequired)
        {
            throw new InvalidOperationException(
                $"The previous install requires recovery before packs can be switched. {health.Summary}");
        }

        progress?.Report(new ProgressUpdate(
            "Switch packs",
            "Applying the checked staged-pack selection without rerunning AI.",
            1,
            2));
        return await installTransactionService.ApplyAsync(
            paths,
            selectedManifestPath,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SelectionMatchesActiveCompositionAsync(
        ProjectPaths paths,
        InstallManifest activeManifest,
        IReadOnlyList<string> selectedManifestPaths,
        CancellationToken cancellationToken)
    {
        var activeBuildManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.StagingPath,
            activeManifest.BuildManifestPath);
        if (!Path.GetFileName(activeBuildManifestPath).Equals(
                "manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The active staged-pack manifest has an invalid filename.");
        }

        var activeBuildDirectory = Path.GetDirectoryName(activeBuildManifestPath)
            ?? throw new InvalidDataException(
                "The active staged-pack manifest has no build directory.");
        var activeBuildParent = Path.GetDirectoryName(activeBuildDirectory);
        if (activeBuildParent is null
            || !PathGuard.SamePath(activeBuildParent, paths.StagingPath))
        {
            throw new InvalidDataException(
                "The active staged-pack manifest is not directly under the managed Staging directory.");
        }

        var compositionPath = PathGuard.ResolveUnderRoot(
            activeBuildDirectory,
            "composition.json");
        if (!File.Exists(compositionPath))
        {
            return false;
        }

        var compositionBefore = await FileIntegrity
            .FingerprintAsync(compositionPath, cancellationToken)
            .ConfigureAwait(false);
        StagedPackCompositionDocument composition;
        await using (var stream = new FileStream(
                         compositionPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         32 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            composition = await JsonSerializer
                .DeserializeAsync<StagedPackCompositionDocument>(
                    stream,
                    CompositionJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The active composition provenance document is empty.");
        }

        var compositionAfter = await FileIntegrity
            .FingerprintAsync(compositionPath, cancellationToken)
            .ConfigureAwait(false);
        if (compositionBefore != compositionAfter)
        {
            throw new InvalidDataException(
                "The active composition provenance changed while it was being inspected.");
        }

        if (composition.SchemaVersion != StagedPackCompositionDocument.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(composition.CompositionId)
            || !composition.CompositionId.Equals(
                Path.GetFileName(activeBuildDirectory),
                StringComparison.OrdinalIgnoreCase)
            || !composition.CompositionId.Equals(
                activeManifest.BuildId,
                StringComparison.OrdinalIgnoreCase)
            || !PathGuard.SamePath(composition.InstallPath, paths.InstallPath)
            || composition.Components is null
            || composition.Components.Count == 0)
        {
            throw new InvalidDataException(
                "The active composition provenance does not match the active installation.");
        }

        var componentByPath = new Dictionary<string, StagedPackCompositionComponent>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var component in composition.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component is null
                || component.ManifestLength < 0
                || string.IsNullOrWhiteSpace(component.ManifestSha256))
            {
                throw new InvalidDataException(
                    "The active composition contains invalid component provenance.");
            }

            var componentManifestPath = PathGuard.ResolveUnderRoot(
                paths.StagingPath,
                component.ManifestRelativePath);
            var componentDirectory = Path.GetDirectoryName(componentManifestPath)
                ?? throw new InvalidDataException(
                    "An active composition component has no build directory.");
            if (!Path.GetFileName(componentManifestPath).Equals(
                    "manifest.json",
                    StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(componentDirectory) is not { } componentParent
                || !PathGuard.SamePath(componentParent, paths.StagingPath)
                || !componentByPath.TryAdd(componentManifestPath, component))
            {
                throw new InvalidDataException(
                    "The active composition contains an unsafe or duplicate component manifest path.");
            }
        }

        if (componentByPath.Count != selectedManifestPaths.Count)
        {
            return false;
        }

        var selectedEntries = new Dictionary<string, BuildManifestEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var requestedPath in selectedManifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeRequestedPath = PathGuard.EnsurePathUnderRoot(
                paths.StagingPath,
                requestedPath);
            if (!componentByPath.TryGetValue(safeRequestedPath, out var component))
            {
                return false;
            }

            var inspected = await stagedPackCatalogService.InspectAsync(
                    paths,
                    safeRequestedPath,
                    StagedPackVerificationMode.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!inspected.IsMetadataValid
                || inspected.Manifest is null
                || inspected.ManifestFingerprint is null)
            {
                return false;
            }

            if (inspected.ManifestFingerprint.Length != component.ManifestLength
                || !inspected.ManifestFingerprint.Sha256.Equals(
                    component.ManifestSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !inspected.Manifest.BuildId.Equals(
                    component.BuildId,
                    StringComparison.OrdinalIgnoreCase)
                || inspected.Manifest.Entries.Count != component.ArtifactCount)
            {
                return false;
            }

            foreach (var entry in inspected.Manifest.Entries)
            {
                var installPath = PathGuard.ResolveUnderRoot(
                    paths.InstallPath,
                    entry.RelativeInstallPath);
                var canonicalRelativePath = Path.GetRelativePath(
                    paths.InstallPath,
                    installPath);
                if (selectedEntries.TryGetValue(canonicalRelativePath, out var existing))
                {
                    if (!BuildEntriesMatch(existing, entry))
                    {
                        return false;
                    }
                }
                else
                {
                    selectedEntries.Add(canonicalRelativePath, entry);
                }
            }
        }

        if (selectedEntries.Count != activeManifest.Entries.Count)
        {
            return false;
        }

        foreach (var installed in activeManifest.Entries)
        {
            var installPath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                installed.RelativeInstallPath);
            var canonicalRelativePath = Path.GetRelativePath(
                paths.InstallPath,
                installPath);
            if (!selectedEntries.TryGetValue(canonicalRelativePath, out var selected)
                || !installed.OriginalExisted
                || installed.OriginalLength != selected.SourceLength
                || installed.OriginalSha256 is null
                || !installed.OriginalSha256.Equals(
                    selected.SourceSha256,
                    StringComparison.OrdinalIgnoreCase)
                || installed.InstalledLength != selected.StagedLength
                || !installed.InstalledSha256.Equals(
                    selected.StagedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BuildEntriesMatch(
        BuildManifestEntry left,
        BuildManifestEntry right) =>
        left.SourceLength == right.SourceLength
        && left.StagedLength == right.StagedLength
        && left.SourceSha256.Equals(right.SourceSha256, StringComparison.OrdinalIgnoreCase)
        && left.StagedSha256.Equals(right.StagedSha256, StringComparison.OrdinalIgnoreCase);

    private async Task EnsurePackSwitchSourcesAreCompatibleAsync(
        ProjectPaths paths,
        string selectedManifestPath,
        InstallManifest activeManifest,
        CancellationToken cancellationToken)
    {
        var selectedManifest = await manifestStore
            .ReadBuildManifestAsync(selectedManifestPath, cancellationToken)
            .ConfigureAwait(false);
        var activeArtifacts = activeManifest.Entries.ToDictionary(
            entry => entry.RelativeInstallPath,
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in selectedManifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activeArtifacts.TryGetValue(entry.RelativeInstallPath, out var activeArtifact))
            {
                if (!activeArtifact.OriginalExisted
                    || activeArtifact.OriginalSha256 is null
                    || activeArtifact.OriginalLength != entry.SourceLength
                    || !activeArtifact.OriginalSha256.Equals(
                        entry.SourceSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The checked pack expects different original bytes for {entry.RelativeInstallPath}. The active pack was left unchanged; restore or rebuild against the current client before switching.");
                }

                continue;
            }

            var livePath = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                entry.RelativeInstallPath);
            try
            {
                await FileIntegrity.EnsureMatchesAsync(
                        livePath,
                        entry.SourceLength,
                        entry.SourceSha256,
                        "Unchanged live artifact required by the checked pack selection",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                throw new InvalidOperationException(
                    $"The checked pack cannot be switched on because {entry.RelativeInstallPath} changed outside the active SpinTexture pack. The active pack was left unchanged; finish the client update and rebuild the affected pack.",
                    exception);
            }
        }
    }

    public async Task<RestoreResult> RestoreLatestAsync(
        ProjectPaths paths,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        clientClosedGuard();
        var manifestPath = FindLatestInstallManifest(paths)
            ?? throw new FileNotFoundException("No active SpinTexture backup was found for this installation.");
        var result = await installTransactionService.RestoreAsync(
            paths,
            manifestPath,
            progress,
            cancellationToken).ConfigureAwait(false);
        await WriteRestoreCompletionMarkerAsync(
            manifestPath,
            result.ApplyId,
            result.RestoredArtifacts,
            cancellationToken,
            result.RestoredUtc).ConfigureAwait(false);
        return result;
    }

    public string? FindLatestInstallManifest(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.BackupPath))
        {
            return null;
        }

        return Directory.EnumerateFiles(
                paths.BackupPath,
                "install-manifest.json",
                SearchOption.AllDirectories)
            .Where(path => !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "restore-complete.json")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public string? FindLatestBuildManifest(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.StagingPath))
        {
            return null;
        }

        return Directory.EnumerateFiles(
                paths.StagingPath,
                "manifest.json",
                SearchOption.AllDirectories)
            .Select(path => PathGuard.EnsurePathUnderRoot(paths.StagingPath, path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private async Task RetireLauncherRevertedInstallAsync(
        ProjectPaths paths,
        InstallHealthReport health,
        CancellationToken cancellationToken)
    {
        var installManifestPath = health.InstallManifestPath
            ?? throw new InvalidDataException("The reverted install health report did not identify its manifest.");

        // Callers use the fast audit so ordinary active-pack checks can avoid
        // hashing thousands of large archives. That audit may identify an
        // original by its unique length, which is useful as a read-only status
        // hint but is not strong enough to retire the only managed restore
        // transaction. Revalidate every claimed original by SHA-256 before
        // committing the restore marker. Otherwise an externally modified file
        // with the original length could become untracked just before the next
        // pack's exact apply preflight rejects it.
        var exactHealth = await installHealthService
            .AuditLatestAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        if (exactHealth.State != InstallHealthState.RevertedToOriginal
            || exactHealth.InstallManifestPath is null
            || !PathGuard.SamePath(
                exactHealth.InstallManifestPath,
                installManifestPath)
            || !string.Equals(
                exactHealth.ApplyId,
                health.ApplyId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SpinTexture could not SHA-256 verify that every previously installed archive was restored to its original bytes. "
                + "The existing restore transaction was kept active; finish the client update or use Restore before installing another pack.");
        }

        health = exactHealth;
        installManifestPath = exactHealth.InstallManifestPath;
        var safeManifestPath = PathGuard.EnsurePathUnderRoot(paths.BackupPath, installManifestPath);
        var manifest = await manifestStore
            .ReadInstallManifestAsync(safeManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (manifest.State != InstallTransactionState.Applied)
        {
            throw new InvalidOperationException(
                $"Only an applied pack can be retired after launcher repair; current state is {manifest.State}.");
        }

        manifest = manifest with { State = InstallTransactionState.Restored };
        await manifestStore.WriteInstallManifestAsync(
            safeManifestPath,
            manifest,
            cancellationToken).ConfigureAwait(false);
        await WriteRestoreCompletionMarkerAsync(
            safeManifestPath,
            manifest.ApplyId,
            health.Entries.Count,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteRestoreCompletionMarkerAsync(
        string installManifestPath,
        string applyId,
        int restoredArtifacts,
        CancellationToken cancellationToken,
        DateTimeOffset? restoredUtc = null)
    {
        var markerPath = Path.Combine(
            Path.GetDirectoryName(installManifestPath)!,
            "restore-complete.json");
        return File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(new
            {
                ApplyId = applyId,
                RestoredArtifacts = restoredArtifacts,
                RestoredUtc = restoredUtc ?? DateTimeOffset.UtcNow
            }),
            cancellationToken);
    }

    internal static IReadOnlyList<string> SelectArchives(string installPath, UpscaleOptions options) =>
        SelectArchives(DiscoverArchiveScopes(installPath), options);

    public static IReadOnlyList<string> ResolveSelectedArchives(
        string installPath,
        UpscaleOptions options) => SelectArchives(installPath, options);

    internal static IReadOnlyList<string> ResolveCharacterAndEquipmentArchives(
        string installPath,
        UpscaleOptions? options = null)
    {
        var scopes = DiscoverArchiveScopes(installPath);
        return options is null
            ? scopes.LegacyCharacterAndEquipmentArchives
            : SelectCharacterAndEquipmentArchives(scopes, options);
    }

    private static ArchiveScopes DiscoverArchiveScopes(string installPath)
    {
        var allArchives = Directory.EnumerateFiles(installPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var zones = ZoneCatalog.Discover(installPath);
        var zoneArchives = zones.SelectMany(zone => zone.WorldArchives)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sharedWorldArchives = allArchives
            .Where(IsSharedWorldArchive)
            // Names such as skyfire and skyshrine begin with "sky" but are
            // complete zones. Keep every discovered zone archive out of the
            // shared bucket so an unselected era can never leak back in.
            .Except(zoneArchives, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var legacySharedWorldArchives = allArchives
            .Where(IsLegacySharedWorldArchive)
            .Except(zoneArchives, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var legacyWorldArchives = zoneArchives
            .Concat(legacySharedWorldArchives)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var discoveredCharacterAndEquipmentArchives = CharacterEquipmentArchiveCatalog
            .Discover(installPath, allArchives)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var legacyCharacterAndEquipmentArchives = discoveredCharacterAndEquipmentArchives
            // Preserve the pre-expansion-filter partition for Characters-only
            // packs and interrupted legacy builds. New explicit World subsets
            // still use the stricter shared boundary below.
            .Except(legacyWorldArchives, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var explicitCharacterAndEquipmentArchives = discoveredCharacterAndEquipmentArchives
            // Explicit World subsets use the safe shared boundary. A zone-named
            // character sidecar such as skyfire_chr is therefore character
            // content in a combined build, never an unselected World dependency.
            .Except(zoneArchives, StringComparer.OrdinalIgnoreCase)
            .Except(sharedWorldArchives, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ArchiveScopes(
            allArchives,
            zones,
            legacyWorldArchives,
            sharedWorldArchives,
            legacyCharacterAndEquipmentArchives,
            explicitCharacterAndEquipmentArchives);
    }

    private static IReadOnlyList<string> SelectArchives(
        ArchiveScopes scopes,
        UpscaleOptions options)
    {
        WorldExpansionSelectionPolicy.Validate(options);
        IEnumerable<string> selected = options.Scope switch
        {
            AssetScope.SelectedZone => SelectZone(scopes.Zones, options.SelectedZone),
            AssetScope.WorldOnly => SelectWorldArchives(scopes, options.WorldExpansions),
            AssetScope.CharactersAndEquipmentOnly =>
                scopes.LegacyCharacterAndEquipmentArchives,
            AssetScope.WorldCharactersAndEquipment => SelectWorldArchives(
                    scopes,
                    options.WorldExpansions)
                .Concat(SelectCharacterAndEquipmentArchives(scopes, options)),
            AssetScope.AllSafeTextures => scopes.AllArchives,
            AssetScope.SpellEffectsOnly => [],
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };

        return selected
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> SelectCharacterAndEquipmentArchives(
        ArchiveScopes scopes,
        UpscaleOptions options) =>
        options.Scope == AssetScope.WorldCharactersAndEquipment
            && options.WorldExpansions is not null
                ? scopes.ExplicitCharacterAndEquipmentArchives
                : scopes.LegacyCharacterAndEquipmentArchives;

    private static IEnumerable<string> SelectWorldArchives(
        ArchiveScopes scopes,
        WorldExpansion? selectedExpansions)
    {
        if (selectedExpansions is null)
        {
            // Exact compatibility behavior for manifests and callers written
            // before expansion selection existed.
            return scopes.LegacyWorldArchives;
        }

        var selected = selectedExpansions.Value;
        var detected = scopes.Zones.Aggregate(
            WorldExpansion.None,
            (current, zone) => current | zone.Expansion);
        var missing = selected & ~detected;
        if (missing != WorldExpansion.None)
        {
            var missingNames = WorldExpansionCatalog.OrderedGroups
                .Where(expansion => (missing & expansion) == expansion)
                .Select(WorldExpansionCatalog.GetDisplayName);
            throw new InvalidOperationException(
                $"The selected World expansion group(s) were not found in this EverQuest installation: {string.Join(", ", missingNames)}. Analyze the matching client or change the World selection.");
        }

        var selectedZoneArchives = scopes.Zones
            .Where(zone => (selected & zone.Expansion) == zone.Expansion)
            .SelectMany(zone => zone.WorldArchives)
            .ToArray();
        if (selectedZoneArchives.Length == 0)
        {
            throw new InvalidOperationException(
                "None of the selected World expansions have zone archives in this EverQuest installation. Analyze the client again and select a detected expansion.");
        }

        return selectedZoneArchives.Concat(scopes.SharedWorldArchives);
    }

    private static IEnumerable<string> SelectZone(
        IReadOnlyList<ZoneAssetSet> zones,
        string? selectedZone)
    {
        if (string.IsNullOrWhiteSpace(selectedZone))
        {
            throw new InvalidOperationException("Choose a zone before starting a selected-zone build.");
        }

        var zone = zones.FirstOrDefault(
            candidate => candidate.Name.Equals(selectedZone, StringComparison.OrdinalIgnoreCase));
        return zone?.WorldArchives
            ?? throw new InvalidOperationException($"Zone '{selectedZone}' was not found in this installation.");
    }

    private static bool IsSharedWorldArchive(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return name.StartsWith("furniture", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("housing", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("interior", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("objects", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("plants", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("trees", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("terrain", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("grass", StringComparison.OrdinalIgnoreCase)
            // sky.s3d is the audited shared legacy sky archive. Prefix matching
            // is unsafe here: complete zones such as Skyfire and Skyshrine,
            // including future suffix variants, must remain era-filtered.
            || (name.Equals("sky", StringComparison.OrdinalIgnoreCase)
                && extension.Equals(".s3d", StringComparison.OrdinalIgnoreCase))
            || name.StartsWith("props", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacySharedWorldArchive(string path)
    {
        if (IsSharedWorldArchive(path))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return name.StartsWith("sky", StringComparison.OrdinalIgnoreCase)
            && !(extension.Equals(".eqg", StringComparison.OrdinalIgnoreCase)
                && name.Length == 3);
    }

    private sealed record ArchiveScopes(
        IReadOnlyList<string> AllArchives,
        IReadOnlyList<ZoneAssetSet> Zones,
        IReadOnlyList<string> LegacyWorldArchives,
        IReadOnlyList<string> SharedWorldArchives,
        IReadOnlyList<string> LegacyCharacterAndEquipmentArchives,
        IReadOnlyList<string> ExplicitCharacterAndEquipmentArchives);

    internal sealed record ManagedOriginalSource(
        string BackupPath,
        long Length,
        string Sha256);

    internal sealed record ResolvedBuildSource(
        string Path,
        long? ExpectedLength,
        string? ExpectedSha256);

    private sealed record ManagedSourceProvenance(
        long OriginalLength,
        string OriginalSha256,
        string BackupPath);

    private sealed record ReusableStagedArtifact(
        string PayloadPath,
        long Length,
        string Sha256);

    private sealed class WholeStagedArtifactReuseBuilder : IStagedArtifactBuilder
    {
        private readonly IReadOnlyDictionary<string, ReusableStagedArtifact> artifacts;
        private readonly IStagedPackPayloadMaterializer materializer;

        public WholeStagedArtifactReuseBuilder(
            IReadOnlyDictionary<string, ReusableStagedArtifact> artifacts,
            IStagedPackPayloadMaterializer? materializer = null)
        {
            this.artifacts = artifacts
                ?? throw new ArgumentNullException(nameof(artifacts));
            this.materializer = materializer
                ?? new HardLinkOrCopyStagedPackPayloadMaterializer();
        }

        public async Task BuildAsync(
            StagedArtifactBuildContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!artifacts.TryGetValue(context.RelativeInstallPath, out var artifact))
            {
                throw new InvalidDataException(
                    $"The source-repair baseline has no reusable payload for {context.RelativeInstallPath}.");
            }

            if (materializer is IVerifiedStagedPackPayloadMaterializer verified)
            {
                await verified.MaterializeVerifiedAsync(
                        artifact.PayloadPath,
                        context.DestinationPath,
                        artifact.Length,
                        artifact.Sha256,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await materializer.MaterializeAsync(
                        artifact.PayloadPath,
                        context.DestinationPath,
                        artifact.Length,
                        artifact.Sha256,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal sealed class BuildOriginalSourceResolver
    {
        private readonly IReadOnlyDictionary<string, ManagedOriginalSource> references;

        internal BuildOriginalSourceResolver(
            IReadOnlyDictionary<string, ManagedOriginalSource> references)
        {
            this.references = references
                ?? throw new ArgumentNullException(nameof(references));
        }

        public static BuildOriginalSourceResolver Empty { get; } = new(
            new Dictionary<string, ManagedOriginalSource>(StringComparer.OrdinalIgnoreCase));

        public async Task<ResolvedBuildSource> ResolveAsync(
            string relativePath,
            string livePath,
            CancellationToken cancellationToken)
        {
            var fullLivePath = Path.GetFullPath(livePath);
            if (!references.TryGetValue(relativePath, out var original))
            {
                return new ResolvedBuildSource(
                    fullLivePath,
                    ExpectedLength: null,
                    ExpectedSha256: null);
            }

            try
            {
                await FileIntegrity.EnsureMatchesAsync(
                        original.BackupPath,
                        original.Length,
                        original.Sha256,
                        "Managed original selected as a staged-build source",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                throw new InvalidDataException(
                    $"SpinTexture cannot build {relativePath} because its managed original backup is missing or corrupt. Restore/recover the active pack before building.",
                    exception);
            }

            return new ResolvedBuildSource(
                original.BackupPath,
                original.Length,
                original.Sha256);
        }
    }

    private static async Task<List<StagedBuildItem>> PlanArchiveItemsAsync(
        ProjectPaths paths,
        UpscaleOptions options,
        IReadOnlyList<string> archivePaths,
        IStagedArtifactBuilder builder,
        TextureBuildCounter counter,
        BuildOriginalSourceResolver originalSources,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var items = new List<StagedBuildItem>();
        for (var index = 0; index < archivePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = archivePaths[index];
            progress?.Report(new ProgressUpdate(
                "Plan",
                "Finding color textures that can be enhanced safely.",
                index,
                archivePaths.Count,
                Path.GetFileName(archivePath)));
            var relativePath = Path.GetRelativePath(paths.InstallPath, archivePath);
            var resolvedSource = await originalSources
                .ResolveAsync(relativePath, archivePath, cancellationToken)
                .ConfigureAwait(false);
            var sourcePath = resolvedSource.Path;
            try
            {
                await using var archive = await PfsArchive.OpenAsync(
                    sourcePath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (archive.Entries.Any(entry =>
                        PfsTextureArchiveBuilder.IsPotentialCandidate(entry, options.MaximumDimension)
                        && (options.Scope is not (
                                AssetScope.CharactersAndEquipmentOnly
                                or AssetScope.WorldCharactersAndEquipment)
                            || CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                                archivePath,
                                entry.Name))))
                {
                    items.Add(new StagedBuildItem(
                        relativePath,
                        builder,
                        PathGuard.SamePath(sourcePath, archivePath) ? null : sourcePath,
                        resolvedSource.ExpectedLength,
                        resolvedSource.ExpectedSha256));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                counter.Warn($"Skipped archive {Path.GetFileName(archivePath)}: {exception.Message}");
            }
        }

        return items;
    }

    private static async Task AddLooseTextureItemsAsync(
        ProjectPaths paths,
        UpscaleOptions options,
        ICollection<StagedBuildItem> items,
        NativeTextureProcessor processor,
        TextureBuildCounter counter,
        TexturePreviewCollector previewCollector,
        BuildOriginalSourceResolver originalSources,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var loosePaths = EverQuestTextureScanner.EnumerateLooseTextures(paths.InstallPath)
            .Where(path => options.Scope != AssetScope.SpellEffectsOnly
                || SpellEffectTexturePolicy.IsEffectPath(
                    Path.GetRelativePath(paths.InstallPath, path)))
            .Where(path => CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                Path.GetRelativePath(paths.InstallPath, path)) is null)
            .ToArray();
        var builder = new LooseTextureArtifactBuilder(
            processor,
            counter,
            previewCollector,
            allowSpellEffects: options.Scope == AssetScope.SpellEffectsOnly);
        for (var index = 0; index < loosePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = loosePaths[index];
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index % 100 == 0)
            {
                progress?.Report(new ProgressUpdate(
                    "Plan",
                    "Checking loose textures.",
                    index,
                    loosePaths.Length,
                    Path.GetFileName(path)));
            }

            var relativePath = Path.GetRelativePath(paths.InstallPath, path);
            var resolvedSource = await originalSources
                .ResolveAsync(relativePath, path, cancellationToken)
                .ConfigureAwait(false);
            var sourcePath = resolvedSource.Path;
            try
            {
                var isCandidate = options.Scope == AssetScope.SpellEffectsOnly
                    ? await LooseTextureArtifactBuilder.IsSpellEffectCandidateAsync(
                        sourcePath,
                        relativePath,
                        options.MaximumDimension,
                        cancellationToken).ConfigureAwait(false)
                    : await LooseTextureArtifactBuilder.IsCandidateAsync(
                        sourcePath,
                        relativePath,
                        options.MaximumDimension,
                        cancellationToken).ConfigureAwait(false);
                if (isCandidate)
                {
                    items.Add(new StagedBuildItem(
                        relativePath,
                        builder,
                        PathGuard.SamePath(sourcePath, path) ? null : sourcePath,
                        resolvedSource.ExpectedLength,
                        resolvedSource.ExpectedSha256));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                counter.Warn($"Skipped loose texture {Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    private static void EnsureClientClosed()
    {
        if (EverQuestInstall.IsGameOrLauncherRunning())
        {
            throw new InvalidOperationException(
                "Close EverQuest and LaunchPad before installing or restoring textures.");
        }
    }

    private static async Task WriteReportAsync(
        string path,
        TextureBuildReport report,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(path, report, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePreviewManifestAsync(
        string path,
        TexturePreviewManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteJsonAsync(path, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = AtomicFile.CreateTemporarySiblingPath(fullPath);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            AtomicFile.CommitTemporaryFile(temporaryPath, fullPath);
        }
        catch
        {
            AtomicFile.TryDelete(temporaryPath);
            throw;
        }
    }

    private static JsonSerializerOptions CreateCompositionJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
