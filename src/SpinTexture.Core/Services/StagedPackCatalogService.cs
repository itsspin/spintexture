using System.Security;
using System.Text.Json;
using System.Collections.Concurrent;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

/// <summary>
/// Discovers and validates immutable completed builds in one SpinTexture profile.
/// A completed pack is identified only by Staging/&lt;build-id&gt;/manifest.json;
/// incomplete work directories without that commit marker are ignored.
/// </summary>
public sealed class StagedPackCatalogService
{
    private readonly ManifestStore manifestStore;
    private static readonly ConcurrentDictionary<ArtifactVerificationCacheKey, byte> ExactVerificationCache = new();

    public StagedPackCatalogService(ManifestStore? manifestStore = null)
    {
        this.manifestStore = manifestStore ?? new ManifestStore();
    }

    public async Task<IReadOnlyList<StagedPackInfo>> DiscoverAsync(
        ProjectPaths paths,
        StagedPackVerificationMode verificationMode = StagedPackVerificationMode.Metadata,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(paths.StagingPath))
        {
            return Array.Empty<StagedPackInfo>();
        }

        var candidates = EnumerateManifestCandidates(paths.StagingPath, cancellationToken);
        var results = new List<StagedPackInfo>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate(
                "Pack catalog",
                verificationMode == StagedPackVerificationMode.Exact
                    ? "Hash-verifying completed staged packs."
                    : "Checking completed staged-pack metadata.",
                index,
                candidates.Count,
                Path.GetFileName(Path.GetDirectoryName(candidates[index]))));
            results.Add(await InspectAsync(
                    paths,
                    candidates[index],
                    verificationMode,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        progress?.Report(new ProgressUpdate(
            "Pack catalog",
            $"Found {results.Count:N0} completed staged pack(s).",
            candidates.Count,
            candidates.Count));
        return results
            .OrderByDescending(info => info.Manifest?.CreatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(info => info.CandidateBuildId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<StagedPackInfo> InspectAsync(
        ProjectPaths paths,
        string manifestPath,
        StagedPackVerificationMode verificationMode = StagedPackVerificationMode.Exact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();

        string safeManifestPath;
        string buildDirectory;
        string candidateBuildId;
        try
        {
            safeManifestPath = ValidateManifestLocation(paths, manifestPath, out buildDirectory);
            candidateBuildId = Path.GetFileName(buildDirectory);
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            var fullPath = TryGetFullPath(manifestPath);
            var directory = Path.GetDirectoryName(fullPath) ?? paths.StagingPath;
            return Invalid(
                fullPath,
                directory,
                Path.GetFileName(directory),
                StagedPackValidationState.InvalidManifest,
                exception.Message);
        }

        try
        {
            if (!File.Exists(safeManifestPath))
            {
                return Invalid(
                    safeManifestPath,
                    buildDirectory,
                    candidateBuildId,
                    StagedPackValidationState.InvalidManifest,
                    "The staged-pack manifest is missing.");
            }

            if (File.Exists(Path.Combine(buildDirectory, "build-checkpoint.json")))
            {
                return Invalid(
                    safeManifestPath,
                    buildDirectory,
                    candidateBuildId,
                    StagedPackValidationState.InvalidManifest,
                    "This staged pack is still finalizing crash-recovery metadata and is not ready to use.");
            }

            var manifestBefore = await FileIntegrity
                .FingerprintAsync(safeManifestPath, cancellationToken)
                .ConfigureAwait(false);
            var manifest = await manifestStore
                .ReadBuildManifestAsync(safeManifestPath, cancellationToken)
                .ConfigureAwait(false);
            var manifestAfter = await FileIntegrity
                .FingerprintAsync(safeManifestPath, cancellationToken)
                .ConfigureAwait(false);
            if (manifestBefore != manifestAfter)
            {
                return Invalid(
                    safeManifestPath,
                    buildDirectory,
                    candidateBuildId,
                    StagedPackValidationState.InvalidManifest,
                    "The staged-pack manifest changed while it was being inspected.");
            }

            ValidateManifest(paths, buildDirectory, manifest);
            var manifestFingerprint = new StagedPackFileFingerprint(
                manifestBefore.Length,
                manifestBefore.Sha256);
            if (!PathGuard.SamePath(paths.InstallPath, manifest.InstallPath))
            {
                return new StagedPackInfo(
                    safeManifestPath,
                    buildDirectory,
                    candidateBuildId,
                    manifestFingerprint,
                    manifest,
                    StagedPackValidationState.DifferentInstall,
                    "This pack belongs to a different EverQuest installation.",
                    Array.Empty<StagedPackArtifactInfo>());
            }

            var artifacts = await InspectArtifactsAsync(
                    paths,
                    buildDirectory,
                    manifest,
                    verificationMode,
                    cancellationToken)
                .ConfigureAwait(false);
            var missing = artifacts.Count(artifact =>
                artifact.State == StagedPackArtifactValidationState.Missing);
            var invalid = artifacts.Count(artifact => artifact.State is
                StagedPackArtifactValidationState.LengthMismatch or
                StagedPackArtifactValidationState.HashMismatch or
                StagedPackArtifactValidationState.Invalid);
            if (missing != 0)
            {
                return new StagedPackInfo(
                    safeManifestPath,
                    buildDirectory,
                    candidateBuildId,
                    manifestFingerprint,
                    manifest,
                    StagedPackValidationState.PayloadMissing,
                    $"{missing:N0} staged artifact(s) are missing.",
                    artifacts);
            }

            if (invalid != 0)
            {
                return new StagedPackInfo(
                    safeManifestPath,
                    buildDirectory,
                    candidateBuildId,
                    manifestFingerprint,
                    manifest,
                    StagedPackValidationState.PayloadInvalid,
                    $"{invalid:N0} staged artifact(s) failed integrity validation.",
                    artifacts);
            }

            var exact = verificationMode == StagedPackVerificationMode.Exact;
            return new StagedPackInfo(
                safeManifestPath,
                buildDirectory,
                candidateBuildId,
                manifestFingerprint,
                manifest,
                exact ? StagedPackValidationState.Ready : StagedPackValidationState.MetadataValid,
                exact
                    ? $"All {artifacts.Count:N0} staged artifact(s) passed exact SHA-256 validation."
                    : $"All {artifacts.Count:N0} staged artifact(s) have the expected length; exact verification is still required before composition.",
                artifacts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            return Invalid(
                safeManifestPath,
                buildDirectory,
                candidateBuildId,
                StagedPackValidationState.InvalidManifest,
                exception.Message);
        }
    }

    private static IReadOnlyList<string> EnumerateManifestCandidates(
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        var candidates = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(stagingPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "build-checkpoint.json")))
            {
                // manifest.json may already be durable while workflow reports
                // are still finalizing. The checkpoint is an explicit
                // incomplete marker until TexturePackWorkflow removes it.
                continue;
            }
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifestPath))
            {
                candidates.Add(PathGuard.EnsurePathUnderRoot(stagingPath, manifestPath));
            }
        }

        return candidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ValidateManifestLocation(
        ProjectPaths paths,
        string manifestPath,
        out string buildDirectory)
    {
        if (!Directory.Exists(paths.StagingPath))
        {
            throw new DirectoryNotFoundException("The staged-pack workspace does not exist.");
        }

        var safeManifestPath = PathGuard.EnsurePathUnderRoot(paths.StagingPath, manifestPath);
        if (!Path.GetFileName(safeManifestPath).Equals(
                "manifest.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A staged pack must be identified by its manifest.json file.");
        }

        buildDirectory = Path.GetDirectoryName(safeManifestPath)
            ?? throw new InvalidDataException("The staged-pack manifest has no build directory.");
        var parent = Path.GetDirectoryName(buildDirectory);
        if (parent is null || !PathGuard.SamePath(parent, paths.StagingPath))
        {
            throw new InvalidDataException(
                "A completed staged-pack manifest must be directly under Staging/<build-id>.");
        }

        _ = PathGuard.EnsurePathUnderRoot(paths.StagingPath, buildDirectory);
        return safeManifestPath;
    }

    private static void ValidateManifest(
        ProjectPaths paths,
        string buildDirectory,
        BuildManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != BuildManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported build manifest schema {manifest.SchemaVersion}; schema 1 was required.");
        }

        var directoryId = Path.GetFileName(buildDirectory);
        if (string.IsNullOrWhiteSpace(manifest.BuildId)
            || !manifest.BuildId.Equals(directoryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The build ID in the manifest does not match its staging directory.");
        }

        _ = PathGuard.ValidateIdentifier(manifest.BuildId, "build");
        if (string.IsNullOrWhiteSpace(manifest.InstallPath))
        {
            throw new InvalidDataException("The staged-pack manifest has no install path.");
        }

        _ = Path.GetFullPath(manifest.InstallPath);
        if (manifest.Options is null
            || !Enum.IsDefined(manifest.Options.Preset)
            || !Enum.IsDefined(manifest.Options.Scope)
            || !Enum.IsDefined(manifest.Options.PaintedTheme)
            || (manifest.Options.Preset != TexturePreset.Illustrated
                && manifest.Options.PaintedTheme != PaintedTheme.ClassicPainted)
            || manifest.Options.MaximumDimension is not (1024 or 2048 or 4096))
        {
            throw new InvalidDataException("The staged-pack manifest contains invalid upscale options.");
        }

        if (manifest.Options.Scope == AssetScope.SelectedZone
            && string.IsNullOrWhiteSpace(manifest.Options.SelectedZone))
        {
            throw new InvalidDataException("A selected-zone pack does not identify its zone.");
        }

        if (manifest.Entries is null || manifest.Entries.Count == 0)
        {
            throw new InvalidDataException("The staged-pack manifest contains no artifacts.");
        }

        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        var uniqueTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.SourceLength < 0 || entry.StagedLength < 0)
            {
                throw new InvalidDataException("A staged-pack artifact has a negative length.");
            }

            ValidateHash(entry.SourceSha256, "source");
            ValidateHash(entry.StagedSha256, "staged");
            var installTarget = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                entry.RelativeInstallPath);
            if (!uniqueTargets.Add(installTarget))
            {
                throw new InvalidDataException(
                    $"The staged-pack manifest contains a duplicate target: {entry.RelativeInstallPath}");
            }

            var canonicalRelativePath = Path.GetRelativePath(paths.InstallPath, installTarget);
            _ = PathGuard.ResolveUnderRoot(payloadDirectory, canonicalRelativePath);
        }
    }

    private async Task<IReadOnlyList<StagedPackArtifactInfo>> InspectArtifactsAsync(
        ProjectPaths paths,
        string buildDirectory,
        BuildManifest manifest,
        StagedPackVerificationMode verificationMode,
        CancellationToken cancellationToken)
    {
        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        var artifacts = new List<StagedPackArtifactInfo>(manifest.Entries.Count);
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installTarget = PathGuard.ResolveUnderRoot(
                paths.InstallPath,
                entry.RelativeInstallPath);
            var canonicalRelativePath = Path.GetRelativePath(paths.InstallPath, installTarget);
            var payloadPath = PathGuard.ResolveUnderRoot(payloadDirectory, canonicalRelativePath);
            try
            {
                var file = new FileInfo(payloadPath);
                file.Refresh();
                if (!file.Exists)
                {
                    artifacts.Add(new StagedPackArtifactInfo(
                        entry,
                        canonicalRelativePath,
                        payloadPath,
                        StagedPackArtifactValidationState.Missing,
                        ObservedLength: null,
                        ObservedSha256: null,
                        "The staged payload is missing."));
                    continue;
                }

                if (file.Length != entry.StagedLength)
                {
                    artifacts.Add(new StagedPackArtifactInfo(
                        entry,
                        canonicalRelativePath,
                        payloadPath,
                        StagedPackArtifactValidationState.LengthMismatch,
                        file.Length,
                        ObservedSha256: null,
                        "The staged payload length differs from the manifest."));
                    continue;
                }

                if (verificationMode == StagedPackVerificationMode.Metadata)
                {
                    artifacts.Add(new StagedPackArtifactInfo(
                        entry,
                        canonicalRelativePath,
                        payloadPath,
                        StagedPackArtifactValidationState.MetadataValid,
                        file.Length,
                        ObservedSha256: null,
                        "The staged payload exists with the expected length."));
                    continue;
                }

                var observedLastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks;
                var cacheKey = new ArtifactVerificationCacheKey(
                    Path.GetFullPath(payloadPath),
                    entry.StagedLength,
                    entry.StagedSha256,
                    observedLastWriteTimeUtcTicks);
                if (ExactVerificationCache.ContainsKey(cacheKey))
                {
                    artifacts.Add(new StagedPackArtifactInfo(
                        entry,
                        canonicalRelativePath,
                        payloadPath,
                        StagedPackArtifactValidationState.HashVerified,
                        file.Length,
                        entry.StagedSha256,
                        "The unchanged staged payload reused its exact SHA-256 verification.",
                        observedLastWriteTimeUtcTicks));
                    continue;
                }

                var fingerprint = await FileIntegrity
                    .FingerprintAsync(payloadPath, cancellationToken)
                    .ConfigureAwait(false);
                file.Refresh();
                if (!file.Exists
                    || file.Length != entry.StagedLength
                    || file.LastWriteTimeUtc.Ticks != observedLastWriteTimeUtcTicks)
                {
                    artifacts.Add(new StagedPackArtifactInfo(
                        entry,
                        canonicalRelativePath,
                        payloadPath,
                        StagedPackArtifactValidationState.Invalid,
                        file.Exists ? file.Length : null,
                        fingerprint.Sha256,
                        "The staged payload changed while its SHA-256 was being verified."));
                    continue;
                }

                var matches = fingerprint.Length == entry.StagedLength
                    && fingerprint.Sha256.Equals(
                        entry.StagedSha256,
                        StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    ExactVerificationCache.TryAdd(cacheKey, 0);
                }

                artifacts.Add(new StagedPackArtifactInfo(
                    entry,
                    canonicalRelativePath,
                    payloadPath,
                    matches
                        ? StagedPackArtifactValidationState.HashVerified
                        : StagedPackArtifactValidationState.HashMismatch,
                    fingerprint.Length,
                    fingerprint.Sha256,
                    matches
                        ? "The staged payload passed exact SHA-256 validation."
                        : "The staged payload SHA-256 differs from the manifest.",
                    matches ? observedLastWriteTimeUtcTicks : null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsCatalogFailure(exception))
            {
                artifacts.Add(new StagedPackArtifactInfo(
                    entry,
                    canonicalRelativePath,
                    payloadPath,
                    StagedPackArtifactValidationState.Invalid,
                    ObservedLength: null,
                    ObservedSha256: null,
                    exception.Message));
            }
        }

        return artifacts.AsReadOnly();
    }

    private static void ValidateHash(string? hash, string description)
    {
        if (hash is null
            || hash.Length != 64
            || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"The staged-pack manifest contains an invalid {description} SHA-256 value.");
        }
    }

    private static StagedPackInfo Invalid(
        string manifestPath,
        string buildDirectory,
        string candidateBuildId,
        StagedPackValidationState state,
        string summary) => new(
        manifestPath,
        buildDirectory,
        candidateBuildId,
        ManifestFingerprint: null,
        Manifest: null,
        state,
        summary,
        Array.Empty<StagedPackArtifactInfo>());

    private static string TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static bool IsCatalogFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        InvalidDataException or
        JsonException or
        ArgumentException or
        NotSupportedException;

    private sealed record ArtifactVerificationCacheKey(
        string Path,
        long Length,
        string Sha256,
        long LastWriteTimeUtcTicks);
}
