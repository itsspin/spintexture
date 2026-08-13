using SpinTexture.Core.Archives;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.Core.Services;

public sealed class PfsTextureArchiveBuilder : IStagedArtifactBuilder, IStagedArtifactCheckpointParticipant
{
    private static readonly HashSet<string> SafeLegacyDdsFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "BC1_UNORM", "BC2_UNORM", "BC3_UNORM",
        "B8G8R8A8_UNORM", "B8G8R8X8_UNORM"
    };

    private readonly NativeTextureProcessor processor;
    private readonly TextureHeaderSniffer sniffer;
    private readonly TextureSemanticClassifier classifier;
    private readonly TextureBuildCounter counter;
    private readonly TexturePreviewCollector? previewCollector;
    private readonly IProgress<ProgressUpdate>? progress;
    private readonly HashSet<string> clampArchiveNames;
    private readonly bool filterCharacterEquipmentEntries;
    private readonly bool retryUnchangedEntries;
    private readonly bool rebuildFromReuseArchive;
    private readonly bool requireRequestedVisualProfile;
    private readonly bool allowRequestedVisualProfileRegeneration;
    private readonly IReadOnlyDictionary<string, string> reuseArchivePaths;
    private readonly IReadOnlyDictionary<string, StagedPackFileFingerprint> reuseArchiveFingerprints;
    private readonly IStagedPackPayloadMaterializer reuseMaterializer;
    private readonly Func<
        IReadOnlyList<NativeTextureProcessRequest>,
        CancellationToken,
        Task<IReadOnlyList<NativeTextureProcessOutcome>>>? processBatch;

    internal PfsTextureArchiveBuilder(
        NativeTextureProcessor processor,
        TextureBuildCounter counter,
        IProgress<ProgressUpdate>? progress = null,
        TexturePreviewCollector? previewCollector = null,
        TextureHeaderSniffer? sniffer = null,
        TextureSemanticClassifier? classifier = null,
        IEnumerable<string>? clampArchivePaths = null,
        bool filterCharacterEquipmentEntries = false,
        bool retryUnchangedEntries = true,
        bool rebuildFromReuseArchive = false,
        bool requireRequestedVisualProfile = false,
        bool allowRequestedVisualProfileRegeneration = false,
        IReadOnlyDictionary<string, string>? reuseArchivePaths = null,
        IReadOnlyDictionary<string, StagedPackFileFingerprint>? reuseArchiveFingerprints = null,
        IStagedPackPayloadMaterializer? reuseMaterializer = null,
        Func<
            IReadOnlyList<NativeTextureProcessRequest>,
            CancellationToken,
            Task<IReadOnlyList<NativeTextureProcessOutcome>>>? processBatch = null)
    {
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.counter = counter ?? throw new ArgumentNullException(nameof(counter));
        this.progress = progress;
        this.previewCollector = previewCollector;
        this.sniffer = sniffer ?? new TextureHeaderSniffer();
        this.classifier = classifier ?? new TextureSemanticClassifier();
        clampArchiveNames = new HashSet<string>(
            clampArchivePaths?.Select(path => Path.GetFileName(path)!) ?? [],
            StringComparer.OrdinalIgnoreCase);
        this.filterCharacterEquipmentEntries = filterCharacterEquipmentEntries;
        this.retryUnchangedEntries = retryUnchangedEntries;
        this.rebuildFromReuseArchive = rebuildFromReuseArchive;
        this.requireRequestedVisualProfile = requireRequestedVisualProfile;
        this.allowRequestedVisualProfileRegeneration = allowRequestedVisualProfileRegeneration;
        this.reuseArchivePaths = reuseArchivePaths
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        this.reuseArchiveFingerprints = reuseArchiveFingerprints
            ?? new Dictionary<string, StagedPackFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        this.reuseMaterializer = reuseMaterializer
            ?? new HardLinkOrCopyStagedPackPayloadMaterializer();
        this.processBatch = processBatch;
    }

    public async Task BuildAsync(
        StagedArtifactBuildContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.WorkingDirectory);

        // sky.s3d is a single renderer-coupled unit: its WLD, palette, color
        // keys, cloud layers, and celestial billboards must stay byte-for-byte
        // aligned. Copying the verified source as one artifact is both safer
        // and faster than rebuilding it member by member.
        if (CelestialTextureSafetyPolicy.GetPreservedReason(
                context.RelativeInstallPath,
                "sky.wld") is not null)
        {
            counter.Preserve(CelestialTextureSafetyPolicy.LegacySkyArchivePreservedReason);
            await CopyAsync(context.SourcePath, context.DestinationPath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var source = await PfsArchive.OpenAsync(
            context.SourcePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        PfsArchive? reuseArchive = null;
        if (reuseArchivePaths.TryGetValue(context.RelativeInstallPath, out var reuseArchivePath))
        {
            if (rebuildFromReuseArchive)
            {
                if (!reuseArchiveFingerprints.TryGetValue(
                        context.RelativeInstallPath,
                        out var expectedReuse))
                {
                    throw new InvalidDataException(
                        $"Targeted cutout repair is missing the exact baseline fingerprint for {context.RelativeInstallPath}.");
                }

                var lockedReuseStream = new FileStream(
                    reuseArchivePath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        BufferSize = 128 * 1024,
                        Options = FileOptions.Asynchronous | FileOptions.RandomAccess
                    });
                try
                {
                    // The first handle denies write/delete sharing. Hash the
                    // path while that handle is held, then transfer ownership
                    // of the same locked bytes to PfsArchive.
                    await FileIntegrity.EnsureMatchesAsync(
                            reuseArchivePath,
                            expectedReuse.Length,
                            expectedReuse.Sha256,
                            "Locked reusable staged archive",
                            cancellationToken)
                        .ConfigureAwait(false);
                    reuseArchive = await PfsArchive.OpenAsync(
                        lockedReuseStream,
                        Path.GetFileName(reuseArchivePath),
                        leaveOpen: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    lockedReuseStream = null!;
                }
                finally
                {
                    if (lockedReuseStream is not null)
                    {
                        await lockedReuseStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            else
            {
                reuseArchive = await PfsArchive.OpenAsync(
                    reuseArchivePath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            ValidateReusableArchiveLayout(source, reuseArchive, context.RelativeInstallPath);
        }
        else if (rebuildFromReuseArchive)
        {
            throw new InvalidDataException(
                $"Targeted cutout repair is missing its verified baseline archive for {context.RelativeInstallPath}.");
        }

        await using var reuseArchiveLifetime = reuseArchive;
        var replacements = new List<PfsArchiveReplacement>();
        var expectedOutputs = new Dictionary<string, ExpectedTexture>(StringComparer.OrdinalIgnoreCase);
        var pendingTextures = new List<PendingTexture>();
        var protectedLegacyTranslucentTextures = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var wldEntry in source.Entries.Where(entry =>
                     entry.Name.EndsWith(".wld", StringComparison.OrdinalIgnoreCase)))
        {
            var wldPayload = await source.ReadEntryAsync(wldEntry.Name, cancellationToken)
                .ConfigureAwait(false);
            protectedLegacyTranslucentTextures.UnionWith(
                LegacyTranslucentMaterialSafetyPolicy.FindProtectedTextureNames(wldPayload));
        }
        var textureEntries = source.Entries
            .Where(entry => entry.IsTexture
                && (rebuildFromReuseArchive
                    || IsEntryAllowed(context.RelativeInstallPath, entry.Name)))
            .ToArray();
        var sourceTexturePaths = new List<string>();
        var enhancedTexturePaths = new List<string>();
        var textureOverrides = TextureOverridePolicy.CreateLookup(
            context.Options.TextureOverrides);
        if (rebuildFromReuseArchive)
        {
            if (new FileInfo(reuseArchivePath!).Length > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"The verified baseline archive exceeds the legacy PFS 4 GiB limit: {context.RelativeInstallPath}.");
            }
        }
        else
        {
            EnsureArchiveSizeBudget(
                context.SourcePath,
                textureEntries
                    .Where(entry => IsEntryAllowed(context.RelativeInstallPath, entry.Name))
                    .ToArray(),
                context.Options.MaximumDimension);
        }

        try
        {
            for (var index = 0; index < textureEntries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = textureEntries[index];
                var entryAllowed = IsEntryAllowed(
                    context.RelativeInstallPath,
                    entry.Name);
                if (entryAllowed)
                {
                    counter.Discover(entry.UncompressedSize);
                }
                progress?.Report(new ProgressUpdate(
                    "Upscale",
                    $"Preparing safe textures in {Path.GetFileName(context.SourcePath)} for batched enhancement.",
                    index,
                    textureEntries.Length,
                    entry.Name));

                var sourceTexturePath = Path.Combine(
                    context.WorkingDirectory,
                    $"{index:D5}-source{GetPayloadExtension(entry.Content.Kind)}");
                sourceTexturePaths.Add(sourceTexturePath);
                await ExtractEntryToFileAsync(
                    source,
                    entry,
                    sourceTexturePath,
                    cancellationToken).ConfigureAwait(false);
                var textureOverride = TextureOverridePolicy.Find(
                    textureOverrides,
                    context.RelativeInstallPath,
                    entry.Name);
                var celestialPreservedReason = CelestialTextureSafetyPolicy.GetPreservedReason(
                    context.RelativeInstallPath,
                    entry.Name);
                if (celestialPreservedReason is not null)
                {
                    // Renderer-coupled sky assets are a hard safety boundary.
                    // Even an explicit Reprocess override cannot change their
                    // dimensions, palette, color key, or sprite border.
                    counter.Preserve(celestialPreservedReason);
                    var restored = await RestoreOriginalIfBaselineChangedAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        context.WorkingDirectory,
                        index,
                        replacements,
                        enhancedTexturePaths,
                        cancellationToken).ConfigureAwait(false);
                    if (!restored)
                    {
                        TryDeleteTemporaryFile(sourceTexturePath);
                    }

                    continue;
                }

                if (protectedLegacyTranslucentTextures.Contains(entry.Name))
                {
                    // WLD material flags, animation frames, and bitmap sizes
                    // form one blend contract. Preserve the verified original
                    // even when an explicit Reprocess override is present.
                    counter.Preserve(LegacyTranslucentMaterialSafetyPolicy.PreservedReason);
                    var restored = await RestoreOriginalIfBaselineChangedAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        context.WorkingDirectory,
                        index,
                        replacements,
                        enhancedTexturePaths,
                        cancellationToken).ConfigureAwait(false);
                    if (!restored)
                    {
                        TryDeleteTemporaryFile(sourceTexturePath);
                    }

                    continue;
                }

                if (textureOverride?.Action == TextureOverrideAction.PreserveOriginal)
                {
                    counter.Preserve("User selected original texture");
                    var restored = await RestoreOriginalIfBaselineChangedAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        context.WorkingDirectory,
                        index,
                        replacements,
                        enhancedTexturePaths,
                        cancellationToken).ConfigureAwait(false);
                    if (!restored)
                    {
                        TryDeleteTemporaryFile(sourceTexturePath);
                    }

                    continue;
                }
                var forceReprocess = textureOverride?.Action == TextureOverrideAction.Reprocess;
                if (!entryAllowed)
                {
                    await ThrowIfPaintedRepairWouldRestoreChangedOriginalAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        "the texture is outside the current character/equipment allowlist",
                        cancellationToken).ConfigureAwait(false);
                    var restored = await RestoreOriginalIfBaselineChangedAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        context.WorkingDirectory,
                        index,
                        replacements,
                        enhancedTexturePaths,
                        cancellationToken).ConfigureAwait(false);
                    if (!restored)
                    {
                        TryDeleteTemporaryFile(sourceTexturePath);
                    }

                    continue;
                }

                var metadata = await sniffer.ReadFileAsync(
                    sourceTexturePath,
                    cancellationToken).ConfigureAwait(false);
                if (metadata is null)
                {
                    await ThrowIfPaintedRepairWouldRestoreChangedOriginalAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        "the prior painted texture is no longer a supported image payload",
                        cancellationToken).ConfigureAwait(false);
                    counter.Preserve("Unsupported image payload");
                    var restored = await RestoreOriginalIfBaselineChangedAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        context.WorkingDirectory,
                        index,
                        replacements,
                        enhancedTexturePaths,
                        cancellationToken).ConfigureAwait(false);
                    if (!restored)
                    {
                        TryDeleteTemporaryFile(sourceTexturePath);
                    }

                    continue;
                }

                byte[]? sourcePayload = null;
                if (metadata.HasAlpha && metadata.FileFormat == TextureFileFormat.Dds)
                {
                    sourcePayload = await File.ReadAllBytesAsync(
                            sourceTexturePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (TextureAlphaInspector.IsKnownFullyTransparent(
                            sourcePayload,
                            metadata))
                    {
                        await ThrowIfPaintedRepairWouldRestoreChangedOriginalAsync(
                            reuseArchive,
                            entry,
                            sourceTexturePath,
                            "the texture is now classified as a fully transparent renderer control",
                            cancellationToken).ConfigureAwait(false);
                        counter.Preserve("Fully transparent renderer-control texture");
                        var restored = await RestoreOriginalIfBaselineChangedAsync(
                            reuseArchive,
                            entry,
                            sourceTexturePath,
                            context.WorkingDirectory,
                            index,
                            replacements,
                            enhancedTexturePaths,
                            cancellationToken).ConfigureAwait(false);
                        if (!restored)
                        {
                            TryDeleteTemporaryFile(sourceTexturePath);
                        }

                        continue;
                    }
                }

                var classification = classifier.Classify(entry.Name, metadata);
                var preservedReason = GetPreservedReason(metadata, classification, context.Options.MaximumDimension);
                if (preservedReason is null
                    && metadata.FileFormat == TextureFileFormat.Bmp
                    && metadata.BitsPerPixel == 8
                    && !LegacyIndexedBmp.CanEncode(
                        sourcePayload ??= await File.ReadAllBytesAsync(
                                sourceTexturePath,
                                cancellationToken)
                            .ConfigureAwait(false)))
                {
                    preservedReason = "Legacy indexed bitmap is too small, low-color, or structurally ambiguous";
                }
                if (preservedReason is not null)
                {
                    await ThrowIfPaintedRepairWouldRestoreChangedOriginalAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        preservedReason,
                        cancellationToken).ConfigureAwait(false);
                    counter.Preserve(preservedReason);
                    var restored = await RestoreOriginalIfBaselineChangedAsync(
                        reuseArchive,
                        entry,
                        sourceTexturePath,
                        context.WorkingDirectory,
                        index,
                        replacements,
                        enhancedTexturePaths,
                        cancellationToken).ConfigureAwait(false);
                    if (!restored)
                    {
                        TryDeleteTemporaryFile(sourceTexturePath);
                    }

                    continue;
                }

                var reviewDimensions = UpscaleDimensions.Calculate(
                    metadata.Width,
                    metadata.Height,
                    context.Options.MaximumDimension);
                previewCollector?.RegisterReview(new TextureReviewEntry(
                    context.RelativeInstallPath,
                    entry.Name,
                    metadata.Width,
                    metadata.Height,
                    reviewDimensions.OutputWidth,
                    reviewDimensions.OutputHeight));

                if (!forceReprocess
                    && reuseArchive is not null
                    && reuseArchive.TryGetEntry(entry.Name, out var reusedEntry)
                    && reusedEntry is not null)
                {
                    var reusedTexturePath = Path.Combine(
                        context.WorkingDirectory,
                        $"{index:D5}-reused{GetPayloadExtension(entry.Content.Kind)}");
                    enhancedTexturePaths.Add(reusedTexturePath);
                    await ExtractEntryToFileAsync(
                        reuseArchive,
                        reusedEntry,
                        reusedTexturePath,
                        cancellationToken).ConfigureAwait(false);
                    if (!await FilesEqualAsync(
                            sourceTexturePath,
                            reusedTexturePath,
                            cancellationToken).ConfigureAwait(false))
                    {
                        var useCutoutMipFloor = await DetectCutoutMipFloorAsync(
                            sourceTexturePath,
                            sourcePayload,
                            metadata,
                            classification,
                            cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var reusedMetadata = await sniffer.ReadFileAsync(
                                reusedTexturePath,
                                cancellationToken).ConfigureAwait(false);
                            var expectedDimensions = UpscaleDimensions.Calculate(
                                metadata.Width,
                                metadata.Height,
                                context.Options.MaximumDimension);
                            var expectedMipCount = GetExpectedMipCount(
                                metadata.FileFormat,
                                expectedDimensions,
                                context.Options.GenerateMipMaps,
                                useCutoutMipFloor);
                            ValidateEnhancedPayload(
                                entry.Name,
                                reusedMetadata,
                                metadata,
                                expectedDimensions,
                                expectedMipCount);
                            if (!rebuildFromReuseArchive)
                            {
                                replacements.Add(PfsArchiveReplacement.FromFile(
                                    entry.Name,
                                    reusedTexturePath));
                            }
                            expectedOutputs.Add(
                                entry.Name,
                                new ExpectedTexture(
                                    expectedDimensions.OutputWidth,
                                    expectedDimensions.OutputHeight,
                                    metadata.FileFormat,
                                    metadata.TexconvFormat,
                                    expectedMipCount));
                            counter.Reused();
                            if (rebuildFromReuseArchive)
                            {
                                TryDeleteTemporaryFile(reusedTexturePath);
                            }

                            TryDeleteTemporaryFile(sourceTexturePath);
                            continue;
                        }
                        catch (Exception exception) when (exception is InvalidDataException
                                                               or NotSupportedException)
                        {
                            // A stale cutout is a targeted cache miss. Any other
                            // invalid changed member in raw-baseline mode is
                            // explicitly restored from the original instead of
                            // being carried forward or entering the AI path.
                            TryDeleteTemporaryFile(reusedTexturePath);
                            if (requireRequestedVisualProfile && !useCutoutMipFloor)
                            {
                                throw new InvalidDataException(
                                    $"Repair stopped because the prior painted output for {entry.Name} no longer passes current validation. Rebuilding the complete painted pack is required; SpinTexture will not replace it with original or differently styled pixels.",
                                    exception);
                            }

                            if (rebuildFromReuseArchive && !useCutoutMipFloor)
                            {
                                replacements.Add(PfsArchiveReplacement.FromFile(
                                    entry.Name,
                                    sourceTexturePath));
                                counter.Preserve("Invalid prior enhancement restored to original");
                                counter.Warn(
                                    $"{entry.Name} was restored to its verified original because its prior non-cutout enhancement failed current validation: {exception.Message}");
                                continue;
                            }
                        }
                    }
                    else
                    {
                        TryDeleteTemporaryFile(reusedTexturePath);
                        if (!retryUnchangedEntries)
                        {
                            counter.Preserve("Source-identical reusable texture");
                            TryDeleteTemporaryFile(sourceTexturePath);
                            continue;
                        }
                    }
                }

                if (requireRequestedVisualProfile
                    && !forceReprocess
                    && !allowRequestedVisualProfileRegeneration)
                {
                    throw new InvalidDataException(
                        $"Repair stopped because {entry.Name} has no validated prior painted output that can be reused with this legacy profile. SpinTexture will not generate a partial painted replacement using a different algorithm revision; rebuild the complete painted pack instead.");
                }

                var payloadExtension = GetPayloadExtension(metadata.FileFormat);
                var enhancedTexturePath = Path.Combine(context.WorkingDirectory, $"{index:D5}-enhanced{payloadExtension}");
                enhancedTexturePaths.Add(enhancedTexturePath);

                // The conservative ESRNet route can reject highly saturated
                // classic armor/character palettes (for example the shared
                // 0401 plate atlas) even when the reconstruction is otherwise
                // valid. The bundled texture model retains those palette blocks
                // more reliably; the normal fidelity gate and faithful fallback
                // still protect every result.
                var textureOptions = ResolveColorProcessingOptions(
                    context.Options,
                    metadata,
                    entry.Name,
                    allowExplicitEffectArt: false);
                if (forceReprocess && textureOverride?.Preset is { } selectedPreset)
                {
                    textureOptions = context.Options with { Preset = selectedPreset };
                }

                // Keep the manifest's requested ZoneAware intent, but pass one
                // concrete reviewed theme to every texture in this archive. This
                // prevents neighboring members from receiving inconsistent color
                // shaping and leaves unknown/shared archives on Classic Painted.
                textureOptions = PaintedThemeResolver.ResolveForArtifact(
                    textureOptions,
                    context.RelativeInstallPath);

                pendingTextures.Add(new PendingTexture(
                    entry,
                    sourceTexturePath,
                    enhancedTexturePath,
                    metadata,
                    new NativeTextureProcessRequest(
                        sourceTexturePath,
                        enhancedTexturePath,
                        context.WorkingDirectory,
                        metadata,
                        classification,
                        textureOptions,
                        WrapEdges: ShouldUseWrapSampling(
                            context.RelativeInstallPath,
                            entry.Name,
                            classification,
                            clampArchiveNames.Contains(
                                Path.GetFileName(context.RelativeInstallPath))))));
            }

            await ProcessPendingTexturesAsync(
                context,
                pendingTextures,
                replacements,
                expectedOutputs,
                cancellationToken).ConfigureAwait(false);

            var rebuildBase = rebuildFromReuseArchive
                ? reuseArchive
                    ?? throw new InvalidDataException(
                        "Targeted cutout repair lost its locked baseline archive.")
                : source;
            if (replacements.Count == 0)
            {
                if (rebuildFromReuseArchive)
                {
                    var expected = reuseArchiveFingerprints[context.RelativeInstallPath];
                    if (reuseMaterializer is IVerifiedStagedPackPayloadMaterializer verified)
                    {
                        await verified.MaterializeVerifiedAsync(
                            reuseArchivePath!,
                            context.DestinationPath,
                            expected.Length,
                            expected.Sha256,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await reuseMaterializer.MaterializeAsync(
                            reuseArchivePath!,
                            context.DestinationPath,
                            expected.Length,
                            expected.Sha256,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await CopyAsync(
                        context.SourcePath,
                        context.DestinationPath,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await rebuildBase.RebuildToFileAsync(
                    context.DestinationPath,
                    replacements,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await ValidateRebuiltArchiveAsync(
                source,
                context.DestinationPath,
                expectedOutputs,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var sourceTexturePath in sourceTexturePaths)
            {
                TryDeleteTemporaryFile(sourceTexturePath);
            }

            foreach (var enhancedTexturePath in enhancedTexturePaths)
            {
                TryDeleteTemporaryFile(enhancedTexturePath);
            }
        }
    }

    StagedArtifactCheckpointCapture IStagedArtifactCheckpointParticipant.CaptureCheckpointState() =>
        new(counter.CaptureCheckpoint(), previewCollector?.CaptureCheckpoint()
            ?? new TexturePreviewCollectorCheckpoint(new HashSet<int>(), new HashSet<string>()));

    StagedBuildCheckpointContribution IStagedArtifactCheckpointParticipant.CompleteCheckpointCapture(
        StagedArtifactCheckpointCapture before)
    {
        var delta = previewCollector?.CaptureDelta(before.Previews)
            ?? new TexturePreviewCollectorDelta([], []);
        return new StagedBuildCheckpointContribution(
            counter.CaptureDelta(before.Statistics),
            delta.Previews.Select(item => new StagedBuildCheckpointPreview(
                item.Slot,
                MakePreviewPathRelative(item.Entry),
                0,
                string.Empty,
                0,
                string.Empty)).ToArray(),
            delta.Reviews);
    }

    void IStagedArtifactCheckpointParticipant.RestoreCheckpointContribution(
        StagedBuildCheckpointContribution contribution)
    {
        counter.RestoreCheckpoint(contribution.Statistics);
        previewCollector?.RestoreCheckpoint(
            contribution.Previews.Select(item => new TexturePreviewSlot(item.Slot, item.Entry)).ToArray(),
            contribution.ReviewEntries);
    }

    private static TexturePreviewEntry MakePreviewPathRelative(TexturePreviewEntry entry)
    {
        var previewDirectory = Path.GetDirectoryName(entry.OriginalImagePath)!;
        var buildDirectory = Path.GetDirectoryName(previewDirectory)!;
        return entry with
        {
            OriginalImagePath = Path.GetRelativePath(buildDirectory, entry.OriginalImagePath),
            EnhancedImagePath = Path.GetRelativePath(buildDirectory, entry.EnhancedImagePath)
        };
    }

    private async Task ProcessPendingTexturesAsync(
        StagedArtifactBuildContext context,
        IReadOnlyList<PendingTexture> pendingTextures,
        ICollection<PfsArchiveReplacement> replacements,
        IDictionary<string, ExpectedTexture> expectedOutputs,
        CancellationToken cancellationToken)
    {
        if (pendingTextures.Count == 0)
        {
            return;
        }

        var requests = pendingTextures.Select(item => item.Request).ToArray();
        var outcomes = processBatch is null
            ? await processor.ProcessBatchAsync(
                    requests,
                    new NativeBatchProgressBridge(
                        progress,
                        Path.GetFileName(context.SourcePath),
                        context.Options.Preset),
                    cancellationToken)
                .ConfigureAwait(false)
            : await processBatch(requests, cancellationToken).ConfigureAwait(false);
        if (outcomes.Count != pendingTextures.Count)
        {
            throw new InvalidDataException("Native texture batching returned an unexpected result count.");
        }

        for (var index = 0; index < pendingTextures.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pendingTextures[index];
            var outcome = outcomes[index];
            progress?.Report(new ProgressUpdate(
                "Upscale",
                $"Validating enhanced textures in {Path.GetFileName(context.SourcePath)}.",
                index,
                pendingTextures.Count,
                item.Entry.Name));

            if (outcome.Error is not null)
            {
                if (outcome.Error is not (NativeProcessException
                    or NativeProcessInactivityException
                    or InvalidDataException
                    or NotSupportedException))
                {
                    ExceptionDispatchInfo.Capture(outcome.Error).Throw();
                }

                if (requireRequestedVisualProfile)
                {
                    throw new InvalidDataException(
                        $"Repair stopped because {item.Entry.Name} could not be regenerated with the pack's requested painted profile. The completed baseline remains unchanged.",
                        outcome.Error);
                }

                counter.Preserve("Texture processing failed safely");
                counter.Warn(
                    $"{item.Entry.Name} was kept original because its enhanced output failed validation: "
                    + outcome.Error.Message);
                TryDeleteTemporaryFile(item.EnhancedTexturePath);
                if (rebuildFromReuseArchive)
                {
                    replacements.Add(PfsArchiveReplacement.FromFile(
                        item.Entry.Name,
                        item.SourceTexturePath));
                }
                else
                {
                    TryDeleteTemporaryFile(item.SourceTexturePath);
                }

                continue;
            }

            try
            {
                var processResult = outcome.Result
                    ?? throw new InvalidDataException("Native texture batching returned neither output nor an error.");
                var usedFallback = processResult.ProcessingRoute.Contains(
                    "fallback",
                    StringComparison.OrdinalIgnoreCase);
                if (requireRequestedVisualProfile
                    && (usedFallback
                        || !IsRequestedVisualProfileRoute(
                            item.Request.Options.Preset,
                            processResult.ProcessingRoute)))
                {
                    throw new InvalidDataException(
                        $"Repair stopped because {item.Entry.Name} would not use its recorded {item.Request.Options.Preset} processing route. The completed baseline remains unchanged; SpinTexture will not publish a mixed-style replacement.");
                }

                var enhancedMetadata = await sniffer.ReadFileAsync(
                    item.EnhancedTexturePath,
                    cancellationToken).ConfigureAwait(false);
                ValidateEnhancedPayload(
                    item.Entry.Name,
                    enhancedMetadata,
                    item.Metadata,
                    processResult.Dimensions,
                    GetValidatedResultMipCount(
                        item.Entry.Name,
                        item.Metadata.FileFormat,
                        processResult,
                        context.Options.GenerateMipMaps));

                var enhancedLength = new FileInfo(item.EnhancedTexturePath).Length;
                replacements.Add(PfsArchiveReplacement.FromFile(item.Entry.Name, item.EnhancedTexturePath));
                expectedOutputs.Add(
                    item.Entry.Name,
                    new ExpectedTexture(
                        processResult.Dimensions.OutputWidth,
                        processResult.Dimensions.OutputHeight,
                        item.Metadata.FileFormat,
                        item.Metadata.TexconvFormat,
                        processResult.ExpectedMipCount));
                counter.Enhanced(enhancedLength);
                if (usedFallback)
                {
                    counter.Fallback();
                    counter.Warn(
                        $"{item.Entry.Name} used the faithful fallback because the requested model was unavailable or failed its fidelity gate.");
                }

                await TryCreatePreviewAsync(
                    context,
                    item.Entry.Name,
                    item.SourceTexturePath,
                    item.EnhancedTexturePath,
                    item.Metadata,
                    processResult.Dimensions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is NativeProcessException
                                                   or InvalidDataException
                                                   or NotSupportedException)
            {
                if (requireRequestedVisualProfile)
                {
                    throw new InvalidDataException(
                        $"Repair stopped because {item.Entry.Name} could not be validated with the pack's requested painted profile. The completed baseline remains unchanged.",
                        exception);
                }

                counter.Preserve("Texture processing failed safely");
                counter.Warn(
                    $"{item.Entry.Name} was kept original because its enhanced output failed validation: "
                    + exception.Message);
                TryDeleteTemporaryFile(item.EnhancedTexturePath);
                if (rebuildFromReuseArchive)
                {
                    replacements.Add(PfsArchiveReplacement.FromFile(
                        item.Entry.Name,
                        item.SourceTexturePath));
                }
            }

            if (!rebuildFromReuseArchive
                || expectedOutputs.ContainsKey(item.Entry.Name))
            {
                TryDeleteTemporaryFile(item.SourceTexturePath);
            }
        }
    }

    private sealed class NativeBatchProgressBridge : IProgress<NativeOutputLine>
    {
        private static readonly TimeSpan ToolOutputThrottle = TimeSpan.FromSeconds(5);
        private readonly IProgress<ProgressUpdate>? progress;
        private readonly string archiveName;
        private readonly TexturePreset preset;
        private readonly object gate = new();
        private DateTimeOffset lastToolOutputReport = DateTimeOffset.MinValue;
        private NativeTextureWorkPhase activePhase = NativeTextureWorkPhase.CpuPreparation;
        private int lastCompleted;
        private int lastTotal = 1;

        public NativeBatchProgressBridge(
            IProgress<ProgressUpdate>? progress,
            string archiveName,
            TexturePreset preset)
        {
            this.progress = progress;
            this.archiveName = archiveName;
            this.preset = preset;
        }

        public void Report(NativeOutputLine value)
        {
            if (progress is null)
            {
                return;
            }

            lock (gate)
            {
                if (value.Phase != NativeTextureWorkPhase.ToolOutput)
                {
                    activePhase = value.Phase;
                    lastCompleted = Math.Max(0, value.Completed);
                    lastTotal = Math.Max(1, value.Total);
                    ReportPhase(value);
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (now - lastToolOutputReport < ToolOutputThrottle)
                {
                    return;
                }

                lastToolOutputReport = now;
                var message = activePhase == NativeTextureWorkPhase.GpuInference
                    ? preset == TexturePreset.MaximumDetail
                        ? "GPU worker active: Material Detail is running eight-view TTA. Bursty GPU use is expected."
                        : "GPU worker active: enhancing this texture batch."
                    : "Native texture worker is active.";
                progress.Report(new ProgressUpdate(
                    "Upscale",
                    message,
                    lastCompleted,
                    lastTotal,
                    archiveName,
                    IsHeartbeat: true));
            }
        }

        private void ReportPhase(NativeOutputLine value)
        {
            var message = value.Phase switch
            {
                NativeTextureWorkPhase.CpuPreparation =>
                    $"CPU preparation: {value.Text}",
                NativeTextureWorkPhase.GpuInference => value.Text,
                NativeTextureWorkPhase.Encoding => value.Text,
                _ => value.Text
            };
            progress!.Report(new ProgressUpdate(
                "Upscale",
                message,
                value.Completed,
                Math.Max(1, value.Total),
                archiveName,
                IsHeartbeat: true));
        }
    }

    internal static bool IsPotentialCandidate(PfsArchiveEntry entry, int maximumDimension)
    {
        if (!entry.IsTexture
            || entry.Content.Kind is not (PfsContentKind.Dds or PfsContentKind.Bitmap or PfsContentKind.Targa)
            || entry.Content.Width < 8
            || entry.Content.Height < 8
            || Math.Max(entry.Content.Width, entry.Content.Height) >= maximumDimension)
        {
            return false;
        }

        var aspect = (double)Math.Max(entry.Content.Width, entry.Content.Height)
            / Math.Min(entry.Content.Width, entry.Content.Height);
        return aspect <= 8;
    }

    private bool IsEntryAllowed(string relativeInstallPath, string entryName) =>
        !filterCharacterEquipmentEntries
        || CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
            relativeInstallPath,
            entryName);

    private async Task ThrowIfPaintedRepairWouldRestoreChangedOriginalAsync(
        PfsArchive? reuseArchive,
        PfsArchiveEntry sourceEntry,
        string sourceTexturePath,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!requireRequestedVisualProfile || !rebuildFromReuseArchive)
        {
            return;
        }

        if (reuseArchive is null
            || !reuseArchive.TryGetEntry(sourceEntry.Name, out var reusedEntry)
            || reusedEntry is null)
        {
            throw new InvalidDataException(
                $"The painted repair baseline is missing {sourceEntry.Name}.");
        }

        var reusedPath = Path.Combine(
            Path.GetDirectoryName(sourceTexturePath)!,
            $"painted-restore-check-{Guid.NewGuid():N}{GetPayloadExtension(sourceEntry.Content.Kind)}");
        try
        {
            await ExtractEntryToFileAsync(
                reuseArchive,
                reusedEntry,
                reusedPath,
                cancellationToken).ConfigureAwait(false);
            if (await FilesEqualAsync(
                    sourceTexturePath,
                    reusedPath,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        finally
        {
            TryDeleteTemporaryFile(reusedPath);
        }

        throw new InvalidDataException(
            $"Repair stopped because restoring {sourceEntry.Name} to original would discard its painted output ({reason}). The completed baseline remains unchanged; rebuild the complete painted pack instead.");
    }

    private async Task<bool> RestoreOriginalIfBaselineChangedAsync(
        PfsArchive? reuseArchive,
        PfsArchiveEntry sourceEntry,
        string sourceTexturePath,
        string workingDirectory,
        int index,
        ICollection<PfsArchiveReplacement> replacements,
        ICollection<string> temporaryPaths,
        CancellationToken cancellationToken)
    {
        if (!rebuildFromReuseArchive)
        {
            return false;
        }

        if (reuseArchive is null
            || !reuseArchive.TryGetEntry(sourceEntry.Name, out var reusedEntry)
            || reusedEntry is null)
        {
            throw new InvalidDataException(
                $"The targeted repair baseline is missing {sourceEntry.Name}.");
        }

        var reusedPath = Path.Combine(
            workingDirectory,
            $"{index:D5}-protected-reused{GetPayloadExtension(sourceEntry.Content.Kind)}");
        temporaryPaths.Add(reusedPath);
        await ExtractEntryToFileAsync(
            reuseArchive,
            reusedEntry,
            reusedPath,
            cancellationToken).ConfigureAwait(false);
        var changed = !await FilesEqualAsync(
            sourceTexturePath,
            reusedPath,
            cancellationToken).ConfigureAwait(false);
        TryDeleteTemporaryFile(reusedPath);
        if (!changed)
        {
            return false;
        }

        replacements.Add(PfsArchiveReplacement.FromFile(
            sourceEntry.Name,
            sourceTexturePath));
        counter.Warn(
            $"{sourceEntry.Name} was restored to its verified original because the current pipeline protects it from enhancement.");
        return true;
    }

    private static void ValidateReusableArchiveLayout(
        PfsArchive source,
        PfsArchive reusable,
        string relativeInstallPath)
    {
        var sourceNames = source.Entries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reusableNames = reusable.Entries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!sourceNames.SetEquals(reusableNames))
        {
            throw new InvalidDataException(
                $"The reusable staged archive no longer matches the original member layout: {relativeInstallPath}");
        }
    }

    private static async Task<bool> FilesEqualAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        const int bufferSize = 128 * 1024;
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        await using var left = new FileStream(
            leftPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(
            rightPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var leftRead = await left.ReadAsync(leftBuffer, cancellationToken).ConfigureAwait(false);
            var rightRead = await right.ReadAsync(rightBuffer, cancellationToken).ConfigureAwait(false);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static void EnsureArchiveSizeBudget(
        string sourcePath,
        IReadOnlyList<PfsArchiveEntry> textureEntries,
        int maximumDimension)
    {
        // PFS directory offsets are 32-bit. Use a deliberately conservative
        // preflight so a multi-hour 4K build cannot discover the format limit
        // only while writing its final archive.
        const long reserveBytes = 16L * 1024 * 1024;
        var estimatedBytes = new FileInfo(sourcePath).Length;
        foreach (var entry in textureEntries)
        {
            if (!IsPotentialCandidate(entry, maximumDimension))
            {
                continue;
            }

            var dimensions = UpscaleDimensions.Calculate(
                entry.Content.Width,
                entry.Content.Height,
                maximumDimension);
            var sourceArea = checked((long)entry.Content.Width * entry.Content.Height);
            var outputArea = checked((long)dimensions.OutputWidth * dimensions.OutputHeight);
            var areaScale = (double)outputArea / sourceArea;
            var estimatedReplacementBytes = checked((long)Math.Ceiling(
                entry.UncompressedSize * areaScale * 1.4));
            estimatedBytes = checked(
                estimatedBytes + Math.Max(0, estimatedReplacementBytes - entry.StoredLength));
            if (estimatedBytes > uint.MaxValue - reserveBytes)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(sourcePath)} could exceed the legacy PFS 4 GiB limit "
                    + $"at the selected {maximumDimension:N0}px texture ceiling. "
                    + "Choose a smaller ceiling or a narrower scope.");
            }
        }
    }

    private static string? GetPreservedReason(
        TextureMetadata metadata,
        TextureClassification classification,
        int maximumDimension)
    {
        if (!metadata.IsSimpleTwoDimensionalTexture)
        {
            return "Cube, volume, or array texture";
        }

        if (metadata.Width < 8 || metadata.Height < 8)
        {
            return "Texture is too small for reliable reconstruction";
        }

        var aspect = (double)Math.Max(metadata.Width, metadata.Height)
            / Math.Min(metadata.Width, metadata.Height);
        if (aspect > 8)
        {
            return "Likely sprite strip or packed cells";
        }

        if (Math.Max(metadata.Width, metadata.Height) >= maximumDimension)
        {
            return "Already at the selected resolution cap";
        }

        if (metadata.FileFormat == TextureFileFormat.Dds
            && (metadata.UsesDx10Header
                || metadata.TexconvFormat is null
                || !SafeLegacyDdsFormats.Contains(metadata.TexconvFormat)))
        {
            return "DDS format is not safe for the legacy Direct3D 9 client";
        }

        if (metadata.FileFormat == TextureFileFormat.Bmp
            && (metadata.BitsPerPixel is not (8 or 32) || metadata.IsCompressed))
        {
            return "Legacy bitmap representation cannot be reproduced byte-compatibly";
        }

        if (metadata.FileFormat == TextureFileFormat.Tga
            && (metadata.BitsPerPixel != 32 || metadata.IsCompressed))
        {
            return "Legacy bitmap representation cannot be reproduced byte-compatibly";
        }

        if (!classification.CanUseColorUpscaler
            || classification.Kind is not (TextureKind.Color or TextureKind.Cutout))
        {
            return $"Protected {classification.Kind.ToString().ToLowerInvariant()} texture";
        }

        return null;
    }

    private void ValidateEnhancedPayload(
        string logicalName,
        TextureMetadata? output,
        TextureMetadata sourceMetadata,
        UpscaleDimensions expectedDimensions,
        int expectedMipCount)
    {
        if (output is null)
        {
            throw new InvalidDataException($"Enhanced output for {logicalName} is not a supported texture.");
        }

        if (output.FileFormat != sourceMetadata.FileFormat
            || output.Width != expectedDimensions.OutputWidth
            || output.Height != expectedDimensions.OutputHeight)
        {
            throw new InvalidDataException(
                $"Enhanced output for {logicalName} did not preserve its container and expected dimensions.");
        }

        if (sourceMetadata.FileFormat == TextureFileFormat.Dds)
        {
            if (!string.Equals(output.TexconvFormat, sourceMetadata.TexconvFormat, StringComparison.OrdinalIgnoreCase)
                || output.UsesDx10Header)
            {
                throw new InvalidDataException(
                    $"Enhanced output for {logicalName} did not preserve its legacy DDS compression family.");
            }

            if (output.MipCount != expectedMipCount)
            {
                throw new InvalidDataException(
                    $"Enhanced DDS output for {logicalName} has {output.MipCount} mip levels; "
                    + $"{expectedMipCount} were required.");
            }
        }
        else if (output.BitsPerPixel != sourceMetadata.BitsPerPixel)
        {
            throw new InvalidDataException(
                $"Enhanced output for {logicalName} changed its legacy pixel representation.");
        }
    }

    private async Task ValidateRebuiltArchiveAsync(
        PfsArchive source,
        string stagedPath,
        IReadOnlyDictionary<string, ExpectedTexture> expectedOutputs,
        CancellationToken cancellationToken)
    {
        await using var rebuilt = await PfsArchive.OpenAsync(
            stagedPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (rebuilt.Entries.Count != source.Entries.Count)
        {
            throw new InvalidDataException("Rebuilt archive entry count differs from the source archive.");
        }

        foreach (var sourceEntry in source.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rebuilt.TryGetEntry(sourceEntry.Name, out var rebuiltEntry)
                || rebuiltEntry is null
                || rebuiltEntry.FilenameCrc != sourceEntry.FilenameCrc)
            {
                throw new InvalidDataException($"Rebuilt archive lost or renamed {sourceEntry.Name}.");
            }

            if (!expectedOutputs.ContainsKey(sourceEntry.Name))
            {
                if (rebuiltEntry.UncompressedSize != sourceEntry.UncompressedSize)
                {
                    throw new InvalidDataException($"Preserved archive member changed size: {sourceEntry.Name}.");
                }

                var sourceHash = await HashEntryAsync(source, sourceEntry, cancellationToken).ConfigureAwait(false);
                var rebuiltHash = await HashEntryAsync(rebuilt, rebuiltEntry, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, rebuiltHash))
                {
                    throw new InvalidDataException($"Preserved archive member changed bytes: {sourceEntry.Name}.");
                }
            }
        }

        foreach (var expected in expectedOutputs)
        {
            var payload = await rebuilt.ReadEntryAsync(expected.Key, cancellationToken).ConfigureAwait(false);
            if (!sniffer.TryRead(payload, out var metadata)
                || metadata is null
                || metadata.Width != expected.Value.Width
                || metadata.Height != expected.Value.Height
                || metadata.FileFormat != expected.Value.FileFormat
                || !string.Equals(metadata.TexconvFormat, expected.Value.TexconvFormat, StringComparison.OrdinalIgnoreCase)
                || (metadata.FileFormat == TextureFileFormat.Dds
                    && metadata.MipCount != expected.Value.MipCount))
            {
                throw new InvalidDataException($"Rebuilt archive failed validation for {expected.Key}.");
            }
        }
    }

    private async Task<bool> DetectCutoutMipFloorAsync(
        string sourceTexturePath,
        byte[]? sourcePayload,
        TextureMetadata metadata,
        TextureClassification classification,
        CancellationToken cancellationToken)
    {
        if (metadata.FileFormat != TextureFileFormat.Dds || !metadata.HasAlpha)
        {
            return false;
        }

        var direct = sourcePayload is null
            ? TextureTopMipAlphaKind.Unknown
            : TextureAlphaInspector.ClassifyTopMipAlpha(sourcePayload, metadata);
        if (direct != TextureTopMipAlphaKind.Unknown)
        {
            return direct == TextureTopMipAlphaKind.AlphaTestedCutout;
        }

        try
        {
            return await processor.DetectAlphaTestedCutoutAsync(
                sourceTexturePath,
                metadata,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                               or NativeProcessException
                                               or InvalidDataException
                                               or NotSupportedException)
        {
            // Reuse must remain available when only the optional alpha preflight
            // cannot run. Explicit semantic cutouts are the conservative fallback;
            // ambiguous Color textures will be checked by fresh processor metadata.
            return classification.Kind == TextureKind.Cutout;
        }
    }

    private static int GetExpectedMipCount(
        TextureFileFormat format,
        UpscaleDimensions dimensions,
        bool generateMipMaps,
        bool useCutoutMipFloor) =>
        format == TextureFileFormat.Dds
            ? TextureMipPolicy.Calculate(
                dimensions.OutputWidth,
                dimensions.OutputHeight,
                generateMipMaps,
                useCutoutMipFloor)
            : 1;

    private static int GetValidatedResultMipCount(
        string logicalName,
        TextureFileFormat format,
        NativeTextureProcessResult result,
        bool generateMipMaps)
    {
        var expected = GetExpectedMipCount(
            format,
            result.Dimensions,
            generateMipMaps,
            result.UsedCutoutMipFloor);
        if (result.ExpectedMipCount != expected)
        {
            throw new InvalidDataException(
                $"Native processing reported {result.ExpectedMipCount} mip levels for {logicalName}; "
                + $"the selected mip policy requires {expected}.");
        }

        return expected;
    }

    private static string GetPayloadExtension(TextureFileFormat format) => format switch
    {
        TextureFileFormat.Dds => ".dds",
        TextureFileFormat.Bmp => ".bmp",
        TextureFileFormat.Tga => ".tga",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static string GetPayloadExtension(PfsContentKind kind) => kind switch
    {
        PfsContentKind.Dds => ".dds",
        PfsContentKind.Bitmap => ".bmp",
        PfsContentKind.Targa => ".tga",
        _ => ".texture"
    };

    public static bool IsLikelyAnimatedEffectName(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        var stem = Path.GetFileNameWithoutExtension(logicalName);
        var digitStart = stem.Length;
        while (digitStart > 0 && char.IsDigit(stem[digitStart - 1]))
        {
            digitStart--;
        }

        if (digitStart == stem.Length)
        {
            return false;
        }

        var tokens = stem[..digitStart]
            .Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            // Lavastorm is a zone prefix, not evidence that every numbered terrain texture is lava animation.
            if (token.Equals("lavastorm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.StartsWith("lava", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("smoke", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("smke", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("flame", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("fire", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("water", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("wave", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ShouldUseConservativeColorModel(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        if (IsLikelyAnimatedEffectName(logicalName))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(logicalName);
        var graphicTerms = new[]
        {
            "banner", "crest", "decal", "emblem", "flag", "logo",
            "painting", "portrait", "poster", "sign"
        };
        return graphicTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsRequestedVisualProfileRoute(
        TexturePreset requestedPreset,
        string processingRoute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processingRoute);
        if (processingRoute.Contains("fallback", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return requestedPreset switch
        {
            TexturePreset.Faithful =>
                processingRoute.Contains("faithful color restoration", StringComparison.OrdinalIgnoreCase)
                || processingRoute.Contains("soft-alpha restoration", StringComparison.OrdinalIgnoreCase),
            TexturePreset.ClassicHd =>
                processingRoute.Contains("game-texture reconstruction", StringComparison.OrdinalIgnoreCase)
                || processingRoute.Contains("texture reconstruction", StringComparison.OrdinalIgnoreCase),
            TexturePreset.MaximumDetail =>
                processingRoute.Contains("maximum-detail", StringComparison.OrdinalIgnoreCase),
            TexturePreset.Illustrated =>
                processingRoute.Contains("illustrated", StringComparison.OrdinalIgnoreCase)
                && processingRoute.Contains("finishing", StringComparison.OrdinalIgnoreCase),
            TexturePreset.RusticPainted =>
                processingRoute.Contains("graphic painted", StringComparison.OrdinalIgnoreCase)
                && processingRoute.Contains("rustic", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static UpscaleOptions ResolveColorProcessingOptions(
        UpscaleOptions requested,
        TextureMetadata metadata,
        string logicalName,
        bool allowExplicitEffectArt)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var isIndexedBitmap = metadata.FileFormat == TextureFileFormat.Bmp
            && metadata.BitsPerPixel == 8;
        var isGraphicPainted = requested.Preset is TexturePreset.Illustrated
            or TexturePreset.RusticPainted;
        var longestEdge = Math.Max(metadata.Width, metadata.Height);

        // Legacy indexed art keeps its exact header and source palette during
        // reconstruction. Texture HD remains the safest default for faithful
        // and material-detail builds, but an explicitly selected painted art
        // direction must reach the illustrated model or most classic world,
        // armor, and character textures would silently ignore that choice.
        if (isIndexedBitmap)
        {
            if (isGraphicPainted
                && longestEdge >= 32
                && !IsLikelyAnimatedEffectName(logicalName))
            {
                return requested;
            }

            return requested with { Preset = TexturePreset.ClassicHd };
        }

        if (allowExplicitEffectArt)
        {
            return requested;
        }

        // Animated material sequences retain the conservative model to avoid
        // per-frame style drift. Painted presets may intentionally transform
        // ordinary signs and small classic diffuse art once the normal semantic
        // and renderer-control policies have already proved them safe.
        var requiresConservativeRoute = IsLikelyAnimatedEffectName(logicalName)
            || longestEdge < 32
            || (!isGraphicPainted
                && (ShouldUseConservativeColorModel(logicalName)
                    || longestEdge < 128));
        return requested.Preset != TexturePreset.Faithful && requiresConservativeRoute
            ? requested with { Preset = TexturePreset.Faithful }
            : requested;
    }

    public static bool ShouldUseWrapSampling(
        string containerRelativePath,
        string logicalName,
        TextureClassification classification,
        bool isCharacterOrEquipmentArchive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentNullException.ThrowIfNull(classification);

        // Alpha-tested cards and decals must not sample the opposite side of
        // the image while their borders and mipmaps are reconstructed.
        if (classification.Kind != TextureKind.Color
            || isCharacterOrEquipmentArchive)
        {
            return false;
        }

        var container = Path.GetFileNameWithoutExtension(containerRelativePath);
        if (container.Contains("_obj", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("objects", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("props", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("furniture", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("housing", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("interior", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("plants", StringComparison.OrdinalIgnoreCase)
            || container.StartsWith("trees", StringComparison.OrdinalIgnoreCase)
            || CharacterEquipmentArchiveCatalog.ShouldClampArchiveSampling(
                containerRelativePath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(logicalName);
        var objectTerms = new[]
        {
            "banner", "decal", "door", "flag", "portrait", "sign", "window"
        };
        return !objectTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private async Task TryCreatePreviewAsync(
        StagedArtifactBuildContext context,
        string logicalName,
        string originalTexturePath,
        string enhancedTexturePath,
        TextureMetadata originalMetadata,
        UpscaleDimensions enhancedDimensions,
        CancellationToken cancellationToken)
    {
        if (previewCollector is null
            || logicalName.Contains("palette", StringComparison.OrdinalIgnoreCase)
            || !previewCollector.TryReserve(context.RelativeInstallPath, logicalName, out var previewIndex))
        {
            return;
        }

        var originalImagePath = Path.Combine(context.PreviewDirectory, $"{previewIndex:D3}-before.png");
        var enhancedImagePath = Path.Combine(context.PreviewDirectory, $"{previewIndex:D3}-after.png");
        try
        {
            Directory.CreateDirectory(context.PreviewDirectory);
            await processor.CreatePngPreviewAsync(
                originalTexturePath,
                originalImagePath,
                enhancedDimensions.OutputWidth,
                enhancedDimensions.OutputHeight,
                nearestNeighbor: false,
                cancellationToken).ConfigureAwait(false);
            await processor.CreatePngPreviewAsync(
                enhancedTexturePath,
                enhancedImagePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            previewCollector.Complete(previewIndex, new TexturePreviewEntry(
                context.RelativeInstallPath,
                logicalName,
                originalImagePath,
                enhancedImagePath,
                originalMetadata.Width,
                originalMetadata.Height,
                enhancedDimensions.OutputWidth,
                enhancedDimensions.OutputHeight));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or NativeProcessException)
        {
            TryDeleteTemporaryFile(originalImagePath);
            TryDeleteTemporaryFile(enhancedImagePath);
            previewCollector.Abandon(previewIndex);
            counter.Warn($"Could not create the preview for {logicalName}: {exception.Message}");
        }
    }

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task ExtractEntryToFileAsync(
        PfsArchive archive,
        PfsArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await archive.ExtractEntryAsync(entry, destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> HashEntryAsync(
        PfsArchive archive,
        PfsArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        using var algorithm = SHA256.Create();
        await using (var hashingStream = new CryptoStream(
            Stream.Null,
            algorithm,
            CryptoStreamMode.Write,
            leaveOpen: true))
        {
            await archive.ExtractEntryAsync(entry, hashingStream, cancellationToken).ConfigureAwait(false);
            hashingStream.FlushFinalBlock();
        }

        return algorithm.Hash
            ?? throw new InvalidOperationException("SHA-256 did not produce an archive-member hash.");
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The enclosing staged-build cleanup removes any intermediate that remains.
        }
    }

    private sealed record ExpectedTexture(
        int Width,
        int Height,
        TextureFileFormat FileFormat,
        string? TexconvFormat,
        int MipCount);

    private sealed record PendingTexture(
        PfsArchiveEntry Entry,
        string SourceTexturePath,
        string EnhancedTexturePath,
        TextureMetadata Metadata,
        NativeTextureProcessRequest Request);
}
