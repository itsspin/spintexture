using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.Core.Services;

/// <summary>
/// Builds a small, genuine option preview from the selected client source or
/// its verified managed original when an enhanced pack is active. Preview work
/// is isolated to the profile cache and never writes to the live client or
/// staging library.
/// </summary>
public sealed class TextureOptionPreviewService
{
    private const int MaximumSamples = 3;
    private const int MaximumCandidatesPerCategory = 4;
    private const int MaximumArchivesToInspect = 24;
    private const int MinimumRepresentativeDimension = 64;
    private const int CacheSchemaVersion = 1;
    private const int MaximumCompletedCacheEntries = 12;
    private const string CacheFolderName = "OptionPreviews";
    private const string CacheManifestFileName = "preview.json";
    private const string WorkDirectoryPrefix = ".work-";
    private const string CacheKeyPrefix = "option-preview-v1";

    private static readonly JsonSerializerOptions CacheJsonOptions = CreateCacheJsonOptions();
    private static readonly string[] PreferredWorldZones =
    [
        "qeynos", "freportw", "freporte", "gfaydark", "felwithea", "neriaka",
        "rivervale", "lavastorm"
    ];

    private static readonly HashSet<string> UnsafeNameTokens = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "water", "wave", "liquid", "glass", "sky", "cloud", "lava", "fire",
        "flame", "smoke", "smke", "particle", "sprite", "palette", "atlas",
        "mask", "alpha", "opacity", "normal", "bump", "spec", "gloss", "ui",
        "hud", "font", "glyph", "cursor"
    };

    private static readonly string[] UnsafeNamePrefixes =
    [
        "water", "wave", "liquid", "glass", "sky", "cloud", "lava", "fire",
        "flame", "smoke", "smke"
    ];

    private static readonly HashSet<string> SurfaceTokens = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "wall", "brick", "stone", "rock", "floor", "ground", "terrain", "dirt",
        "road", "path", "cliff", "cave", "grass", "tile", "plaster", "marble",
        "wood", "sand", "snow"
    };

    private static readonly HashSet<string> StructureTokens = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "door", "roof", "building", "house", "arch", "column", "beam", "window",
        "gate", "bridge", "fence", "stair", "castle", "tower", "trim", "ceiling"
    };

    private static readonly HashSet<string> DetailTokens = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "crate", "barrel", "sign", "banner", "tapestry", "carpet", "cloth", "metal",
        "painting", "portrait", "shield", "armor", "book", "table", "chair", "chest"
    };

    private readonly ToolchainDiscovery toolchainDiscovery;
    private readonly TexturePackWorkflow workflow;
    private readonly TextureHeaderSniffer sniffer;
    private readonly TextureSemanticClassifier classifier;
    private readonly ConcurrentDictionary<string, Lazy<PreviewFlight>> flights =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim generationGate = new(1, 1);

    public TextureOptionPreviewService(ToolchainDiscovery? toolchainDiscovery = null)
        : this(
            toolchainDiscovery ?? new ToolchainDiscovery(),
            new TexturePackWorkflow(),
            new TextureHeaderSniffer(),
            new TextureSemanticClassifier())
    {
    }

    internal TextureOptionPreviewService(
        ToolchainDiscovery toolchainDiscovery,
        TexturePackWorkflow workflow,
        TextureHeaderSniffer sniffer,
        TextureSemanticClassifier classifier)
    {
        this.toolchainDiscovery = toolchainDiscovery
            ?? throw new ArgumentNullException(nameof(toolchainDiscovery));
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
        this.classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public async Task<TextureOptionPreviewResult> GenerateAsync(
        ProjectPaths paths,
        UpscaleOptions options,
        ScanSummary? analysis = null,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);

        var installValidation = EverQuestInstall.Validate(paths.InstallPath);
        if (!installValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, installValidation.Errors));
        }

        if (analysis is not null
            && !PathGuard.SamePath(analysis.InstallPath, installValidation.NormalizedPath))
        {
            throw new ArgumentException(
                "The supplied analysis belongs to a different EverQuest installation.",
                nameof(analysis));
        }

        EnsurePreviewWriteBoundary(paths);
        Directory.CreateDirectory(paths.WorkspacePath);
        var cacheRoot = GetCacheRoot(paths);
        Directory.CreateDirectory(cacheRoot);
        CleanupStaleWorkDirectoriesBestEffort(cacheRoot);
        var tools = toolchainDiscovery.Discover(paths);
        if (!tools.IsReady)
        {
            throw new FileNotFoundException(
                "SpinTexture's bundled processing tools are incomplete. "
                + string.Join(" ", tools.Diagnostics));
        }

        progress?.Report(new ProgressUpdate(
            "Preview",
            "Finding representative opaque textures in the selected client scope.",
            0,
            1,
            "Read-only source selection"));

        var originalSources = await workflow
            .PrepareBuildOriginalSourcesAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        var candidates = await FindCandidatesAsync(
                paths,
                options,
                originalSources,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            if (options.Scope == AssetScope.SpellEffectsOnly)
            {
                throw new InvalidOperationException(
                    "Live option preview is unavailable for Spell Effects because the conservative preview set intentionally excludes animated, translucent, cutout, and renderer-effect textures.");
            }

            throw new InvalidOperationException(
                "No conservative opaque color textures were found for a live preview in this scope.");
        }

        var toolSignature = await CreateToolSignatureAsync(
                tools,
                candidates,
                cancellationToken)
            .ConfigureAwait(false);
        var cacheKey = ComputeCacheKey(options, candidates, toolSignature);
        var cacheDirectory = GetCacheDirectory(paths, cacheKey);
        var cached = await TryReadCacheAsync(
                cacheRoot,
                cacheDirectory,
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            progress?.Report(new ProgressUpdate(
                "Preview",
                "The option preview is ready from the verified local cache.",
                1,
                1,
                $"{cached.Samples.Count} example(s)"));
            return cached;
        }

        var flightKey = Path.TrimEndingDirectorySeparator(cacheDirectory)
            .ToUpperInvariant();
        return await JoinFlightAsync(
                flightKey,
                token => GenerateAndPublishAsync(
                    paths,
                    options,
                    tools,
                    candidates,
                    cacheKey,
                    cacheRoot,
                    cacheDirectory,
                    progress,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TextureOptionPreviewResult> JoinFlightAsync(
        string flightKey,
        Func<CancellationToken, Task<TextureOptionPreviewResult>> operation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lazyFlight = flights.GetOrAdd(
                flightKey,
                _ => new Lazy<PreviewFlight>(
                    () => new PreviewFlight(operation),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var flight = lazyFlight.Value;
            var joinedTask = flight.TryJoinAsync(cancellationToken);
            if (joinedTask is null)
            {
                flights.TryRemove(
                    new KeyValuePair<string, Lazy<PreviewFlight>>(
                        flightKey,
                        lazyFlight));
                continue;
            }

            try
            {
                return await joinedTask.ConfigureAwait(false);
            }
            finally
            {
                if (flight.Task.IsCompleted)
                {
                    flights.TryRemove(
                        new KeyValuePair<string, Lazy<PreviewFlight>>(
                            flightKey,
                            lazyFlight));
                }
            }
        }
    }

    internal Task<TextureOptionPreviewResult> JoinFlightForTestAsync(
        string flightKey,
        Func<CancellationToken, Task<TextureOptionPreviewResult>> operation,
        CancellationToken cancellationToken = default) =>
        JoinFlightAsync(flightKey, operation, cancellationToken);

    private async Task<TextureOptionPreviewResult> GenerateAndPublishAsync(
        ProjectPaths paths,
        UpscaleOptions requestedOptions,
        ExternalToolPaths tools,
        IReadOnlyList<PreviewCandidate> candidates,
        string cacheKey,
        string cacheRoot,
        string cacheDirectory,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        await generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GenerateAndPublishExclusiveAsync(
                    paths,
                    requestedOptions,
                    tools,
                    candidates,
                    cacheKey,
                    cacheRoot,
                    cacheDirectory,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            generationGate.Release();
        }
    }

    private async Task<TextureOptionPreviewResult> GenerateAndPublishExclusiveAsync(
        ProjectPaths paths,
        UpscaleOptions requestedOptions,
        ExternalToolPaths tools,
        IReadOnlyList<PreviewCandidate> candidates,
        string cacheKey,
        string cacheRoot,
        string cacheDirectory,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var cached = await TryReadCacheAsync(
                cacheRoot,
                cacheDirectory,
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        if (Directory.Exists(cacheDirectory))
        {
            DeleteOwnedCacheDirectory(cacheRoot, cacheDirectory, requireCacheKey: true);
        }

        var workDirectory = Path.Combine(cacheRoot, $"{WorkDirectoryPrefix}{Guid.NewGuid():N}");
        _ = PathGuard.EnsurePathUnderRoot(cacheRoot, workDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var processor = new NativeTextureProcessor(tools);
            var selected = SelectProcessingCandidates(candidates);
            var successful = new List<GeneratedSample>(MaximumSamples);
            var cursor = 0;
            while (successful.Count < MaximumSamples && cursor < selected.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = MaximumSamples - successful.Count;
                var batch = selected.Skip(cursor).Take(Math.Max(remaining, 1)).ToArray();
                cursor += batch.Length;
                var pending = new List<PendingPreview>(batch.Length);
                foreach (var candidate in batch)
                {
                    var index = successful.Count + pending.Count;
                    var sourcePath = Path.Combine(
                        workDirectory,
                        $"candidate-{cursor:D2}-{index:D2}{GetPayloadExtension(candidate.Metadata.FileFormat)}");
                    var enhancedPath = Path.Combine(
                        workDirectory,
                        $"enhanced-{cursor:D2}-{index:D2}{GetPayloadExtension(candidate.Metadata.FileFormat)}");
                    await WriteBytesAsync(sourcePath, candidate.Payload, cancellationToken)
                        .ConfigureAwait(false);
                    pending.Add(new PendingPreview(
                        candidate,
                        sourcePath,
                        enhancedPath,
                        new NativeTextureProcessRequest(
                            sourcePath,
                            enhancedPath,
                            workDirectory,
                            candidate.Metadata,
                            candidate.Classification,
                            candidate.ResolvedOptions,
                            WrapEdges: candidate.WrapEdges)));
                }

                progress?.Report(new ProgressUpdate(
                    "Preview",
                    "Rendering the selected examples with the production texture pipeline.",
                    successful.Count,
                    MaximumSamples,
                    string.Join(", ", batch.Select(item => item.Title))));
                var outcomes = await processor.ProcessBatchAsync(
                        pending.Select(item => item.Request).ToArray(),
                        nativeProgress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (outcomes.Count != pending.Count)
                {
                    throw new InvalidDataException(
                        "The native preview batch returned an unexpected result count.");
                }

                for (var index = 0; index < pending.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = pending[index];
                    var outcome = outcomes[index];
                    if (!outcome.Succeeded || outcome.Result is null)
                    {
                        TryDeleteOwnedFile(workDirectory, item.SourcePath);
                        TryDeleteOwnedFile(workDirectory, item.EnhancedPath);
                        continue;
                    }

                    var outputMetadata = await sniffer
                        .ReadFileAsync(item.EnhancedPath, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        ValidateOutput(
                            item.Candidate,
                            outputMetadata,
                            outcome.Result,
                            requestedOptions.GenerateMipMaps);
                    }
                    catch (InvalidDataException)
                    {
                        TryDeleteOwnedFile(workDirectory, item.SourcePath);
                        TryDeleteOwnedFile(workDirectory, item.EnhancedPath);
                        continue;
                    }

                    var sampleIndex = successful.Count;
                    var originalFileName = $"{sampleIndex:D2}-original.png";
                    var enhancedFileName = $"{sampleIndex:D2}-enhanced.png";
                    var originalPngPath = Path.Combine(workDirectory, originalFileName);
                    var enhancedPngPath = Path.Combine(workDirectory, enhancedFileName);
                    try
                    {
                        await processor.CreatePngPreviewAsync(
                                item.SourcePath,
                                originalPngPath,
                                outcome.Result.Dimensions.OutputWidth,
                                outcome.Result.Dimensions.OutputHeight,
                                nearestNeighbor: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await processor.CreatePngPreviewAsync(
                                item.EnhancedPath,
                                enhancedPngPath,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is
                                                       IOException or
                                                       UnauthorizedAccessException or
                                                       InvalidDataException or
                                                       NativeProcessException)
                    {
                        TryDeleteOwnedFile(workDirectory, originalPngPath);
                        TryDeleteOwnedFile(workDirectory, enhancedPngPath);
                        TryDeleteOwnedFile(workDirectory, item.SourcePath);
                        TryDeleteOwnedFile(workDirectory, item.EnhancedPath);
                        continue;
                    }

                    var originalFingerprint = await FileIntegrity
                        .FingerprintAsync(originalPngPath, cancellationToken)
                        .ConfigureAwait(false);
                    var enhancedFingerprint = await FileIntegrity
                        .FingerprintAsync(enhancedPngPath, cancellationToken)
                        .ConfigureAwait(false);
                    await EnsurePngAsync(originalPngPath, cancellationToken)
                        .ConfigureAwait(false);
                    await EnsurePngAsync(enhancedPngPath, cancellationToken)
                        .ConfigureAwait(false);
                    successful.Add(new GeneratedSample(
                        item.Candidate,
                        outcome.Result,
                        outputMetadata!,
                        originalFileName,
                        enhancedFileName,
                        originalFingerprint.Length,
                        originalFingerprint.Sha256,
                        enhancedFingerprint.Length,
                        enhancedFingerprint.Sha256));
                    TryDeleteOwnedFile(workDirectory, item.SourcePath);
                    TryDeleteOwnedFile(workDirectory, item.EnhancedPath);
                    if (successful.Count == MaximumSamples)
                    {
                        break;
                    }
                }

                foreach (var item in pending)
                {
                    TryDeleteOwnedFile(workDirectory, item.SourcePath);
                    TryDeleteOwnedFile(workDirectory, item.EnhancedPath);
                }
            }

            if (successful.Count == 0)
            {
                throw new InvalidOperationException(
                    "The selected safe textures could not be rendered by the current native toolchain.");
            }

            var createdUtc = DateTimeOffset.UtcNow;
            var document = new PreviewCacheDocument(
                CacheSchemaVersion,
                cacheKey,
                createdUtc,
                requestedOptions.Preset,
                requestedOptions.PaintedTheme,
                successful.Select(item => ToCachedSample(item, requestedOptions)).ToArray());
            await WriteCacheDocumentAsync(
                    Path.Combine(workDirectory, CacheManifestFileName),
                    document,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.Move(workDirectory, cacheDirectory);
            }
            catch (IOException) when (Directory.Exists(cacheDirectory))
            {
                TryDeleteOwnedCacheDirectoryBestEffort(
                    cacheRoot,
                    workDirectory,
                    requireCacheKey: false);
                var raced = await TryReadCacheAsync(
                        cacheRoot,
                        cacheDirectory,
                        cacheKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (raced is not null)
                {
                    return raced;
                }

                throw;
            }

            progress?.Report(new ProgressUpdate(
                "Preview",
                "The live option preview is ready.",
                successful.Count,
                successful.Count,
                $"{successful.Count} client-derived example(s)"));
            var result = ToPublicResult(document, cacheDirectory);
            EvictCompletedCacheEntriesBestEffort(
                cacheRoot,
                flights.Keys.Select(key => Path.GetFileName(key)!).Append(cacheKey));
            return result;
        }
        finally
        {
            if (Directory.Exists(workDirectory))
            {
                TryDeleteOwnedCacheDirectoryBestEffort(
                    cacheRoot,
                    workDirectory,
                    requireCacheKey: false);
            }
        }
    }

    private async Task<IReadOnlyList<PreviewCandidate>> FindCandidatesAsync(
        ProjectPaths paths,
        UpscaleOptions options,
        TexturePackWorkflow.BuildOriginalSourceResolver originalSources,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var selectedArchives = TexturePackWorkflow.ResolveSelectedArchives(
            paths.InstallPath,
            options);
        var characterAndEquipmentArchives = TexturePackWorkflow
            .ResolveCharacterAndEquipmentArchives(paths.InstallPath)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderedArchives = PrioritizeArchives(
            paths.InstallPath,
            selectedArchives,
            options);
        var byCategory = new Dictionary<string, List<PreviewCandidate>>(
            StringComparer.Ordinal);
        var inspectedArchives = 0;
        foreach (var liveArchivePath in orderedArchives.Take(MaximumArchivesToInspect))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspectedArchives++;
            var relativePath = Path.GetRelativePath(paths.InstallPath, liveArchivePath);
            var isCharacterOrEquipmentArchive = characterAndEquipmentArchives.Contains(
                Path.GetFullPath(liveArchivePath));
            var source = await originalSources
                .ResolveAsync(relativePath, liveArchivePath, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(
                "Preview",
                "Inspecting original client art for a representative preview.",
                inspectedArchives,
                Math.Min(orderedArchives.Count, MaximumArchivesToInspect),
                Path.GetFileName(relativePath)));

            await using var archive = await PfsArchive.OpenAsync(
                    source.Path,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var protectedTranslucent = await ReadProtectedTranslucentNamesAsync(
                    archive,
                    cancellationToken)
                .ConfigureAwait(false);
            var preliminaries = archive.Entries
                .Where(entry =>
                    (options.Scope is not (AssetScope.CharactersAndEquipmentOnly
                        or AssetScope.WorldCharactersAndEquipment)
                     || CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                         relativePath,
                         entry.Name))
                    && IsPlausibleBeforeExtraction(
                        relativePath,
                        entry,
                        protectedTranslucent,
                        options.MaximumDimension))
                .Select(entry => new PreliminaryCandidate(
                    entry,
                    ClassifyCategory(
                        relativePath,
                        entry.Name,
                        isCharacterOrEquipmentArchive),
                    CalculatePreliminaryScore(entry)))
                .GroupBy(item => item.Category, StringComparer.Ordinal)
                .SelectMany(group => group
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumCandidatesPerCategory * 2))
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var preliminary in preliminaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await archive
                    .ReadEntryAsync(preliminary.Entry.Name, cancellationToken)
                    .ConfigureAwait(false);
                if (!sniffer.TryRead(payload, out var metadata)
                    || metadata is null)
                {
                    continue;
                }

                var classification = classifier.Classify(preliminary.Entry.Name, metadata);
                if (!IsSafeOpaqueCandidate(
                    relativePath,
                    preliminary.Entry.Name,
                    metadata,
                    classification,
                    payload,
                    protectedTranslucent,
                    options.MaximumDimension))
                {
                    continue;
                }

                var textureOverride = TextureOverridePolicy.Find(
                    options.TextureOverrides,
                    relativePath,
                    preliminary.Entry.Name);
                if (textureOverride?.Action == TextureOverrideAction.PreserveOriginal)
                {
                    continue;
                }

                var resolvedOptions = PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
                    options,
                    metadata,
                    preliminary.Entry.Name,
                    allowExplicitEffectArt: false);
                if (textureOverride is
                    {
                        Action: TextureOverrideAction.Reprocess,
                        Preset: { } selectedPreset
                    })
                {
                    resolvedOptions = options with { Preset = selectedPreset };
                }

                resolvedOptions = PaintedThemeResolver.ResolveForArtifact(
                    resolvedOptions,
                    relativePath);
                var wrapEdges = PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                    relativePath,
                    preliminary.Entry.Name,
                    classification,
                    isCharacterOrEquipmentArchive);
                var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
                var candidate = new PreviewCandidate(
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    preliminary.Entry.Name,
                    payload,
                    payloadSha256,
                    metadata,
                    classification,
                    resolvedOptions,
                    wrapEdges,
                    preliminary.Category,
                    CreateTitle(preliminary.Entry.Name),
                    preliminary.Score);
                candidate = candidate with
                {
                    SourceProvenance = source.ExpectedSha256 is not null
                        ? TextureOptionPreviewSourceProvenance.ManagedVerifiedOriginal
                        : TextureOptionPreviewSourceProvenance.ClientSource
                };
                if (!byCategory.TryGetValue(candidate.Category, out var categoryCandidates))
                {
                    categoryCandidates = [];
                    byCategory.Add(candidate.Category, categoryCandidates);
                }

                categoryCandidates.Add(candidate);
                categoryCandidates.Sort(CompareCandidates);
                if (categoryCandidates.Count > MaximumCandidatesPerCategory)
                {
                    categoryCandidates.RemoveRange(
                        MaximumCandidatesPerCategory,
                        categoryCandidates.Count - MaximumCandidatesPerCategory);
                }
            }

            if (byCategory.Count >= MaximumSamples
                && byCategory.Values.All(items => items.Count >= 2))
            {
                break;
            }
        }

        return byCategory
            .OrderBy(pair => GetCategoryOrder(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value)
            .ToArray();
    }

    private static async Task<IReadOnlySet<string>> ReadProtectedTranslucentNamesAsync(
        PfsArchive archive,
        CancellationToken cancellationToken)
    {
        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries
                     .Where(item => item.Name.EndsWith(".wld", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await archive.ReadEntryAsync(entry.Name, cancellationToken)
                .ConfigureAwait(false);
            protectedNames.UnionWith(
                LegacyTranslucentMaterialSafetyPolicy.FindProtectedTextureNames(payload));
        }

        return protectedNames;
    }

    internal static bool IsSafeOpaqueCandidate(
        string containerRelativePath,
        string logicalName,
        TextureMetadata metadata,
        TextureClassification classification,
        ReadOnlySpan<byte> payload,
        IReadOnlySet<string>? protectedTranslucentNames,
        int maximumDimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(classification);

        if (CelestialTextureSafetyPolicy.GetPreservedReason(
                containerRelativePath,
                logicalName) is not null
            || protectedTranslucentNames?.Contains(logicalName) == true
            || metadata.Width < MinimumRepresentativeDimension
            || metadata.Height < MinimumRepresentativeDimension
            || metadata.FileFormat == TextureFileFormat.Bmp && metadata.BitsPerPixel == 8
            || classification.Kind != TextureKind.Color
            || !classification.CanUseColorUpscaler
            || HasUnsafeNameToken(logicalName)
            || PfsTextureArchiveBuilder.IsLikelyAnimatedEffectName(logicalName)
            || PfsTextureArchiveBuilder.GetPreservedReason(
                metadata,
                classification,
                maximumDimension) is not null)
        {
            return false;
        }

        if (metadata.AlphaStatus == TextureAlphaStatus.None)
        {
            return true;
        }

        // DXT1 declares only possible alpha. Accept it solely when the real top
        // mip proves every visible pixel is opaque; unknown is fail-closed.
        return metadata.FileFormat == TextureFileFormat.Dds
            && TextureAlphaInspector.ClassifyTopMipAlpha(payload, metadata)
                == TextureTopMipAlphaKind.Opaque;
    }

    internal static bool IsSameAs2048(TextureMetadata metadata, UpscaleOptions options)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);
        var requested = UpscaleDimensions.Calculate(
            metadata.Width,
            metadata.Height,
            options.MaximumDimension);
        var at2048 = UpscaleDimensions.Calculate(metadata.Width, metadata.Height, 2048);
        var requestedMips = metadata.FileFormat == TextureFileFormat.Dds
            ? TextureMipPolicy.Calculate(
                requested.OutputWidth,
                requested.OutputHeight,
                options.GenerateMipMaps,
                useCutoutFloor: false)
            : 1;
        var at2048Mips = metadata.FileFormat == TextureFileFormat.Dds
            ? TextureMipPolicy.Calculate(
                at2048.OutputWidth,
                at2048.OutputHeight,
                options.GenerateMipMaps,
                useCutoutFloor: false)
            : 1;
        return requested.OutputWidth == at2048.OutputWidth
            && requested.OutputHeight == at2048.OutputHeight
            && requestedMips == at2048Mips;
    }

    internal static string ComputeDeterministicKeyForTest(params string[] parts) =>
        HashParts(parts);

    internal static string GetCacheDirectory(ProjectPaths paths, string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!IsSha256(cacheKey))
        {
            throw new ArgumentException("A preview cache key must be a lowercase SHA-256 value.", nameof(cacheKey));
        }

        return PathGuard.ResolveUnderRoot(GetCacheRoot(paths), cacheKey);
    }

    private static bool IsPlausibleBeforeExtraction(
        string relativePath,
        PfsArchiveEntry entry,
        IReadOnlySet<string> protectedTranslucent,
        int maximumDimension)
    {
        if (!entry.IsTexture
            || entry.Content.Kind is not (PfsContentKind.Dds
                or PfsContentKind.Bitmap
                or PfsContentKind.Targa)
            || entry.Content.Width < MinimumRepresentativeDimension
            || entry.Content.Height < MinimumRepresentativeDimension
            || Math.Max(entry.Content.Width, entry.Content.Height) >= maximumDimension
            || (double)Math.Max(entry.Content.Width, entry.Content.Height)
                / Math.Min(entry.Content.Width, entry.Content.Height) > 8
            || protectedTranslucent.Contains(entry.Name)
            || CelestialTextureSafetyPolicy.GetPreservedReason(relativePath, entry.Name) is not null
            || HasUnsafeNameToken(entry.Name)
            || PfsTextureArchiveBuilder.IsLikelyAnimatedEffectName(entry.Name))
        {
            return false;
        }

        return entry.Content.Kind != PfsContentKind.Bitmap
            || !entry.Content.PixelFormat.StartsWith("BMP8/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PreviewCandidate> SelectProcessingCandidates(
        IReadOnlyList<PreviewCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(item => GetCategoryOrder(item.Category))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.ContainerRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = new List<PreviewCandidate>(ordered.Length);
        var usedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Put one example from each semantic category first. Later candidates
        // remain deterministic fallbacks when a native fidelity gate rejects one.
        foreach (var group in ordered.GroupBy(item => item.Category, StringComparer.Ordinal))
        {
            var candidate = group.FirstOrDefault(item =>
                usedFamilies.Add(TexturePreviewCollector.CreateFamilyKey(item.LogicalName)));
            if (candidate is not null)
            {
                selected.Add(candidate);
            }
        }

        foreach (var candidate in ordered)
        {
            if (selected.Contains(candidate))
            {
                continue;
            }

            var family = TexturePreviewCollector.CreateFamilyKey(candidate.LogicalName);
            if (usedFamilies.Add(family) || selected.Count < MaximumSamples)
            {
                selected.Add(candidate);
            }
        }

        return selected;
    }

    private static IReadOnlyList<string> PrioritizeArchives(
        string installPath,
        IReadOnlyList<string> selectedArchives,
        UpscaleOptions options)
    {
        var zones = ZoneCatalog.Discover(installPath)
            .ToDictionary(zone => zone.Name, StringComparer.OrdinalIgnoreCase);
        return selectedArchives
            .OrderBy(path => GetArchivePriority(path, options, zones))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetArchivePriority(
        string path,
        UpscaleOptions options,
        IReadOnlyDictionary<string, ZoneAssetSet> zones)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var zoneStem = stem.EndsWith("_2_obj", StringComparison.OrdinalIgnoreCase)
            ? stem[..^"_2_obj".Length]
            : stem.EndsWith("_obj", StringComparison.OrdinalIgnoreCase)
                ? stem[..^"_obj".Length]
                : stem;
        if (options.Scope == AssetScope.SelectedZone
            || !string.IsNullOrWhiteSpace(options.SelectedZone)
                && zoneStem.Equals(options.SelectedZone, StringComparison.OrdinalIgnoreCase))
        {
            return stem.Equals(zoneStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        var preferredIndex = Array.FindIndex(
            PreferredWorldZones,
            item => item.Equals(zoneStem, StringComparison.OrdinalIgnoreCase));
        if (preferredIndex >= 0)
        {
            return 10 + (preferredIndex * 2)
                + (stem.Equals(zoneStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }

        return zones.ContainsKey(zoneStem)
            ? 100
            : 200;
    }

    private static int CalculatePreliminaryScore(PfsArchiveEntry entry)
    {
        var longest = Math.Max(entry.Content.Width, entry.Content.Height);
        var shortest = Math.Min(entry.Content.Width, entry.Content.Height);
        var score = Math.Min(longest, 2048) / 4;
        if (longest is >= 128 and <= 1024)
        {
            score += 300;
        }

        if ((double)longest / shortest <= 2)
        {
            score += 150;
        }

        var tokens = Tokenize(entry.Name);
        if (tokens.Overlaps(SurfaceTokens)
            || tokens.Overlaps(StructureTokens)
            || tokens.Overlaps(DetailTokens))
        {
            score += 250;
        }

        return score;
    }

    private static int CompareCandidates(PreviewCandidate left, PreviewCandidate right)
    {
        var score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        var container = StringComparer.OrdinalIgnoreCase.Compare(
            left.ContainerRelativePath,
            right.ContainerRelativePath);
        return container != 0
            ? container
            : StringComparer.OrdinalIgnoreCase.Compare(left.LogicalName, right.LogicalName);
    }

    private static string ClassifyCategory(
        string containerRelativePath,
        string logicalName,
        bool isCharacterOrEquipmentArchive)
    {
        var tokens = Tokenize(logicalName);
        if (tokens.Overlaps(SurfaceTokens))
        {
            return "World surfaces";
        }

        if (tokens.Overlaps(StructureTokens))
        {
            return "Architecture";
        }

        if (tokens.Overlaps(DetailTokens))
        {
            return "Material details";
        }

        if (isCharacterOrEquipmentArchive)
        {
            return "Character materials";
        }

        var container = Path.GetFileNameWithoutExtension(containerRelativePath);
        return container.Contains("_obj", StringComparison.OrdinalIgnoreCase)
            ? "Architecture"
            : "World surfaces";
    }

    private static int GetCategoryOrder(string category) => category switch
    {
        "World surfaces" => 0,
        "Architecture" => 1,
        "Material details" => 2,
        "Character materials" => 2,
        _ => 3
    };

    private static string CreateTitle(string logicalName)
    {
        var stem = Path.GetFileNameWithoutExtension(logicalName);
        var words = stem
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return "Client texture";
        }

        return string.Join(" ", words.Select(word =>
            word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static bool HasUnsafeNameToken(string logicalName)
    {
        var tokens = Tokenize(logicalName);
        if (tokens.Overlaps(UnsafeNameTokens))
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (UnsafeNamePrefixes.Any(prefix =>
                    token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var withoutDigits = token.TrimEnd(
                '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (UnsafeNameTokens.Contains(withoutDigits))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        foreach (var character in Path.ChangeExtension(value, null) ?? value)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static void ValidateOutput(
        PreviewCandidate candidate,
        TextureMetadata? output,
        NativeTextureProcessResult processResult,
        bool generateMipMaps)
    {
        if (output is null
            || output.FileFormat != candidate.Metadata.FileFormat
            || output.Width != processResult.Dimensions.OutputWidth
            || output.Height != processResult.Dimensions.OutputHeight)
        {
            throw new InvalidDataException(
                $"Preview output for {candidate.LogicalName} did not preserve its container and expected dimensions.");
        }

        var expectedMips = output.FileFormat == TextureFileFormat.Dds
            ? TextureMipPolicy.Calculate(
                output.Width,
                output.Height,
                generateMipMaps,
                processResult.UsedCutoutMipFloor)
            : 1;
        if (processResult.ExpectedMipCount != expectedMips)
        {
            throw new InvalidDataException(
                $"Preview output for {candidate.LogicalName} reported an invalid mip count.");
        }

        if (output.FileFormat == TextureFileFormat.Dds)
        {
            if (output.UsesDx10Header
                || !string.Equals(
                    output.TexconvFormat,
                    candidate.Metadata.TexconvFormat,
                    StringComparison.OrdinalIgnoreCase)
                || output.MipCount != expectedMips)
            {
                throw new InvalidDataException(
                    $"Preview output for {candidate.LogicalName} changed its legacy DDS representation.");
            }
        }
        else if (output.BitsPerPixel != candidate.Metadata.BitsPerPixel)
        {
            throw new InvalidDataException(
                $"Preview output for {candidate.LogicalName} changed its legacy pixel representation.");
        }
    }

    private static PreviewCacheSample ToCachedSample(
        GeneratedSample generated,
        UpscaleOptions requestedOptions)
    {
        var candidate = generated.Candidate;
        var route = generated.ProcessResult.ProcessingRoute;
        return new PreviewCacheSample(
            CreateSampleKey(candidate.ContainerRelativePath, candidate.LogicalName),
            candidate.Category,
            candidate.Title,
            candidate.ContainerRelativePath,
            candidate.LogicalName,
            generated.OriginalFileName,
            generated.EnhancedFileName,
            requestedOptions.Preset,
            candidate.ResolvedOptions.Preset,
            requestedOptions.PaintedTheme,
            candidate.ResolvedOptions.PaintedTheme,
            route,
            route.Contains("fallback", StringComparison.OrdinalIgnoreCase),
            candidate.Metadata.Width,
            candidate.Metadata.Height,
            generated.OutputMetadata.Width,
            generated.OutputMetadata.Height,
            generated.OutputMetadata.FileFormat == TextureFileFormat.Dds
                ? generated.OutputMetadata.MipCount
                : 1,
            IsSameAs2048(candidate.Metadata, requestedOptions),
            generated.OriginalPngLength,
            generated.OriginalPngSha256,
            generated.EnhancedPngLength,
            generated.EnhancedPngSha256,
            candidate.SourceProvenance);
    }

    private static string CreateSampleKey(string containerRelativePath, string logicalName) =>
        HashParts([containerRelativePath.ToLowerInvariant(), logicalName.ToLowerInvariant()])[..16];

    private static async Task<TextureOptionPreviewResult?> TryReadCacheAsync(
        string cacheRoot,
        string cacheDirectory,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        _ = PathGuard.EnsurePathUnderRoot(cacheRoot, cacheDirectory);
        var manifestPath = PathGuard.ResolveUnderRoot(
            cacheDirectory,
            CacheManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<PreviewCacheDocument>(
                    stream,
                    CacheJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (document is null
                || document.SchemaVersion != CacheSchemaVersion
                || !document.CacheKey.Equals(cacheKey, StringComparison.Ordinal)
                || document.Samples is not { Count: > 0 and <= MaximumSamples }
                || !IsValidCacheDocument(document))
            {
                return null;
            }

            foreach (var sample in document.Samples)
            {
                var originalPath = ResolveCacheImagePath(
                    cacheDirectory,
                    sample.OriginalFileName);
                var enhancedPath = ResolveCacheImagePath(
                    cacheDirectory,
                    sample.EnhancedFileName);
                await FileIntegrity.EnsureMatchesAsync(
                        originalPath,
                        sample.OriginalPngLength,
                        sample.OriginalPngSha256,
                        "Cached original preview image",
                        cancellationToken)
                    .ConfigureAwait(false);
                await FileIntegrity.EnsureMatchesAsync(
                        enhancedPath,
                        sample.EnhancedPngLength,
                        sample.EnhancedPngSha256,
                        "Cached enhanced preview image",
                        cancellationToken)
                    .ConfigureAwait(false);
                await EnsurePngAsync(originalPath, cancellationToken).ConfigureAwait(false);
                await EnsurePngAsync(enhancedPath, cancellationToken).ConfigureAwait(false);
            }

            return ToPublicResult(document, cacheDirectory);
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            return null;
        }
    }

    private static TextureOptionPreviewResult ToPublicResult(
        PreviewCacheDocument document,
        string cacheDirectory)
    {
        var samples = document.Samples.Select(sample =>
        {
            var originalPath = ResolveCacheImagePath(
                cacheDirectory,
                sample.OriginalFileName);
            var enhancedPath = ResolveCacheImagePath(
                cacheDirectory,
                sample.EnhancedFileName);
            if (!File.Exists(originalPath) || !File.Exists(enhancedPath))
            {
                throw new InvalidDataException("A cached option preview is missing an image.");
            }

            return new TextureOptionPreviewSample(
                sample.SampleKey,
                sample.Category,
                sample.Title,
                sample.ContainerRelativePath,
                sample.LogicalName,
                originalPath,
                enhancedPath,
                sample.RequestedPreset,
                sample.ResolvedPreset,
                sample.RequestedTheme,
                sample.ResolvedTheme,
                sample.ProcessingRoute,
                sample.UsedFallback,
                sample.SourceWidth,
                sample.SourceHeight,
                sample.ResultWidth,
                sample.ResultHeight,
                sample.ResultMipCount,
                sample.IsSameAs2048,
                sample.SourceProvenance);
        }).ToArray();
        return new TextureOptionPreviewResult(
            document.CacheKey,
            document.CreatedUtc,
            document.RequestedPreset,
            document.RequestedTheme,
            samples);
    }

    private static async Task WriteCacheDocumentAsync(
        string path,
        PreviewCacheDocument document,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
                stream,
                document,
                CacheJsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsValidCacheDocument(PreviewCacheDocument document)
    {
        if (!Enum.IsDefined(document.RequestedPreset)
            || !Enum.IsDefined(document.RequestedTheme)
            || document.RequestedPreset != TexturePreset.Illustrated
                && document.RequestedTheme != PaintedTheme.ClassicPainted)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in document.Samples)
        {
            if (!keys.Add(sample.SampleKey)
                || sample.SampleKey.Length != 16
                || sample.SampleKey.Any(character =>
                    character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || string.IsNullOrWhiteSpace(sample.Category)
                || sample.Category.Length > 80
                || string.IsNullOrWhiteSpace(sample.Title)
                || sample.Title.Length > 160
                || string.IsNullOrWhiteSpace(sample.ContainerRelativePath)
                || Path.IsPathFullyQualified(sample.ContainerRelativePath)
                || sample.ContainerRelativePath.Split('/', '\\').Any(segment => segment is "." or "..")
                || string.IsNullOrWhiteSpace(sample.LogicalName)
                || string.IsNullOrWhiteSpace(sample.ProcessingRoute)
                || sample.ProcessingRoute.Length > 512
                || !Enum.IsDefined(sample.RequestedPreset)
                || !Enum.IsDefined(sample.ResolvedPreset)
                || !Enum.IsDefined(sample.RequestedTheme)
                || !Enum.IsDefined(sample.ResolvedTheme)
                || !Enum.IsDefined(sample.SourceProvenance)
                || sample.RequestedPreset != document.RequestedPreset
                || sample.RequestedTheme != document.RequestedTheme
                || sample.UsedFallback != sample.ProcessingRoute.Contains(
                    "fallback",
                    StringComparison.OrdinalIgnoreCase)
                || sample.SourceWidth <= 0
                || sample.SourceHeight <= 0
                || sample.ResultWidth < sample.SourceWidth
                || sample.ResultHeight < sample.SourceHeight
                || sample.ResultWidth > 4096
                || sample.ResultHeight > 4096
                || sample.ResultMipCount <= 0
                || sample.ResultMipCount > 13
                || sample.OriginalPngLength <= 8
                || sample.EnhancedPngLength <= 8
                || !IsSha256(sample.OriginalPngSha256)
                || !IsSha256(sample.EnhancedPngSha256))
            {
                return false;
            }

            try
            {
                _ = SafePath.RequireFileName(
                    sample.OriginalFileName,
                    nameof(sample.OriginalFileName));
                _ = SafePath.RequireFileName(
                    sample.EnhancedFileName,
                    nameof(sample.EnhancedFileName));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    internal static void EnsurePreviewWriteBoundary(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var cacheRoot = GetCacheRoot(paths);
        if (PathsOverlap(paths.InstallPath, cacheRoot)
            || PathsOverlap(paths.StagingPath, cacheRoot))
        {
            throw new InvalidDataException(
                "The option-preview cache must be isolated from the live client and staged-pack library.");
        }
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(root),
            Path.GetFullPath(candidate));
        return relative.Equals(".", StringComparison.Ordinal)
            || (!Path.IsPathFullyQualified(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    private static string ResolveCacheImagePath(string cacheDirectory, string fileName)
    {
        var safeFileName = SafePath.RequireFileName(fileName, nameof(fileName));
        return PathGuard.ResolveUnderRoot(cacheDirectory, safeFileName);
    }

    private static async Task EnsurePngAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] signature = new byte[8];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytesRead = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> expectedSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytesRead != signature.Length
            || !signature.AsSpan().SequenceEqual(expectedSignature))
        {
            throw new InvalidDataException("A cached preview image is not a PNG file.");
        }
    }

    private static async Task WriteBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeCacheKey(
        UpscaleOptions options,
        IReadOnlyList<PreviewCandidate> candidates,
        string toolSignature)
    {
        var parts = new List<string>
        {
            CacheKeyPrefix,
            TextureProcessingPipeline.CurrentRevision.ToString(CultureInfo.InvariantCulture),
            TextureBuildReport.CurrentPaintedProfileRevision.ToString(CultureInfo.InvariantCulture),
            options.Preset.ToString(),
            options.Scope.ToString(),
            options.MaximumDimension.ToString(CultureInfo.InvariantCulture),
            options.GenerateMipMaps ? "mips" : "top-only",
            options.SelectedZone?.Trim().ToLowerInvariant() ?? string.Empty,
            options.PaintedTheme.ToString(),
            SerializeOverrides(options.TextureOverrides),
            toolSignature
        };
        foreach (var candidate in candidates
                     .OrderBy(item => item.ContainerRelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(candidate.ContainerRelativePath.ToLowerInvariant());
            parts.Add(candidate.LogicalName.ToLowerInvariant());
            parts.Add(candidate.PayloadSha256);
            parts.Add(candidate.ResolvedOptions.Preset.ToString());
            parts.Add(candidate.ResolvedOptions.PaintedTheme.ToString());
            parts.Add(candidate.WrapEdges ? "wrap" : "clamp");
            parts.Add(candidate.SourceProvenance.ToString());
        }

        return HashParts(parts);
    }

    private static string SerializeOverrides(IReadOnlyList<TextureOverride>? overrides)
    {
        if (overrides is not { Count: > 0 })
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            overrides
                .OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase)
                .Select(item => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{item.ArchivePath.ToLowerInvariant()}:{item.LogicalName.ToLowerInvariant()}:{item.Action}:{item.Preset}")));
    }

    private static async Task<string> CreateToolSignatureAsync(
        ExternalToolPaths tools,
        IReadOnlyList<PreviewCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRequiredFile(files, tools.TexconvPath);
        AddRequiredFile(files, tools.RealEsrganPath);
        if (tools.RealEsrganModelsPath is not null)
        {
            var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                RealEsrganCommandBuilder.LegacyFaithfulModelName
            };
            foreach (var preset in candidates.Select(item => item.ResolvedOptions.Preset).Distinct())
            {
                var effectivePreset = preset == TexturePreset.ClassicHd
                    ? TexturePreset.Faithful
                    : preset;
                modelNames.Add(new RealEsrganCommandBuilder()
                    .ResolveModel(tools.RealEsrganModelsPath, effectivePreset)
                    .Name);
            }

            foreach (var modelName in modelNames)
            {
                AddRequiredFile(files, Path.Combine(tools.RealEsrganModelsPath, $"{modelName}.param"));
                AddRequiredFile(files, Path.Combine(tools.RealEsrganModelsPath, $"{modelName}.bin"));
            }
        }

        if (candidates.Any(item => item.ResolvedOptions.Preset == TexturePreset.ClassicHd)
            && tools.HasTextureUpscaler)
        {
            AddRequiredFile(files, tools.TextureUpscalerPath);
            var selection = new UpscaylCommandBuilder().FindTextureModel(tools.TextureModelsPath!);
            if (selection is not null)
            {
                AddRequiredFile(files, Path.Combine(tools.TextureModelsPath!, $"{selection.Name}.param"));
                AddRequiredFile(files, Path.Combine(tools.TextureModelsPath!, $"{selection.Name}.bin"));
            }
        }

        var parts = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = await FileIntegrity.FingerprintAsync(file, cancellationToken)
                .ConfigureAwait(false);
            parts.Add(Path.GetFileName(file).ToLowerInvariant());
            parts.Add(fingerprint.Length.ToString(CultureInfo.InvariantCulture));
            parts.Add(fingerprint.Sha256);
        }

        return HashParts(parts);
    }

    private static void AddRequiredFile(ISet<string> files, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("A required preview tool or model is missing.", path);
        }

        files.Add(Path.GetFullPath(path));
    }

    private static string HashParts(IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string GetCacheRoot(ProjectPaths paths)
    {
        var root = Path.Combine(paths.CachePath, CacheFolderName);
        var descendant = SafePath.RequireDescendant(
            paths.WorkspacePath,
            root,
            nameof(paths));
        return Directory.Exists(paths.WorkspacePath)
            ? PathGuard.EnsurePathUnderRoot(paths.WorkspacePath, descendant)
            : descendant;
    }

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void DeleteOwnedCacheDirectory(
        string cacheRoot,
        string targetDirectory,
        bool requireCacheKey)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var relative = Path.GetRelativePath(root, target);
        var validName = requireCacheKey
            ? IsSha256(relative)
            : relative.StartsWith(WorkDirectoryPrefix, StringComparison.Ordinal)
                && relative.Length == WorkDirectoryPrefix.Length + 32;
        if (!validName
            || relative.Contains(Path.DirectorySeparatorChar)
            || relative.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException("Refusing to remove an unowned preview cache directory.");
        }

        DeleteDirectoryWithoutFollowingReparsePoints(root, target);
    }

    private static void TryDeleteOwnedCacheDirectoryBestEffort(
        string cacheRoot,
        string targetDirectory,
        bool requireCacheKey)
    {
        try
        {
            DeleteOwnedCacheDirectory(cacheRoot, targetDirectory, requireCacheKey);
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException)
        {
            // Work/cache cleanup must not replace the original result,
            // especially caller cancellation. Stale work is retried later.
        }
    }

    internal static void EvictCompletedCacheEntriesBestEffort(
        string cacheRoot,
        IEnumerable<string> excludedKeys)
    {
        try
        {
            var excluded = excludedKeys.ToHashSet(StringComparer.Ordinal);
            var completed = Directory.EnumerateDirectories(
                    cacheRoot,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Key = Path.GetFileName(path),
                    LastWriteUtc = Directory.GetLastWriteTimeUtc(path)
                })
                .Where(item => IsSha256(item.Key)
                    && !IsReparsePoint(item.Path)
                    && File.Exists(Path.Combine(item.Path, CacheManifestFileName)))
                .OrderBy(item => item.LastWriteUtc)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            var removeCount = Math.Max(0, completed.Length - MaximumCompletedCacheEntries);
            foreach (var entry in completed
                         .Where(item => !excluded.Contains(item.Key))
                         .Take(removeCount))
            {
                DeleteOwnedCacheDirectory(cacheRoot, entry.Path, requireCacheKey: true);
            }
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException)
        {
            // Cache eviction never changes preview correctness. A later publish
            // will retry the same bounded, cache-owned cleanup.
        }
    }

    private static void CleanupStaleWorkDirectoriesBestEffort(string cacheRoot)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateDirectories(
                         cacheRoot,
                         $"{WorkDirectoryPrefix}*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.Length != WorkDirectoryPrefix.Length + 32
                    || Directory.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                DeleteOwnedCacheDirectory(cacheRoot, path, requireCacheKey: false);
            }
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException)
        {
            // A current operation never uses a day-old work directory. Stale
            // cleanup remains best effort and never affects preview generation.
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void DeleteDirectoryWithoutFollowingReparsePoints(
        string cacheRoot,
        string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        var attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(target, recursive: false);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(
                     target,
                     "*",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = false,
                         IgnoreInaccessible = false,
                         AttributesToSkip = 0,
                         ReturnSpecialDirectories = false
                     }))
        {
            var fullChild = Path.GetFullPath(child);
            var relative = Path.GetRelativePath(cacheRoot, fullChild);
            if (Path.IsPathFullyQualified(relative)
                || relative is "." or ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Refusing to remove a preview cache entry outside its cache root.");
            }

            var childAttributes = File.GetAttributes(fullChild);
            if ((childAttributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(cacheRoot, fullChild);
            }
            else
            {
                File.Delete(fullChild);
            }
        }

        Directory.Delete(target, recursive: false);
    }

    private static void TryDeleteOwnedFile(string workDirectory, string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory));
            var target = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, target);
            if (Path.IsPathFullyQualified(relative)
                || relative is "." or ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar)
                || relative.Contains(Path.AltDirectorySeparatorChar))
            {
                return;
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The work directory is removed as one owned unit in the outer finally.
        }
    }

    private static string GetPayloadExtension(TextureFileFormat format) => format switch
    {
        TextureFileFormat.Dds => ".dds",
        TextureFileFormat.Bmp => ".bmp",
        TextureFileFormat.Tga => ".tga",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static void ValidateOptions(UpscaleOptions options)
    {
        if (options.MaximumDimension is not (1024 or 2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The texture cap must be 1024, 2048, or 4096 pixels.");
        }

        if (!Enum.IsDefined(options.Preset)
            || !Enum.IsDefined(options.Scope)
            || !Enum.IsDefined(options.PaintedTheme)
            || options.Preset != TexturePreset.Illustrated
                && options.PaintedTheme != PaintedTheme.ClassicPainted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The selected preview options are not supported.");
        }

        TextureOverridePolicy.ValidateAll(options.TextureOverrides);
    }

    private static JsonSerializerOptions CreateCacheJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    internal sealed class PreviewFlight
    {
        private readonly CancellationTokenSource operationCancellation = new();
        private readonly object gate = new();
        private int waiters;
        private bool taskCompleted;
        private bool cancellationDisposed;
        private bool cancellationInProgress;
        private bool acceptingWaiters = true;

        public PreviewFlight(Func<CancellationToken, Task<TextureOptionPreviewResult>> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            Task = operation(operationCancellation.Token);
            _ = Task.ContinueWith(
                _ => MarkCompleted(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<TextureOptionPreviewResult> Task { get; }

        public Task<TextureOptionPreviewResult> JoinAsync(
            CancellationToken cancellationToken) =>
            TryJoinAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The preview operation is no longer accepting callers.");

        internal Task<TextureOptionPreviewResult>? TryJoinAsync(
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!acceptingWaiters)
                {
                    return null;
                }

                waiters++;
            }

            return JoinRegisteredAsync(cancellationToken);
        }

        private async Task<TextureOptionPreviewResult> JoinRegisteredAsync(
            CancellationToken cancellationToken)
        {
            var released = false;
            try
            {
                return await Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                released = true;
                var canceledOperation = ReleaseWaiter();
                if (canceledOperation)
                {
                    try
                    {
                        await Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The shared operation is now fully drained.
                    }
                    catch
                    {
                        // Preserve the caller's cancellation as the public result.
                    }
                }

                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                if (!released)
                {
                    _ = ReleaseWaiter();
                }
            }
        }

        private bool ReleaseWaiter()
        {
            var cancel = false;
            var dispose = false;
            lock (gate)
            {
                waiters--;
                cancel = waiters == 0 && !taskCompleted;
                if (cancel)
                {
                    acceptingWaiters = false;
                    cancellationInProgress = true;
                }

                dispose = waiters == 0
                    && taskCompleted
                    && !cancellationInProgress
                    && !cancellationDisposed;
                if (dispose)
                {
                    cancellationDisposed = true;
                }
            }

            if (cancel)
            {
                try
                {
                    operationCancellation.Cancel();
                }
                finally
                {
                    lock (gate)
                    {
                        cancellationInProgress = false;
                        dispose = taskCompleted
                            && waiters == 0
                            && !cancellationDisposed;
                        if (dispose)
                        {
                            cancellationDisposed = true;
                        }
                    }
                }
            }

            if (dispose)
            {
                operationCancellation.Dispose();
            }

            return cancel;
        }

        private void MarkCompleted()
        {
            var dispose = false;
            lock (gate)
            {
                taskCompleted = true;
                dispose = waiters == 0
                    && !cancellationInProgress
                    && !cancellationDisposed;
                if (dispose)
                {
                    cancellationDisposed = true;
                }
            }

            if (dispose)
            {
                operationCancellation.Dispose();
            }
        }
    }

    private sealed record PreliminaryCandidate(
        PfsArchiveEntry Entry,
        string Category,
        int Score);

    private sealed record PreviewCandidate(
        string ContainerRelativePath,
        string LogicalName,
        byte[] Payload,
        string PayloadSha256,
        TextureMetadata Metadata,
        TextureClassification Classification,
        UpscaleOptions ResolvedOptions,
        bool WrapEdges,
        string Category,
        string Title,
        int Score)
    {
        public TextureOptionPreviewSourceProvenance SourceProvenance { get; init; }
    }

    private sealed record PendingPreview(
        PreviewCandidate Candidate,
        string SourcePath,
        string EnhancedPath,
        NativeTextureProcessRequest Request);

    private sealed record GeneratedSample(
        PreviewCandidate Candidate,
        NativeTextureProcessResult ProcessResult,
        TextureMetadata OutputMetadata,
        string OriginalFileName,
        string EnhancedFileName,
        long OriginalPngLength,
        string OriginalPngSha256,
        long EnhancedPngLength,
        string EnhancedPngSha256);

    private sealed record PreviewCacheDocument(
        int SchemaVersion,
        string CacheKey,
        DateTimeOffset CreatedUtc,
        TexturePreset RequestedPreset,
        PaintedTheme RequestedTheme,
        IReadOnlyList<PreviewCacheSample> Samples);

    private sealed record PreviewCacheSample(
        string SampleKey,
        string Category,
        string Title,
        string ContainerRelativePath,
        string LogicalName,
        string OriginalFileName,
        string EnhancedFileName,
        TexturePreset RequestedPreset,
        TexturePreset ResolvedPreset,
        PaintedTheme RequestedTheme,
        PaintedTheme ResolvedTheme,
        string ProcessingRoute,
        bool UsedFallback,
        int SourceWidth,
        int SourceHeight,
        int ResultWidth,
        int ResultHeight,
        int ResultMipCount,
        bool IsSameAs2048,
        long OriginalPngLength,
        string OriginalPngSha256,
        long EnhancedPngLength,
        string EnhancedPngSha256,
        TextureOptionPreviewSourceProvenance SourceProvenance);
}
