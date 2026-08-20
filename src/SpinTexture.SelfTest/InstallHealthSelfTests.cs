using System.Security.Cryptography;
using SpinTexture.Core;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class InstallHealthSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-health-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var service = new InstallHealthService();
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await AssertCanceledAsync(
                    () => service.AuditLatestFastAsync(paths, canceled.Token),
                    "pre-canceled fast health audit").ConfigureAwait(false);
            }

            var none = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(InstallHealthState.None, none.State, "missing active manifest health");
            AssertEqual(0, none.Entries.Count, "missing active manifest entry count");

            paths.EnsureWorkspaceDirectories();
            var originalZone = "original-zone"u8.ToArray();
            var enhancedZone = "enhanced-zone-with-hd-textures"u8.ToArray();
            var originalObjects = "original-objects"u8.ToArray();
            var enhancedObjects = "enhanced-objects-with-hd-textures"u8.ToArray();
            var zonePath = Path.Combine(installPath, "hateplane.s3d");
            var objectsPath = Path.Combine(installPath, "hateplane_obj.s3d");
            await File.WriteAllBytesAsync(zonePath, enhancedZone, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(objectsPath, enhancedObjects, cancellationToken).ConfigureAwait(false);
            var zoneInstalledTimestamp = File.GetLastWriteTimeUtc(zonePath);
            var objectsInstalledTimestamp = File.GetLastWriteTimeUtc(objectsPath);

            var appliedManifest = CreateManifest(
                paths,
                "health-applied",
                InstallTransactionState.Applied,
                [
                    CreateArtifact(
                        "hateplane.s3d",
                        originalZone,
                        enhancedZone,
                        zoneInstalledTimestamp.Ticks),
                    CreateArtifact(
                        "hateplane_obj.s3d",
                        originalObjects,
                        enhancedObjects,
                        objectsInstalledTimestamp.Ticks)
                ]);
            var appliedManifestPath = Path.Combine(
                paths.BackupPath,
                appliedManifest.ApplyId,
                "install-manifest.json");
            await new ManifestStore().WriteInstallManifestAsync(
                appliedManifestPath,
                appliedManifest,
                cancellationToken).ConfigureAwait(false);

            var enhanced = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(InstallHealthState.EnhancedActive, enhanced.State, "enhanced install health");
            AssertEqual(2, enhanced.Entries.Count, "enhanced install entry count");
            Assert(
                enhanced.Entries.All(entry => entry.State == InstalledArtifactHealthState.Enhanced),
                "enhanced install should verify every artifact");
            AssertEqual(
                Path.GetFullPath(appliedManifestPath),
                Path.GetFullPath(enhanced.InstallManifestPath!),
                "enhanced install manifest path");
            var fastEnhanced = await service
                .AuditLatestFastAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.EnhancedActive,
                fastEnhanced.State,
                "length-optimized enhanced install health");
            Assert(
                fastEnhanced.Entries.All(entry => entry.ObservedSha256 is null),
                "metadata-matched audit should not rehash files verified by the install transaction");

            var sameSizeCorruption = enhancedZone.ToArray();
            sameSizeCorruption[0] ^= 0x5A;
            await File.WriteAllBytesAsync(zonePath, sameSizeCorruption, cancellationToken)
                .ConfigureAwait(false);
            File.SetLastWriteTimeUtc(zonePath, zoneInstalledTimestamp.AddMinutes(1));
            var fastSameSizeCorruption = await service
                .AuditLatestFastAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.MixedOrModified,
                fastSameSizeCorruption.State,
                "same-size enhanced-file corruption fast health");
            var corruptedEntry = fastSameSizeCorruption.Entries.Single(entry =>
                entry.RelativeInstallPath == "hateplane.s3d");
            AssertEqual(
                InstalledArtifactHealthState.ModifiedOrMissing,
                corruptedEntry.State,
                "same-size enhanced-file corruption state");
            Assert(
                corruptedEntry.ObservedSha256 is not null,
                "changed metadata must force SHA-256 verification before enhanced play");
            await File.WriteAllBytesAsync(zonePath, enhancedZone, cancellationToken).ConfigureAwait(false);

            await File.WriteAllBytesAsync(zonePath, originalZone, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(objectsPath, originalObjects, cancellationToken).ConfigureAwait(false);
            var original = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.RevertedToOriginal,
                original.State,
                "fully reverted install health");
            Assert(
                original.Entries.All(entry => entry.State == InstalledArtifactHealthState.Original),
                "reverted install should verify every original artifact");
            var fastOriginal = await service
                .AuditLatestFastAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.RevertedToOriginal,
                fastOriginal.State,
                "length-optimized reverted install health");
            Assert(
                fastOriginal.Entries.All(entry => entry.ObservedSha256 is null),
                "length-optimized audit should identify launcher restoration without hashing different-size archives");

            await File.WriteAllBytesAsync(zonePath, enhancedZone, cancellationToken).ConfigureAwait(false);
            var mixed = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(InstallHealthState.MixedOrModified, mixed.State, "mixed install health");
            AssertEqual(
                InstalledArtifactHealthState.Enhanced,
                mixed.Entries.Single(entry => entry.RelativeInstallPath == "hateplane.s3d").State,
                "mixed enhanced artifact state");
            AssertEqual(
                InstalledArtifactHealthState.Original,
                mixed.Entries.Single(entry => entry.RelativeInstallPath == "hateplane_obj.s3d").State,
                "mixed original artifact state");

            await File.WriteAllTextAsync(objectsPath, "externally-modified", cancellationToken)
                .ConfigureAwait(false);
            var modified = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(InstallHealthState.MixedOrModified, modified.State, "modified install health");
            AssertEqual(
                InstalledArtifactHealthState.ModifiedOrMissing,
                modified.Entries.Single(entry => entry.RelativeInstallPath == "hateplane_obj.s3d").State,
                "externally modified artifact state");
            var fastModified = await service
                .AuditLatestFastAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.MixedOrModified,
                fastModified.State,
                "length-optimized modified install health");
            AssertEqual(
                InstalledArtifactHealthState.ModifiedOrMissing,
                fastModified.Entries.Single(
                    entry => entry.RelativeInstallPath == "hateplane_obj.s3d").State,
                "length-optimized externally modified artifact state");

            File.Delete(objectsPath);
            var missing = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(InstallHealthState.MixedOrModified, missing.State, "missing install health");
            var missingEntry = missing.Entries.Single(
                entry => entry.RelativeInstallPath == "hateplane_obj.s3d");
            AssertEqual(
                InstalledArtifactHealthState.ModifiedOrMissing,
                missingEntry.State,
                "missing artifact state");
            Assert(missingEntry.ObservedLength is null, "missing artifact should not report a length");
            Assert(missingEntry.ObservedSha256 is null, "missing artifact should not report a hash");
            var fastMissing = await service
                .AuditLatestFastAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.MixedOrModified,
                fastMissing.State,
                "length-optimized missing install health");
            AssertEqual(
                InstalledArtifactHealthState.ModifiedOrMissing,
                fastMissing.Entries.Single(
                    entry => entry.RelativeInstallPath == "hateplane_obj.s3d").State,
                "length-optimized missing artifact state");

            var recoveryManifest = appliedManifest with
            {
                State = InstallTransactionState.RecoveryRequired
            };
            await new ManifestStore().WriteInstallManifestAsync(
                appliedManifestPath,
                recoveryManifest,
                cancellationToken).ConfigureAwait(false);
            var recovery = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.RecoveryRequired,
                recovery.State,
                "recovery transaction health");
            AssertEqual(2, recovery.Entries.Count, "recovery audit should still inspect live artifacts");

            await File.WriteAllBytesAsync(objectsPath, enhancedObjects, cancellationToken).ConfigureAwait(false);
            await new ManifestStore().WriteInstallManifestAsync(
                appliedManifestPath,
                appliedManifest,
                cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(appliedManifestPath, DateTime.UtcNow.AddMinutes(-2));

            var restoredManifest = CreateManifest(
                paths,
                "newer-restored",
                InstallTransactionState.Restored,
                [CreateArtifact("hateplane.s3d", originalZone, enhancedZone)]);
            var restoredManifestPath = Path.Combine(
                paths.BackupPath,
                restoredManifest.ApplyId,
                "install-manifest.json");
            await new ManifestStore().WriteInstallManifestAsync(
                restoredManifestPath,
                restoredManifest,
                cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(restoredManifestPath, DateTime.UtcNow.AddMinutes(-1));

            var latestActive = await service.AuditLatestAsync(paths, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.EnhancedActive,
                latestActive.State,
                "newer completed transaction should not hide active install");
            AssertEqual(
                appliedManifest.ApplyId,
                latestActive.ApplyId!,
                "latest active transaction selection");

            await TestEqualLengthFastAuditAsync(root, cancellationToken).ConfigureAwait(false);
            await TestLauncherUpdateDetectionAuditAsync(root, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestEqualLengthFastAuditAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var installPath = Path.Combine(root, "EqualLengthEverQuest");
        var workspacePath = Path.Combine(root, "EqualLengthWorkspace");
        Directory.CreateDirectory(installPath);
        var paths = new ProjectPaths(installPath, workspacePath);
        paths.EnsureWorkspaceDirectories();

        var original = "original-payload"u8.ToArray();
        var enhanced = "enhanced-payload"u8.ToArray();
        var modified = "modified-payload"u8.ToArray();
        AssertEqual(original.Length, enhanced.Length, "equal-length fixture setup");
        AssertEqual(original.Length, modified.Length, "same-length modified fixture setup");
        var livePath = Path.Combine(installPath, "hateplane.s3d");
        var manifest = CreateManifest(
            paths,
            "equal-length-health",
            InstallTransactionState.Applied,
            [CreateArtifact("hateplane.s3d", original, enhanced)]);
        var manifestPath = Path.Combine(
            paths.BackupPath,
            manifest.ApplyId,
            "install-manifest.json");
        await new ManifestStore().WriteInstallManifestAsync(
            manifestPath,
            manifest,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllBytesAsync(livePath, enhanced, cancellationToken).ConfigureAwait(false);
        var enhancedHealth = await new InstallHealthService()
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.EnhancedActive,
            enhancedHealth.State,
            "equal-length enhanced fast health");
        Assert(
            enhancedHealth.Entries.Single().ObservedSha256 is not null,
            "equal-length enhanced artifact must be hash-verified");

        await File.WriteAllBytesAsync(livePath, original, cancellationToken).ConfigureAwait(false);
        var originalHealth = await new InstallHealthService()
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.RevertedToOriginal,
            originalHealth.State,
            "equal-length original fast health");
        Assert(
            originalHealth.Entries.Single().ObservedSha256 is not null,
            "equal-length original artifact must be hash-verified");

        await File.WriteAllBytesAsync(livePath, modified, cancellationToken).ConfigureAwait(false);
        var modifiedHealth = await new InstallHealthService()
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.MixedOrModified,
            modifiedHealth.State,
            "equal-length modified fast health");
        AssertEqual(
            InstalledArtifactHealthState.ModifiedOrMissing,
            modifiedHealth.Entries.Single().State,
            "equal-length modified artifact state");
        Assert(
            modifiedHealth.Entries.Single().ObservedSha256 is not null,
            "equal-length modified artifact must be hash-verified");

        var invalidManifest = manifest with
        {
            Entries =
            [
                manifest.Entries[0] with
                {
                    InstalledSha256 = "not-a-valid-sha256"
                }
            ]
        };
        await new ManifestStore().WriteInstallManifestAsync(
            manifestPath,
            invalidManifest,
            cancellationToken).ConfigureAwait(false);
        var invalidHealth = await new InstallHealthService()
            .AuditLatestFastAsync(paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.RecoveryRequired,
            invalidHealth.State,
            "fast audit invalid-manifest health");
        AssertEqual(
            0,
            invalidHealth.Entries.Count,
            "invalid manifest must not emit length-derived artifact states");
    }

    private static async Task TestLauncherUpdateDetectionAuditAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var installPath = Path.Combine(root, "LauncherDetectionEverQuest");
        var workspacePath = Path.Combine(root, "LauncherDetectionWorkspace");
        Directory.CreateDirectory(installPath);
        var paths = new ProjectPaths(installPath, workspacePath);
        paths.EnsureWorkspaceDirectories();

        var original = "original-client"u8.ToArray();
        var enhanced = "enhanced-texture-pack"u8.ToArray();
        var launcherReplacement = "launcher-patched-file"u8.ToArray();
        AssertEqual(
            enhanced.Length,
            launcherReplacement.Length,
            "same-length launcher detection fixture setup");
        var livePath = Path.Combine(installPath, "globalogm_chr.s3d");
        await File.WriteAllBytesAsync(livePath, enhanced, cancellationToken)
            .ConfigureAwait(false);
        var installedTimestamp = File.GetLastWriteTimeUtc(livePath);
        var appliedUtc = new DateTimeOffset(
            DateTime.UtcNow.AddHours(-2),
            TimeSpan.Zero);
        var artifact = CreateArtifact(
            "globalogm_chr.s3d",
            original,
            enhanced,
            installedTimestamp.Ticks);
        var manifest = new InstallManifest(
            InstallManifest.CurrentSchemaVersion,
            "launcher-detection",
            appliedUtc,
            paths.InstallPath,
            "launcher-detection-build",
            Path.Combine(
                paths.StagingPath,
                "launcher-detection-build",
                "build-manifest.json"),
            InstallTransactionState.Applied,
            [artifact]);
        var manifestPath = Path.Combine(
            paths.BackupPath,
            manifest.ApplyId,
            "install-manifest.json");
        var store = new ManifestStore();
        await store.WriteInstallManifestAsync(
            manifestPath,
            manifest,
            cancellationToken).ConfigureAwait(false);
        var healthService = new InstallHealthService(store);
        var workflow = new TexturePackWorkflow(
            manifestStore: store,
            installHealthService: healthService);

        await WriteLaunchPadLogAsync(
            installPath,
            [
                LaunchPadHeader(appliedUtc.AddMinutes(-10)),
                "1111-00:00:01:All files are up to date"
            ],
            cancellationToken).ConfigureAwait(false);
        var clean = await workflow
            .AuditInstallHealthForLauncherUpdateDetectionAsync(
                paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.EnhancedActive,
            clean.State,
            "clean launcher-detection health");
        Assert(
            clean.Entries.Single().ObservedSha256 is null,
            "clean enhanced launcher detection should retain the metadata fast path");

        await File.WriteAllBytesAsync(
            livePath,
            launcherReplacement,
            cancellationToken).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(livePath, installedTimestamp);
        await WriteLaunchPadLogAsync(
            installPath,
            [
                LaunchPadHeader(appliedUtc.AddMinutes(10)),
                "2222-00:00:00:Found 1 file(s) to update.",
                "2222-00:00:01:Replacing globalogm_chr.s3d",
                "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)"
            ],
            cancellationToken).ConfigureAwait(false);
        var patched = await workflow
            .AuditInstallHealthForLauncherUpdateDetectionAsync(
                paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.MixedOrModified,
            patched.State,
            "same-length launcher replacement detection health");
        AssertEqual(
            InstalledArtifactHealthState.ModifiedOrMissing,
            patched.Entries.Single().State,
            "same-length launcher replacement detection state");
        Assert(
            patched.Entries.Single().ObservedSha256 is { Length: 64 },
            "post-install LaunchPad activity must force exact SHA-256 detection");

        await File.WriteAllBytesAsync(livePath, original, cancellationToken)
            .ConfigureAwait(false);
        await WriteLaunchPadLogAsync(
            installPath,
            [
                LaunchPadHeader(appliedUtc.AddMinutes(-10)),
                "3333-00:00:01:All files are up to date"
            ],
            cancellationToken).ConfigureAwait(false);
        var reverted = await workflow
            .AuditInstallHealthForLauncherUpdateDetectionAsync(
                paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            InstallHealthState.RevertedToOriginal,
            reverted.State,
            "non-enhanced launcher-detection health");
        Assert(
            reverted.Entries.Single().ObservedSha256 is { Length: 64 },
            "non-enhanced detection state must escalate from metadata to exact health");
    }

    private static Task WriteLaunchPadLogAsync(
        string installPath,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken) =>
        File.WriteAllLinesAsync(
            Path.Combine(
                installPath,
                LaunchPadUpdateEvidenceService.DownloadLogFileName),
            lines,
            cancellationToken);

    private static string LaunchPadHeader(DateTimeOffset utc) =>
        $"**** Starting at {utc.ToLocalTime():ddd MMM d HH:mm:ss yyyy} with plug-in 1.0.3.204 ****";

    private static InstallManifest CreateManifest(
        ProjectPaths paths,
        string applyId,
        InstallTransactionState state,
        IReadOnlyList<InstalledArtifact> entries) => new(
        InstallManifest.CurrentSchemaVersion,
        applyId,
        DateTimeOffset.UtcNow,
        paths.InstallPath,
        "health-build",
        Path.Combine(paths.StagingPath, "health-build", "build-manifest.json"),
        state,
        entries);

    private static InstalledArtifact CreateArtifact(
        string relativePath,
        byte[] original,
        byte[] enhanced,
        long? installedLastWriteTimeUtcTicks = null) => new(
        relativePath,
        OriginalExisted: true,
        original.LongLength,
        Hash(original),
        Path.Combine("payload", relativePath),
        enhanced.LongLength,
        Hash(enhanced),
        installedLastWriteTimeUtcTicks);

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task AssertCanceledAsync(
        Func<Task> action,
        string description)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException($"Self-test failed: {description} did not cancel.");
    }

    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(root, recursive: true);
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description}; expected '{expected}', got '{actual}'.");
        }
    }
}
