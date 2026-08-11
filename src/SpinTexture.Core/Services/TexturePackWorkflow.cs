using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Tooling;

namespace SpinTexture.Core.Services;

public sealed record TexturePackBuildResult(
    StagedBuildResult StagedBuild,
    TextureBuildReport Report,
    string ReportPath,
    ApplyResult? ApplyResult,
    string? PreviewManifestPath);

public sealed class TexturePackWorkflow
{
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
        if (selectedArchives.Count == 0)
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
            clampArchivePaths: archiveScopes.CharacterAndEquipmentArchives,
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

        if (options.Scope == AssetScope.AllSafeTextures)
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

        var staged = await stagedBuildService.BuildAsync(
            new StagedBuildRequest(paths, options, items),
            progress,
            cancellationToken).ConfigureAwait(false);
        var statistics = counter.Snapshot();
        if (statistics.EnhancedTextures == 0)
        {
            throw new InvalidOperationException(
                "The build completed without an eligible texture. No client files were installed.");
        }

        var buildCompletedUtc = DateTimeOffset.UtcNow;
        var report = new TextureBuildReport(
            TextureBuildReport.CurrentSchemaVersion,
            staged.BuildId,
            buildCompletedUtc,
            paths.InstallPath,
            staged.BuildDirectory,
            selectedArchives.Count,
            statistics)
        {
            StartedUtc = buildStartedUtc,
            DurationSeconds = (buildCompletedUtc - buildStartedUtc).TotalSeconds,
            TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision
        };
        var reportPath = Path.Combine(staged.BuildDirectory, "texture-report.json");
        await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

        string? previewManifestPath = null;
        var previewEntries = previewCollector.Snapshot();
        if (previewEntries.Count > 0)
        {
            var previewManifest = new TexturePreviewManifest(
                TexturePreviewManifest.CurrentSchemaVersion,
                staged.BuildId,
                DateTimeOffset.UtcNow,
                previewEntries);
            previewManifestPath = Path.Combine(
                staged.BuildDirectory,
                "previews",
                "preview-manifest.json");
            await WritePreviewManifestAsync(
                previewManifestPath,
                previewManifest,
                cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineManifestPath);
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
        var isPipelineRepairScope = baseline.Options.Scope is
            AssetScope.CharactersAndEquipmentOnly
            or AssetScope.WorldOnly
            or AssetScope.WorldCharactersAndEquipment
            or AssetScope.SelectedZone;
        var requiresPipelineRepair = TextureProcessingPipeline.RequiresRepair(
            baselineReport,
            baseline.Options.Scope);
        var isCutoutMipRepair = TextureProcessingPipeline.RequiresCutoutMipUpgrade(
            baselineReport,
            baseline.Options.Scope);
        if (isPipelineRepairScope && !requiresPipelineRepair)
        {
            throw new InvalidOperationException(
                "This staged pack already uses the current texture pipeline. The completed pack was left unchanged.");
        }

        if (baseline.Options.Scope == AssetScope.AllSafeTextures
            || baseline.Entries.Any(entry =>
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

        var repairOptions = baseline.Options with
        {
            // A revision upgrade must preserve the original pack's visual
            // profile. The explicit retry preset remains available to the
            // legacy character/equipment missing-texture repair.
            Preset = isCutoutMipRepair ? baseline.Options.Preset : retryPreset,
            InstallAfterBuild = false
        };
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

        var repairArchives = isCutoutMipRepair
            ? selectedArchives
                .Where(path => baselineEntries.ContainsKey(
                    Path.GetRelativePath(paths.InstallPath, path)))
                .ToArray()
            : selectedArchives;

        var counter = new TextureBuildCounter();
        var processor = new NativeTextureProcessor(tools);
        var previewCollector = new TexturePreviewCollector(maximumEntries: 24);
        var archiveBuilder = new PfsTextureArchiveBuilder(
            processor,
            counter,
            progress,
            previewCollector,
            clampArchivePaths: archiveScopes.CharacterAndEquipmentArchives,
            filterCharacterEquipmentEntries:
                repairOptions.Scope is AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment,
            reuseArchivePaths: reuseArchivePaths,
            reuseArchiveFingerprints: reuseArchiveFingerprints,
            rebuildFromReuseArchive: isCutoutMipRepair,
            retryUnchangedEntries: !isCutoutMipRepair);

        var items = new List<StagedBuildItem>();
        for (var index = 0; index < repairArchives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveArchivePath = repairArchives[index];
            var relativePath = Path.GetRelativePath(paths.InstallPath, liveArchivePath);
            progress?.Report(new ProgressUpdate(
                "Repair plan",
                isCutoutMipRepair
                    ? "Verifying prior output and locating only enhanced cutouts built with the retired mip chain."
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
                        baselineEntry?.SourceSha256));
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


        if (isCutoutMipRepair && items.Count != baselineEntries.Count)
        {
            throw new InvalidOperationException(
                "The World/zone revision repair could not account for every archive in the verified baseline. The original staged pack remains intact; no incomplete replacement was created.");
        }

        var staged = await stagedBuildService.BuildAsync(
            new StagedBuildRequest(
                paths,
                repairOptions,
                items,
                RequireAllItems: isCutoutMipRepair),
            progress,
            cancellationToken).ConfigureAwait(false);
        var preliminaryStatistics = counter.Snapshot();
        counter.Warn(isCutoutMipRepair
            ? $"Cutout mip repair reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and reprocessed only changed alpha-tested outputs that failed the current mip policy; source-identical entries were not retried."
            : $"Repair reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and retried only unchanged eligible entries.");
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
            IsCutoutMipRepair = isCutoutMipRepair,
            BaselineBuildId = baseline.BuildId,
            BaselineTexturePipelineRevision = baselineReport?.TexturePipelineRevision ?? 0,
            TexturePipelineRevision = TextureProcessingPipeline.CurrentRevision
        };
        var reportPath = Path.Combine(staged.BuildDirectory, "texture-report.json");
        await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

        string? previewManifestPath = null;
        var previewEntries = previewCollector.Snapshot();
        if (previewEntries.Count > 0)
        {
            var previewManifest = new TexturePreviewManifest(
                TexturePreviewManifest.CurrentSchemaVersion,
                staged.BuildId,
                DateTimeOffset.UtcNow,
                previewEntries);
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
            progress,
            cancellationToken);

    private async Task<TexturePackBuildResult> RepairStagedPackSourceMismatchCoreAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        IStagedArtifactBuilder? rebuildBuilderOverride,
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
        if (rebuildBuilderOverride is null)
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
            clampArchivePaths: archiveScopes.CharacterAndEquipmentArchives,
            filterCharacterEquipmentEntries:
                baseline.Options.Scope is AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment);
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
                items.Add(new StagedBuildItem(
                    relativePath,
                    reuseBuilder,
                    PathGuard.SamePath(sourcePath, livePath) ? null : sourcePath,
                    currentSource.Length,
                    currentSource.Sha256,
                    entry.StagedLength,
                    entry.StagedSha256));
                reusedCount++;
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
                currentSource.Sha256));
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
        counter.Warn(
            $"Source repair reused {reusedCount:N0} complete staged archive(s) and rebuilt {rebuiltCount:N0} contaminated archive(s) from verified originals.");
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
            IsSourceMismatchRepair = true,
            BaselineBuildId = baseline.BuildId,
            BaselineTexturePipelineRevision = baselineReport?.TexturePipelineRevision ?? 0,
            ReusedArtifacts = reusedCount,
            RebuiltArtifacts = rebuiltCount,
            TexturePipelineRevision = baselineReport?.TexturePipelineRevision ?? 0
        };
        var reportPath = Path.Combine(staged.BuildDirectory, "texture-report.json");
        await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

        string? previewManifestPath = null;
        var previewEntries = previewCollector.Snapshot();
        if (previewEntries.Count > 0)
        {
            var previewManifest = new TexturePreviewManifest(
                TexturePreviewManifest.CurrentSchemaVersion,
                staged.BuildId,
                DateTimeOffset.UtcNow,
                previewEntries);
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

        var installedArtifact = activeInstall?.Entries.FirstOrDefault(entry =>
            entry.RelativeInstallPath.Equals(
                baselineEntry.RelativeInstallPath,
                StringComparison.OrdinalIgnoreCase));
        if (installedArtifact is null
            || !installedArtifact.OriginalExisted
            || installedArtifact.BackupRelativePath is null
            || installedArtifact.OriginalLength != baselineEntry.SourceLength
            || !string.Equals(
                installedArtifact.OriginalSha256,
                baselineEntry.SourceSha256,
                StringComparison.OrdinalIgnoreCase)
            || activeInstallDirectory is null)
        {
            throw new InvalidOperationException(
                $"The original source for {baselineEntry.RelativeInstallPath} is not available. "
                + "Finish any LaunchPad update and rebuild this pack against the current client.");
        }

        var backupPath = PathGuard.ResolveUnderRoot(
            activeInstallDirectory,
            installedArtifact.BackupRelativePath);
        await FileIntegrity.EnsureMatchesAsync(
            backupPath,
            baselineEntry.SourceLength,
            baselineEntry.SourceSha256,
            "Managed original used for repair",
            cancellationToken).ConfigureAwait(false);
        if (!PathGuard.IsPathUnderRoot(paths.BackupPath, backupPath))
        {
            throw new InvalidDataException("The managed repair source escaped the backup root.");
        }

        return backupPath;
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

    private static ArchiveScopes DiscoverArchiveScopes(string installPath)
    {
        var allArchives = Directory.EnumerateFiles(installPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var zones = ZoneCatalog.Discover(installPath);
        var worldArchives = zones.SelectMany(zone => zone.WorldArchives)
            .Concat(allArchives.Where(IsSharedWorldArchive))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var characterAndEquipmentArchives = CharacterEquipmentArchiveCatalog
            .Discover(installPath, allArchives)
            .Except(worldArchives, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ArchiveScopes(
            allArchives,
            zones,
            worldArchives,
            characterAndEquipmentArchives);
    }

    private static IReadOnlyList<string> SelectArchives(
        ArchiveScopes scopes,
        UpscaleOptions options)
    {
        IEnumerable<string> selected = options.Scope switch
        {
            AssetScope.SelectedZone => SelectZone(scopes.Zones, options.SelectedZone),
            AssetScope.WorldOnly => scopes.WorldArchives,
            AssetScope.CharactersAndEquipmentOnly => scopes.CharacterAndEquipmentArchives,
            AssetScope.WorldCharactersAndEquipment => scopes.WorldArchives
                .Concat(scopes.CharacterAndEquipmentArchives),
            AssetScope.AllSafeTextures => scopes.AllArchives,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };

        return selected
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            || (name.StartsWith("sky", StringComparison.OrdinalIgnoreCase)
                && !(extension.Equals(".eqg", StringComparison.OrdinalIgnoreCase)
                    && name.Length == 3))
            || name.StartsWith("props", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ArchiveScopes(
        IReadOnlyList<string> AllArchives,
        IReadOnlyList<ZoneAssetSet> Zones,
        IReadOnlyList<string> WorldArchives,
        IReadOnlyList<string> CharacterAndEquipmentArchives);

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
        var loosePaths = EverQuestTextureScanner.EnumerateLooseTextures(paths.InstallPath).ToArray();
        var builder = new LooseTextureArtifactBuilder(processor, counter, previewCollector);
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
                if (await LooseTextureArtifactBuilder.IsCandidateAsync(
                    sourcePath,
                    options.MaximumDimension,
                    cancellationToken).ConfigureAwait(false))
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
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
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
