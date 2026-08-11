using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedPackReinstallSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-staged-reinstall-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var originalZone = "original-hateplane-zone-archive"u8.ToArray();
            var enhancedZone = "enhanced-hateplane-zone-archive-with-hd-textures"u8.ToArray();
            var originalObjects = "original-hateplane-object-archive"u8.ToArray();
            var enhancedObjects = "enhanced-hateplane-object-archive-with-hd-textures"u8.ToArray();
            var zonePath = Path.Combine(installPath, "hateplane.s3d");
            var objectsPath = Path.Combine(installPath, "hateplane_obj.s3d");
            await File.WriteAllBytesAsync(zonePath, originalZone, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(objectsPath, originalObjects, cancellationToken)
                .ConfigureAwait(false);

            var buildInvocationCount = 0;
            var builder = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    Interlocked.Increment(ref buildInvocationCount);
                    var bytes = context.RelativeInstallPath.Equals(
                        "hateplane.s3d",
                        StringComparison.OrdinalIgnoreCase)
                        ? enhancedZone
                        : enhancedObjects;
                    await File.WriteAllBytesAsync(context.DestinationPath, bytes, token)
                        .ConfigureAwait(false);
                });
            var staged = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended with { InstallAfterBuild = false },
                    [
                        new StagedBuildItem("hateplane.s3d", builder),
                        new StagedBuildItem("hateplane_obj.s3d", builder)
                    ],
                    BuildId: "staged-hateplane-pack"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(2, buildInvocationCount, "initial staged artifact build count");

            var stagedManifestFingerprint = await FileIntegrity
                .FingerprintAsync(staged.ManifestPath, cancellationToken)
                .ConfigureAwait(false);
            var stagedPayloadFingerprints = await FingerprintStagedPayloadAsync(
                staged,
                cancellationToken).ConfigureAwait(false);

            var manifestStore = new ManifestStore();
            var transactions = new InstallTransactionService(
                manifestStore,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var healthService = new InstallHealthService(manifestStore);
            var workflow = new TexturePackWorkflow(
                installTransactionService: transactions,
                manifestStore: manifestStore,
                installHealthService: healthService);

            var firstApply = await workflow.ApplyLatestStagedPackAsync(
                paths,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertLiveHashesMatchStagedManifest(paths, staged.Manifest);

            var firstHealth = await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.EnhancedActive,
                firstHealth.State,
                "first staged-pack install health");
            AssertEqual(firstApply.ApplyId, firstHealth.ApplyId!, "first active apply id");

            // Simulate LaunchPad repairing every modified archive back to its original bytes.
            await File.WriteAllBytesAsync(zonePath, originalZone, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(objectsPath, originalObjects, cancellationToken)
                .ConfigureAwait(false);

            var revertedHealth = await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.RevertedToOriginal,
                revertedHealth.State,
                "launcher-reverted staged-pack health");
            Assert(
                revertedHealth.Entries.All(entry =>
                    entry.State == InstalledArtifactHealthState.Original),
                "launcher-reverted health should identify every original artifact");

            var secondApply = await workflow.ApplyLatestStagedPackAsync(
                paths,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            AssertEqual(
                2,
                buildInvocationCount,
                "reinstall should reuse the staged payload without invoking artifact builders");
            Assert(
                !firstApply.ApplyId.Equals(secondApply.ApplyId, StringComparison.OrdinalIgnoreCase),
                "reinstall should create a distinct install transaction");
            Assert(
                PathGuard.SamePath(staged.ManifestPath, firstApply.Manifest.BuildManifestPath),
                "first install should reference the staged manifest");
            Assert(
                PathGuard.SamePath(staged.ManifestPath, secondApply.Manifest.BuildManifestPath),
                "reinstall should reference the exact same staged manifest");
            AssertEqual(staged.BuildId, secondApply.Manifest.BuildId, "reinstalled build id");

            await AssertStagedPayloadUnchangedAsync(
                staged,
                stagedManifestFingerprint,
                stagedPayloadFingerprints,
                cancellationToken).ConfigureAwait(false);
            AssertLiveHashesMatchStagedManifest(paths, staged.Manifest);

            var firstRetiredManifest = await manifestStore.ReadInstallManifestAsync(
                firstApply.InstallManifestPath,
                cancellationToken).ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.Restored,
                firstRetiredManifest.State,
                "launcher-reverted install manifest should be retired");
            Assert(
                File.Exists(Path.Combine(firstApply.BackupDirectory, "restore-complete.json")),
                "launcher-reverted install should have a retirement marker");

            var allInstallManifests = Directory.EnumerateFiles(
                    paths.BackupPath,
                    "install-manifest.json",
                    SearchOption.AllDirectories)
                .ToArray();
            AssertEqual(2, allInstallManifests.Length, "install transaction count after reinstall");
            var activeInstallManifests = allInstallManifests
                .Where(path => !File.Exists(Path.Combine(
                    Path.GetDirectoryName(path)!,
                    "restore-complete.json")))
                .ToArray();
            AssertEqual(1, activeInstallManifests.Length, "active backup count after reinstall");
            Assert(
                PathGuard.SamePath(secondApply.InstallManifestPath, activeInstallManifests[0]),
                "the reinstalled transaction should be the sole active backup");

            var secondHealth = await workflow.AuditInstallHealthAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.EnhancedActive,
                secondHealth.State,
                "reinstalled staged-pack health");
            AssertEqual(secondApply.ApplyId, secondHealth.ApplyId!, "reinstalled active apply id");

            await transactions.RestoreAsync(
                paths,
                secondApply.InstallManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var restoredZone = await File.ReadAllBytesAsync(zonePath, cancellationToken)
                .ConfigureAwait(false);
            var restoredObjects = await File.ReadAllBytesAsync(objectsPath, cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                originalZone,
                restoredZone,
                "final restore should recover the exact original zone archive");
            AssertSequenceEqual(
                originalObjects,
                restoredObjects,
                "final restore should recover the exact original object archive");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, FileFingerprint>>
        FingerprintStagedPayloadAsync(
            StagedBuildResult staged,
            CancellationToken cancellationToken)
    {
        var fingerprints = new Dictionary<string, FileFingerprint>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in staged.Manifest.Entries)
        {
            var stagedPath = PathGuard.ResolveUnderRoot(
                Path.Combine(staged.BuildDirectory, "payload"),
                entry.RelativeInstallPath);
            fingerprints.Add(
                entry.RelativeInstallPath,
                await FileIntegrity.FingerprintAsync(stagedPath, cancellationToken)
                    .ConfigureAwait(false));
        }

        return fingerprints;
    }

    private static async Task AssertStagedPayloadUnchangedAsync(
        StagedBuildResult staged,
        FileFingerprint expectedManifest,
        IReadOnlyDictionary<string, FileFingerprint> expectedPayload,
        CancellationToken cancellationToken)
    {
        var observedManifest = await FileIntegrity
            .FingerprintAsync(staged.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(expectedManifest, observedManifest, "staged manifest after reinstall");

        var observedPayload = await FingerprintStagedPayloadAsync(staged, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(expectedPayload.Count, observedPayload.Count, "staged payload file count");
        foreach (var expected in expectedPayload)
        {
            Assert(
                observedPayload.TryGetValue(expected.Key, out var observed),
                $"staged payload should still contain {expected.Key}");
            AssertEqual(expected.Value, observed!, $"staged payload hash for {expected.Key}");
        }
    }

    private static void AssertLiveHashesMatchStagedManifest(
        ProjectPaths paths,
        BuildManifest stagedManifest)
    {
        foreach (var entry in stagedManifest.Entries)
        {
            var livePath = PathGuard.ResolveUnderRoot(paths.InstallPath, entry.RelativeInstallPath);
            var bytes = File.ReadAllBytes(livePath);
            var hash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(bytes));
            AssertEqual(entry.StagedLength, bytes.LongLength, $"live enhanced length for {entry.RelativeInstallPath}");
            AssertEqual(entry.StagedSha256, hash, $"live enhanced hash for {entry.RelativeInstallPath}");
        }
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

    private static void AssertSequenceEqual(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }
}
