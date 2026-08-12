using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Textures;

namespace SpinTexture.Core.Services;

/// <summary>
/// Builds one artifact for an immutable loose-texture safety repair. Protected
/// celestial sprites are restored from the verified original source snapshot;
/// every unaffected artifact is reused from the exact verified staged baseline.
/// </summary>
internal sealed class LooseTextureSafetyRepairBuilder : IStagedArtifactBuilder
{
    private readonly string baselinePayloadPath;
    private readonly long baselinePayloadLength;
    private readonly string baselinePayloadSha256;
    private readonly TextureBuildCounter counter;
    private readonly IStagedPackPayloadMaterializer materializer;

    public LooseTextureSafetyRepairBuilder(
        string baselinePayloadPath,
        long baselinePayloadLength,
        string baselinePayloadSha256,
        TextureBuildCounter counter,
        IStagedPackPayloadMaterializer? materializer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePayloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePayloadSha256);
        if (baselinePayloadLength < 0
            || baselinePayloadSha256.Length != 64
            || baselinePayloadSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The reusable staged loose-artifact fingerprint is invalid.",
                nameof(baselinePayloadSha256));
        }

        this.baselinePayloadPath = Path.GetFullPath(baselinePayloadPath);
        this.baselinePayloadLength = baselinePayloadLength;
        this.baselinePayloadSha256 = baselinePayloadSha256;
        this.counter = counter ?? throw new ArgumentNullException(nameof(counter));
        this.materializer = materializer
            ?? new HardLinkOrCopyStagedPackPayloadMaterializer();
    }

    public async Task BuildAsync(
        StagedArtifactBuildContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var logicalName = Path.GetFileName(context.RelativeInstallPath);
        counter.Discover(new FileInfo(context.SourcePath).Length);
        var preservedReason = CelestialTextureSafetyPolicy.GetSkyResourcePreservedReason(
                context.RelativeInstallPath)
            ?? CelestialTextureSafetyPolicy.GetPreservedReason(
                context.RelativeInstallPath,
                logicalName);
        if (preservedReason is not null)
        {
            await CopyOriginalAsync(
                    context.SourcePath,
                    context.DestinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            counter.Preserve(preservedReason);
            return;
        }

        // Verification belongs inside the builder even though catalog
        // inspection already validated the completed baseline pack. This
        // closes the time-of-check/time-of-use gap before a hard link or copy.
        await FileIntegrity.EnsureMatchesAsync(
                baselinePayloadPath,
                baselinePayloadLength,
                baselinePayloadSha256,
                "Reusable staged loose texture",
                cancellationToken)
            .ConfigureAwait(false);
        if (materializer is IVerifiedStagedPackPayloadMaterializer verified)
        {
            await verified.MaterializeVerifiedAsync(
                    baselinePayloadPath,
                    context.DestinationPath,
                    baselinePayloadLength,
                    baselinePayloadSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await materializer.MaterializeAsync(
                    baselinePayloadPath,
                    context.DestinationPath,
                    baselinePayloadLength,
                    baselinePayloadSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        counter.Reused();
    }

    private static async Task CopyOriginalAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException(
                $"Loose safety-repair destination already exists: {destinationPath}");
        }

        var sourceFingerprint = await FileIntegrity
            .FingerprintAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        await AtomicFile.CopyAndReplaceAsync(
                sourcePath,
                destinationPath,
                sourceFingerprint.Length,
                sourceFingerprint.Sha256,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
