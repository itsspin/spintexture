using System.Text;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class IncrementalPackPromotionSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestLargeAdditivePromotionAndExactRestoreAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestConflictHasZeroWritesAsync(cancellationToken).ConfigureAwait(false);
        await TestFailureAndCancellationRollbackAsync(cancellationToken).ConfigureAwait(false);
        await TestLauncherRaceGateRollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TestLauncherRaceGateRollbackAsync(
        CancellationToken cancellationToken)
    {
        var gate = new OneShotPromotionGate();
        await using var fixture = await PromotionFixture.CreateAsync(
                carriedCount: 3,
                addedCount: 2,
                cancellationToken,
                gate.Check)
            .ConfigureAwait(false);
        var active = await fixture.Workflow.ApplySelectedStagedPacksAsync(
                fixture.Paths,
                [fixture.CarriedPack.ManifestPath],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        fixture.Atomic.Reset();
        gate.ArmOnSecondNewInstallCheck();
        await AssertThrowsAsync<InvalidOperationException>(
                () => fixture.Workflow.ApplySelectedStagedPacksAsync(
                    fixture.Paths,
                    [fixture.CarriedPack.ManifestPath, fixture.AddedPack.ManifestPath],
                    cancellationToken: cancellationToken),
                "launcher race gate")
            .ConfigureAwait(false);
        await AssertRolledBackToCarriedOnlyAsync(fixture, active, cancellationToken)
            .ConfigureAwait(false);
        Assert(
            fixture.Atomic.LiveWrites(fixture.Paths.InstallPath).All(path =>
                fixture.AddedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)),
            "launcher race recovery never rewrites carried archives");
    }

    private static async Task TestLargeAdditivePromotionAndExactRestoreAsync(
        CancellationToken cancellationToken)
    {
        await using var fixture = await PromotionFixture.CreateAsync(
                carriedCount: 1_273,
                addedCount: 2,
                cancellationToken)
            .ConfigureAwait(false);
        var initialBuildCount = fixture.BuilderInvocations;
        var active = await fixture.Workflow.ApplySelectedStagedPacksAsync(
                fixture.Paths,
                [fixture.CarriedPack.ManifestPath],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var carriedTimestamps = fixture.CarriedPaths.ToDictionary(
            path => path,
            File.GetLastWriteTimeUtc,
            StringComparer.OrdinalIgnoreCase);

        fixture.Atomic.Reset();
        var progress = new InlineProgress<ProgressUpdate>();
        var promoted = await fixture.Workflow.ApplySelectedStagedPacksAsync(
                fixture.Paths,
                [fixture.CarriedPack.ManifestPath, fixture.AddedPack.ManifestPath],
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        AssertEqual(active.ApplyId, promoted.ApplyId, "additive promotion keeps transaction identity");
        AssertEqual(active.InstallManifestPath, promoted.InstallManifestPath,
            "additive promotion updates the one active manifest");
        AssertEqual(1_275, promoted.Manifest.Entries.Count, "merged promotion entry count");
        AssertEqual(initialBuildCount, fixture.BuilderInvocations,
            "additive install must not rerun any artifact builder");
        AssertEqual(2, fixture.Atomic.LiveWrites(fixture.Paths.InstallPath).Count,
            "1,273 + 2 promotion live-write count");
        Assert(
            fixture.Atomic.LiveWrites(fixture.Paths.InstallPath).All(path =>
                fixture.AddedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)),
            "only the two added airplane archives may be written");
        AssertEqual(2, fixture.Atomic.WritesUnder(fixture.Paths.BackupPath).Count,
            "only added archives receive new original backups");
        Assert(
            fixture.CarriedPaths.All(path =>
                File.GetLastWriteTimeUtc(path) == carriedTimestamps[path]),
            "carried active archive timestamps must remain untouched");
        Assert(
            progress.Values.Any(update => update.Message.Contains(
                "2 new archives; 1,273 already active and untouched",
                StringComparison.Ordinal)),
            "promotion progress must clearly report the untouched carried set");

        var stagingDirectoriesBeforeRepeat = Directory
            .EnumerateDirectories(fixture.Paths.StagingPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        fixture.Atomic.Reset();
        var repeated = await fixture.Workflow.ApplySelectedStagedPacksAsync(
                fixture.Paths,
                [fixture.CarriedPack.ManifestPath, fixture.AddedPack.ManifestPath],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(promoted.ApplyId, repeated.ApplyId, "same selection is idempotent");
        AssertEqual(promoted.InstallManifestPath, repeated.InstallManifestPath,
            "same selection retains one active composition");
        AssertEqual(0, fixture.Atomic.Writes.Count, "same selection performs no transaction writes");
        AssertEqual(initialBuildCount, fixture.BuilderInvocations,
            "same selection must not rerun builders");
        var stagingDirectoriesAfterRepeat = Directory
            .EnumerateDirectories(fixture.Paths.StagingPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert(
            stagingDirectoriesBeforeRepeat.SequenceEqual(
                stagingDirectoriesAfterRepeat,
                StringComparer.OrdinalIgnoreCase),
            "same active leaf selection must not create or materialize another staged composition");

        await fixture.Workflow.RestoreLatestAsync(
                fixture.Paths,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (var pair in fixture.Originals)
        {
            AssertSequenceEqual(
                pair.Value,
                await File.ReadAllBytesAsync(pair.Key, cancellationToken).ConfigureAwait(false),
                $"exact final restore for {Path.GetFileName(pair.Key)}");
        }
    }

    private static async Task TestConflictHasZeroWritesAsync(
        CancellationToken cancellationToken)
    {
        await using var fixture = await PromotionFixture.CreateAsync(
                carriedCount: 3,
                addedCount: 2,
                cancellationToken)
            .ConfigureAwait(false);
        var active = await fixture.Workflow.ApplySelectedStagedPacksAsync(
                fixture.Paths,
                [fixture.CarriedPack.ManifestPath],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var externallyChanged = Encoding.UTF8.GetBytes("launcher changed this source archive");
        await File.WriteAllBytesAsync(
                fixture.AddedPaths[0],
                externallyChanged,
                cancellationToken)
            .ConfigureAwait(false);

        fixture.Atomic.Reset();
        await AssertThrowsAsync<InvalidOperationException>(
                () => fixture.Workflow.ApplySelectedStagedPacksAsync(
                    fixture.Paths,
                    [fixture.CarriedPack.ManifestPath, fixture.AddedPack.ManifestPath],
                    cancellationToken: cancellationToken),
                "changed new source must block promotion")
            .ConfigureAwait(false);
        AssertEqual(0, fixture.Atomic.Writes.Count, "promotion conflict must cause zero transaction writes");
        AssertSequenceEqual(
            externallyChanged,
            await File.ReadAllBytesAsync(fixture.AddedPaths[0], cancellationToken).ConfigureAwait(false),
            "conflicting source remains untouched");
        var stillActive = await fixture.Store.ReadInstallManifestAsync(
                active.InstallManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(active.Manifest.BuildManifestPath, stillActive.BuildManifestPath,
            "conflict retains previous active composition");
        AssertEqual(3, stillActive.Entries.Count, "conflict retains carried-only manifest");
        Assert(
            fixture.CarriedPaths.All(path => fixture.Enhanced[path].AsSpan().SequenceEqual(
                File.ReadAllBytes(path))),
            "conflict leaves every carried enhanced archive active");
    }

    private static async Task TestFailureAndCancellationRollbackAsync(
        CancellationToken cancellationToken)
    {
        await using var fixture = await PromotionFixture.CreateAsync(
                carriedCount: 4,
                addedCount: 2,
                cancellationToken)
            .ConfigureAwait(false);
        var active = await fixture.Workflow.ApplySelectedStagedPacksAsync(
                fixture.Paths,
                [fixture.CarriedPack.ManifestPath],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        fixture.Atomic.Reset();
        fixture.Atomic.FailOnceAfterCommit(fixture.AddedPaths[1]);
        await AssertThrowsAsync<IOException>(
                () => fixture.Workflow.ApplySelectedStagedPacksAsync(
                    fixture.Paths,
                    [fixture.CarriedPack.ManifestPath, fixture.AddedPack.ManifestPath],
                    cancellationToken: cancellationToken),
                "post-commit promotion failure")
            .ConfigureAwait(false);
        await AssertRolledBackToCarriedOnlyAsync(fixture, active, cancellationToken)
            .ConfigureAwait(false);
        Assert(
            fixture.Atomic.LiveWrites(fixture.Paths.InstallPath).All(path =>
                fixture.AddedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)),
            "promotion failure and rollback never rewrite carried archives");

        fixture.Atomic.Reset();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fixture.Atomic.CancelAfterCommit(fixture.AddedPaths[0], canceled);
        await AssertThrowsAsync<OperationCanceledException>(
                () => fixture.Workflow.ApplySelectedStagedPacksAsync(
                    fixture.Paths,
                    [fixture.CarriedPack.ManifestPath, fixture.AddedPack.ManifestPath],
                    cancellationToken: canceled.Token),
                "promotion cancellation")
            .ConfigureAwait(false);
        await AssertRolledBackToCarriedOnlyAsync(fixture, active, cancellationToken)
            .ConfigureAwait(false);

        await fixture.Workflow.RestoreLatestAsync(
                fixture.Paths,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (var pair in fixture.Originals)
        {
            AssertSequenceEqual(
                pair.Value,
                await File.ReadAllBytesAsync(pair.Key, cancellationToken).ConfigureAwait(false),
                $"rollback scenario final restore for {Path.GetFileName(pair.Key)}");
        }
    }

    private static async Task AssertRolledBackToCarriedOnlyAsync(
        PromotionFixture fixture,
        ApplyResult active,
        CancellationToken cancellationToken)
    {
        foreach (var path in fixture.AddedPaths)
        {
            AssertSequenceEqual(
                fixture.Originals[path],
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                $"failed promotion restores {Path.GetFileName(path)}");
        }

        foreach (var path in fixture.CarriedPaths)
        {
            AssertSequenceEqual(
                fixture.Enhanced[path],
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                $"failed promotion preserves carried {Path.GetFileName(path)}");
        }

        var manifest = await fixture.Store.ReadInstallManifestAsync(
                active.InstallManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(InstallTransactionState.Applied, manifest.State, "rolled-back manifest state");
        AssertEqual(active.Manifest.BuildManifestPath, manifest.BuildManifestPath,
            "rolled-back manifest build identity");
        AssertEqual(fixture.CarriedPaths.Count, manifest.Entries.Count,
            "rolled-back manifest entry count");
        var health = await new InstallHealthService(fixture.Store)
            .AuditLatestAsync(fixture.Paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(InstallHealthState.EnhancedActive, health.State,
            "rolled-back carried install health");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}: {message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}: expected '{expected}', actual '{actual}'");
        }
    }

    private static void AssertSequenceEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class OneShotPromotionGate
    {
        private bool armed;
        private bool thrown;
        private int installChecks;

        public void ArmOnSecondNewInstallCheck()
        {
            armed = true;
            thrown = false;
            installChecks = 0;
        }

        public void Check(string operation)
        {
            if (!armed
                || thrown
                || !operation.Contains(
                    "install new additive pack artifacts",
                    StringComparison.Ordinal))
            {
                return;
            }

            installChecks++;
            if (installChecks == 2)
            {
                thrown = true;
                throw new InvalidOperationException("Simulated LaunchPad start during promotion.");
            }
        }
    }

    private sealed class TrackingAtomicFileOperations : IAtomicFileOperations
    {
        private readonly AtomicFileOperations inner = new();
        private string? failAfterCommitPath;
        private string? cancelAfterCommitPath;
        private CancellationTokenSource? cancelSource;
        private bool failed;
        private bool canceled;

        public List<string> Writes { get; } = [];

        public void Reset()
        {
            Writes.Clear();
            failAfterCommitPath = null;
            cancelAfterCommitPath = null;
            cancelSource = null;
            failed = false;
            canceled = false;
        }

        public void FailOnceAfterCommit(string path) => failAfterCommitPath = Path.GetFullPath(path);

        public void CancelAfterCommit(string path, CancellationTokenSource source)
        {
            cancelAfterCommitPath = Path.GetFullPath(path);
            cancelSource = source;
        }

        public IReadOnlyList<string> LiveWrites(string installPath) => Writes
            .Where(path => IsUnder(installPath, path))
            .ToArray();

        public IReadOnlyList<string> WritesUnder(string root) => Writes
            .Where(path => IsUnder(root, path))
            .ToArray();

        public Task CopyAndReplaceAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default,
            Action? onCommitted = null)
        {
            var fullDestination = Path.GetFullPath(destinationPath);
            return inner.CopyAndReplaceAsync(
                sourcePath,
                destinationPath,
                expectedLength,
                expectedSha256,
                cancellationToken,
                () =>
                {
                    Writes.Add(fullDestination);
                    onCommitted?.Invoke();
                    if (!canceled
                        && cancelAfterCommitPath is not null
                        && PathGuard.SamePath(fullDestination, cancelAfterCommitPath))
                    {
                        canceled = true;
                        cancelSource!.Cancel();
                    }

                    if (!failed
                        && failAfterCommitPath is not null
                        && PathGuard.SamePath(fullDestination, failAfterCommitPath))
                    {
                        failed = true;
                        throw new IOException("Simulated failure after additive live commit.");
                    }
                });
        }

        private static bool IsUnder(string root, string path)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relative != "."
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathFullyQualified(relative);
        }
    }

    private sealed class PromotionFixture : IAsyncDisposable
    {
        private PromotionFixture(
            string root,
            ProjectPaths paths,
            ManifestStore store,
            TrackingAtomicFileOperations atomic,
            TexturePackWorkflow workflow,
            StagedBuildResult carriedPack,
            StagedBuildResult addedPack,
            IReadOnlyList<string> carriedPaths,
            IReadOnlyList<string> addedPaths,
            Dictionary<string, byte[]> originals,
            Dictionary<string, byte[]> enhanced,
            Func<int> builderInvocations)
        {
            Root = root;
            Paths = paths;
            Store = store;
            Atomic = atomic;
            Workflow = workflow;
            CarriedPack = carriedPack;
            AddedPack = addedPack;
            CarriedPaths = carriedPaths;
            AddedPaths = addedPaths;
            Originals = originals;
            Enhanced = enhanced;
            this.builderInvocations = builderInvocations;
        }

        private readonly Func<int> builderInvocations;
        public string Root { get; }
        public ProjectPaths Paths { get; }
        public ManifestStore Store { get; }
        public TrackingAtomicFileOperations Atomic { get; }
        public TexturePackWorkflow Workflow { get; }
        public StagedBuildResult CarriedPack { get; }
        public StagedBuildResult AddedPack { get; }
        public IReadOnlyList<string> CarriedPaths { get; }
        public IReadOnlyList<string> AddedPaths { get; }
        public Dictionary<string, byte[]> Originals { get; }
        public Dictionary<string, byte[]> Enhanced { get; }
        public int BuilderInvocations => builderInvocations();

        public static async Task<PromotionFixture> CreateAsync(
            int carriedCount,
            int addedCount,
            CancellationToken cancellationToken,
            Action<string>? processGate = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"spintexture-additive-promotion-{Guid.NewGuid():N}");
            var installPath = Path.Combine(root, "EverQuest");
            var workspacePath = Path.Combine(root, "Workspace");
            Directory.CreateDirectory(installPath);
            var paths = new ProjectPaths(installPath, workspacePath);
            var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var enhanced = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var carried = Enumerable.Range(0, carriedCount)
                .Select(index => $"global_chr_{index:D4}.s3d")
                .ToArray();
            var added = Enumerable.Range(0, addedCount)
                .Select(index => index == 0 ? "airplane.s3d" : $"airplane_obj{index:D2}.s3d")
                .ToArray();
            foreach (var relativePath in carried.Concat(added))
            {
                var livePath = Path.Combine(installPath, relativePath);
                var original = Encoding.UTF8.GetBytes($"original::{relativePath}");
                var installed = Encoding.UTF8.GetBytes(
                    $"enhanced-crisp-payload::{relativePath}::larger-than-original");
                originals.Add(livePath, original);
                enhanced.Add(livePath, installed);
                await File.WriteAllBytesAsync(livePath, original, cancellationToken)
                    .ConfigureAwait(false);
            }

            var builderCount = 0;
            var carriedPack = await BuildPackAsync(
                    paths,
                    "build-promotion-carried",
                    carried,
                    AssetScope.CharactersAndEquipmentOnly,
                    null,
                    enhanced,
                    () => Interlocked.Increment(ref builderCount),
                    cancellationToken)
                .ConfigureAwait(false);
            var addedPack = await BuildPackAsync(
                    paths,
                    "build-promotion-airplane",
                    added,
                    AssetScope.SelectedZone,
                    "airplane",
                    enhanced,
                    () => Interlocked.Increment(ref builderCount),
                    cancellationToken)
                .ConfigureAwait(false);
            var store = new ManifestStore();
            var atomic = new TrackingAtomicFileOperations();
            var transactions = new InstallTransactionService(
                store,
                atomic,
                processGate ?? (_ => { }));
            var health = new InstallHealthService(store);
            var catalog = new StagedPackCatalogService(store);
            var composer = new StagedPackComposer(catalog, store);
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: health,
                stagedPackCatalogService: catalog,
                stagedPackComposer: composer);
            return new PromotionFixture(
                root,
                paths,
                store,
                atomic,
                workflow,
                carriedPack,
                addedPack,
                carried.Select(path => Path.Combine(installPath, path)).ToArray(),
                added.Select(path => Path.Combine(installPath, path)).ToArray(),
                originals,
                enhanced,
                () => Volatile.Read(ref builderCount));
        }

        private static Task<StagedBuildResult> BuildPackAsync(
            ProjectPaths paths,
            string buildId,
            IReadOnlyList<string> relativePaths,
            AssetScope scope,
            string? selectedZone,
            IReadOnlyDictionary<string, byte[]> enhanced,
            Action onBuild,
            CancellationToken cancellationToken)
        {
            var items = relativePaths.Select(relativePath =>
            {
                var livePath = Path.Combine(paths.InstallPath, relativePath);
                var installed = enhanced[livePath];
                return new StagedBuildItem(
                    relativePath,
                    new DelegateStagedArtifactBuilder(async (context, token) =>
                    {
                        onBuild();
                        await File.WriteAllBytesAsync(context.DestinationPath, installed, token)
                            .ConfigureAwait(false);
                    }));
            }).ToArray();
            return new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    new UpscaleOptions(
                        TexturePreset.MaximumDetail,
                        scope,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: selectedZone),
                    items,
                    buildId),
                cancellationToken: cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
