using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedBuildCheckpointSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestRestartResumeAndFinalizationAsync(cancellationToken).ConfigureAwait(false);
        await TestManifestFinalizationWindowResumesAsync(cancellationToken).ConfigureAwait(false);
        await TestFinalizerFailureResumesWithoutRebuildingAsync(cancellationToken).ConfigureAwait(false);
        await TestFinalizerRunsUnderLibraryLockAsync(cancellationToken).ConfigureAwait(false);
        await TestFinalizerRejectsCorruptPreviewAsync(cancellationToken).ConfigureAwait(false);
        await TestLockedCheckpointNeverPublishesAsync(cancellationToken).ConfigureAwait(false);
        await TestRecoveryDiscoverySkipsReparseDirectoryAsync(cancellationToken).ConfigureAwait(false);
        await TestCancellationRetainsCheckpointAsync(cancellationToken).ConfigureAwait(false);
        await TestTamperedPayloadRebuildsOnlyArtifactAsync(cancellationToken).ConfigureAwait(false);
        await TestChangedPlanNeverResumesAsync(cancellationToken).ConfigureAwait(false);
        await TestChangedPaintedThemeNeverResumesAsync(cancellationToken).ConfigureAwait(false);
        await TestChangedOperationNeverResumesAsync(cancellationToken).ConfigureAwait(false);
        await TestChangedSourceNeverResumesAsync(cancellationToken).ConfigureAwait(false);
        await TestStatisticsAndPreviewReplayAsync(cancellationToken).ConfigureAwait(false);
        await TestNoKeyBuildIsImmediatelyVisibleAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TestCancellationRetainsCheckpointAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("cancel", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await AssertThrowsAsync<OperationCanceledException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts, index =>
            {
                if (index == 1) cancel.Cancel();
            })), cancellationToken: cancel.Token)).ConfigureAwait(false);
        var result = await CompleteAsync(
            fixture,
            CreateBuilders(fixture, counts),
            cancellationToken).ConfigureAwait(false);
        Assert(counts[0] == 1 && counts[1] == 2, "cancellation retains the completed artifact checkpoint");
        Assert(result.ResumedArtifactCount == 1, "canceled build resumes completed work");
    }

    private static async Task TestChangedSourceNeverResumesAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("source", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts, index =>
            {
                if (index == 1 && !failed)
                {
                    failed = true;
                    throw new IOException("simulated crash");
                }
            })), cancellationToken: cancellationToken)).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Paths.InstallPath, fixture.RelativePaths[0]),
            "changed source"u8.ToArray(),
            cancellationToken).ConfigureAwait(false);
        var result = await CompleteAsync(
            fixture,
            CreateBuilders(fixture, counts),
            cancellationToken).ConfigureAwait(false);
        Assert(counts[0] == 2 && counts[1] == 2, "changed source creates a distinct build plan");
        Assert(result.ResumedArtifactCount == 0, "source hash mismatch never resumes stale output");
    }

    private static async Task TestManifestFinalizationWindowResumesAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("finalization", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var first = await new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert(File.Exists(first.ManifestPath), "manifest is durable before workflow finalization");
        Assert(File.Exists(Path.Combine(first.BuildDirectory, "build-checkpoint.json")),
            "checkpoint covers the manifest-to-report crash window");
        var hidden = await new SpinTexture.Core.Services.StagedPackCatalogService()
            .DiscoverAsync(fixture.Paths, cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert(hidden.Count == 0, "half-finalized manifest remains hidden from install catalog");
        var inspected = await new StagedPackCatalogService().InspectAsync(
            fixture.Paths,
            first.ManifestPath,
            StagedPackVerificationMode.Exact,
            cancellationToken).ConfigureAwait(false);
        Assert(inspected.State == StagedPackValidationState.InvalidManifest
               && inspected.Summary.Contains("still finalizing", StringComparison.OrdinalIgnoreCase),
            "direct inspection rejects a half-finalized manifest");

        var resumed = await new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts)),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        Assert(counts[0] == 1 && counts[1] == 1, "manifest-finalization resume runs no builders");
        Assert(resumed.ResumedArtifactCount == 2, "all manifest payloads resume after finalization crash");
        var visible = await new SpinTexture.Core.Services.StagedPackCatalogService()
            .DiscoverAsync(fixture.Paths, cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert(visible.Count == 1, "finalized manifest becomes catalog-visible");
    }

    private static async Task TestFinalizerFailureResumesWithoutRebuildingAsync(
        CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("finalizer-failure", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var metadataAttempts = 0;
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts)),
            cancellationToken: cancellationToken,
            finalizeAsync: async (result, token) =>
            {
                metadataAttempts++;
                await File.WriteAllTextAsync(
                    Path.Combine(result.BuildDirectory, "texture-report.json"),
                    "partial",
                    token).ConfigureAwait(false);
                throw new IOException("simulated metadata crash");
            })).ConfigureAwait(false);

        var result = await new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts)),
            cancellationToken: cancellationToken,
            finalizeAsync: async (completed, token) =>
            {
                metadataAttempts++;
                await File.WriteAllTextAsync(
                    Path.Combine(completed.BuildDirectory, "texture-report.json"),
                    "complete",
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Assert(counts[0] == 1 && counts[1] == 1,
            "metadata failure resumes without rerunning completed builders");
        Assert(metadataAttempts == 2 && result.ResumedArtifactCount == 2,
            "metadata callback is retried after all artifact checkpoints resume");
        Assert(await File.ReadAllTextAsync(
                Path.Combine(result.BuildDirectory, "texture-report.json"),
                cancellationToken).ConfigureAwait(false) == "complete",
            "metadata retry replaces the interrupted report");
    }

    private static async Task TestFinalizerRunsUnderLibraryLockAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("finalizer-lock", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var competingOperationWasBlocked = false;
        await new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts)),
            cancellationToken: cancellationToken,
            finalizeAsync: (_, _) =>
            {
                try
                {
                    using var unexpected = StagingLibraryLock.Acquire(fixture.Paths.WorkspacePath);
                }
                catch (InvalidOperationException)
                {
                    competingOperationWasBlocked = true;
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        Assert(competingOperationWasBlocked,
            "metadata finalization remains inside the cross-process staging-library lock");
    }

    private static async Task TestFinalizerRejectsCorruptPreviewAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("preview-finalization", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var firstCounter = new TextureBuildCounter();
        var firstPreviews = new TexturePreviewCollector(24);
        var firstItems = new StagedBuildItem[]
        {
            new(fixture.RelativePaths[0], new ContributionBuilder(firstCounter, firstPreviews, counts, 0))
        };
        await AssertThrowsAsync<InvalidDataException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, firstItems),
            cancellationToken: cancellationToken,
            finalizeAsync: async (result, token) =>
            {
                var preview = Directory.EnumerateFiles(result.BuildDirectory, "*-before.png", SearchOption.AllDirectories)
                    .Single();
                await File.WriteAllTextAsync(preview, "corrupt", token).ConfigureAwait(false);
            })).ConfigureAwait(false);

        var checkpoint = Directory.EnumerateFiles(
            fixture.Paths.StagingPath,
            "build-checkpoint.json",
            SearchOption.AllDirectories).Single();
        Assert(File.Exists(checkpoint), "preview corruption leaves the pack unpublished and recoverable");

        var retryCounter = new TextureBuildCounter();
        var retryPreviews = new TexturePreviewCollector(24);
        var retryItems = new StagedBuildItem[]
        {
            new(fixture.RelativePaths[0], new ContributionBuilder(retryCounter, retryPreviews, counts, 0))
        };
        await new StagedBuildService().BuildAsync(
            Request(fixture, retryItems),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        Assert(counts[0] == 2,
            "a corrupt checkpointed preview rebuilds only its owning artifact before publication");
    }

    private static async Task TestLockedCheckpointNeverPublishesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await Fixture.CreateAsync("locked-marker", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        FileStream? checkpointLock = null;
        try
        {
            await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
                Request(fixture, CreateBuilders(fixture, counts)),
                cancellationToken: cancellationToken,
                finalizeAsync: (result, _) =>
                {
                    checkpointLock = new FileStream(
                        Path.Combine(result.BuildDirectory, "build-checkpoint.json"),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                    return Task.CompletedTask;
                })).ConfigureAwait(false);
        }
        finally
        {
            checkpointLock?.Dispose();
        }

        var hidden = await new StagedPackCatalogService().DiscoverAsync(
            fixture.Paths,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert(hidden.Count == 0, "a checkpoint unlink failure cannot publish the pack");
        var resumed = await CompleteAsync(
            fixture,
            CreateBuilders(fixture, counts),
            cancellationToken).ConfigureAwait(false);
        Assert(counts[0] == 1 && counts[1] == 1 && resumed.ResumedArtifactCount == 2,
            "a pack whose marker was temporarily locked remains recoverable without rebuilding");
    }

    private static async Task TestRecoveryDiscoverySkipsReparseDirectoryAsync(
        CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("reparse-source", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            new StagedBuildRequest(
                fixture.Paths,
                fixture.Options,
                CreateBuilders(fixture, counts, index =>
                {
                    if (index == 1 && !failed)
                    {
                        failed = true;
                        throw new IOException("simulated crash");
                    }
                }),
                BuildId: "build-linked",
                ResumeOperationKey: "checkpoint-self-test-v1"),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var alternateWorkspace = Path.Combine(fixture.Root, "AlternateWorkspace");
        var alternatePaths = new ProjectPaths(fixture.Paths.InstallPath, alternateWorkspace);
        Directory.CreateDirectory(alternatePaths.StagingPath);
        var target = Path.Combine(fixture.Paths.StagingPath, "build-linked");
        var link = Path.Combine(alternatePaths.StagingPath, "build-linked");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return;
        }

        var recovery = await new StagedBuildService().FindRecoverableBuildAsync(
            alternatePaths,
            "checkpoint-self-test-v1",
            cancellationToken).ConfigureAwait(false);
        Assert(recovery is null,
            "recovery discovery never reads a checkpoint through a staged-library reparse directory");
    }

    private static async Task TestRestartResumeAndFinalizationAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("restart", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        var firstBuilders = CreateBuilders(fixture, counts, index =>
        {
            if (index == 1 && !failed)
            {
                failed = true;
                throw new IOException("simulated crash");
            }
        });
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, firstBuilders), cancellationToken: cancellationToken)).ConfigureAwait(false);

        Assert(Directory.EnumerateFiles(fixture.Paths.StagingPath, "build-checkpoint.json", SearchOption.AllDirectories).Any(),
            "crash leaves a durable checkpoint");
        Assert(!Directory.EnumerateFiles(fixture.Paths.StagingPath, "manifest.json", SearchOption.AllDirectories).Any(),
            "manifest is not published before all artifacts finish");

        var progress = new List<ProgressUpdate>();
        var result = await new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts)),
            new InlineProgress(progress),
            cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        Assert(counts[0] == 1 && counts[1] == 2, "restart skips the completed first artifact");
        Assert(result.ResumedArtifactCount == 1, "result reports resumed artifact count");
        Assert(progress.Any(update => update.Stage == "Build"
            && update.Message.Contains("skipped duplicate", StringComparison.OrdinalIgnoreCase)),
            "resume is visible through ordinary Build progress");
        Assert(!File.Exists(Path.Combine(result.BuildDirectory, "build-checkpoint.json")),
            "lock-held workflow finalization removes checkpoint");
    }

    private static async Task TestTamperedPayloadRebuildsOnlyArtifactAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("tamper", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts, index =>
            {
                if (index == 1 && !failed)
                {
                    failed = true;
                    throw new IOException("simulated crash");
                }
            })), cancellationToken: cancellationToken)).ConfigureAwait(false);
        var payload = Directory.EnumerateFiles(fixture.Paths.StagingPath, "a.s3d", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(payload, "tampered"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var result = await CompleteAsync(
            fixture,
            CreateBuilders(fixture, counts),
            cancellationToken).ConfigureAwait(false);
        Assert(counts[0] == 2 && counts[1] == 2, "tampered completed payload is rebuilt while unaffected work remains bounded");
        Assert(result.ResumedArtifactCount == 0, "tampered payload is never counted as resumed");
    }

    private static async Task TestChangedPlanNeverResumesAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("plan", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts, index =>
            {
                if (index == 1 && !failed)
                {
                    failed = true;
                    throw new IOException("simulated crash");
                }
            })), cancellationToken: cancellationToken)).ConfigureAwait(false);

        var changed = fixture.Options with { MaximumDimension = 4096 };
        var result = await new StagedBuildService().BuildAsync(
            new StagedBuildRequest(
                fixture.Paths,
                changed,
                CreateBuilders(fixture, counts),
                ResumeOperationKey: "checkpoint-self-test-v1"),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        Assert(counts[0] == 2 && counts[1] == 2, "changed options start a distinct transaction");
        Assert(result.ResumedArtifactCount == 0, "changed options never resume old outputs");
    }

    private static async Task TestChangedPaintedThemeNeverResumesAsync(
        CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("painted-theme", cancellationToken)
            .ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        var original = fixture.Options with
        {
            Preset = TexturePreset.Illustrated,
            PaintedTheme = PaintedTheme.ClassicPainted
        };
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            new StagedBuildRequest(
                fixture.Paths,
                original,
                CreateBuilders(fixture, counts, index =>
                {
                    if (index == 1 && !failed)
                    {
                        failed = true;
                        throw new IOException("simulated crash");
                    }
                }),
                ResumeOperationKey: "checkpoint-self-test-v1"),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var changed = original with { PaintedTheme = PaintedTheme.DarkGothic };
        var result = await new StagedBuildService().BuildAsync(
            new StagedBuildRequest(
                fixture.Paths,
                changed,
                CreateBuilders(fixture, counts),
                ResumeOperationKey: "checkpoint-self-test-v1"),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        Assert(counts[0] == 2 && counts[1] == 2,
            "a changed painted theme starts a distinct transaction");
        Assert(result.ResumedArtifactCount == 0,
            "a different painted theme never resumes output from another look");
    }

    private static async Task TestChangedOperationNeverResumesAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("operation", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var failed = false;
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts, index =>
            {
                if (index == 1 && !failed)
                {
                    failed = true;
                    throw new IOException("simulated crash");
                }
            }), "checkpoint-self-test-v1"),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var result = await new StagedBuildService().BuildAsync(
            Request(fixture, CreateBuilders(fixture, counts), "checkpoint-self-test-v2"),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        Assert(counts[0] == 2 && counts[1] == 2,
            "a changed workflow revision starts an independent build transaction");
        Assert(result.ResumedArtifactCount == 0,
            "a different operation key never resumes stale pipeline output");
    }

    private static async Task TestNoKeyBuildIsImmediatelyVisibleAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("no-key", cancellationToken).ConfigureAwait(false);
        var counts = new int[2];
        var result = await new StagedBuildService().BuildAsync(
            new StagedBuildRequest(fixture.Paths, fixture.Options, CreateBuilders(fixture, counts)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert(!File.Exists(Path.Combine(result.BuildDirectory, "build-checkpoint.json")),
            "non-resumable repair build publishes without a lingering incomplete marker");
        var catalog = await new StagedPackCatalogService().DiscoverAsync(
            fixture.Paths,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert(catalog.Count == 1, "non-resumable build is immediately catalog-visible");
    }

    private static async Task TestStatisticsAndPreviewReplayAsync(CancellationToken cancellationToken)
    {
        using var fixture = await Fixture.CreateAsync("contribution", cancellationToken).ConfigureAwait(false);
        var firstCounter = new TextureBuildCounter();
        var firstPreviews = new TexturePreviewCollector(24);
        var counts = new int[2];
        var failed = false;
        var firstItems = new StagedBuildItem[]
        {
            new(fixture.RelativePaths[0], new ContributionBuilder(firstCounter, firstPreviews, counts, 0)),
            new(fixture.RelativePaths[1], new DelegateStagedArtifactBuilder((_, _) =>
            {
                counts[1]++;
                if (!failed)
                {
                    failed = true;
                    throw new IOException("simulated crash after contributed artifact");
                }
                return Task.CompletedTask;
            }))
        };
        await AssertThrowsAsync<IOException>(() => new StagedBuildService().BuildAsync(
            Request(fixture, firstItems), cancellationToken: cancellationToken)).ConfigureAwait(false);

        var counter = new TextureBuildCounter();
        var previews = new TexturePreviewCollector(24);
        var secondItems = new StagedBuildItem[]
        {
            new(fixture.RelativePaths[0], new ContributionBuilder(counter, previews, counts, 0)),
            new(fixture.RelativePaths[1], new ContributionBuilder(counter, previews, counts, 1))
        };
        var result = await new StagedBuildService().BuildAsync(
            Request(fixture, secondItems),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        var statistics = counter.Snapshot();
        Assert(result.ResumedArtifactCount == 1 && counts[0] == 1,
            "restart replays the first contribution without rerunning its builder");
        Assert(statistics.DiscoveredTextures == 2
               && statistics.EnhancedTextures == 2
               && statistics.PreservedTextures == 2
               && statistics.ReusedTextures == 2
               && statistics.FallbackTextures == 2,
            "resumed statistics preserve every exact per-artifact counter");
        Assert(statistics.PreservedReasons.GetValueOrDefault("checkpoint-test") == 2
               && statistics.Warnings.Count == 2,
            "resumed reasons and warnings are replayed exactly");
        var snapshot = previews.Snapshot();
        Assert(snapshot.Count == 2 && previews.ReviewSnapshot().Count == 2,
            "resumed preview and review metadata are replayed exactly");
        Assert(Path.GetFileName(snapshot[0].OriginalImagePath) == "000-before.png"
               && Path.GetFileName(snapshot[1].OriginalImagePath) == "001-before.png",
            "preview slots remain stable across a gap-free restart");
    }

    private static Task<StagedBuildResult> CompleteAsync(
        Fixture fixture,
        IReadOnlyList<StagedBuildItem> items,
        CancellationToken cancellationToken) =>
        new StagedBuildService().BuildAsync(
            Request(fixture, items),
            cancellationToken: cancellationToken,
            finalizeAsync: static (_, _) => Task.CompletedTask);

    private static StagedBuildRequest Request(
        Fixture fixture,
        IReadOnlyList<StagedBuildItem> items,
        string resumeOperationKey = "checkpoint-self-test-v1") =>
        new(fixture.Paths, fixture.Options, items, ResumeOperationKey: resumeOperationKey);

    private static IReadOnlyList<StagedBuildItem> CreateBuilders(
        Fixture fixture,
        int[] counts,
        Action<int>? beforeWrite = null) =>
        fixture.RelativePaths.Select((path, index) => new StagedBuildItem(
            path,
            new DelegateStagedArtifactBuilder(async (context, token) =>
            {
                counts[index]++;
                beforeWrite?.Invoke(index);
                await File.WriteAllBytesAsync(
                    context.DestinationPath,
                    System.Text.Encoding.UTF8.GetBytes(
                        $"enhanced-{index}-{context.Options.MaximumDimension}"),
                    token).ConfigureAwait(false);
            }))).ToArray();

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException($"Checkpoint assertion failed: {message}.");
    }

    private sealed class InlineProgress(List<ProgressUpdate> updates) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => updates.Add(value);
    }

    private sealed class ContributionBuilder(
        TextureBuildCounter counter,
        TexturePreviewCollector previews,
        int[] counts,
        int builderIndex) : IStagedArtifactBuilder, IStagedArtifactCheckpointParticipant
    {
        public async Task BuildAsync(
            StagedArtifactBuildContext context,
            CancellationToken cancellationToken = default)
        {
            counts[builderIndex]++;
            counter.Discover(10 + builderIndex);
            counter.Enhanced(20 + builderIndex);
            counter.Preserve("checkpoint-test");
            counter.Reused();
            counter.Fallback();
            counter.Warn($"warning-{builderIndex}");
            if (!previews.TryReserve(context.RelativeInstallPath, $"texture-{builderIndex}.dds", out var slot))
            {
                throw new InvalidOperationException("Expected preview reservation.");
            }

            Directory.CreateDirectory(context.PreviewDirectory);
            var before = Path.Combine(context.PreviewDirectory, $"{slot:D3}-before.png");
            var after = Path.Combine(context.PreviewDirectory, $"{slot:D3}-after.png");
            await File.WriteAllTextAsync(before, $"before-{builderIndex}", cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(after, $"after-{builderIndex}", cancellationToken)
                .ConfigureAwait(false);
            var entry = new TexturePreviewEntry(
                context.RelativeInstallPath,
                $"texture-{builderIndex}.dds",
                before,
                after,
                16,
                16,
                32,
                32);
            previews.Complete(slot, entry);
            previews.RegisterReview(new TextureReviewEntry(
                context.RelativeInstallPath,
                entry.LogicalName,
                16,
                16,
                32,
                32));
            await File.WriteAllTextAsync(
                context.DestinationPath,
                $"enhanced-contribution-{builderIndex}",
                cancellationToken).ConfigureAwait(false);
        }

        StagedArtifactCheckpointCapture IStagedArtifactCheckpointParticipant.CaptureCheckpointState() =>
            new(counter.CaptureCheckpoint(), previews.CaptureCheckpoint());

        StagedBuildCheckpointContribution IStagedArtifactCheckpointParticipant.CompleteCheckpointCapture(
            StagedArtifactCheckpointCapture before)
        {
            var delta = previews.CaptureDelta(before.Previews);
            return new StagedBuildCheckpointContribution(
                counter.CaptureDelta(before.Statistics),
                delta.Previews.Select(item => new StagedBuildCheckpointPreview(
                    item.Slot,
                    MakeRelative(item.Entry),
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
            previews.RestoreCheckpoint(
                contribution.Previews.Select(item => new TexturePreviewSlot(item.Slot, item.Entry)).ToArray(),
                contribution.ReviewEntries);
        }

        private static TexturePreviewEntry MakeRelative(TexturePreviewEntry entry)
        {
            var buildDirectory = Path.GetDirectoryName(Path.GetDirectoryName(entry.OriginalImagePath)!)!;
            return entry with
            {
                OriginalImagePath = Path.GetRelativePath(buildDirectory, entry.OriginalImagePath),
                EnhancedImagePath = Path.GetRelativePath(buildDirectory, entry.EnhancedImagePath)
            };
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, ProjectPaths paths, UpscaleOptions options, string[] relativePaths)
        {
            Root = root; Paths = paths; Options = options; RelativePaths = relativePaths;
        }
        public string Root { get; }
        public ProjectPaths Paths { get; }
        public UpscaleOptions Options { get; }
        public string[] RelativePaths { get; }

        public static async Task<Fixture> CreateAsync(string name, CancellationToken token)
        {
            var root = Path.Combine(Path.GetTempPath(), $"spintexture-checkpoint-{name}-{Guid.NewGuid():N}");
            var install = Path.Combine(root, "EverQuest");
            Directory.CreateDirectory(install);
            string[] paths = ["a.s3d", "b.s3d"];
            foreach (var path in paths)
            {
                await File.WriteAllBytesAsync(
                        Path.Combine(install, path),
                        System.Text.Encoding.UTF8.GetBytes($"original-{path}"),
                        token)
                    .ConfigureAwait(false);
            }
            return new Fixture(root, new ProjectPaths(install, Path.Combine(root, "Workspace")), UpscaleOptions.Recommended, paths);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
