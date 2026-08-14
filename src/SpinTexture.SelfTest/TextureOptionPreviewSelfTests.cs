using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;

namespace SpinTexture.SelfTest;

internal static class TextureOptionPreviewSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        TestConservativeSelectionPolicy();
        TestResolutionCapTruth();
        TestSampleSeedRotation();
        TestCacheKeyAndPathSafety();
        TestBoundedCacheEviction();
        TestFallbackTruth();
        TestWorldCharacterArchiveOverlapUsesWorldSemantics();
        await TestSingleFlightAsync(cancellationToken).ConfigureAwait(false);
        await TestCancellationDrainsFlightAsync(cancellationToken).ConfigureAwait(false);
        await TestLateJoinGetsFreshFlightAsync(cancellationToken).ConfigureAwait(false);
        await TestPreCanceledRequestIsReadOnlyAsync().ConfigureAwait(false);
    }

    private static void TestSampleSeedRotation()
    {
        string[] keys = ["wall-a", "wall-b", "wall-c", "wall-d"];
        var seed = 812UL;
        var first = TextureOptionPreviewService.RotateCandidateKeysForTest(
            "World surfaces",
            keys,
            seed);
        var repeat = TextureOptionPreviewService.RotateCandidateKeysForTest(
            "World surfaces",
            keys,
            seed);
        var next = TextureOptionPreviewService.RotateCandidateKeysForTest(
            "World surfaces",
            keys,
            seed + 1UL);

        Assert(
            first.SequenceEqual(repeat, StringComparer.Ordinal),
            "the same preview seed must select the same deterministic candidate order");
        Assert(
            first.Distinct(StringComparer.Ordinal).Count() == keys.Length,
            "a varied preview set must never duplicate a safe candidate");
        Assert(
            first[0] != next[0],
            "New samples must advance to another candidate when a category has alternatives");
        Assert(
            first.OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(next.OrderBy(item => item, StringComparer.Ordinal)),
            "sample variation may reorder only the already-vetted candidate pool");
    }

    private static void TestConservativeSelectionPolicy()
    {
        var classifier = new TextureSemanticClassifier();
        var opaque = Metadata(
            TextureFileFormat.Dds,
            256,
            128,
            24,
            TextureAlphaStatus.None,
            "B8G8R8X8_UNORM");
        var opaqueClassification = classifier.Classify("castle_wall.dds", opaque);
        Assert(
            TextureOptionPreviewService.IsSafeOpaqueCandidate(
                "qeynos.s3d",
                "castle_wall.dds",
                opaque,
                opaqueClassification,
                ReadOnlySpan<byte>.Empty,
                protectedTranslucentNames: null,
                maximumDimension: 2048),
            "an ordinary opaque production-safe wall should be eligible");

        AssertExcluded("water01.dds", opaque, classifier, "numbered water");
        AssertExcluded("skyCloud01.dds", opaque, classifier, "sky/cloud prefix");
        AssertExcluded("wall_mask.dds", opaque, classifier, "mask");
        AssertExcluded("ui_wall.dds", opaque, classifier, "UI");
        AssertExcluded("wall_sprite.dds", opaque, classifier, "animated sprite");

        var indexed = Metadata(
            TextureFileFormat.Bmp,
            256,
            256,
            8,
            TextureAlphaStatus.None,
            texconvFormat: null);
        AssertExcluded("stonewall.bmp", indexed, classifier, "indexed bitmap");

        var cutout = Metadata(
            TextureFileFormat.Dds,
            256,
            256,
            8,
            TextureAlphaStatus.Explicit,
            "BC3_UNORM");
        AssertExcluded("ivy.dds", cutout, classifier, "explicit alpha/cutout");

        var tooSmall = opaque with { Width = 32, Height = 32 };
        AssertExcluded("tiny_wall.dds", tooSmall, classifier, "sub-64px sample");

        Assert(
            !TextureOptionPreviewService.IsSafeOpaqueCandidate(
                "sky.s3d",
                "blue.bmp",
                opaque,
                opaqueClassification,
                ReadOnlySpan<byte>.Empty,
                protectedTranslucentNames: null,
                maximumDimension: 2048),
            "the protected legacy sky archive must never enter preview selection");

        Assert(
            !TextureOptionPreviewService.IsSafeOpaqueCandidate(
                "qeynos.s3d",
                "glasswall.dds",
                opaque,
                classifier.Classify("glasswall.dds", opaque),
                ReadOnlySpan<byte>.Empty,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "glasswall.dds" },
                maximumDimension: 2048),
            "a WLD-referenced translucent texture must never enter preview selection");
    }

    private static void TestResolutionCapTruth()
    {
        var options4096 = UpscaleOptions.Recommended with { MaximumDimension = 4096 };
        Assert(
            TextureOptionPreviewService.IsSameAs2048(
                Metadata(
                    TextureFileFormat.Dds,
                    256,
                    128,
                    24,
                    TextureAlphaStatus.None,
                    "B8G8R8X8_UNORM"),
                options4096),
            "a 256px source should truthfully report the same 4x result at 2K and 4K caps");
        Assert(
            !TextureOptionPreviewService.IsSameAs2048(
                Metadata(
                    TextureFileFormat.Dds,
                    1024,
                    512,
                    24,
                    TextureAlphaStatus.None,
                    "B8G8R8X8_UNORM"),
                options4096),
            "a 1024px source should truthfully report a larger result at the 4K cap");

        var options1024 = options4096 with { MaximumDimension = 1024 };
        Assert(
            TextureOptionPreviewService.IsSameAs2048(
                Metadata(
                    TextureFileFormat.Dds,
                    256,
                    256,
                    24,
                    TextureAlphaStatus.None,
                    "B8G8R8X8_UNORM"),
                options1024),
            "the 4x model ceiling should make a 256px source identical at 1K and 2K caps");
    }

    private static void TestCacheKeyAndPathSafety()
    {
        var first = TextureOptionPreviewService.ComputeDeterministicKeyForTest(
            "source-sha",
            "pipeline-7",
            "cap-2048");
        var repeat = TextureOptionPreviewService.ComputeDeterministicKeyForTest(
            "source-sha",
            "pipeline-7",
            "cap-2048");
        var changed = TextureOptionPreviewService.ComputeDeterministicKeyForTest(
            "source-sha",
            "pipeline-7",
            "cap-4096");
        Assert(first == repeat, "preview cache keys must be deterministic");
        Assert(first != changed, "a changed cap must invalidate the preview cache key");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"SpinTexture-preview-path-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "client"),
            Path.Combine(root, "profile"));
        var cacheDirectory = TextureOptionPreviewService.GetCacheDirectory(paths, first);
        Assert(
            cacheDirectory.StartsWith(
                Path.Combine(paths.CachePath, "OptionPreviews")
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "a preview cache entry must remain under the profile Cache directory");
        AssertThrows<ArgumentException>(
            () => TextureOptionPreviewService.GetCacheDirectory(paths, "..\\Staging"),
            "an unsafe cache key must be rejected before path resolution");
        TextureOptionPreviewService.EnsurePreviewWriteBoundary(paths);
        AssertThrows<InvalidDataException>(
            () => TextureOptionPreviewService.EnsurePreviewWriteBoundary(
                new ProjectPaths(
                    Path.Combine(root, "client"),
                    Path.Combine(root, "client"))),
            "a cache nested inside the live client must be rejected");
        AssertThrows<InvalidDataException>(
            () => TextureOptionPreviewService.EnsurePreviewWriteBoundary(
                new ProjectPaths(
                    Path.Combine(root, "client"),
                    Path.Combine(root, "profile"),
                    Path.Combine(root, "profile", "Cache"))),
            "a cache overlapping the staging library must be rejected");
    }

    private static void TestFallbackTruth()
    {
        var sample = new TextureOptionPreviewSample(
            "0123456789abcdef",
            "World surfaces",
            "Stone Wall",
            "qeynos.s3d",
            "stonewall.dds",
            "original.png",
            "enhanced.png",
            TexturePreset.MaximumDetail,
            TexturePreset.MaximumDetail,
            PaintedTheme.ClassicPainted,
            PaintedTheme.ClassicPainted,
            "Batched faithful Real-ESRNet reconstruction (fidelity fallback)",
            UsedFallback: true,
            256,
            256,
            1024,
            1024,
            11,
            IsSameAs2048: true,
            TextureOptionPreviewSourceProvenance.ManagedVerifiedOriginal);
        Assert(
            sample.ActualPreset == TexturePreset.Faithful,
            "runtime fallback metadata must identify the faithful worker rather than the requested preset");
    }

    private static void TestWorldCharacterArchiveOverlapUsesWorldSemantics()
    {
        var installPath = Path.Combine(
            Path.GetTempPath(),
            $"SpinTexture-preview-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installPath);
        try
        {
            var overlappingArchive = Path.Combine(installPath, "overlap_chr.s3d");
            File.WriteAllBytes(overlappingArchive, [0]);
            File.WriteAllBytes(Path.Combine(installPath, "overlap_chr.xmi"), [0]);

            var discoveredCharacters = CharacterEquipmentArchiveCatalog.Discover(
                installPath,
                [overlappingArchive]);
            Assert(
                discoveredCharacters.Contains(
                    overlappingArchive,
                    StringComparer.OrdinalIgnoreCase),
                "the overlap fixture must independently qualify as a character archive");

            var productionCharacters = TexturePackWorkflow
                .ResolveCharacterAndEquipmentArchives(installPath);
            Assert(
                !productionCharacters.Contains(
                    overlappingArchive,
                    StringComparer.OrdinalIgnoreCase),
                "an archive also discovered as world content must retain production world semantics");
        }
        finally
        {
            if (Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }
        }
    }

    private static void TestBoundedCacheEviction()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"SpinTexture-preview-eviction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var keys = Enumerable.Range(0, 14)
                .Select(index => TextureOptionPreviewService.ComputeDeterministicKeyForTest(
                    $"eviction-{index}"))
                .ToArray();
            for (var index = 0; index < keys.Length; index++)
            {
                var directory = Path.Combine(cacheRoot, keys[index]);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "preview.json"), "{}");
                Directory.SetLastWriteTimeUtc(
                    directory,
                    DateTime.UtcNow - TimeSpan.FromHours(keys.Length - index));
            }

            var workDirectory = Path.Combine(cacheRoot, $".work-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDirectory);
            TextureOptionPreviewService.EvictCompletedCacheEntriesBestEffort(
                cacheRoot,
                [keys[0]]);

            var completed = Directory.EnumerateDirectories(
                    cacheRoot,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is { Length: 64 })
                .ToArray();
            Assert(
                completed.Length == 12,
                "preview cache eviction must retain at most twelve completed entries");
            Assert(
                Directory.Exists(Path.Combine(cacheRoot, keys[0])),
                "cache eviction must never remove an explicitly active entry");
            Assert(
                Directory.Exists(workDirectory),
                "cache eviction must never remove an in-progress work directory");
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private static async Task TestSingleFlightAsync(CancellationToken cancellationToken)
    {
        var calls = 0;
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = EmptyResult("single-flight");
        var flight = new TextureOptionPreviewService.PreviewFlight(async token =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return expected;
        });
        var first = flight.JoinAsync(cancellationToken);
        var second = flight.JoinAsync(cancellationToken);
        Assert(calls == 1, "two waiters must share exactly one preview operation");
        release.SetResult();
        var results = await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert(
            results.All(result => ReferenceEquals(result, expected)),
            "every waiter must receive the shared preview result");
    }

    private static async Task TestCancellationDrainsFlightAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var flight = new TextureOptionPreviewService.PreviewFlight(async token =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return EmptyResult("unexpected");
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                drained.SetResult();
                throw;
            }
        });
        await entered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var callerCancellation = new CancellationTokenSource();
        var joined = flight.JoinAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
                () => joined,
                "canceling the last waiter must cancel the shared operation")
            .ConfigureAwait(false);
        Assert(
            drained.Task.IsCompletedSuccessfully,
            "caller cancellation must not return until native preview work is fully drained");
    }

    private static async Task TestLateJoinGetsFreshFlightAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = new TextureOptionPreviewService();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstDrain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = EmptyResult("fresh-flight");
        var operationStarts = 0;

        async Task<TextureOptionPreviewResult> RunAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref operationStarts) != 1)
            {
                return expected;
            }

            firstEntered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return EmptyResult("unexpected");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                firstCancellationObserved.SetResult();
                await allowFirstDrain.Task.ConfigureAwait(false);
                throw;
            }
        }

        using var firstCallerCancellation = new CancellationTokenSource();
        var first = service.JoinFlightForTestAsync(
            "late-join-boundary",
            RunAsync,
            firstCallerCancellation.Token);
        await firstEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        firstCallerCancellation.Cancel();
        await firstCancellationObserved.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var lateResult = await service.JoinFlightForTestAsync(
                    "late-join-boundary",
                    RunAsync,
                    cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            Assert(
                ReferenceEquals(lateResult, expected),
                "a live late caller must receive the fresh flight's successful result");
            Assert(
                Volatile.Read(ref operationStarts) == 2,
                "a caller arriving after the zero-waiter boundary must start exactly one fresh flight");
        }
        finally
        {
            allowFirstDrain.TrySetResult();
        }

        await AssertThrowsAsync<OperationCanceledException>(
                () => first,
                "the departed caller must retain its cancellation result")
            .ConfigureAwait(false);
    }

    private static async Task TestPreCanceledRequestIsReadOnlyAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SpinTexture-preview-cancel-{Guid.NewGuid():N}");
        var paths = new ProjectPaths(
            Path.Combine(root, "missing-client"),
            Path.Combine(root, "profile"));
        await AssertThrowsAsync<OperationCanceledException>(
                () => new TextureOptionPreviewService().GenerateAsync(
                    paths,
                    UpscaleOptions.Recommended,
                    cancellationToken: cancellation.Token),
                "a pre-canceled request must stop before validation or writes")
            .ConfigureAwait(false);
        Assert(
            !Directory.Exists(paths.WorkspacePath),
            "a pre-canceled preview must not create even a partial workspace cache");
    }

    private static TextureMetadata Metadata(
        TextureFileFormat format,
        int width,
        int height,
        int bitsPerPixel,
        TextureAlphaStatus alpha,
        string? texconvFormat) => new(
        format,
        format.ToString(),
        width,
        height,
        MipCount: 1,
        bitsPerPixel,
        alpha,
        IsCompressed: format == TextureFileFormat.Dds,
        IsTopDown: false,
        IsCubeMap: false,
        IsVolumeTexture: false,
        ArraySize: 1,
        texconvFormat);

    private static void AssertExcluded(
        string logicalName,
        TextureMetadata metadata,
        TextureSemanticClassifier classifier,
        string description)
    {
        Assert(
            !TextureOptionPreviewService.IsSafeOpaqueCandidate(
                "qeynos.s3d",
                logicalName,
                metadata,
                classifier.Classify(logicalName, metadata),
                ReadOnlySpan<byte>.Empty,
                protectedTranslucentNames: null,
                maximumDimension: 2048),
            $"{description} must be excluded from conservative preview selection");
    }

    private static TextureOptionPreviewResult EmptyResult(string key) => new(
        key,
        DateTimeOffset.UnixEpoch,
        TexturePreset.Faithful,
        PaintedTheme.ClassicPainted,
        []);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Texture option preview self-test failed: {message}.");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Texture option preview self-test failed: {message}.");
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
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
            $"Texture option preview self-test failed: {message}.");
    }
}
