using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.Core.Services;

internal sealed class LooseTextureArtifactBuilder : IStagedArtifactBuilder, IStagedArtifactCheckpointParticipant
{
    private static readonly HashSet<string> SafeLegacyDdsFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "BC1_UNORM", "BC2_UNORM", "BC3_UNORM",
        "B8G8R8A8_UNORM", "B8G8R8X8_UNORM"
    };

    private readonly NativeTextureProcessor processor;
    private readonly TextureBuildCounter counter;
    private readonly TexturePreviewCollector? previewCollector;
    private readonly TextureHeaderSniffer sniffer = new();
    private readonly TextureSemanticClassifier classifier = new();
    private readonly Func<
        NativeTextureProcessRequest,
        CancellationToken,
        Task<NativeTextureProcessResult>> processTexture;
    private readonly bool allowSpellEffects;

    public LooseTextureArtifactBuilder(
        NativeTextureProcessor processor,
        TextureBuildCounter counter,
        TexturePreviewCollector? previewCollector = null,
        bool allowSpellEffects = false,
        Func<
            NativeTextureProcessRequest,
            CancellationToken,
            Task<NativeTextureProcessResult>>? processTexture = null)
    {
        this.processor = processor;
        this.counter = counter;
        this.previewCollector = previewCollector;
        this.allowSpellEffects = allowSpellEffects;
        this.processTexture = processTexture
            ?? ((request, cancellationToken) => processor.ProcessAsync(
                request,
                cancellationToken: cancellationToken));
    }

    public async Task BuildAsync(
        StagedArtifactBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var metadata = await sniffer.ReadFileAsync(context.SourcePath, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new InvalidDataException($"Loose texture is not supported: {context.RelativeInstallPath}");
        }

        counter.Discover(new FileInfo(context.SourcePath).Length);
        var celestialPreservedReason = CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                context.RelativeInstallPath)
            ?? CelestialTextureSafetyPolicy.GetPreservedReason(
                context.RelativeInstallPath,
                Path.GetFileName(context.RelativeInstallPath));
        if (celestialPreservedReason is not null)
        {
            counter.Preserve(celestialPreservedReason);
            await CopyAsync(context.SourcePath, context.DestinationPath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        byte[]? sourcePayload = null;
        var fullyTransparentControl = false;
        if (metadata.HasAlpha && metadata.FileFormat == TextureFileFormat.Dds)
        {
            sourcePayload = await File.ReadAllBytesAsync(context.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            fullyTransparentControl = TextureAlphaInspector.IsKnownFullyTransparent(
                sourcePayload,
                metadata);
        }

        var classification = allowSpellEffects
            && SpellEffectTexturePolicy.CanEnhance(context.RelativeInstallPath, metadata)
                ? SpellEffectTexturePolicy.CreateColorClassification(metadata)
                : classifier.Classify(context.RelativeInstallPath, metadata);
        var unsafeIndexedBitmap = metadata.FileFormat == TextureFileFormat.Bmp
            && metadata.BitsPerPixel == 8
            && !LegacyIndexedBmp.CanEncode(
                sourcePayload ??= await File.ReadAllBytesAsync(
                        context.SourcePath,
                        cancellationToken)
                    .ConfigureAwait(false));
        if (!metadata.IsSimpleTwoDimensionalTexture
            || metadata.Width < 8
            || metadata.Height < 8
            || Math.Max(metadata.Width, metadata.Height) > context.Options.MaximumDimension
            || (Math.Max(metadata.Width, metadata.Height) == context.Options.MaximumDimension
                && context.Options.Preset is not (TexturePreset.Illustrated
                    or TexturePreset.RusticPainted))
            || !HasSafeContainer(metadata)
            || fullyTransparentControl
            || unsafeIndexedBitmap
            || !classification.CanUseColorUpscaler
            || classification.Kind is not (TextureKind.Color or TextureKind.Cutout))
        {
            counter.Preserve($"Protected loose {classification.Kind.ToString().ToLowerInvariant()} texture");
            await CopyAsync(context.SourcePath, context.DestinationPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var reviewDimensions = UpscaleDimensions.Calculate(
            metadata.Width,
            metadata.Height,
            context.Options.MaximumDimension);
        previewCollector?.RegisterReview(new TextureReviewEntry(
            context.RelativeInstallPath,
            Path.GetFileName(context.RelativeInstallPath),
            metadata.Width,
            metadata.Height,
            reviewDimensions.OutputWidth,
            reviewDimensions.OutputHeight));

        var textureOptions = PfsTextureArchiveBuilder.ResolveColorProcessingOptions(
            context.Options,
            metadata,
            context.RelativeInstallPath,
            allowExplicitEffectArt: allowSpellEffects);
        textureOptions = PaintedThemeResolver.ResolveForArtifact(
            textureOptions,
            context.RelativeInstallPath);

        try
        {
            var result = await processTexture(
                new NativeTextureProcessRequest(
                    context.SourcePath,
                    context.DestinationPath,
                    context.WorkingDirectory,
                    metadata,
                    classification,
                    textureOptions,
                    // Loose files do not carry enough container context to prove
                    // that opposite-edge sampling is intended.
                    WrapEdges: false,
                    LogicalName: Path.GetFileName(context.RelativeInstallPath)),
                cancellationToken).ConfigureAwait(false);
            var usedFallback = result.ProcessingRoute.Contains(
                "fallback",
                StringComparison.OrdinalIgnoreCase);

            var output = await sniffer.ReadFileAsync(context.DestinationPath, cancellationToken).ConfigureAwait(false);
            var expectedMipCount = GetValidatedResultMipCount(
                context.RelativeInstallPath,
                metadata.FileFormat,
                result,
                context.Options.GenerateMipMaps);
            if (output is null
                || output.FileFormat != metadata.FileFormat
                || output.Width != result.Dimensions.OutputWidth
                || output.Height != result.Dimensions.OutputHeight
                || (metadata.FileFormat == TextureFileFormat.Dds
                    && (!string.Equals(output.TexconvFormat, metadata.TexconvFormat, StringComparison.OrdinalIgnoreCase)
                        || output.UsesDx10Header
                        || output.MipCount != expectedMipCount)))
            {
                throw new InvalidDataException(
                    $"Enhanced loose texture failed validation: {context.RelativeInstallPath}");
            }

            await TryCreatePreviewAsync(
                context,
                metadata,
                result.Dimensions,
                cancellationToken).ConfigureAwait(false);
            counter.Enhanced(new FileInfo(context.DestinationPath).Length, result);
            if (result.UsedBoundedRepaintMemoryGuard)
            {
                counter.Fallback();
                counter.Warn(result.UsedExternalArtisticWorker
                    ? $"{context.RelativeInstallPath} kept the diffusion renderer but used its bounded single-pass route because a full-resolution tile blend exceeded the safe working-set limit."
                    : $"{context.RelativeInstallPath} used the memory-bounded built-in painted renderer because both the full-resolution tile blend and one external output exceeded safe limits; this pack is mixed-renderer and must be fully rebuilt rather than repaired.");
            }
            else if (usedFallback)
            {
                counter.Fallback();
                counter.Warn(
                    $"{context.RelativeInstallPath} used a validated fallback because the requested model was unavailable or failed its fidelity gate.");
            }
        }
        catch (Exception exception) when (exception is NativeProcessException
                                               or InvalidDataException
                                               or NotSupportedException)
        {
            counter.Preserve("Loose texture processing failed safely");
            counter.Warn(
                $"{context.RelativeInstallPath} was kept original because its enhanced output failed validation: "
                + exception.Message);
            TryDelete(context.DestinationPath);
            await CopyAsync(context.SourcePath, context.DestinationPath, cancellationToken).ConfigureAwait(false);
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

    public static async Task<bool> IsCandidateAsync(
        string path,
        string relativePath,
        int maximumDimension,
        TexturePreset preset,
        CancellationToken cancellationToken)
    {
        if (CelestialTextureSafetyPolicy.GetPreservedReason(
                relativePath,
                Path.GetFileName(relativePath)) is not null)
        {
            return false;
        }

        var metadata = await new TextureHeaderSniffer().ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata is null
            || !metadata.IsSimpleTwoDimensionalTexture
            || metadata.Width < 8
            || metadata.Height < 8
            || Math.Max(metadata.Width, metadata.Height) > maximumDimension
            || (Math.Max(metadata.Width, metadata.Height) == maximumDimension
                && preset is not (TexturePreset.Illustrated or TexturePreset.RusticPainted))
            || !HasSafeContainer(metadata))
        {
            return false;
        }

        if (metadata.FileFormat == TextureFileFormat.Bmp
            && metadata.BitsPerPixel == 8
            && !LegacyIndexedBmp.CanEncode(
                await File.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false)))
        {
            return false;
        }

        if (metadata.HasAlpha
            && metadata.FileFormat == TextureFileFormat.Dds
            && TextureAlphaInspector.IsKnownFullyTransparent(
                await File.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false),
                metadata))
        {
            return false;
        }

        var classification = new TextureSemanticClassifier().Classify(Path.GetFileName(path), metadata);
        return classification.CanUseColorUpscaler
            && classification.Kind is TextureKind.Color or TextureKind.Cutout;
    }

    public static async Task<bool> IsSpellEffectCandidateAsync(
        string path,
        string relativePath,
        int maximumDimension,
        CancellationToken cancellationToken)
    {
        if (CelestialTextureSafetyPolicy.GetPreservedReason(
                relativePath,
                Path.GetFileName(relativePath)) is not null)
        {
            return false;
        }

        var metadata = await new TextureHeaderSniffer()
            .ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        return metadata is not null
            && Math.Max(metadata.Width, metadata.Height) < maximumDimension
            && HasSafeContainer(metadata)
            && SpellEffectTexturePolicy.CanEnhance(relativePath, metadata);
    }

    private static bool HasSafeContainer(TextureMetadata metadata) =>
        metadata.FileFormat switch
        {
            TextureFileFormat.Dds => !metadata.UsesDx10Header
                && metadata.TexconvFormat is not null
                && SafeLegacyDdsFormats.Contains(metadata.TexconvFormat),
            TextureFileFormat.Bmp => metadata.BitsPerPixel is 8 or 32
                && !metadata.IsCompressed,
            TextureFileFormat.Tga => metadata.BitsPerPixel == 32 && !metadata.IsCompressed,
            _ => false
        };

    private static int GetValidatedResultMipCount(
        string logicalName,
        TextureFileFormat format,
        NativeTextureProcessResult result,
        bool generateMipMaps)
    {
        var expected = format == TextureFileFormat.Dds
            ? TextureMipPolicy.Calculate(
                result.Dimensions.OutputWidth,
                result.Dimensions.OutputHeight,
                generateMipMaps,
                result.UsedCutoutMipFloor)
            : 1;
        if (result.ExpectedMipCount != expected)
        {
            throw new InvalidDataException(
                $"Native processing reported {result.ExpectedMipCount} mip levels for {logicalName}; "
                + $"the selected mip policy requires {expected}.");
        }

        return expected;
    }

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
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

    private async Task TryCreatePreviewAsync(
        StagedArtifactBuildContext context,
        TextureMetadata originalMetadata,
        UpscaleDimensions enhancedDimensions,
        CancellationToken cancellationToken)
    {
        var relativeDirectory = Path.GetDirectoryName(context.RelativeInstallPath);
        if (previewCollector is null
            || !previewCollector.TryReserve(
                string.IsNullOrWhiteSpace(relativeDirectory) ? "loose-textures" : relativeDirectory,
                Path.GetFileName(context.RelativeInstallPath),
                out var previewIndex))
        {
            return;
        }

        var originalImagePath = Path.Combine(context.PreviewDirectory, $"{previewIndex:D3}-before.png");
        var enhancedImagePath = Path.Combine(context.PreviewDirectory, $"{previewIndex:D3}-after.png");
        try
        {
            Directory.CreateDirectory(context.PreviewDirectory);
            await processor.CreatePngPreviewAsync(
                context.SourcePath,
                originalImagePath,
                enhancedDimensions.OutputWidth,
                enhancedDimensions.OutputHeight,
                nearestNeighbor: false,
                cancellationToken).ConfigureAwait(false);
            await processor.CreatePngPreviewAsync(
                context.DestinationPath,
                enhancedImagePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            previewCollector.Complete(previewIndex, new TexturePreviewEntry(
                context.RelativeInstallPath,
                Path.GetFileName(context.RelativeInstallPath),
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
            TryDelete(originalImagePath);
            TryDelete(enhancedImagePath);
            previewCollector.Abandon(previewIndex);
            counter.Warn($"Could not create the preview for {context.RelativeInstallPath}: {exception.Message}");
        }
    }

    private static void TryDelete(string path)
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
            // The enclosing staged-build cleanup owns any remaining partial preview.
        }
    }
}
