using System.Security;
using System.Text.Json;

namespace SpinTexture.Core.Pipeline;

public enum InstallHealthState
{
    None,
    EnhancedActive,
    RevertedToOriginal,
    MixedOrModified,
    RecoveryRequired
}

public enum InstalledArtifactHealthState
{
    Enhanced,
    Original,
    EnhancedAndOriginal,
    ModifiedOrMissing
}

public sealed record InstalledArtifactHealth(
    string RelativeInstallPath,
    InstalledArtifactHealthState State,
    long? ObservedLength,
    string? ObservedSha256);

public sealed record InstallHealthReport(
    InstallHealthState State,
    string? InstallManifestPath,
    string? ApplyId,
    InstallTransactionState? TransactionState,
    string Summary,
    IReadOnlyList<InstalledArtifactHealth> Entries);

/// <summary>
/// Verifies the live install against the most recent unfinished install transaction.
/// This service is read-only: it never changes the install, manifest, or backup files.
/// </summary>
public sealed class InstallHealthService
{
    private readonly ManifestStore _manifestStore;

    public InstallHealthService(ManifestStore? manifestStore = null)
    {
        _manifestStore = manifestStore ?? new ManifestStore();
    }

    public async Task<InstallHealthReport> AuditLatestAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default) =>
        await AuditLatestCoreAsync(paths, optimizeKnownLengths: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<InstallHealthReport> AuditLatestFastAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default) =>
        await AuditLatestCoreAsync(paths, optimizeKnownLengths: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<InstallHealthReport> AuditLatestCoreAsync(
        ProjectPaths paths,
        bool optimizeKnownLengths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(paths.BackupPath))
        {
            return NoActiveInstall();
        }

        IReadOnlyList<string> candidates;
        try
        {
            candidates = FindManifestCandidates(paths.BackupPath, cancellationToken);
        }
        catch (Exception exception) when (IsAuditFailure(exception))
        {
            return RecoveryRequired(
                manifestPath: null,
                manifest: null,
                $"SpinTexture could not inspect its install backups: {exception.Message}");
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backupDirectory = Path.GetDirectoryName(candidate)!;
            if (File.Exists(Path.Combine(backupDirectory, "restore-complete.json")))
            {
                continue;
            }

            InstallManifest manifest;
            try
            {
                manifest = await _manifestStore
                    .ReadInstallManifestAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsAuditFailure(exception))
            {
                return RecoveryRequired(
                    candidate,
                    manifest: null,
                    $"The latest SpinTexture install manifest could not be read: {exception.Message}");
            }

            if (!Enum.IsDefined(manifest.State))
            {
                return RecoveryRequired(
                    candidate,
                    manifest,
                    $"The latest SpinTexture install manifest has an unknown transaction state: {manifest.State}.");
            }

            if (manifest.State is InstallTransactionState.Restored or InstallTransactionState.RolledBack)
            {
                continue;
            }

            return await AuditManifestAsync(
                    paths,
                    candidate,
                    manifest,
                    optimizeKnownLengths,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return NoActiveInstall();
    }

    private static IReadOnlyList<string> FindManifestCandidates(
        string backupPath,
        CancellationToken cancellationToken)
    {
        // Apply IDs are validated as single directory names and every transaction
        // manifest is stored at Backups/<apply-id>/install-manifest.json. Searching
        // deeper would needlessly enumerate every archived payload in a whole-game
        // backup each time the user launches.
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };
        var candidates = new List<string>();
        foreach (var transactionDirectory in Directory.EnumerateDirectories(
                     backupPath,
                     "*",
                     enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(transactionDirectory, "install-manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            candidates.Add(PathGuard.EnsurePathUnderRoot(backupPath, manifestPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<InstallHealthReport> AuditManifestAsync(
        ProjectPaths paths,
        string manifestPath,
        InstallManifest manifest,
        bool optimizeKnownLengths,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateManifest(paths, manifest);
            var entries = new List<InstalledArtifactHealth>(manifest.Entries.Count);
            foreach (var artifact in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var livePath = PathGuard.ResolveUnderRoot(paths.InstallPath, artifact.RelativeInstallPath);
                entries.Add(await ObserveArtifactAsync(
                        artifact,
                        livePath,
                        optimizeKnownLengths,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            if (manifest.State is InstallTransactionState.Preparing
                or InstallTransactionState.RecoveryRequired)
            {
                return RecoveryRequired(
                    manifestPath,
                    manifest,
                    manifest.State == InstallTransactionState.Preparing
                        ? "The latest SpinTexture install transaction did not finish and requires recovery."
                        : "The latest SpinTexture install transaction is marked as requiring recovery.",
                    entries);
            }

            var allEnhanced = entries.All(entry => entry.State is
                InstalledArtifactHealthState.Enhanced or
                InstalledArtifactHealthState.EnhancedAndOriginal);
            var allOriginal = entries.All(entry => entry.State is
                InstalledArtifactHealthState.Original or
                InstalledArtifactHealthState.EnhancedAndOriginal);
            if (allEnhanced)
            {
                return new InstallHealthReport(
                    InstallHealthState.EnhancedActive,
                    manifestPath,
                    manifest.ApplyId,
                    manifest.State,
                    $"All {entries.Count} installed artifact(s) match the enhanced files recorded by SpinTexture.",
                    entries.AsReadOnly());
            }

            if (allOriginal)
            {
                return new InstallHealthReport(
                    InstallHealthState.RevertedToOriginal,
                    manifestPath,
                    manifest.ApplyId,
                    manifest.State,
                    $"All {entries.Count} installed artifact(s) match their original files; the enhanced pack is not active.",
                    entries.AsReadOnly());
            }

            var enhancedCount = entries.Count(entry =>
                entry.State == InstalledArtifactHealthState.Enhanced);
            var originalCount = entries.Count(entry =>
                entry.State == InstalledArtifactHealthState.Original);
            var identicalCount = entries.Count(entry =>
                entry.State == InstalledArtifactHealthState.EnhancedAndOriginal);
            var modifiedCount = entries.Count(entry =>
                entry.State == InstalledArtifactHealthState.ModifiedOrMissing);
            return new InstallHealthReport(
                InstallHealthState.MixedOrModified,
                manifestPath,
                manifest.ApplyId,
                manifest.State,
                $"The live install is mixed or modified: {enhancedCount} enhanced, "
                + $"{originalCount} original, {identicalCount} identical in both versions, "
                + $"and {modifiedCount} missing or unknown artifact(s).",
                entries.AsReadOnly());
        }
        catch (Exception exception) when (IsAuditFailure(exception))
        {
            return RecoveryRequired(
                manifestPath,
                manifest,
                $"SpinTexture could not safely verify the latest install: {exception.Message}");
        }
    }

    private static async Task<InstalledArtifactHealth> ObserveArtifactAsync(
        InstalledArtifact artifact,
        string livePath,
        bool optimizeKnownLengths,
        CancellationToken cancellationToken)
    {
        if (optimizeKnownLengths)
        {
            var quickResult = ObserveArtifactByLength(artifact, livePath);
            if (quickResult is not null)
            {
                return quickResult;
            }
        }

        FileFingerprint? fingerprint = null;
        if (File.Exists(livePath))
        {
            try
            {
                fingerprint = await FileIntegrity.FingerprintAsync(livePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // The file disappeared between existence and fingerprint checks.
            }
            catch (DirectoryNotFoundException)
            {
                // The containing directory disappeared during the read-only audit.
            }
        }

        var matchesEnhanced = fingerprint is not null
            && fingerprint.Length == artifact.InstalledLength
            && string.Equals(
                fingerprint.Sha256,
                artifact.InstalledSha256,
                StringComparison.OrdinalIgnoreCase);
        var matchesOriginal = artifact.OriginalExisted
            ? fingerprint is not null
              && artifact.OriginalSha256 is not null
              && fingerprint.Length == artifact.OriginalLength
              && string.Equals(
                  fingerprint.Sha256,
                  artifact.OriginalSha256,
                  StringComparison.OrdinalIgnoreCase)
            : fingerprint is null;

        var state = (matchesEnhanced, matchesOriginal) switch
        {
            (true, true) => InstalledArtifactHealthState.EnhancedAndOriginal,
            (true, false) => InstalledArtifactHealthState.Enhanced,
            (false, true) => InstalledArtifactHealthState.Original,
            _ => InstalledArtifactHealthState.ModifiedOrMissing
        };
        return new InstalledArtifactHealth(
            artifact.RelativeInstallPath,
            state,
            fingerprint?.Length,
            fingerprint?.Sha256);
    }

    private static InstalledArtifactHealth? ObserveArtifactByLength(
        InstalledArtifact artifact,
        string livePath)
    {
        var file = new FileInfo(livePath);
        file.Refresh();
        if (!file.Exists)
        {
            return artifact.OriginalExisted
                ? new InstalledArtifactHealth(
                    artifact.RelativeInstallPath,
                    InstalledArtifactHealthState.ModifiedOrMissing,
                    ObservedLength: null,
                    ObservedSha256: null)
                : new InstalledArtifactHealth(
                    artifact.RelativeInstallPath,
                    InstalledArtifactHealthState.Original,
                    ObservedLength: null,
                    ObservedSha256: null);
        }

        var matchesEnhancedLength = file.Length == artifact.InstalledLength;
        var matchesOriginalLength = artifact.OriginalExisted
            && file.Length == artifact.OriginalLength;
        if (matchesEnhancedLength && !matchesOriginalLength)
        {
            // Length alone is not an integrity signal: a corrupt or externally
            // modified payload can retain the enhanced file's exact size. Trust
            // metadata only when it matches the timestamp captured immediately
            // after the install's SHA-256-verified atomic commit. Older manifests
            // have no timestamp and intentionally fall through to hashing.
            if (artifact.InstalledLastWriteTimeUtcTicks is not null
                && file.LastWriteTimeUtc.Ticks == artifact.InstalledLastWriteTimeUtcTicks.Value)
            {
                return new InstalledArtifactHealth(
                    artifact.RelativeInstallPath,
                    InstalledArtifactHealthState.Enhanced,
                    file.Length,
                    ObservedSha256: null);
            }

            return null;
        }

        if (matchesOriginalLength && !matchesEnhancedLength)
        {
            return new InstalledArtifactHealth(
                artifact.RelativeInstallPath,
                InstalledArtifactHealthState.Original,
                file.Length,
                ObservedSha256: null);
        }

        if (!matchesEnhancedLength && !matchesOriginalLength)
        {
            return new InstalledArtifactHealth(
                artifact.RelativeInstallPath,
                InstalledArtifactHealthState.ModifiedOrMissing,
                file.Length,
                ObservedSha256: null);
        }

        // Same-size original/enhanced payloads cannot be distinguished safely
        // from metadata, so retain the exact SHA-256 path for those entries.
        return null;
    }

    private static void ValidateManifest(ProjectPaths paths, InstallManifest manifest)
    {
        if (!PathGuard.SamePath(paths.InstallPath, manifest.InstallPath))
        {
            throw new InvalidDataException("The install manifest belongs to a different EverQuest installation.");
        }

        if (manifest.Entries is null || manifest.Entries.Count == 0)
        {
            throw new InvalidDataException("The active install manifest does not contain any artifacts.");
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in manifest.Entries)
        {
            if (!uniquePaths.Add(artifact.RelativeInstallPath))
            {
                throw new InvalidDataException(
                    $"The install manifest contains a duplicate artifact: {artifact.RelativeInstallPath}");
            }

            ValidateHash(artifact.InstalledSha256, "installed");
            if (artifact.InstalledLength < 0 || artifact.OriginalLength < 0)
            {
                throw new InvalidDataException("The install manifest contains a negative artifact length.");
            }

            if (artifact.InstalledLastWriteTimeUtcTicks is <= 0)
            {
                throw new InvalidDataException(
                    "The install manifest contains an invalid installed-file timestamp.");
            }

            if (artifact.OriginalExisted)
            {
                if (artifact.OriginalSha256 is null || artifact.BackupRelativePath is null)
                {
                    throw new InvalidDataException(
                        $"The install manifest is missing original metadata for {artifact.RelativeInstallPath}.");
                }

                ValidateHash(artifact.OriginalSha256, "original");
            }

            _ = PathGuard.ResolveUnderRoot(paths.InstallPath, artifact.RelativeInstallPath);
        }
    }

    private static void ValidateHash(string? hash, string description)
    {
        if (hash is null
            || hash.Length != 64
            || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"The install manifest contains an invalid {description} SHA-256 value.");
        }
    }

    private static InstallHealthReport NoActiveInstall() => new(
        InstallHealthState.None,
        InstallManifestPath: null,
        ApplyId: null,
        TransactionState: null,
        "No active SpinTexture install manifest was found.",
        Array.Empty<InstalledArtifactHealth>());

    private static InstallHealthReport RecoveryRequired(
        string? manifestPath,
        InstallManifest? manifest,
        string summary,
        IReadOnlyList<InstalledArtifactHealth>? entries = null) => new(
        InstallHealthState.RecoveryRequired,
        manifestPath,
        manifest?.ApplyId,
        manifest?.State,
        summary,
        entries ?? Array.Empty<InstalledArtifactHealth>());

    private static bool IsAuditFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        InvalidDataException or
        JsonException or
        ArgumentException or
        NotSupportedException;
}
