using System.Security.Cryptography;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedPackDeletionSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-pack-deletion-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);
        var paths = new ProjectPaths(installPath, workspacePath);
        paths.EnsureWorkspaceDirectories();

        try
        {
            var options = new UpscaleOptions(
                TexturePreset.ClassicHd,
                AssetScope.SelectedZone,
                2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false,
                SelectedZone: "airplane");
            var deletion = new StagedPackDeletionService();

            var ordinaryManifest = await CreatePackAsync(
                    paths,
                    "build-delete-ordinary",
                    options,
                    "airplane.s3d",
                    "ordinary-source"u8.ToArray(),
                    "ordinary-enhanced"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var neighborManifest = await CreatePackAsync(
                    paths,
                    "build-keep-neighbor",
                    options with { SelectedZone = "hateplane" },
                    "hateplane.s3d",
                    "neighbor-source"u8.ToArray(),
                    "neighbor-enhanced"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var ordinaryPlan = await deletion.PlanAsync(
                    paths,
                    ordinaryManifest,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(ordinaryPlan.CanDelete, "ordinary unreferenced pack should be deletable");
            var ordinaryDirectory = Path.GetDirectoryName(ordinaryManifest)!;
            await deletion.DeleteAsync(
                    paths,
                    ordinaryManifest,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(!Directory.Exists(ordinaryDirectory), "selected ordinary pack should be removed");
            Assert(File.Exists(neighborManifest), "neighbor pack must remain untouched");

            var finalizingManifest = await CreatePackAsync(
                    paths,
                    "build-finalizing-delete-guard",
                    options with { SelectedZone = "finalizing" },
                    "finalizing.s3d",
                    "finalizing-source"u8.ToArray(),
                    "finalizing-enhanced"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var finalizingCheckpoint = Path.Combine(
                Path.GetDirectoryName(finalizingManifest)!,
                "build-checkpoint.json");
            await File.WriteAllTextAsync(finalizingCheckpoint, "{}", cancellationToken)
                .ConfigureAwait(false);
            var finalizingPlan = await deletion.PlanAsync(
                    paths,
                    finalizingManifest,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(!finalizingPlan.CanDelete,
                "a finalizing or resumable pack must never be selectable for deletion");
            await AssertThrowsAsync<InvalidOperationException>(
                    () => deletion.DeleteAsync(
                        paths,
                        finalizingManifest,
                        cancellationToken: cancellationToken),
                    "DeleteAsync must recheck the resumable checkpoint under the staging lock")
                .ConfigureAwait(false);
            Assert(File.Exists(finalizingManifest),
                "a checkpoint-protected pack must remain intact");

            var componentOne = await CreatePackAsync(
                    paths,
                    "build-component-one",
                    options,
                    "component-one.s3d",
                    "component-one-source"u8.ToArray(),
                    "component-one-enhanced"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var componentTwo = await CreatePackAsync(
                    paths,
                    "build-component-two",
                    options with { SelectedZone = "fearplane" },
                    "component-two.s3d",
                    "component-two-source"u8.ToArray(),
                    "component-two-enhanced"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var componentPlanBeforeComposition = await deletion.PlanAsync(
                    paths,
                    componentOne,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(componentPlanBeforeComposition.CanDelete,
                "component should be deletable before a composition references it");
            var composition = await new StagedPackComposer().ComposeAsync(
                    paths,
                    [componentOne, componentTwo],
                    cancellationToken: cancellationToken,
                    compositionId: "composite-deletion-test")
                .ConfigureAwait(false);
            var blockedComponent = await deletion.PlanAsync(
                    paths,
                    componentOne,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(!blockedComponent.CanDelete, "composition component deletion must be blocked");
            var libraryPlans = await deletion.PlanManyAsync(
                    paths,
                    [componentOne, componentTwo, composition.ManifestPath],
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(3, libraryPlans.Count, "library-wide deletion plan count");
            var blockedBatch = await deletion.PlanBatchAsync(
                    paths,
                    [componentOne],
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(!blockedBatch.CanDelete,
                "a source-only cleanup must keep its retained composition dependency");

            var cleanupCandidates = libraryPlans
                .Select(plan => new StagedPackCleanupCandidate(
                    plan.ManifestPath,
                    PathGuard.SamePath(plan.ManifestPath, componentTwo)
                        ? DateTimeOffset.UtcNow
                        : DateTimeOffset.UtcNow - TimeSpan.FromDays(10),
                    plan))
                .ToArray();
            var recommended = StagedPackCleanupPolicy.RecommendSafeOldPackDeletions(
                cleanupCandidates,
                DateTimeOffset.UtcNow);
            Assert(recommended.Contains(Path.GetFullPath(composition.ManifestPath)),
                "old uninstalled composition should be recommended for cleanup");
            Assert(recommended.Contains(Path.GetFullPath(componentOne)),
                "old source should be recommended when its old composition is also removed");
            Assert(!recommended.Contains(Path.GetFullPath(componentTwo)),
                "recent source should be retained by the safe-old recommendation");
            var dependencyAwareBatch = await deletion.PlanBatchAsync(
                    paths,
                    recommended.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(dependencyAwareBatch.CanDelete,
                "recommended cleanup should pass as one dependency-aware batch");
            AssertEqual(
                composition.ManifestPath,
                dependencyAwareBatch.OrderedPlans[0].ManifestPath,
                "composition must be ordered before its selected source");
            await AssertThrowsAsync<InvalidOperationException>(
                    () => deletion.DeleteAsync(
                        paths,
                        componentOne,
                        cancellationToken: cancellationToken),
                    "DeleteAsync must recheck a dependency added after an earlier plan")
                .ConfigureAwait(false);
            AssertEqual(
                "composite-deletion-test",
                blockedComponent.CompositionDependencies.Single().CompositionId,
                "component dependency identity");
            var compositePlan = await deletion.PlanAsync(
                    paths,
                    composition.ManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(compositePlan.CanDelete, "unreferenced composition should be deletable");
            await deletion.DeleteAsync(
                    paths,
                    composition.ManifestPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(
                !Directory.Exists(composition.BuildDirectory),
                "deleted composition directory should be absent");
            Assert(File.Exists(componentOne), "deleting a composition must preserve component one");
            Assert(File.Exists(componentTwo), "deleting a composition must preserve component two");
            Assert(
                (await deletion.PlanAsync(paths, componentOne, cancellationToken).ConfigureAwait(false))
                .CanDelete,
                "component should become deletable after its composition is removed");

            var outsideDirectory = Path.Combine(root, "OutsidePack");
            Directory.CreateDirectory(outsideDirectory);
            var outsideManifest = Path.Combine(outsideDirectory, "manifest.json");
            await File.WriteAllTextAsync(outsideManifest, "{}", cancellationToken)
                .ConfigureAwait(false);
            await AssertThrowsAsync<InvalidDataException>(
                () => deletion.PlanAsync(paths, outsideManifest, cancellationToken),
                "manifest outside Staging must be rejected").ConfigureAwait(false);

            var activeSource = "active-original"u8.ToArray();
            var activeEnhanced = "active-enhanced"u8.ToArray();
            var activeRelativePath = "active-zone.s3d";
            var activeManifest = await CreatePackAsync(
                    paths,
                    "build-active-delete-guard",
                    options with { SelectedZone = "active-zone" },
                    activeRelativePath,
                    activeSource,
                    activeEnhanced,
                    cancellationToken)
                .ConfigureAwait(false);
            var beforeActivePlan = await deletion.PlanAsync(
                    paths,
                    activeManifest,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(beforeActivePlan.CanDelete,
                "pack should be deletable before an install references it");
            await CreateActiveInstallAsync(
                    paths,
                    activeManifest,
                    activeRelativePath,
                    activeSource,
                    activeEnhanced,
                    cancellationToken)
                .ConfigureAwait(false);
            var activePlan = await deletion.PlanAsync(
                    paths,
                    activeManifest,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(!activePlan.CanDelete, "current install pack deletion must be blocked");
            Assert(activePlan.IsReferencedByCurrentInstall, "active install reference flag");
            var activeBatch = await deletion.PlanBatchAsync(
                    paths,
                    [activeManifest],
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(!activeBatch.CanDelete, "batch cleanup must reject the installed pack");
            await AssertThrowsAsync<InvalidOperationException>(
                    () => deletion.DeleteAsync(
                        paths,
                        activeManifest,
                        cancellationToken: cancellationToken),
                    "DeleteAsync must recheck an active reference added after an earlier plan")
                .ConfigureAwait(false);
            Assert(File.Exists(activeManifest), "blocked active pack must remain intact");

            await TestReparseGuardWhenSupportedAsync(
                    paths,
                    deletion,
                    options,
                    root,
                    cancellationToken)
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

    private static async Task TestReparseGuardWhenSupportedAsync(
        ProjectPaths paths,
        StagedPackDeletionService deletion,
        UpscaleOptions options,
        string root,
        CancellationToken cancellationToken)
    {
        var manifestPath = await CreatePackAsync(
                paths,
                "build-reparse-guard",
                options with { SelectedZone = "reparse" },
                "reparse.s3d",
                "reparse-source"u8.ToArray(),
                "reparse-enhanced"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var outside = Path.Combine(root, "ReparseOutside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(Path.GetDirectoryName(manifestPath)!, "unsafe-link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var plan = await deletion.PlanAsync(paths, manifestPath, cancellationToken)
            .ConfigureAwait(false);
        Assert(!plan.CanDelete, "pack containing a reparse point must be blocked");
        Assert(
            plan.SafetyBlockers.Any(blocker =>
                blocker.Contains("reparse", StringComparison.OrdinalIgnoreCase)),
            "reparse blocker should be explained");
        Assert(File.Exists(manifestPath), "reparse-blocked pack must remain intact");
    }

    private static async Task<string> CreatePackAsync(
        ProjectPaths paths,
        string buildId,
        UpscaleOptions options,
        string relativePath,
        byte[] source,
        byte[] staged,
        CancellationToken cancellationToken)
    {
        var buildDirectory = Path.Combine(paths.StagingPath, buildId);
        var payloadPath = Path.Combine(buildDirectory, "payload", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await File.WriteAllBytesAsync(payloadPath, staged, cancellationToken)
            .ConfigureAwait(false);
        var manifest = new BuildManifest(
            BuildManifest.CurrentSchemaVersion,
            buildId,
            DateTimeOffset.UtcNow,
            paths.InstallPath,
            options,
            [new BuildManifestEntry(
                relativePath,
                source.LongLength,
                Hash(source),
                staged.LongLength,
                Hash(staged))]);
        var manifestPath = Path.Combine(buildDirectory, "manifest.json");
        await new ManifestStore().WriteBuildManifestAsync(
                manifestPath,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
        return manifestPath;
    }

    private static async Task CreateActiveInstallAsync(
        ProjectPaths paths,
        string buildManifestPath,
        string relativePath,
        byte[] original,
        byte[] enhanced,
        CancellationToken cancellationToken)
    {
        var livePath = Path.Combine(paths.InstallPath, relativePath);
        await File.WriteAllBytesAsync(livePath, enhanced, cancellationToken)
            .ConfigureAwait(false);
        var backupDirectory = Path.Combine(paths.BackupPath, "apply-active-deletion-test");
        var backupPayload = Path.Combine(backupDirectory, "payload", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPayload)!);
        await File.WriteAllBytesAsync(backupPayload, original, cancellationToken)
            .ConfigureAwait(false);
        var installed = new InstalledArtifact(
            relativePath,
            OriginalExisted: true,
            OriginalLength: original.LongLength,
            OriginalSha256: Hash(original),
            BackupRelativePath: Path.GetRelativePath(backupDirectory, backupPayload),
            InstalledLength: enhanced.LongLength,
            InstalledSha256: Hash(enhanced),
            InstalledLastWriteTimeUtcTicks: File.GetLastWriteTimeUtc(livePath).Ticks);
        var manifest = new InstallManifest(
            InstallManifest.CurrentSchemaVersion,
            "apply-active-deletion-test",
            DateTimeOffset.UtcNow,
            paths.InstallPath,
            Path.GetFileName(Path.GetDirectoryName(buildManifestPath)!),
            buildManifestPath,
            InstallTransactionState.Applied,
            [installed]);
        await new ManifestStore().WriteInstallManifestAsync(
                Path.Combine(backupDirectory, "install-manifest.json"),
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string description)
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
