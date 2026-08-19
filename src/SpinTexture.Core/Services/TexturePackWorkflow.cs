using System.Globalization;
using System.Security.Cryptography;
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

public sealed record ArtisticWorkerRoute(
    string WorkerPath,
    string? Fingerprint,
    string? Preset,
    string? IdentityError)
{
    public bool HasVerifiedIdentity => Fingerprint is { Length: 64 }
        && string.IsNullOrWhiteSpace(IdentityError);
}

internal sealed record SourceRepairPaintedRendererProvenance(
    bool? UsedExternalArtisticWorker,
    string? ArtisticWorkerFingerprint,
    string? ArtisticWorkerPreset,
    PaintedRendererOutcome RendererOutcome);

internal sealed record SourceRepairVisualCompatibility(
    TextureBuildReport? BaselineReport,
    SourceRepairPaintedRendererProvenance RendererProvenance,
    ArtisticWorkerIdentity? CurrentArtisticIdentity,
    bool IsPaintedBaseline,
    bool HasReproduciblePaintedProfile);

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

    internal static PaintedRendererOutcome ResolvePaintedRendererOutcome(
        TexturePreset preset,
        TextureBuildStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (preset != TexturePreset.Illustrated)
        {
            return PaintedRendererOutcome.Unknown;
        }

        return (statistics.ExternalArtisticTextures > 0,
            statistics.BuiltInPaintedTextures > 0) switch
        {
            (true, true) => PaintedRendererOutcome.Mixed,
            (true, false) => PaintedRendererOutcome.ExternalOnly,
            (false, true) => PaintedRendererOutcome.BuiltInOnly,
            _ => PaintedRendererOutcome.Unknown
        };
    }

    internal static SourceRepairPaintedRendererProvenance
        ResolveSourceRepairPaintedRendererProvenance(
            UpscaleOptions baselineOptions,
            TextureBuildReport? baselineReport,
            bool enforceCurrentRoute,
            bool currentWorkerAvailable,
            string? currentWorkerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(baselineOptions);
        var legacyWorker = baselineReport?.UsedExternalArtisticWorker;
        // Schema 4 is the first report contract whose renderer outcome means
        // the routes that actually produced completed pixels. Older nullable
        // booleans described worker availability and cannot prove provenance.
        var outcome = baselineReport is
            { SchemaVersion: >= 4, PaintedRendererOutcome: var recordedOutcome }
            ? recordedOutcome
            : PaintedRendererOutcome.Unknown;

        var usedExternal = outcome switch
        {
            PaintedRendererOutcome.ExternalOnly => true,
            PaintedRendererOutcome.BuiltInOnly => false,
            PaintedRendererOutcome.Mixed => true,
            _ => legacyWorker
        };
        var fingerprint = outcome == PaintedRendererOutcome.ExternalOnly
            ? baselineReport?.ArtisticWorkerFingerprint
                ?? baselineOptions.ArtisticWorkerFingerprint
            : null;
        var preset = outcome == PaintedRendererOutcome.ExternalOnly
            ? baselineReport?.ArtisticWorkerPreset
                ?? baselineOptions.ArtisticWorkerPreset
            : null;

        if (baselineOptions.Preset != TexturePreset.Illustrated
            || !enforceCurrentRoute)
        {
            return new SourceRepairPaintedRendererProvenance(
                usedExternal,
                fingerprint,
                preset,
                outcome);
        }

        if (outcome == PaintedRendererOutcome.Mixed)
        {
            throw new InvalidOperationException(
                "This Graphic Painted Fantasy pack contains both external-diffusion and built-in painted outputs. Source repair must rebuild contaminated archives, but per-texture renderer provenance is unavailable; rebuild the complete pack instead.");
        }

        if (outcome == PaintedRendererOutcome.Unknown)
        {
            throw new InvalidOperationException(
                "This Graphic Painted Fantasy pack predates reliable painted-renderer provenance. Source repair cannot prove which renderer created reused members; rebuild the complete pack instead of mixing an unknown legacy look.");
        }

        if (outcome == PaintedRendererOutcome.BuiltInOnly)
        {
            if (currentWorkerAvailable)
            {
                throw new InvalidOperationException(
                    "This Graphic Painted Fantasy pack was created with the built-in painterly renderer, but a diffusion repaint worker is currently enabled. Disable it or rebuild the complete pack before source repair.");
            }

            return new SourceRepairPaintedRendererProvenance(
                usedExternal,
                fingerprint,
                preset,
                outcome);
        }

        if (!currentWorkerAvailable)
        {
            throw new InvalidOperationException(
                "This Graphic Painted Fantasy pack was created with the diffusion repaint worker, but that worker is not currently available. Restore the exact worker or rebuild the complete pack before source repair.");
        }

        if (fingerprint is not { Length: 64 }
            || fingerprint.Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(preset))
        {
            throw new InvalidOperationException(
                "This diffusion-painted pack has no complete exact renderer identity. Source repair cannot safely reproduce contaminated archives; rebuild the complete pack instead.");
        }

        if (string.IsNullOrWhiteSpace(currentWorkerFingerprint)
            || !fingerprint.Equals(currentWorkerFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This Graphic Painted Fantasy pack was created with a different diffusion worker, model, or configuration. Restore that exact renderer or rebuild the complete pack before source repair.");
        }

        return new SourceRepairPaintedRendererProvenance(
            usedExternal,
            fingerprint,
            preset,
            outcome);
    }

    private async Task<SourceRepairVisualCompatibility>
        ResolveSourceRepairVisualCompatibilityAsync(
            ProjectPaths paths,
            StagedPackInfo baselineInfo,
            ExternalToolPaths? tools,
            CancellationToken cancellationToken)
    {
        var baseline = baselineInfo.Manifest
            ?? throw new InvalidDataException(
                "The source-repair baseline has no verified manifest.");
        var baselineReport = await TryReadTextureBuildReportAsync(
                baselineInfo.BuildDirectory,
                baseline.BuildId,
                paths.InstallPath,
                cancellationToken)
            .ConfigureAwait(false);
        var currentArtisticIdentity = baseline.Options.Preset
                == TexturePreset.Illustrated
            && tools is not null
            ? await ArtisticWorkerIdentityProvider.ResolveAsync(
                    tools,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var rendererProvenance = ResolveSourceRepairPaintedRendererProvenance(
            baseline.Options,
            baselineReport,
            enforceCurrentRoute: tools is not null,
            currentWorkerAvailable: tools?.HasArtisticWorker == true,
            currentArtisticIdentity?.Fingerprint);
        var isPaintedBaseline = baseline.Options.Preset is
            TexturePreset.Illustrated or TexturePreset.RusticPainted;
        var hasReproduciblePaintedProfile = !isPaintedBaseline
            || (baselineReport?.PaintedProfileRevision ?? 0)
                == GetFreshPaintedProfileRevision(baseline.Options.Preset);
        if (tools is not null
            && isPaintedBaseline
            && !hasReproduciblePaintedProfile)
        {
            throw new InvalidOperationException(
                "This painted pack uses an older or unknown art-profile revision. Focused source repair cannot reproduce changed archives without mixing painted algorithms; build the complete pack fresh instead.");
        }

        return new SourceRepairVisualCompatibility(
            baselineReport,
            rendererProvenance,
            currentArtisticIdentity,
            isPaintedBaseline,
            hasReproduciblePaintedProfile);
    }

    private async Task EnsureArtisticWorkerIdentityUnchangedAsync(
        ProjectPaths paths,
        ArtisticWorkerIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        var currentTools = toolchainDiscovery.Discover(paths);
        if (!currentTools.IsReady)
        {
            throw new InvalidOperationException(
                "Graphic Painted build tools changed or became incomplete while the build was running. The staged manifest was not published.");
        }

        var currentIdentity = await ArtisticWorkerIdentityProvider.ResolveAsync(
                currentTools,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                expectedIdentity?.Fingerprint,
                currentIdentity?.Fingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                expectedIdentity?.Preset,
                currentIdentity?.Preset,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Graphic Painted diffusion worker, model, or style configuration changed while the build was running. SpinTexture kept the verified checkpoint but did not publish a mixed-identity pack; resume with the original setup or start a fresh build.");
        }
    }

    private static readonly JsonSerializerOptions CompositionJsonOptions =
        CreateCompositionJsonOptions();

    private readonly EverQuestTextureScanner scanner;
    private readonly ToolchainDiscovery toolchainDiscovery;
    private readonly StagedBuildService stagedBuildService;
    private readonly InstallTransactionService installTransactionService;
    private readonly ManifestStore manifestStore;
    private readonly InstallHealthService installHealthService;
    private readonly LaunchPadUpdateEvidenceService launchPadUpdateEvidenceService;
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
        StagedPackComposer? stagedPackComposer = null,
        LaunchPadUpdateEvidenceService? launchPadUpdateEvidenceService = null)
    {
        this.scanner = scanner ?? new EverQuestTextureScanner();
        this.toolchainDiscovery = toolchainDiscovery ?? new ToolchainDiscovery();
        this.stagedBuildService = stagedBuildService ?? new StagedBuildService();
        this.installTransactionService = installTransactionService ?? new InstallTransactionService();
        this.manifestStore = manifestStore ?? new ManifestStore();
        this.installHealthService = installHealthService ?? new InstallHealthService(this.manifestStore);
        this.launchPadUpdateEvidenceService = launchPadUpdateEvidenceService
            ?? new LaunchPadUpdateEvidenceService();
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
        StagedPackComposer? stagedPackComposer = null,
        LaunchPadUpdateEvidenceService? launchPadUpdateEvidenceService = null)
        : this(
            scanner,
            toolchainDiscovery,
            stagedBuildService,
            installTransactionService,
            manifestStore,
            installHealthService,
            stagedPackCatalogService,
            stagedPackComposer,
            launchPadUpdateEvidenceService)
    {
        this.clientClosedGuard = clientClosedGuard
            ?? throw new ArgumentNullException(nameof(clientClosedGuard));
    }

    public Task<ScanSummary> AnalyzeAsync(
        string installPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default) =>
        scanner.AnalyzeAsync(installPath, progress, cancellationToken);

    /// <summary>
    /// Resolves the same env/workspace/application artistic worker route used
    /// by builds, then computes its semantic renderer identity off the UI
    /// thread. A discovered but invalid worker is still returned with an
    /// actionable identity error so status surfaces do not silently call it
    /// unavailable or match it to historical timing data.
    /// </summary>
    public async Task<ArtisticWorkerRoute?> ResolveArtisticWorkerRouteAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var tools = toolchainDiscovery.Discover(paths);
        if (!tools.HasArtisticWorker)
        {
            return null;
        }

        try
        {
            await using var artisticWorkerLease =
                await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                        paths,
                        tools,
                        mayUseArtisticWorker: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            var identity = await ArtisticWorkerIdentityProvider.ResolveAsync(
                    tools,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The discovered artistic worker did not resolve an identity.");
            return new ArtisticWorkerRoute(
                tools.ArtisticWorkerPath!,
                identity.Fingerprint,
                identity.Preset,
                IdentityError: null);
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException
                                               or ArgumentException
                                               or InvalidOperationException
                                               or NotSupportedException
                                               or CryptographicException)
        {
            return new ArtisticWorkerRoute(
                tools.ArtisticWorkerPath!,
                Fingerprint: null,
                Preset: null,
                IdentityError: exception.Message);
        }
    }

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
        await using var artisticWorkerLease =
            await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                    paths,
                    tools,
                    mayUseArtisticWorker: options.Preset == TexturePreset.Illustrated,
                    cancellationToken)
                .ConfigureAwait(false);
        var artisticIdentity = options.Preset == TexturePreset.Illustrated
            ? await ArtisticWorkerIdentityProvider.ResolveAsync(tools, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var recordedOptions = options with
        {
            ArtisticWorkerFingerprint = artisticIdentity?.Fingerprint,
            ArtisticWorkerPreset = artisticIdentity?.Preset
        };

        var archiveScopes = DiscoverArchiveScopes(paths.InstallPath);
        var selectedArchives = SelectArchives(archiveScopes, recordedOptions);
        if (selectedArchives.Count == 0 && recordedOptions.Scope != AssetScope.SpellEffectsOnly)
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
                recordedOptions),
            filterCharacterEquipmentEntries:
                recordedOptions.Scope is AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment);
        var items = await PlanArchiveItemsAsync(
            paths,
            recordedOptions,
            selectedArchives,
            archiveBuilder,
            counter,
            originalSources,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (recordedOptions.Scope is AssetScope.AllSafeTextures or AssetScope.SpellEffectsOnly)
        {
            await AddLooseTextureItemsAsync(
                paths,
                recordedOptions,
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
                "No safe textures eligible for the selected resolution cap were found in this scope.");
        }

        TextureBuildReport? report = null;
        string? reportPath = null;
        string? previewManifestPath = null;
        var staged = await stagedBuildService.BuildAsync(
            new StagedBuildRequest(
                paths,
                recordedOptions,
                items,
                ResumeOperationKey: GetFreshBuildResumeOperationKey(recordedOptions.Preset),
                BeforeManifestCommitAsync: recordedOptions.Preset == TexturePreset.Illustrated
                    ? token => EnsureArtisticWorkerIdentityUnchangedAsync(
                        paths,
                        artisticIdentity,
                        token)
                    : null),
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
                var rendererOutcome = ResolvePaintedRendererOutcome(
                    recordedOptions.Preset,
                    statistics);
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
                    PaintedProfileRevision = GetFreshPaintedProfileRevision(recordedOptions.Preset),
                    UsedExternalArtisticWorker = rendererOutcome switch
                    {
                        PaintedRendererOutcome.ExternalOnly or PaintedRendererOutcome.Mixed => true,
                        PaintedRendererOutcome.BuiltInOnly => false,
                        _ => null
                    },
                    ArtisticWorkerFingerprint = statistics.ExternalArtisticTextures > 0
                        ? artisticIdentity?.Fingerprint
                        : null,
                    ArtisticWorkerPreset = statistics.ExternalArtisticTextures > 0
                        ? artisticIdentity?.Preset
                        : null,
                    PaintedRendererOutcome = rendererOutcome,
                    AppliedRepairRuleIds = TextureProcessingPipeline
                        .GetCurrentRepairRuleIds(
                            recordedOptions.Scope,
                            finalizing.Manifest.Entries.Select(entry => entry.RelativeInstallPath),
                            recordedOptions.Preset)
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
        var hasManualReprocess = textureOverrides?.Any(choice =>
            choice.Action == TextureOverrideAction.Reprocess) == true;
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
                baseline.BuildId,
                paths.InstallPath,
                cancellationToken)
            .ConfigureAwait(false);
        var currentPaintedProfileRevision =
            GetFreshPaintedProfileRevision(baseline.Options.Preset);
        if (isManualTextureRevision
            && currentPaintedProfileRevision > 0
            && (baselineReport?.PaintedProfileRevision ?? 0) != currentPaintedProfileRevision
            && hasManualReprocess)
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
            baselineArtifactPaths,
            baseline.Options.Preset);
        if (isManualTextureRevision && requiresPipelineRepair)
        {
            throw new InvalidOperationException(
                "Upgrade this staged pack to the current texture pipeline before applying individual texture choices. The baseline was left unchanged.");
        }
        var isTargetedSafetyRepair = !isManualTextureRevision
            && TextureProcessingPipeline.RequiresTargetedSafetyRepair(
                baselineReport,
                baseline.Options.Scope,
                baselineArtifactPaths,
                baseline.Options.Preset);
        if (!isManualTextureRevision && isPipelineRepairScope && !requiresPipelineRepair)
        {
            throw new InvalidOperationException(
                "This staged pack already uses the current texture pipeline. The completed pack was left unchanged.");
        }

        var missingRepairRules = TextureProcessingPipeline.GetMissingRepairRuleIds(
            baselineReport,
            baseline.Options.Scope,
            baselineArtifactPaths,
            baseline.Options.Preset);
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
                    StringComparison.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.LegacyMaterialClassificationRuleId,
                    StringComparison.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.PaintedAtCapRepaintRuleId,
                    StringComparison.Ordinal)
                || ruleId.Equals(
                    TextureProcessingPipeline.ClassicWldVisibleSurfaceCoverageRuleId,
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
        await using var artisticWorkerLease =
            await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                    paths,
                    tools,
                    mayUseArtisticWorker: baseline.Options.Preset == TexturePreset.Illustrated,
                    cancellationToken)
                .ConfigureAwait(false);
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
        var legacyRecordedArtisticWorker = baselineReport?.UsedExternalArtisticWorker;
        // Schema 4 is the first report contract that records actual completed
        // renderer routes. The legacy nullable worker flag only recorded
        // availability, so it is never promoted into repair provenance.
        var recordedRendererOutcome = baselineReport is
            { SchemaVersion: >= 4, PaintedRendererOutcome: var actualOutcome }
            ? actualOutcome
            : PaintedRendererOutcome.Unknown;
        var enforceRecordedPaintedRenderer = !isManualTextureRevision
            || hasManualReprocess;
        // The route outcome is the authoritative completed-build record. Older
        // reports only have the nullable compatibility flag, but a modern
        // report can legitimately say BuiltInOnly even when the manifest
        // records the external worker that was configured before all of its
        // jobs fell back. Never let that stale configuration win here.
        var recordedArtisticWorker = recordedRendererOutcome switch
        {
            PaintedRendererOutcome.ExternalOnly => true,
            PaintedRendererOutcome.BuiltInOnly => false,
            PaintedRendererOutcome.Mixed => true,
            _ => legacyRecordedArtisticWorker
        };
        if (baseline.Options.Preset == TexturePreset.Illustrated
            && recordedRendererOutcome == PaintedRendererOutcome.Mixed
            && enforceRecordedPaintedRenderer)
        {
            throw new InvalidOperationException(
                "This Graphic Painted Fantasy pack contains both external-diffusion and built-in painted outputs. Per-texture renderer provenance is unavailable, so repairing it could mix the styles further; rebuild the pack instead.");
        }
        if (baseline.Options.Preset == TexturePreset.Illustrated
            && recordedRendererOutcome == PaintedRendererOutcome.Unknown
            && enforceRecordedPaintedRenderer)
        {
            throw new InvalidOperationException(
                "This Graphic Painted Fantasy pack predates reliable painted-renderer provenance. An automatic repair could mix its existing look with a different renderer, so rebuild the complete pack instead; the completed baseline was left unchanged.");
        }
        // The completed report records what actually rendered, while manifest
        // options are written before native work finishes and may only record
        // what was configured. A worker that failed every eligible texture
        // produces BuiltInOnly output; its configured fingerprint must not make
        // that pack impossible to repair with the worker correctly disabled.
        var recordedArtisticFingerprint = recordedRendererOutcome ==
                PaintedRendererOutcome.ExternalOnly
            ? baselineReport?.ArtisticWorkerFingerprint
                ?? baseline.Options.ArtisticWorkerFingerprint
            : null;
        var recordedArtisticPreset = recordedRendererOutcome ==
                PaintedRendererOutcome.ExternalOnly
            ? baselineReport?.ArtisticWorkerPreset
                ?? baseline.Options.ArtisticWorkerPreset
            : null;
        if (baseline.Options.Preset == TexturePreset.Illustrated
            && recordedRendererOutcome == PaintedRendererOutcome.ExternalOnly
            && enforceRecordedPaintedRenderer
            && (recordedArtisticFingerprint is not { Length: 64 }
                || recordedArtisticFingerprint.Any(character => !Uri.IsHexDigit(character))
                || string.IsNullOrWhiteSpace(recordedArtisticPreset)))
        {
            throw new InvalidOperationException(
                "This diffusion-painted pack predates complete exact renderer identity. Repair cannot safely reproduce its look; rebuild the complete pack instead.");
        }
        repairOptions = repairOptions with
        {
            ArtisticWorkerFingerprint = recordedArtisticFingerprint,
            ArtisticWorkerPreset = recordedArtisticPreset
        };
        var currentArtisticIdentity = baseline.Options.Preset == TexturePreset.Illustrated
            ? await ArtisticWorkerIdentityProvider.ResolveAsync(tools, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (baseline.Options.Preset == TexturePreset.Illustrated
            && hasReproduciblePaintedProfile
            && enforceRecordedPaintedRenderer)
        {
            if (recordedArtisticFingerprint is not null)
            {
                if (currentArtisticIdentity is not null
                    && !recordedArtisticFingerprint.Equals(
                        currentArtisticIdentity.Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "This Graphic Painted Fantasy pack was created with a different diffusion worker or worker configuration. Restore that exact worker-config (or rebuild the pack) before repairing, so one pack cannot mix two diffusion recipes.");
                }
            }

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

        var repairArchives = isManualTextureRevision
            ? selectedArchives
                .Where(path => baselineEntries.ContainsKey(
                    Path.GetRelativePath(paths.InstallPath, path)))
                .ToArray()
            : selectedArchives;
        var repairAdditionPaths = repairArchives
            .Where(path => !baselineEntries.ContainsKey(
                Path.GetRelativePath(paths.InstallPath, path)))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (repairAdditionPaths.Count != 0
            && requireRequestedVisualProfile
            && !hasReproduciblePaintedProfile)
        {
            throw new InvalidOperationException(
                "This painted pack omitted archives that are eligible under the current coverage rules, but its older art-profile revision cannot reproduce them consistently. Build a fresh painted pack instead; the completed baseline was left unchanged.");
        }

        var repairAdditionSources = repairAdditionPaths.Count == 0
            ? BuildOriginalSourceResolver.Empty
            : await PrepareBuildOriginalSourcesAsync(paths, cancellationToken)
                .ConfigureAwait(false);

        var processor = new NativeTextureProcessor(tools);
        var previewCollector = new TexturePreviewCollector(maximumEntries: 24);
        var clampedRepairArchives = SelectCharacterAndEquipmentArchives(
            archiveScopes,
            repairOptions);
        var filtersCharacterEquipmentEntries =
            repairOptions.Scope is AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment;
        var requiresLegacyMaterialRepair = missingRepairRules.Contains(
            TextureProcessingPipeline.LegacyMaterialClassificationRuleId,
            StringComparer.Ordinal);
        var requiresPaintedAtCapRepair = missingRepairRules.Contains(
            TextureProcessingPipeline.PaintedAtCapRepaintRuleId,
            StringComparer.Ordinal);
        var requiresClassicWldVisibleSurfaceRepair = missingRepairRules.Contains(
            TextureProcessingPipeline.ClassicWldVisibleSurfaceCoverageRuleId,
            StringComparer.Ordinal);
        var requiresExternalArtisticWorker =
            recordedRendererOutcome == PaintedRendererOutcome.ExternalOnly;
        var archiveBuilder = new PfsTextureArchiveBuilder(
            processor,
            counter,
            progress,
            previewCollector,
            clampArchivePaths: clampedRepairArchives,
            filterCharacterEquipmentEntries: filtersCharacterEquipmentEntries,
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
                        StringComparer.Ordinal)
                    || missingRepairRules.Contains(
                        TextureProcessingPipeline.LegacyMaterialClassificationRuleId,
                        StringComparer.Ordinal)
                    || missingRepairRules.Contains(
                        TextureProcessingPipeline.PaintedAtCapRepaintRuleId,
                        StringComparer.Ordinal)),
            requireRequestedVisualProfile: requireRequestedVisualProfile,
            // Only the explicitly versioned current painted pipeline can
            // reproduce a missing member. Legacy painted pixels can still be
            // reused byte-for-byte, but mixing them with today's algorithm is
            // refused so users receive a rebuild instruction instead.
            allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile,
            repairLegacyMaterialClassification: requiresLegacyMaterialRepair,
            repairPaintedAtCap: requiresPaintedAtCapRepair,
            repairClassicWldVisibleSurfaceCoverage:
                requiresClassicWldVisibleSurfaceRepair,
            requireExternalArtisticWorker: requiresExternalArtisticWorker);
        // An archive absent from the baseline has no reusable staged payload.
        // Build it independently from an exact original source; never put it
        // through the baseline-rebuild path, which intentionally fails closed
        // when a verified reuse archive is missing.
        var additionArchiveBuilder = new PfsTextureArchiveBuilder(
            processor,
            counter,
            progress,
            previewCollector,
            clampArchivePaths: clampedRepairArchives,
            filterCharacterEquipmentEntries: filtersCharacterEquipmentEntries,
            retryUnchangedEntries: true,
            rebuildFromReuseArchive: false,
            requireRequestedVisualProfile: requireRequestedVisualProfile,
            allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile,
            repairLegacyMaterialClassification: requiresLegacyMaterialRepair,
            repairPaintedAtCap: requiresPaintedAtCapRepair,
            repairClassicWldVisibleSurfaceCoverage:
                requiresClassicWldVisibleSurfaceRepair,
            requireExternalArtisticWorker: requiresExternalArtisticWorker,
            // This builder starts from a verified original rather than from a
            // prior painted archive. A rejected member can therefore remain
            // original without accepting a different visual route; if every
            // member is rejected, the source-identical addition is omitted.
            allowRequestedProfileFailureToKeepOriginal:
                requireRequestedVisualProfile);

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
            ResolvedBuildSource? resolvedAdditionSource = null;
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
            else
            {
                resolvedAdditionSource = await repairAdditionSources
                    .ResolveAsync(relativePath, liveArchivePath, cancellationToken)
                    .ConfigureAwait(false);
                sourcePath = resolvedAdditionSource.Path;
            }

            try
            {
                await using var archive = await PfsArchive.OpenAsync(
                    sourcePath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (HasPotentialArchiveCandidate(
                        archive,
                        relativePath,
                        repairOptions))
                {
                    items.Add(new StagedBuildItem(
                        relativePath,
                        baselineEntry is null ? additionArchiveBuilder : archiveBuilder,
                        PathGuard.SamePath(sourcePath, liveArchivePath) ? null : sourcePath,
                        baselineEntry?.SourceLength
                            ?? resolvedAdditionSource?.ExpectedLength,
                        baselineEntry?.SourceSha256
                            ?? resolvedAdditionSource?.ExpectedSha256,
                        AllowSourceIdenticalOmission: isTargetedSafetyRepair
                            && baselineEntry is null));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                if (baselineEntries.ContainsKey(relativePath)
                    || (isTargetedSafetyRepair
                        && repairAdditionPaths.Contains(
                            Path.GetFullPath(liveArchivePath))))
                {
                    throw new InvalidDataException(
                        $"A selected archive could not be read during repair: {relativePath}. The original staged pack remains intact and no incomplete replacement was created.",
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


        var plannedRepairPaths = items
            .Select(item => item.RelativeInstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingBaselineItems = baselineEntries.Keys
            .Where(relativePath => !plannedRepairPaths.Contains(relativePath))
            .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if ((isTargetedSafetyRepair || isManualTextureRevision)
            && missingBaselineItems.Length != 0)
        {
            throw new InvalidOperationException(
                "The targeted safety repair could not account for every archive in the verified baseline. The original staged pack remains intact; no incomplete replacement was created.");
        }
        TextureBuildReport? report = null;
        string? reportPath = null;
        string? previewManifestPath = null;
        var staged = await stagedBuildService.BuildAsync(
            new StagedBuildRequest(
                paths,
                repairOptions,
                items,
                RequireAllItems: isTargetedSafetyRepair || isManualTextureRevision,
                BeforeManifestCommitAsync:
                    baseline.Options.Preset == TexturePreset.Illustrated
                        ? token => EnsureArtisticWorkerIdentityUnchangedAsync(
                            paths,
                            currentArtisticIdentity,
                            token)
                        : null),
            progress,
            cancellationToken,
            async (finalizing, metadataCancellationToken) =>
            {
                if (isTargetedSafetyRepair && repairAdditionPaths.Count != 0)
                {
                    var committedAdditions = finalizing.Manifest.Entries.Count(entry =>
                        !baselineEntries.ContainsKey(entry.RelativeInstallPath));
                    counter.Warn(
                        $"Safety repair reconsidered {repairAdditionPaths.Count:N0} selected archive(s) that the older pack omitted entirely; {committedAdditions:N0} produced verified enhanced output and were added.");
                }

                var preliminaryStatistics = counter.Snapshot();
                var retriesPreviouslyPreserved = missingRepairRules.Contains(
                        TextureProcessingPipeline.ExpandedClassicCoverageRuleId,
                        StringComparer.Ordinal)
                    || missingRepairRules.Contains(
                        TextureProcessingPipeline.LegacyMaterialClassificationRuleId,
                        StringComparer.Ordinal)
                    || missingRepairRules.Contains(
                        TextureProcessingPipeline.PaintedAtCapRepaintRuleId,
                        StringComparer.Ordinal)
                    || missingRepairRules.Contains(
                        TextureProcessingPipeline.ClassicWldVisibleSurfaceCoverageRuleId,
                        StringComparer.Ordinal);
                counter.Warn(isManualTextureRevision
                    ? $"Texture revision reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and changed only explicitly reviewed entries."
                    : isTargetedSafetyRepair
                        ? $"Safety repair reused {preliminaryStatistics.ReusedTextures:N0} prior enhanced textures and changed only outputs affected by newer safety rules; "
                            + (retriesPreviouslyPreserved
                                ? "textures previously kept original by superseded coverage/material rules were retried."
                                : "source-identical entries were not retried.")
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
                report = new TextureBuildReport(
                    TextureBuildReport.CurrentSchemaVersion,
                    finalizing.BuildId,
                    repairCompletedUtc,
                    paths.InstallPath,
                    finalizing.BuildDirectory,
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
                    UsedExternalArtisticWorker = recordedArtisticWorker,
                    ArtisticWorkerFingerprint = recordedArtisticFingerprint,
                    ArtisticWorkerPreset = recordedArtisticPreset,
                    PaintedRendererOutcome = recordedRendererOutcome,
                    AppliedRepairRuleIds = TextureProcessingPipeline
                        .GetCurrentRepairRuleIds(
                            baseline.Options.Scope,
                            finalizing.Manifest.Entries.Select(
                                entry => entry.RelativeInstallPath),
                            baseline.Options.Preset)
                };
                reportPath = Path.Combine(finalizing.BuildDirectory, "texture-report.json");
                await WriteReportAsync(reportPath, report, metadataCancellationToken)
                    .ConfigureAwait(false);

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

        var completedReport = report
            ?? throw new InvalidDataException("The repaired texture pack report was not finalized.");
        var completedReportPath = reportPath
            ?? throw new InvalidDataException("The repaired texture pack report path was not finalized.");

        return new TexturePackBuildResult(
            staged,
            completedReport,
            completedReportPath,
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
        TextureBuildReport? report = null;
        string? reportPath = null;
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
                    cancellationToken,
                    async (finalizing, metadataCancellationToken) =>
                    {
                        var statistics = counter.Snapshot();
                        counter.Warn(
                            $"Safety repair exact-reused {exactReusedArtifacts:N0} unaffected artifact(s) and upgraded {safetyUpgradedArtifacts:N0} artifact(s) by restoring protected sky/celestial or legacy translucent-material content from verified originals without AI processing.");
                        statistics = counter.Snapshot();
                        var completedUtc = DateTimeOffset.UtcNow;
                        report = new TextureBuildReport(
                            TextureBuildReport.CurrentSchemaVersion,
                            finalizing.BuildId,
                            completedUtc,
                            paths.InstallPath,
                            finalizing.BuildDirectory,
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
                            ArtisticWorkerFingerprint = baseline.Options.ArtisticWorkerFingerprint
                                ?? baselineReport?.ArtisticWorkerFingerprint,
                            ArtisticWorkerPreset = baseline.Options.ArtisticWorkerPreset
                                ?? baselineReport?.ArtisticWorkerPreset,
                            PaintedRendererOutcome = baselineReport?.PaintedRendererOutcome
                                ?? PaintedRendererOutcome.Unknown,
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
                        reportPath = Path.Combine(
                            finalizing.BuildDirectory,
                            "texture-report.json");
                        await WriteReportAsync(
                                reportPath,
                                report,
                                metadataCancellationToken)
                            .ConfigureAwait(false);
                    })
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

        var completedReport = report
            ?? throw new InvalidDataException("The safety-repair texture report was not finalized.");
        var completedReportPath = reportPath
            ?? throw new InvalidDataException("The safety-repair report path was not finalized.");
        return new TexturePackBuildResult(
            staged,
            completedReport,
            completedReportPath,
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
            launcherUpdatePlan: null,
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
            launcherUpdatePlan: null,
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
            launcherUpdatePlan: null,
            progress,
            cancellationToken);

    private async Task<TexturePackBuildResult> RepairStagedPackSourceMismatchCoreAsync(
        ProjectPaths paths,
        string baselineManifestPath,
        IStagedArtifactBuilder? rebuildBuilderOverride,
        IStagedArtifactBuilder? targetedSafetyBuilderOverride,
        bool? forceTargetedSafetyRepair,
        LauncherUpdateSourceRepairPlan? launcherUpdatePlan,
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
        await using var artisticWorkerLease = tools is null
            ? null
            : await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                    paths,
                    tools,
                    mayUseArtisticWorker: baseline.Options.Preset == TexturePreset.Illustrated,
                    cancellationToken)
                .ConfigureAwait(false);
        var visualCompatibility = await ResolveSourceRepairVisualCompatibilityAsync(
                paths,
                baselineInfo,
                tools,
                cancellationToken)
            .ConfigureAwait(false);
        var baselineReport = visualCompatibility.BaselineReport;
        var rendererProvenance = visualCompatibility.RendererProvenance;
        var currentArtisticIdentity =
            visualCompatibility.CurrentArtisticIdentity;
        var isPaintedBaseline = visualCompatibility.IsPaintedBaseline;
        var hasReproduciblePaintedProfile =
            visualCompatibility.HasReproduciblePaintedProfile;
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
                        artifact.CanonicalRelativeInstallPath),
                    baseline.Options.Preset));
        var sourceRepairMissingRules = TextureProcessingPipeline.GetMissingRepairRuleIds(
            baselineReport,
            baseline.Options.Scope,
            baselineInfo.Artifacts.Select(artifact =>
                artifact.CanonicalRelativeInstallPath),
            baseline.Options.Preset);
        var sourceRepairNeedsClassicWldVisibleSurfaceCoverage =
            applyTargetedSafetyRepair
            && sourceRepairMissingRules.Contains(
                TextureProcessingPipeline.ClassicWldVisibleSurfaceCoverageRuleId,
                StringComparer.Ordinal);
        var sourceRepairNeedsLegacyMaterialClassification =
            applyTargetedSafetyRepair
            && sourceRepairMissingRules.Contains(
                TextureProcessingPipeline.LegacyMaterialClassificationRuleId,
                StringComparer.Ordinal);
        var sourceRepairNeedsPaintedAtCap = applyTargetedSafetyRepair
            && sourceRepairMissingRules.Contains(
                TextureProcessingPipeline.PaintedAtCapRepaintRuleId,
                StringComparer.Ordinal);
        var sourceRepairRetriesPreviouslyPreserved = applyTargetedSafetyRepair
            && (sourceRepairMissingRules.Contains(
                    TextureProcessingPipeline.ExpandedClassicCoverageRuleId,
                    StringComparer.Ordinal)
                || sourceRepairNeedsLegacyMaterialClassification
                || sourceRepairNeedsPaintedAtCap);
        var originalSources = launcherUpdatePlan?.OriginalSources
            ?? await PrepareBuildOriginalSourcesAsync(paths, cancellationToken)
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
            allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile,
            requireExternalArtisticWorker:
                rendererProvenance.RendererOutcome == PaintedRendererOutcome.ExternalOnly);
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
                retryUnchangedEntries: sourceRepairRetriesPreviouslyPreserved,
                rebuildFromReuseArchive: true,
                reuseArchivePaths: reuseArchivePaths,
                reuseArchiveFingerprints: reuseArchiveFingerprints,
                requireRequestedVisualProfile: isPaintedBaseline,
                allowRequestedVisualProfileRegeneration: hasReproduciblePaintedProfile,
                repairLegacyMaterialClassification:
                    sourceRepairNeedsLegacyMaterialClassification,
                repairPaintedAtCap: sourceRepairNeedsPaintedAtCap,
                repairClassicWldVisibleSurfaceCoverage:
                    sourceRepairNeedsClassicWldVisibleSurfaceCoverage,
                requireExternalArtisticWorker:
                    rendererProvenance.RendererOutcome == PaintedRendererOutcome.ExternalOnly)
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

            if (launcherUpdatePlan?.UpdatedSources.TryGetValue(
                    relativePath,
                    out var authorizedUpdate) == true)
            {
                if (currentSource.Length != authorizedUpdate.Length
                    || !currentSource.Sha256.Equals(
                        authorizedUpdate.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The LaunchPad-updated source for {relativePath} changed after verification. The completed baseline remains unchanged; close the launcher and refresh again.");
                }

                // This exact live fingerprint was named by a completed
                // post-install LaunchPad receipt. Rebuild the whole archive
                // from the patched original; carrying the prior archive would
                // silently discard official non-texture or texture changes.
                items.Add(new StagedBuildItem(
                    relativePath,
                    rebuildBuilder,
                    PathGuard.SamePath(sourcePath, livePath) ? null : sourcePath,
                    currentSource.Length,
                    currentSource.Sha256,
                    AllowSourceIdenticalOmission: applyTargetedSafetyRepair
                        && CanOmitSourceIdenticalSafetyArtifact(relativePath)));
                rebuiltCount++;
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

        var repairOptions = baseline.Options with
        {
            InstallAfterBuild = false,
            ArtisticWorkerFingerprint = rendererProvenance.ArtisticWorkerFingerprint,
            ArtisticWorkerPreset = rendererProvenance.ArtisticWorkerPreset
        };
        TextureBuildReport? report = null;
        string? reportPath = null;
        string? previewManifestPath = null;
        var staged = await stagedBuildService.BuildAsync(
                new StagedBuildRequest(
                    paths,
                    repairOptions,
                    items,
                    RequireAllItems: true,
                    BeforeManifestCommitAsync:
                        baseline.Options.Preset == TexturePreset.Illustrated
                        && tools is not null
                            ? token => EnsureArtisticWorkerIdentityUnchangedAsync(
                                paths,
                                currentArtisticIdentity,
                                token)
                            : null),
                progress,
                cancellationToken,
                async (finalizing, metadataCancellationToken) =>
                {
                    counter.Warn(launcherUpdatePlan is not null
                        ? $"Completed game-update refresh reused {reusedCount:N0} unaffected archive(s), rebuilt {rebuiltCount:N0} LaunchPad-updated archive(s) from their exact new originals, and safety-upgraded {safetyUpgradedCount:N0} archive(s)."
                        : applyTargetedSafetyRepair
                        ? $"Source and safety repair upgraded {safetyUpgradedCount:N0} clean archive(s) from their exact staged baselines and rebuilt {rebuiltCount:N0} contaminated archive(s) from verified originals."
                        : $"Source repair reused {reusedCount:N0} complete staged archive(s) and rebuilt {rebuiltCount:N0} contaminated archive(s) from verified originals.");
                    var statistics = counter.Snapshot();
                    var repairCompletedUtc = DateTimeOffset.UtcNow;
                    report = new TextureBuildReport(
                        TextureBuildReport.CurrentSchemaVersion,
                        finalizing.BuildId,
                        repairCompletedUtc,
                        paths.InstallPath,
                        finalizing.BuildDirectory,
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
                        UsedExternalArtisticWorker =
                            rendererProvenance.UsedExternalArtisticWorker,
                        ArtisticWorkerFingerprint =
                            rendererProvenance.ArtisticWorkerFingerprint,
                        ArtisticWorkerPreset = rendererProvenance.ArtisticWorkerPreset,
                        PaintedRendererOutcome = rendererProvenance.RendererOutcome,
                        AppliedRepairRuleIds = applyTargetedSafetyRepair
                            ? TextureProcessingPipeline.GetCurrentRepairRuleIds(
                                baseline.Options.Scope,
                                baselineInfo.Artifacts.Select(
                                    artifact => artifact.CanonicalRelativeInstallPath),
                                baseline.Options.Preset)
                            : TextureProcessingPipeline.GetRecordedRepairRuleIds(
                                baselineReport,
                                baseline.Options.Scope)
                    };
                    reportPath = Path.Combine(
                        finalizing.BuildDirectory,
                        "texture-report.json");
                    await WriteReportAsync(reportPath, report, metadataCancellationToken)
                        .ConfigureAwait(false);

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
                })
            .ConfigureAwait(false);

        var completedReport = report
            ?? throw new InvalidDataException("The source-repair texture report was not finalized.");
        var completedReportPath = reportPath
            ?? throw new InvalidDataException("The source-repair report path was not finalized.");

        return new TexturePackBuildResult(
            staged,
            completedReport,
            completedReportPath,
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
        string expectedBuildId,
        string expectedInstallPath,
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
            var report = await JsonSerializer.DeserializeAsync<TextureBuildReport>(
                    stream,
                    CompositionJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return TextureBuildReportValidation.IsUsableForStagedPack(
                report,
                expectedBuildId,
                expectedInstallPath,
                buildDirectory)
                ? report
                : null;
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

    public async Task<LauncherUpdateRefreshAssessment> AssessLauncherUpdateRefreshAsync(
        ProjectPaths paths,
        InstallHealthReport? verifiedHealth = null,
        CancellationToken cancellationToken = default)
    {
        var context = await TryCreateLauncherUpdateRefreshContextAsync(
                paths,
                verifiedHealth,
                cancellationToken)
            .ConfigureAwait(false);
        return context.Assessment;
    }

    private async Task<LauncherUpdateRefreshContext>
        TryCreateLauncherUpdateRefreshContextAsync(
            ProjectPaths paths,
            InstallHealthReport? verifiedHealth,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var canReuseHealth = verifiedHealth is not null
            && verifiedHealth.Entries.All(entry =>
                entry.State == InstalledArtifactHealthState.ModifiedOrMissing
                    ? entry.ObservedSha256 is { Length: 64 }
                        || entry.ObservedLength is null
                    : entry.ObservedSha256 is { Length: 64 });
        var health = canReuseHealth
            ? verifiedHealth!
            : await installHealthService
                .AuditLatestAsync(paths, cancellationToken)
                .ConfigureAwait(false);
        var isOrdinaryLauncherUpdate = health.State == InstallHealthState.MixedOrModified
            && health.Entries.Any(entry =>
                entry.State == InstalledArtifactHealthState.ModifiedOrMissing);
        var isInterruptedReconciliation = health.State == InstallHealthState.RecoveryRequired
            && health.TransactionState == InstallTransactionState.RecoveryRequired;
        if (!isOrdinaryLauncherUpdate && !isInterruptedReconciliation)
        {
            return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.NotApplicable,
                "No completed game update needs texture-pack refresh.",
                health.InstallManifestPath,
                ActiveBuildManifestPath: null,
                UpdatedArtifactCount: 0,
                UpdatedRelativePaths: []));
        }

        if (string.IsNullOrWhiteSpace(health.InstallManifestPath))
        {
            return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.UnverifiedChanges,
                "The mixed installation has no verified SpinTexture transaction manifest. No game files were changed.",
                ActiveInstallManifestPath: null,
                ActiveBuildManifestPath: null,
                UpdatedArtifactCount: 0,
                UpdatedRelativePaths: []));
        }

        var safeInstallManifestPath = PathGuard.EnsurePathUnderRoot(
            paths.BackupPath,
            health.InstallManifestPath);
        var install = await manifestStore
            .ReadInstallManifestAsync(safeInstallManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (install.State is not (InstallTransactionState.Applied
                or InstallTransactionState.RecoveryRequired)
            || !PathGuard.SamePath(install.InstallPath, paths.InstallPath))
        {
            return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.UnverifiedChanges,
                "The active transaction is not an applied pack for this installation. No game files were changed.",
                safeInstallManifestPath,
                install.BuildManifestPath,
                UpdatedArtifactCount: 0,
                UpdatedRelativePaths: []));
        }

        LauncherUpdateReconciliationReceipt? interruptedReceipt = null;
        if (install.State == InstallTransactionState.RecoveryRequired)
        {
            var receiptPath = Path.Combine(
                Path.GetDirectoryName(safeInstallManifestPath)!,
                InstallTransactionService.LauncherUpdateReconciliationReceiptFileName);
            if (!File.Exists(receiptPath))
            {
                return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                    LauncherUpdateRefreshState.UnverifiedChanges,
                    "The interrupted install has no durable launcher-update receipt. Use the normal recovery guidance; no updated originals were trusted.",
                    safeInstallManifestPath,
                    install.BuildManifestPath,
                    UpdatedArtifactCount: 0,
                    UpdatedRelativePaths: []));
            }

            try
            {
                interruptedReceipt = await manifestStore
                    .ReadLauncherUpdateReconciliationReceiptAsync(
                        receiptPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException)
            {
                return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                    LauncherUpdateRefreshState.UnverifiedChanges,
                    $"SpinTexture could not validate the interrupted launcher-update receipt: {exception.Message}",
                    safeInstallManifestPath,
                    install.BuildManifestPath,
                    UpdatedArtifactCount: 0,
                    UpdatedRelativePaths: []));
            }

            if (interruptedReceipt.State is not (
                    LauncherUpdateReconciliationState.Preparing
                    or LauncherUpdateReconciliationState.Completed)
                || interruptedReceipt.State
                        == LauncherUpdateReconciliationState.Completed
                    && interruptedReceipt.ReconciledUtc is null
                || interruptedReceipt.State
                        == LauncherUpdateReconciliationState.Preparing
                    && string.IsNullOrWhiteSpace(
                        interruptedReceipt.SafetyDirectoryName)
                || !interruptedReceipt.ApplyId.Equals(
                    install.ApplyId,
                    StringComparison.OrdinalIgnoreCase)
                || interruptedReceipt.AppliedUtc != install.AppliedUtc
                || !PathGuard.SamePath(interruptedReceipt.InstallPath, paths.InstallPath)
                || interruptedReceipt.Entries.Count != install.Entries.Count)
            {
                return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                    LauncherUpdateRefreshState.UnverifiedChanges,
                    "The interrupted launcher-update receipt does not match the active install transaction. SpinTexture will not guess how to resume or finalize it.",
                    safeInstallManifestPath,
                    install.BuildManifestPath,
                    UpdatedArtifactCount: 0,
                    UpdatedRelativePaths: []));
            }
        }

        var evidence = await launchPadUpdateEvidenceService
            .InspectAsync(paths, install.AppliedUtc, cancellationToken)
            .ConfigureAwait(false);
        if (!evidence.IsCompleted)
        {
            return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.LauncherIncomplete,
                evidence.Summary,
                safeInstallManifestPath,
                install.BuildManifestPath,
                UpdatedArtifactCount: 0,
                UpdatedRelativePaths: []));
        }

        var updated = new List<AuthorizedLauncherUpdateSource>();
        var unauthorized = new List<string>();
        var removed = new List<string>();
        var unreadableArchives = new List<string>();
        var authorizationSnapshots = new List<AdoptedOriginalArtifact>();
        if (interruptedReceipt is not null)
        {
            var healthByPath = health.Entries.ToDictionary(
                entry => entry.RelativeInstallPath,
                StringComparer.OrdinalIgnoreCase);
            var receiptChanges = interruptedReceipt.Entries
                .Where(entry => entry.Disposition is
                    LauncherUpdateOriginalDisposition.AdoptedUpdatedFile or
                    LauncherUpdateOriginalDisposition.AdoptedRemovedFile)
                .ToArray();
            if (receiptChanges.Length == 0
                || receiptChanges.Select(entry => entry.RelativeInstallPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != receiptChanges.Length)
            {
                return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                    LauncherUpdateRefreshState.UnverifiedChanges,
                    "The interrupted launcher-update receipt has no unique adopted update snapshots. SpinTexture will not guess how to resume it.",
                    safeInstallManifestPath,
                    install.BuildManifestPath,
                    UpdatedArtifactCount: 0,
                    UpdatedRelativePaths: []));
            }

            var receiptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var receiptChange in receiptChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!evidence.TryGetChangedFile(
                        receiptChange.RelativeInstallPath,
                        out var loggedChange)
                    || loggedChange is null
                    || (receiptChange.Exists
                        ? loggedChange.Action == LaunchPadFileAction.Removed
                        : loggedChange.Action != LaunchPadFileAction.Removed)
                    || !healthByPath.TryGetValue(
                        receiptChange.RelativeInstallPath,
                        out var observed))
                {
                    unauthorized.Add(receiptChange.RelativeInstallPath);
                    continue;
                }

                var livePath = PathGuard.ResolveUnderRoot(
                    paths.InstallPath,
                    receiptChange.RelativeInstallPath);
                var matchesReceipt = receiptChange.Exists
                    ? File.Exists(livePath)
                        && observed.ObservedLength == receiptChange.Length
                        && string.Equals(
                            observed.ObservedSha256,
                            receiptChange.Sha256,
                            StringComparison.OrdinalIgnoreCase)
                    : !File.Exists(livePath)
                        && observed.ObservedLength is null
                        && observed.ObservedSha256 is null;
                if (!matchesReceipt)
                {
                    unauthorized.Add(receiptChange.RelativeInstallPath);
                    continue;
                }

                receiptPaths.Add(receiptChange.RelativeInstallPath);
                authorizationSnapshots.Add(new AdoptedOriginalArtifact(
                    receiptChange.RelativeInstallPath,
                    receiptChange.Exists,
                    receiptChange.Length,
                    receiptChange.Sha256));
                if (!receiptChange.Exists)
                {
                    removed.Add(receiptChange.RelativeInstallPath);
                }
                else
                {
                    updated.Add(new AuthorizedLauncherUpdateSource(
                        receiptChange.RelativeInstallPath,
                        livePath,
                        receiptChange.Length,
                        receiptChange.Sha256!));
                }
            }

            foreach (var unknown in health.Entries.Where(entry =>
                         entry.State == InstalledArtifactHealthState.ModifiedOrMissing
                         && !receiptPaths.Contains(entry.RelativeInstallPath)))
            {
                unauthorized.Add(unknown.RelativeInstallPath);
            }
        }
        else
        {
            foreach (var entry in health.Entries.Where(candidate =>
                         candidate.State == InstalledArtifactHealthState.ModifiedOrMissing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!evidence.TryGetChangedFile(entry.RelativeInstallPath, out var changed)
                    || changed is null)
                {
                    unauthorized.Add(entry.RelativeInstallPath);
                    continue;
                }

                var livePath = PathGuard.ResolveUnderRoot(
                    paths.InstallPath,
                    entry.RelativeInstallPath);
                if (changed.Action == LaunchPadFileAction.Removed)
                {
                    if (File.Exists(livePath))
                    {
                        unauthorized.Add(entry.RelativeInstallPath);
                    }
                    else
                    {
                        removed.Add(entry.RelativeInstallPath);
                        authorizationSnapshots.Add(new AdoptedOriginalArtifact(
                            entry.RelativeInstallPath,
                            Exists: false,
                            Length: 0,
                            Sha256: null));
                    }

                    continue;
                }

                if (entry.ObservedLength is null
                    || entry.ObservedSha256 is not { Length: 64 }
                    || !File.Exists(livePath))
                {
                    unauthorized.Add(entry.RelativeInstallPath);
                    continue;
                }

                updated.Add(new AuthorizedLauncherUpdateSource(
                    entry.RelativeInstallPath,
                    livePath,
                    entry.ObservedLength.Value,
                    entry.ObservedSha256));
                authorizationSnapshots.Add(new AdoptedOriginalArtifact(
                    entry.RelativeInstallPath,
                    Exists: true,
                    entry.ObservedLength.Value,
                    entry.ObservedSha256));
            }
        }

        foreach (var source in updated.Where(source =>
                     EverQuestInstall.IsPfsArchiveExtension(
                         Path.GetExtension(source.RelativeInstallPath))))
        {
            try
            {
                await using var archive = await PfsArchive.OpenAsync(
                        source.LivePath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (archive.Entries.Count == 0)
                {
                    unreadableArchives.Add(source.RelativeInstallPath);
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                InvalidDataException or
                PfsArchiveException or
                NotSupportedException)
            {
                unreadableArchives.Add(source.RelativeInstallPath);
            }
        }

        if (unauthorized.Count != 0)
        {
            return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.UnverifiedChanges,
                $"{unauthorized.Count:N0} changed archive(s) were not explicitly recorded by a completed post-install LaunchPad session. SpinTexture will not adopt them: {string.Join(", ", unauthorized.Take(3))}",
                safeInstallManifestPath,
                install.BuildManifestPath,
                UpdatedArtifactCount: updated.Count,
                UpdatedRelativePaths: updated.Select(item => item.RelativeInstallPath).ToArray()));
        }

        if (unreadableArchives.Count != 0)
        {
            return LauncherUpdateRefreshContext.Blocked(new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.UnverifiedChanges,
                $"LaunchPad named {unreadableArchives.Count:N0} changed archive(s), but they do not parse as complete EverQuest archives. Reopen LaunchPad and let verification finish; no game files were changed.",
                safeInstallManifestPath,
                install.BuildManifestPath,
                UpdatedArtifactCount: updated.Count,
                UpdatedRelativePaths: updated.Select(item => item.RelativeInstallPath).ToArray()));
        }

        if (removed.Count != 0)
        {
            var freshBuildAssessment = new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.FreshBuildRequired,
                $"Game update detected — LaunchPad removed {removed.Count:N0} file(s) used by the active pack. SpinTexture can accept the verified update safely, then this selection must be analyzed and built fresh instead of silently dropping content.",
                safeInstallManifestPath,
                install.BuildManifestPath,
                UpdatedArtifactCount: authorizationSnapshots.Count,
                UpdatedRelativePaths: authorizationSnapshots
                    .Select(item => item.RelativeInstallPath)
                    .ToArray());
            return new LauncherUpdateRefreshContext(
                freshBuildAssessment,
                health,
                install,
                evidence,
                updated.AsReadOnly(),
                authorizationSnapshots.AsReadOnly(),
                IsReconciliationResume: interruptedReceipt is not null,
                SelectionLeaves: []);
        }

        if (updated.Any(item => !EverQuestInstall.IsPfsArchiveExtension(
                Path.GetExtension(item.RelativeInstallPath))))
        {
            var freshBuildAssessment = new LauncherUpdateRefreshAssessment(
                LauncherUpdateRefreshState.FreshBuildRequired,
                "Game update detected — LaunchPad changed one or more loose files used by this pack. SpinTexture can accept the verified update safely, then Analyze and build fresh so no stale loose output is mixed with the updated client.",
                safeInstallManifestPath,
                install.BuildManifestPath,
                UpdatedArtifactCount: authorizationSnapshots.Count,
                UpdatedRelativePaths: authorizationSnapshots
                    .Select(item => item.RelativeInstallPath)
                    .ToArray());
            return new LauncherUpdateRefreshContext(
                freshBuildAssessment,
                health,
                install,
                evidence,
                updated.AsReadOnly(),
                authorizationSnapshots.AsReadOnly(),
                IsReconciliationResume: interruptedReceipt is not null,
                SelectionLeaves: []);
        }

        var assessment = new LauncherUpdateRefreshAssessment(
            interruptedReceipt is null
                ? LauncherUpdateRefreshState.Ready
                : LauncherUpdateRefreshState.ResumeRequired,
            interruptedReceipt is null
                ? $"Game update detected — {updated.Count:N0} archive(s) changed after LaunchPad completed. SpinTexture can reuse unaffected staged work, rebuild only updated sources, and reinstall safely."
                : $"Game-update recovery is ready — SpinTexture verified the durable interrupted receipt and all {updated.Count:N0} LaunchPad-updated archive snapshot(s). Refresh + Reinstall will safely resume from that receipt.",
            safeInstallManifestPath,
            install.BuildManifestPath,
            updated.Count,
            updated.Select(item => item.RelativeInstallPath).ToArray());
        var candidateContext = new LauncherUpdateRefreshContext(
            assessment,
            health,
            install,
            evidence,
            updated.AsReadOnly(),
            authorizationSnapshots.AsReadOnly(),
            IsReconciliationResume: interruptedReceipt is not null,
            SelectionLeaves: []);
        LauncherUpdateSelectionPreflight selectionPreflight;
        try
        {
            var sourcePlan = CreateLauncherUpdateSourceRepairPlan(
                paths,
                candidateContext);
            selectionPreflight = await PreflightLauncherUpdateSelectionAsync(
                    paths,
                    install.BuildManifestPath,
                    install,
                    sourcePlan,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            JsonException or
            System.Security.SecurityException or
            NotSupportedException)
        {
            selectionPreflight = new LauncherUpdateSelectionPreflight(
                CanFocusedRefresh: false,
                $"The active staged selection could not be verified for focused refresh: {exception.Message}",
                Leaves: []);
        }

        if (!selectionPreflight.CanFocusedRefresh)
        {
            return candidateContext with
            {
                Assessment = new LauncherUpdateRefreshAssessment(
                    LauncherUpdateRefreshState.FreshBuildRequired,
                    $"Game update detected — {selectionPreflight.Summary} SpinTexture can still accept the verified official update safely; then Analyze and build a fresh pack.",
                    safeInstallManifestPath,
                    install.BuildManifestPath,
                    authorizationSnapshots.Count,
                    authorizationSnapshots
                        .Select(item => item.RelativeInstallPath)
                        .ToArray()),
                SelectionLeaves = []
            };
        }

        return candidateContext with
        {
            SelectionLeaves = selectionPreflight.Leaves
        };
    }

    public async Task<LauncherUpdateRefreshResult>
        RefreshAndApplyActivePackAfterLauncherUpdateAsync(
            ProjectPaths paths,
            IProgress<ProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default) =>
        await RefreshAndApplyActivePackAfterLauncherUpdateCoreAsync(
                paths,
                rebuildBuilderOverride: null,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<LauncherUpdateRefreshResult>
        RefreshAndApplyActivePackAfterLauncherUpdateAsync(
            ProjectPaths paths,
            IStagedArtifactBuilder rebuildBuilder,
            IProgress<ProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default) =>
        await RefreshAndApplyActivePackAfterLauncherUpdateCoreAsync(
                paths,
                rebuildBuilder
                    ?? throw new ArgumentNullException(nameof(rebuildBuilder)),
                progress,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Accepts an exact, completed LaunchPad update that cannot safely reuse the
    /// active staged selection (for example a removed archive or changed loose
    /// file). This retires only the old install transaction; it never applies
    /// stale staged output. The caller can then Analyze and create a fresh pack.
    /// </summary>
    public async Task<LauncherUpdateReconciliationResult>
        ReconcileActivePackForFreshBuildAfterLauncherUpdateAsync(
            ProjectPaths paths,
            IProgress<ProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        clientClosedGuard();
        var context = await TryCreateLauncherUpdateRefreshContextAsync(
                paths,
                verifiedHealth: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!context.Assessment.CanReconcileForFreshBuild
            || context.Assessment.ActiveInstallManifestPath is null
            || context.InstallManifest is null
            || context.AuthorizedChanges.Count == 0)
        {
            throw new InvalidOperationException(context.Assessment.Summary);
        }

        var reconciliation = await installTransactionService
            .RestoreAfterVerifiedLauncherUpdateAsync(
                paths,
                context.Assessment.ActiveInstallManifestPath,
                context.AuthorizedChanges,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteRestoreCompletionMarkerAsync(
                context.Assessment.ActiveInstallManifestPath,
                reconciliation.ApplyId,
                reconciliation.ReconciledArtifacts,
                cancellationToken,
                reconciliation.ReconciledUtc)
            .ConfigureAwait(false);
        return reconciliation;
    }

    private async Task<LauncherUpdateRefreshResult>
        RefreshAndApplyActivePackAfterLauncherUpdateCoreAsync(
            ProjectPaths paths,
            IStagedArtifactBuilder? rebuildBuilderOverride,
            IProgress<ProgressUpdate>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Fail before reading sources or creating immutable replacement work
        // when EverQuest/LaunchPad is still open. The transaction repeats this
        // gate immediately around each live write.
        clientClosedGuard();
        var context = await TryCreateLauncherUpdateRefreshContextAsync(
                paths,
                verifiedHealth: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!context.Assessment.CanRefresh
            || context.Health is null
            || context.InstallManifest is null
            || context.Assessment.ActiveInstallManifestPath is null
            || context.Assessment.ActiveBuildManifestPath is null)
        {
            throw new InvalidOperationException(context.Assessment.Summary);
        }

        var sourcePlan = CreateLauncherUpdateSourceRepairPlan(paths, context);
        var selectedLeafPlans = context.SelectionLeaves;
        if (selectedLeafPlans.Count == 0)
        {
            throw new InvalidDataException(
                "The active pack selection has no reusable source manifests. No game files were changed.");
        }

        var refreshedSelection = new List<string>(selectedLeafPlans.Count);
        var repairs = new List<TexturePackBuildResult>();
        var reusedPackCount = 0;
        for (var index = 0; index < selectedLeafPlans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leafPlan = selectedLeafPlans[index];
            var manifestPath = leafPlan.ManifestPath;
            progress?.Report(new ProgressUpdate(
                "Refresh game update",
                "Checking whether this staged pack uses an archive changed by LaunchPad.",
                index,
                selectedLeafPlans.Count,
                Path.GetFileName(Path.GetDirectoryName(manifestPath))));
            if (!leafPlan.RequiresRefresh)
            {
                refreshedSelection.Add(manifestPath);
                reusedPackCount++;
                continue;
            }

            var repaired = await RepairStagedPackSourceMismatchCoreAsync(
                    paths,
                    manifestPath,
                    rebuildBuilderOverride,
                    targetedSafetyBuilderOverride: null,
                    // A launcher refresh is deliberately narrow: whole-reuse
                    // every unaffected archive and rebuild only an exact
                    // official changed source. Normal safety repair remains a
                    // separate user action and must not turn patch recovery
                    // into an unexpected whole-library upgrade.
                    forceTargetedSafetyRepair: false,
                    sourcePlan,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            repairs.Add(repaired);
            refreshedSelection.Add(repaired.StagedBuild.ManifestPath);
        }

        string refreshedManifestPath;
        if (refreshedSelection.Count == 1)
        {
            refreshedManifestPath = refreshedSelection[0];
        }
        else
        {
            var composition = await stagedPackComposer.ComposeAsync(
                    paths,
                    refreshedSelection,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            refreshedManifestPath = composition.ManifestPath;
        }

        // No live write occurs until every changed archive has a completed,
        // exact replacement. The transaction primitive rechecks these hashes,
        // restores only any still-enhanced files, and leaves the authorized
        // patched originals untouched.
        var reconciliation = await installTransactionService
            .RestoreAfterVerifiedLauncherUpdateAsync(
                paths,
                context.Assessment.ActiveInstallManifestPath,
                context.AuthorizedChanges,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteRestoreCompletionMarkerAsync(
                context.Assessment.ActiveInstallManifestPath,
                reconciliation.ApplyId,
                reconciliation.ReconciledArtifacts,
                cancellationToken,
                reconciliation.ReconciledUtc)
            .ConfigureAwait(false);

        progress?.Report(new ProgressUpdate(
            "Refresh game update",
            "The completed LaunchPad update is reconciled. Installing the refreshed staged selection.",
            selectedLeafPlans.Count,
            selectedLeafPlans.Count));
        var apply = await installTransactionService.ApplyAsync(
                paths,
                refreshedManifestPath,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return new LauncherUpdateRefreshResult(
            repairs.AsReadOnly(),
            refreshedManifestPath,
            apply,
            context.UpdatedSources.Count,
            reusedPackCount);
    }

    private LauncherUpdateSourceRepairPlan CreateLauncherUpdateSourceRepairPlan(
        ProjectPaths paths,
        LauncherUpdateRefreshContext context)
    {
        var health = context.Health
            ?? throw new InvalidDataException("Launcher-update health context is missing.");
        var install = context.InstallManifest
            ?? throw new InvalidDataException("Launcher-update install context is missing.");
        var installManifestPath = context.Assessment.ActiveInstallManifestPath
            ?? throw new InvalidDataException("Launcher-update manifest path is missing.");
        var backupDirectory = Path.GetDirectoryName(installManifestPath)!;
        var healthByPath = health.Entries.ToDictionary(
            entry => entry.RelativeInstallPath,
            StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, ManagedOriginalSource>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in install.Entries)
        {
            if (!healthByPath.TryGetValue(artifact.RelativeInstallPath, out var observed)
                || observed.State != InstalledArtifactHealthState.Enhanced)
            {
                continue;
            }

            if (!artifact.OriginalExisted
                || artifact.OriginalSha256 is not { Length: 64 }
                || string.IsNullOrWhiteSpace(artifact.BackupRelativePath))
            {
                throw new InvalidDataException(
                    $"The still-enhanced archive {artifact.RelativeInstallPath} has no verified original backup for game-update refresh.");
            }

            references.Add(
                artifact.RelativeInstallPath,
                new ManagedOriginalSource(
                    PathGuard.ResolveUnderRoot(
                        backupDirectory,
                        artifact.BackupRelativePath),
                    artifact.OriginalLength,
                    artifact.OriginalSha256));
        }

        return new LauncherUpdateSourceRepairPlan(
            new BuildOriginalSourceResolver(references),
            context.UpdatedSources.ToDictionary(
                source => source.RelativeInstallPath,
                StringComparer.OrdinalIgnoreCase));
    }

    private async Task<LauncherUpdateSelectionPreflight>
        PreflightLauncherUpdateSelectionAsync(
        ProjectPaths paths,
        string rootManifestPath,
        InstallManifest activeInstall,
        LauncherUpdateSourceRepairPlan plan,
        CancellationToken cancellationToken)
    {
        var rootInfo = await stagedPackCatalogService
            .InspectAsync(
                paths,
                rootManifestPath,
                StagedPackVerificationMode.Exact,
                cancellationToken)
            .ConfigureAwait(false);
        if (!rootInfo.IsReady)
        {
            return new LauncherUpdateSelectionPreflight(
                CanFocusedRefresh: false,
                $"The active staged manifest or payload is unavailable or invalid ({rootInfo.Summary}).",
                Leaves: []);
        }

        var leafManifestPaths = await ExpandRefreshSelectionManifestsAsync(
                paths,
                rootInfo.ManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (leafManifestPaths.Count == 0)
        {
            return new LauncherUpdateSelectionPreflight(
                CanFocusedRefresh: false,
                "The active staged selection contains no completed source packs.",
                Leaves: []);
        }

        if (File.Exists(Path.Combine(rootInfo.BuildDirectory, "composition.json"))
            && !await SelectionMatchesActiveCompositionAsync(
                    paths,
                    activeInstall,
                    leafManifestPaths,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return new LauncherUpdateSelectionPreflight(
                CanFocusedRefresh: false,
                "The active composition provenance does not exactly reconstruct the installed selection.",
                Leaves: []);
        }

        var leaves = new List<LauncherUpdateLeafPlan>(leafManifestPaths.Count);
        var mappedUpdates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leafManifestPath in leafManifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = PathGuard.SamePath(rootInfo.ManifestPath, leafManifestPath)
                ? rootInfo
                : await stagedPackCatalogService
                    .InspectAsync(
                        paths,
                        leafManifestPath,
                        StagedPackVerificationMode.Exact,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!info.IsReady)
            {
                return new LauncherUpdateSelectionPreflight(
                    CanFocusedRefresh: false,
                    $"An active component pack is unavailable or invalid ({info.Summary}).",
                    Leaves: []);
            }

            var requiresRefresh = false;
            foreach (var artifact in info.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = artifact.CanonicalRelativeInstallPath;
                var livePath = PathGuard.ResolveUnderRoot(
                    paths.InstallPath,
                    relativePath);
                var resolved = await plan.OriginalSources
                    .ResolveAsync(relativePath, livePath, cancellationToken)
                    .ConfigureAwait(false);
                var fingerprint = await FileIntegrity
                    .FingerprintAsync(resolved.Path, cancellationToken)
                    .ConfigureAwait(false);
                var matchesStagedSource = fingerprint.Length
                        == artifact.Entry.SourceLength
                    && fingerprint.Sha256.Equals(
                        artifact.Entry.SourceSha256,
                        StringComparison.OrdinalIgnoreCase);
                if (plan.UpdatedSources.TryGetValue(relativePath, out var update))
                {
                    if (fingerprint.Length != update.Length
                        || !fingerprint.Sha256.Equals(
                            update.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new LauncherUpdateSelectionPreflight(
                            CanFocusedRefresh: false,
                            $"The current source for {relativePath} no longer matches the exact completed LaunchPad update snapshot.",
                            Leaves: []);
                    }

                    mappedUpdates.Add(relativePath);
                }

                if (matchesStagedSource)
                {
                    continue;
                }

                if (!plan.UpdatedSources.ContainsKey(relativePath))
                {
                    return new LauncherUpdateSelectionPreflight(
                        CanFocusedRefresh: false,
                        $"{relativePath} differs from its staged source but was not changed by the verified LaunchPad session.",
                        Leaves: []);
                }

                requiresRefresh = true;
            }

            if (requiresRefresh && !IsSourceMismatchRepairEligible(info))
            {
                return new LauncherUpdateSelectionPreflight(
                    CanFocusedRefresh: false,
                    "A LaunchPad-changed component cannot be source-repaired safely with its recorded scope or artifact type.",
                    Leaves: []);
            }

            if (requiresRefresh)
            {
                await EnsureProductionSourceRepairCompatibilityAsync(
                        paths,
                        info,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            leaves.Add(new LauncherUpdateLeafPlan(
                info.ManifestPath,
                requiresRefresh));
        }

        var unmappedUpdates = plan.UpdatedSources.Keys
            .Where(path => !mappedUpdates.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmappedUpdates.Length != 0)
        {
            return new LauncherUpdateSelectionPreflight(
                CanFocusedRefresh: false,
                $"The active staged selection does not contain {unmappedUpdates.Length:N0} LaunchPad-changed archive(s), including {unmappedUpdates[0]}.",
                Leaves: []);
        }

        return new LauncherUpdateSelectionPreflight(
            CanFocusedRefresh: true,
            "The complete active staged selection and all source mappings passed exact verification.",
            leaves.AsReadOnly());
    }

    private async Task EnsureProductionSourceRepairCompatibilityAsync(
        ProjectPaths paths,
        StagedPackInfo baselineInfo,
        CancellationToken cancellationToken)
    {
        var tools = toolchainDiscovery.Discover(paths);
        if (!tools.IsReady)
        {
            throw new FileNotFoundException(
                "SpinTexture's bundled processing tools are incomplete. "
                + string.Join(" ", tools.Diagnostics));
        }

        var preset = baselineInfo.Manifest?.Options.Preset
            ?? throw new InvalidDataException(
                "The changed source-repair component has no verified options.");
        await using var artisticWorkerLease =
            await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                    paths,
                    tools,
                    mayUseArtisticWorker: preset == TexturePreset.Illustrated,
                    cancellationToken)
                .ConfigureAwait(false);
        _ = await ResolveSourceRepairVisualCompatibilityAsync(
                paths,
                baselineInfo,
                tools,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ExpandRefreshSelectionManifestsAsync(
        ProjectPaths paths,
        string rootManifestPath,
        CancellationToken cancellationToken)
    {
        var leaves = new List<string>();
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task ExpandAsync(string requestedPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = PathGuard.EnsurePathUnderRoot(paths.StagingPath, requestedPath);
            if (!stack.Add(safePath))
            {
                throw new InvalidDataException(
                    "The active staged-pack composition contains a dependency cycle.");
            }

            try
            {
                if (!expanded.Add(safePath))
                {
                    return;
                }

                var buildDirectory = Path.GetDirectoryName(safePath)!;
                var compositionPath = Path.Combine(buildDirectory, "composition.json");
                if (!File.Exists(compositionPath))
                {
                    leaves.Add(safePath);
                    return;
                }

                StagedPackCompositionDocument document;
                await using (var stream = new FileStream(
                                 compositionPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    document = await JsonSerializer
                        .DeserializeAsync<StagedPackCompositionDocument>(
                            stream,
                            CompositionJsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException(
                            "The active staged-pack composition document is empty.");
                }

                if (document.SchemaVersion
                        != StagedPackCompositionDocument.CurrentSchemaVersion
                    || !PathGuard.SamePath(document.InstallPath, paths.InstallPath)
                    || document.Components is not { Count: > 0 })
                {
                    throw new InvalidDataException(
                        "The active staged-pack composition is invalid for this installation.");
                }

                foreach (var component in document.Components)
                {
                    var componentPath = PathGuard.ResolveUnderRoot(
                        paths.StagingPath,
                        component.ManifestRelativePath);
                    await FileIntegrity.EnsureMatchesAsync(
                            componentPath,
                            component.ManifestLength,
                            component.ManifestSha256,
                            "Active game-update composition component manifest",
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ExpandAsync(componentPath).ConfigureAwait(false);
                }
            }
            finally
            {
                stack.Remove(safePath);
            }
        }

        await ExpandAsync(rootManifestPath).ConfigureAwait(false);
        return leaves.AsReadOnly();
    }

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
        // This state-changing entry point uses an exact audit and resolves any
        // launcher-update gate before touching the selected manifests. In
        // particular, a multi-pack selection must not create a large, unusable
        // composition while the active official update still needs refresh or
        // reconciliation on the main Build screen.
        var health = await installHealthService
            .AuditLatestAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        var hasUnknownUpdatedArtifact = health.State == InstallHealthState.MixedOrModified
            && health.Entries.Any(entry =>
                entry.State == InstalledArtifactHealthState.ModifiedOrMissing);
        var hasLauncherReconciliationReceipt = health.State
                == InstallHealthState.RecoveryRequired
            && !string.IsNullOrWhiteSpace(health.InstallManifestPath)
            && File.Exists(Path.Combine(
                Path.GetDirectoryName(health.InstallManifestPath)!,
                InstallTransactionService.LauncherUpdateReconciliationReceiptFileName));
        if (hasUnknownUpdatedArtifact || hasLauncherReconciliationReceipt)
        {
            var assessment = await AssessLauncherUpdateRefreshAsync(
                    paths,
                    health,
                    cancellationToken)
                .ConfigureAwait(false);
            if (assessment.State != LauncherUpdateRefreshState.NotApplicable)
            {
                throw new LauncherUpdateActionRequiredException(assessment);
            }
        }

        // Other mixed/recovery states are also resolved before any checked
        // manifests are inspected or composed. They are not verified launcher
        // updates, but creating an unusable composition first would still waste
        // pack-library space and obscure the actual recovery action.
        if (health.State == InstallHealthState.MixedOrModified)
        {
            throw new InvalidOperationException(
                "The current installation is partly enhanced and partly original. Use the verified Restore action before switching pack selections.");
        }

        if (health.State == InstallHealthState.RecoveryRequired)
        {
            throw new InvalidOperationException(
                $"The previous install requires recovery before packs can be switched. {health.Summary}");
        }

        var distinctManifestPaths = manifestPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    internal static bool HasPotentialArchiveCandidate(
        PfsArchive archive,
        string archivePath,
        UpscaleOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(options);

        return archive.Entries.Any(entry =>
            PfsTextureArchiveBuilder.IsPotentialCandidate(
                entry,
                options.MaximumDimension,
                options.Preset)
            && (options.Scope is not (
                    AssetScope.CharactersAndEquipmentOnly
                    or AssetScope.WorldCharactersAndEquipment)
                || CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    archivePath,
                    entry.Name)));
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

    private sealed record AuthorizedLauncherUpdateSource(
        string RelativeInstallPath,
        string LivePath,
        long Length,
        string Sha256);

    private sealed record LauncherUpdateRefreshContext(
        LauncherUpdateRefreshAssessment Assessment,
        InstallHealthReport? Health,
        InstallManifest? InstallManifest,
        LaunchPadUpdateEvidence? Evidence,
        IReadOnlyList<AuthorizedLauncherUpdateSource> UpdatedSources,
        IReadOnlyList<AdoptedOriginalArtifact> AuthorizedChanges,
        bool IsReconciliationResume,
        IReadOnlyList<LauncherUpdateLeafPlan> SelectionLeaves)
    {
        public static LauncherUpdateRefreshContext Blocked(
            LauncherUpdateRefreshAssessment assessment) => new(
                assessment,
                Health: null,
                InstallManifest: null,
                Evidence: null,
                UpdatedSources: [],
                AuthorizedChanges: [],
                IsReconciliationResume: false,
                SelectionLeaves: []);
    }

    private sealed record LauncherUpdateLeafPlan(
        string ManifestPath,
        bool RequiresRefresh);

    private sealed record LauncherUpdateSelectionPreflight(
        bool CanFocusedRefresh,
        string Summary,
        IReadOnlyList<LauncherUpdateLeafPlan> Leaves);

    private sealed record LauncherUpdateSourceRepairPlan(
        BuildOriginalSourceResolver OriginalSources,
        IReadOnlyDictionary<string, AuthorizedLauncherUpdateSource> UpdatedSources);

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
                if (HasPotentialArchiveCandidate(archive, archivePath, options))
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
                        options.Preset,
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
