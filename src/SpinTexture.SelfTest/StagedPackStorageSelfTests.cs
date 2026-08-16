using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedPackStorageSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-pack-storage-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var profilesRoot = Path.Combine(root, "Profiles");
        var storageBase = Path.Combine(root, "LargePackDrive");
        Directory.CreateDirectory(installPath);
        Directory.CreateDirectory(storageBase);

        try
        {
            var paths = WorkspaceLocator.ForInstallDefault(installPath, profilesRoot);
            paths.EnsureWorkspaceDirectories();
            var firstPack = Path.Combine(paths.StagingPath, "build-one");
            var secondPack = Path.Combine(paths.StagingPath, "build-two", "payload");
            Directory.CreateDirectory(firstPack);
            Directory.CreateDirectory(secondPack);
            await File.WriteAllBytesAsync(
                Path.Combine(firstPack, "manifest.json"),
                "manifest-one"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(firstPack, "texture-report.json"),
                "report-one"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(secondPack, "airplane.s3d"),
                Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(paths.StagingPath, "build-two", "manifest.json"),
                "manifest-two"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            var resumableBuild = Path.Combine(paths.StagingPath, "build-resumable");
            Directory.CreateDirectory(Path.Combine(resumableBuild, "work"));
            await File.WriteAllBytesAsync(
                Path.Combine(resumableBuild, "build-checkpoint.json"),
                "checkpoint"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(resumableBuild, "work", "partial.bin"),
                new byte[512],
                cancellationToken).ConfigureAwait(false);
            var leftoverBuild = Path.Combine(paths.StagingPath, "build-leftover");
            Directory.CreateDirectory(leftoverBuild);
            await File.WriteAllBytesAsync(
                Path.Combine(leftoverBuild, "orphan.tmp"),
                new byte[256],
                cancellationToken).ConfigureAwait(false);
            var installTransactionDirectory = Path.Combine(paths.BackupPath, "apply-storage-test");
            Directory.CreateDirectory(installTransactionDirectory);
            var installManifestPath = Path.Combine(installTransactionDirectory, "install-manifest.json");
            var manifestStore = new ManifestStore();
            await manifestStore.WriteInstallManifestAsync(
                installManifestPath,
                new InstallManifest(
                    InstallManifest.CurrentSchemaVersion,
                    "apply-storage-test",
                    DateTimeOffset.UtcNow,
                    paths.InstallPath,
                    "build-one",
                    Path.Combine(firstPack, "manifest.json"),
                    InstallTransactionState.Applied,
                    []),
                cancellationToken).ConfigureAwait(false);

            var service = new StagedPackStorageService();
            var original = await service.InspectAsync(paths, cancellationToken).ConfigureAwait(false);
            Assert(original.UsesDefaultLocation, "initial pack library should use the default location");
            AssertEqual(2, original.PackCount, "completed pack count before migration");
            AssertEqual(1, original.ResumableBuildCount, "resumable build count before migration");
            AssertEqual(1, original.LeftoverDirectoryCount, "leftover build count before migration");
            Assert(original.CompletedPackBytes > original.LeftoverBytes,
                "completed-pack storage should be broken out from leftovers");
            Assert(original.ResumableBuildBytes >= 512,
                "resumable-build storage should include partial payloads");
            Assert(original.Summary.Contains("resumable", StringComparison.OrdinalIgnoreCase),
                "storage summary should explain protected resumable space");
            Assert(original.Summary.Contains("logical", StringComparison.OrdinalIgnoreCase),
                "storage summary must not overstate hard-linked payload sizes as exact disk use");

            var plan = await service.PlanAsync(paths, storageBase, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(!plan.IsNoOp, "custom destination should require a move");
            AssertEqual(7, plan.FileCount, "planned migration file count");
            var moved = await service.MoveAsync(paths, plan, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(moved.OldLibraryRemoved, "verified migration should remove the old managed copy");
            Assert(!Directory.Exists(paths.StagingPath), "old default staging directory should be removed");
            Assert(Directory.Exists(plan.DestinationPath), "custom staged-pack directory should exist");
            var relocatedInstall = await manifestStore.ReadInstallManifestAsync(
                installManifestPath,
                cancellationToken).ConfigureAwait(false);
            AssertEqual(
                Path.Combine(plan.DestinationPath, "build-one", "manifest.json"),
                relocatedInstall.BuildManifestPath,
                "active install should reference the relocated staged manifest");

            var resolved = WorkspaceLocator.ForInstall(installPath, profilesRoot);
            AssertEqual(
                Path.GetFullPath(plan.DestinationPath),
                resolved.StagingPath,
                "workspace locator should honor the committed storage setting");
            var after = await service.InspectAsync(resolved, cancellationToken).ConfigureAwait(false);
            AssertEqual(original.StoredBytes, after.StoredBytes, "migrated byte count");
            AssertEqual(original.FileCount, after.FileCount, "migrated file count");

            var samePlan = await service.PlanAsync(resolved, storageBase, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(samePlan.IsNoOp, "selecting the active destination should be a no-op");

            var occupiedBase = Path.Combine(root, "Occupied");
            var occupiedManaged = Path.Combine(
                occupiedBase,
                "SpinTexture Packs",
                Path.GetFileName(resolved.WorkspacePath));
            Directory.CreateDirectory(occupiedManaged);
            await File.WriteAllTextAsync(
                Path.Combine(occupiedManaged, "unrelated.txt"),
                "do not overwrite",
                cancellationToken).ConfigureAwait(false);
            await AssertThrowsAsync<InvalidOperationException>(
                () => service.PlanAsync(resolved, occupiedBase, cancellationToken: cancellationToken),
                "different destination contents must block migration").ConfigureAwait(false);
            Assert(File.Exists(Path.Combine(occupiedManaged, "unrelated.txt")),
                "blocked migration must preserve unrelated destination data");

            var restorePlan = await service.PlanAsync(
                    resolved,
                    resolved.DefaultStagingPath,
                    useDefaultLocation: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var restored = await service.MoveAsync(
                    resolved,
                    restorePlan,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(restored.Status.UsesDefaultLocation, "move-back should reactivate default storage");
            var defaultResolved = WorkspaceLocator.ForInstall(installPath, profilesRoot);
            AssertEqual(defaultResolved.DefaultStagingPath, defaultResolved.StagingPath,
                "default storage should resolve after move-back");
            Assert(!Directory.Exists(plan.DestinationPath), "old custom managed copy should be removed");
            var restoredInstall = await manifestStore.ReadInstallManifestAsync(
                installManifestPath,
                cancellationToken).ConfigureAwait(false);
            AssertEqual(
                Path.Combine(defaultResolved.StagingPath, "build-one", "manifest.json"),
                restoredInstall.BuildManifestPath,
                "moving back should rebase the active install reference to default storage");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(
                () => service.PlanAsync(defaultResolved, storageBase, cancellationToken: canceled.Token),
                "pre-canceled storage planning").ConfigureAwait(false);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string description)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Self-test failed: {description}; expected {typeof(TException).Name}.");
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

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
