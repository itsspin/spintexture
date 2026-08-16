using SpinTexture.Core;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

internal static class ArtisticWorkerSetupSelfTests
{
    public static async Task RunAsync(TextWriter output)
    {
        await CancellationPreservesVerifiedWorkerAsync().ConfigureAwait(false);
        await ThrownVerificationErrorLeavesFirstInstallDisabledAsync().ConfigureAwait(false);
        await DisabledMarkerOverridesStaleLiveWorkerAsync().ConfigureAwait(false);
        await UnremovableDisabledMarkerRollsBackPublicationAsync().ConfigureAwait(false);
        await PendingGenerationBlocksSecondServiceMutationAsync().ConfigureAwait(false);
        await SecondServiceRemovalWaitsForRejectionAsync().ConfigureAwait(false);
        await SuccessfulPublicationReleasesMutationLeaseAsync().ConfigureAwait(false);
        await SharedLeasesBlockExclusiveMutationAsync().ConfigureAwait(false);
        await ManagedSharedLeaseScopesOnlyOwnedWorkersAsync().ConfigureAwait(false);
        await output.WriteLineAsync(
            "Artistic worker staged-verification, cancellation, disabled-marker, and cross-process lease tests passed.").ConfigureAwait(false);
    }

    private static async Task CancellationPreservesVerifiedWorkerAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var setup = new ArtisticWorkerSetupService(root);
            setup.WriteWorkerScripts(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                Path.Combine(root, "realesrgan-models"));
            var liveBatchPath = Path.Combine(setup.WorkerDirectory, "worker.bat");
            var livePowerShellPath = Path.Combine(setup.WorkerDirectory, "worker.ps1");
            var originalBatch = await File.ReadAllBytesAsync(liveBatchPath).ConfigureAwait(false);
            var originalPowerShell = await File.ReadAllBytesAsync(livePowerShellPath).ConfigureAwait(false);
            var originalConfig = await File.ReadAllBytesAsync(setup.ConfigPath).ConfigureAwait(false);

            setup.StageWorkerScriptsForVerification(
                Path.Combine(root, "replacement-realesrgan.exe"),
                Path.Combine(root, "replacement-models"));
            using var cancellation = new CancellationTokenSource();
            var runner = new DelegateNativeProcessRunner((command, token) =>
            {
                Assert(command.WorkingDirectory?.Contains(
                        ".worker-generations",
                        StringComparison.OrdinalIgnoreCase) == true,
                    "verification must execute the staged generation, not the discoverable worker");
                AssertFileBytes(originalBatch, liveBatchPath,
                    "the live shim must remain untouched while staged verification is running");
                cancellation.Cancel();
                return Task.FromCanceled<NativeProcessResult>(token);
            });

            await AssertThrowsAsync<OperationCanceledException>(() => setup.VerifyAsync(
                runner,
                WritePlaceholderInputAsync,
                _ => throw new InvalidOperationException("A canceled worker must not reach image validation."),
                cancellation.Token)).ConfigureAwait(false);

            AssertFileBytes(originalBatch, liveBatchPath,
                "verification cancellation must preserve the previously verified batch shim");
            AssertFileBytes(originalPowerShell, livePowerShellPath,
                "verification cancellation must preserve the previously verified worker script");
            AssertFileBytes(originalConfig, setup.ConfigPath,
                "verification cancellation must preserve the previously verified configuration");
            Assert(setup.GetStatus() is { IsInstalled: true, IsEnabled: true },
                "verification cancellation during an update must leave the previous worker enabled");
            Assert(!File.Exists(liveBatchPath + ".disabled"),
                "a preserved verified worker must not gain a disabled marker");
            AssertNoPendingGeneration(setup.WorkerDirectory);

            var secondProcess = new ArtisticWorkerSetupService(root);
            secondProcess.ApplyStylePreset("dark-oil");
            Assert(secondProcess.TryReadConfig()?.Preset == "dark-oil",
                "verification cancellation must release the mutation lease for another process");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task ThrownVerificationErrorLeavesFirstInstallDisabledAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var setup = new ArtisticWorkerSetupService(root);
            setup.StageWorkerScriptsForVerification(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                Path.Combine(root, "realesrgan-models"));
            var runner = new DelegateNativeProcessRunner((_, _) =>
            {
                Assert(!File.Exists(Path.Combine(setup.WorkerDirectory, "worker.bat")),
                    "a first install must remain undiscoverable while verification is running");
                return Task.FromException<NativeProcessResult>(
                    new InvalidOperationException("simulated GPU initialization failure"));
            });

            await AssertThrowsAsync<InvalidOperationException>(() => setup.VerifyAsync(
                runner,
                WritePlaceholderInputAsync,
                _ => throw new InvalidOperationException("A failed worker must not reach image validation.")))
                .ConfigureAwait(false);

            Assert(!File.Exists(Path.Combine(setup.WorkerDirectory, "worker.bat")),
                "a thrown verification error must not publish a discoverable worker");
            Assert(!File.Exists(Path.Combine(setup.WorkerDirectory, "worker.ps1")),
                "a thrown verification error must not publish the pending PowerShell worker");
            Assert(!File.Exists(setup.ConfigPath),
                "a thrown verification error must not publish the pending configuration");
            Assert(File.Exists(Path.Combine(setup.WorkerDirectory, "worker.bat.disabled")),
                "a failed first install should leave an explicit disabled marker");
            Assert(setup.GetStatus() is { IsInstalled: true, IsEnabled: false },
                "a thrown verification error must leave a first install disabled");
            AssertNoPendingGeneration(setup.WorkerDirectory);

            var secondProcess = new ArtisticWorkerSetupService(root);
            secondProcess.ApplyStylePreset("comic-ink");
            Assert(secondProcess.TryReadConfig()?.Preset == "comic-ink",
                "a thrown verification error must release the mutation lease for another process");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task DisabledMarkerOverridesStaleLiveWorkerAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var paths = new ProjectPaths(
                Path.Combine(root, "EverQuest"),
                Path.Combine(root, "Workspace"));
            paths.EnsureWorkspaceDirectories();
            var setup = new ArtisticWorkerSetupService(paths.ToolsPath);
            Directory.CreateDirectory(setup.WorkerDirectory);
            var liveBatchPath = Path.Combine(setup.WorkerDirectory, "worker.bat");
            await File.WriteAllTextAsync(liveBatchPath, "@echo unsafe-stale-worker").ConfigureAwait(false);
            await File.WriteAllTextAsync(liveBatchPath + ".disabled", "disabled").ConfigureAwait(false);

            Assert(setup.GetStatus() is { IsInstalled: true, IsEnabled: false },
                "a disabled marker must override a stale live worker after rollback failure");
            var discovered = new ToolchainDiscovery().Discover(paths);
            Assert(discovered.ArtisticWorkerPath is null
                    || !PathGuard.SamePath(discovered.ArtisticWorkerPath, liveBatchPath),
                "tool discovery must not use a worker protected by a disabled marker");
            Assert(discovered.Diagnostics.Any(message =>
                    message.Contains("safety marker", StringComparison.OrdinalIgnoreCase)),
                "tool discovery should explain why a marked worker was ignored");

            File.Delete(liveBatchPath + ".disabled");
            Directory.CreateDirectory(liveBatchPath + ".disabled");
            Assert(setup.GetStatus() is { IsInstalled: true, IsEnabled: false },
                "a directory occupying the safety-marker path must also fail closed");
            var directoryMarkedDiscovery = new ToolchainDiscovery().Discover(paths);
            Assert(directoryMarkedDiscovery.ArtisticWorkerPath is null
                    || !PathGuard.SamePath(directoryMarkedDiscovery.ArtisticWorkerPath, liveBatchPath),
                "tool discovery must not bypass a safety marker represented by a directory");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task UnremovableDisabledMarkerRollsBackPublicationAsync()
    {
        var root = CreateTestRoot();
        string? disabledMarkerPath = null;
        try
        {
            var setup = new ArtisticWorkerSetupService(root);
            setup.StageWorkerScriptsForVerification(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                Path.Combine(root, "realesrgan-models"));
            disabledMarkerPath = Path.Combine(setup.WorkerDirectory, "worker.bat.disabled");
            await File.WriteAllTextAsync(disabledMarkerPath, "locked safety marker").ConfigureAwait(false);
            File.SetAttributes(disabledMarkerPath, FileAttributes.ReadOnly);

            var runner = new DelegateNativeProcessRunner((command, _) =>
            {
                var outputIndex = command.Arguments.ToList().IndexOf("-o");
                Assert(outputIndex >= 0 && outputIndex + 1 < command.Arguments.Count,
                    "verification command must identify its output directory");
                var outputPath = Path.Combine(command.Arguments[outputIndex + 1], "verify.png");
                File.WriteAllBytes(outputPath, [0x56, 0x45, 0x52, 0x49, 0x46, 0x59]);
                return Task.FromResult(new NativeProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero));
            });

            await AssertThrowsAsync<UnauthorizedAccessException>(() => setup.VerifyAsync(
                runner,
                WritePlaceholderInputAsync,
                _ => Task.FromResult((Width: 384, Height: 384)))).ConfigureAwait(false);

            Assert(!File.Exists(Path.Combine(setup.WorkerDirectory, "worker.bat")),
                "publication must roll back the live shim when its disabled marker cannot be removed");
            Assert(!File.Exists(setup.ConfigPath),
                "publication rollback must remove the uncommitted first-install configuration");
            Assert(setup.GetStatus() is { IsInstalled: true, IsEnabled: false },
                "an unremovable safety marker must keep the worker disabled");
            AssertNoPendingGeneration(setup.WorkerDirectory);
        }
        finally
        {
            if (disabledMarkerPath is not null && File.Exists(disabledMarkerPath))
            {
                File.SetAttributes(disabledMarkerPath, FileAttributes.Normal);
            }

            DeleteTree(root);
        }
    }

    private static async Task PendingGenerationBlocksSecondServiceMutationAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var firstProcess = new ArtisticWorkerSetupService(root);
            var secondProcess = new ArtisticWorkerSetupService(root);
            firstProcess.StageWorkerScriptsForVerification(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                Path.Combine(root, "realesrgan-models"));

            await AssertThrowsAsync<IOException>(() => Task.Run(() =>
                secondProcess.ApplyStylePreset("epic-cinematic"))).ConfigureAwait(false);
            Assert(!File.Exists(secondProcess.ConfigPath),
                "a second process must not change live configuration while a staged generation owns the mutation lease");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() => firstProcess.VerifyAsync(
                new DelegateNativeProcessRunner((_, _) => throw new InvalidOperationException(
                    "an already-canceled verification must not invoke the worker")),
                WritePlaceholderInputAsync,
                _ => throw new InvalidOperationException(
                    "an already-canceled verification must not inspect an output"),
                cancellation.Token)).ConfigureAwait(false);

            secondProcess.ApplyStylePreset("epic-cinematic");
            Assert(secondProcess.TryReadConfig()?.Preset == "epic-cinematic",
                "canceling staged verification must release the cross-process mutation lease");
            AssertNoPendingGeneration(firstProcess.WorkerDirectory);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task SecondServiceRemovalWaitsForRejectionAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var firstProcess = new ArtisticWorkerSetupService(root);
            var secondProcess = new ArtisticWorkerSetupService(root);
            firstProcess.WriteWorkerScripts(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                Path.Combine(root, "realesrgan-models"));
            firstProcess.StageWorkerScriptsForVerification(
                Path.Combine(root, "replacement-realesrgan.exe"),
                Path.Combine(root, "replacement-models"));

            var removalStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var removal = Task.Run(() =>
            {
                removalStarted.SetResult();
                secondProcess.Remove();
            });
            await removalStarted.Task.ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false);
            Assert(!removal.IsCompleted,
                "a second process must wait instead of deleting a generation that is being verified");

            var rejection = await firstProcess.VerifyAsync(
                new DelegateNativeProcessRunner((_, _) => Task.FromResult(
                    new NativeProcessResult(19, string.Empty, "simulated rejection", TimeSpan.Zero))),
                WritePlaceholderInputAsync,
                _ => throw new InvalidOperationException("a rejected worker must not reach image validation."))
                .ConfigureAwait(false);
            Assert(rejection?.Contains("code 19", StringComparison.OrdinalIgnoreCase) == true,
                "the staged worker should follow the normal rejection path");

            await removal.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Assert(!Directory.Exists(firstProcess.WorkerDirectory),
                "waiting removal should run after rejected verification releases the lease");
            Assert(File.Exists(ArtisticWorkerDirectoryLock.GetLockFilePath(firstProcess.WorkerDirectory)),
                "the coordination file must remain outside the recursively removed worker directory");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task SuccessfulPublicationReleasesMutationLeaseAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var firstProcess = new ArtisticWorkerSetupService(root);
            var secondProcess = new ArtisticWorkerSetupService(root);
            firstProcess.StageWorkerScriptsForVerification(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                Path.Combine(root, "realesrgan-models"));
            var runner = new DelegateNativeProcessRunner((command, _) =>
            {
                var outputIndex = command.Arguments.ToList().IndexOf("-o");
                Assert(outputIndex >= 0 && outputIndex + 1 < command.Arguments.Count,
                    "successful verification must identify its output directory");
                File.WriteAllBytes(
                    Path.Combine(command.Arguments[outputIndex + 1], "verify.png"),
                    [0x53, 0x41, 0x4D, 0x45]);
                return Task.FromResult(
                    new NativeProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero));
            });

            var failure = await firstProcess.VerifyAsync(
                    runner,
                    WritePlaceholderInputAsync,
                    _ => Task.FromResult((Width: 384, Height: 384)))
                .ConfigureAwait(false);
            Assert(failure is null,
                "deterministic exact-4x staged verification should publish successfully");
            Assert(firstProcess.GetStatus() is { IsInstalled: true, IsEnabled: true },
                "successful publication must make the verified generation discoverable");

            secondProcess.ApplyStylePreset("storybook-watercolor");
            Assert(secondProcess.TryReadConfig()?.Preset == "storybook-watercolor",
                "successful publication must release its mutation lease for another process");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task SharedLeasesBlockExclusiveMutationAsync()
    {
        var root = CreateTestRoot();
        ArtisticWorkerDirectoryLease? firstReader = null;
        ArtisticWorkerDirectoryLease? secondReader = null;
        ArtisticWorkerDirectoryLease? writer = null;
        try
        {
            var workerDirectory = new ArtisticWorkerSetupService(root).WorkerDirectory;
            var lockPath = ArtisticWorkerDirectoryLock.GetLockFilePath(workerDirectory);
            Assert(!Path.GetFullPath(lockPath).StartsWith(
                    Path.GetFullPath(workerDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "the coordination file must be outside WorkerDirectory so Remove can retain its lease");

            firstReader = await ArtisticWorkerDirectoryLock.AcquireSharedAsync(workerDirectory)
                .ConfigureAwait(false);
            secondReader = await ArtisticWorkerDirectoryLock.AcquireSharedAsync(workerDirectory)
                .ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var writerTask = ArtisticWorkerDirectoryLock.AcquireExclusiveAsync(
                workerDirectory,
                timeout.Token);
            await Task.Delay(100).ConfigureAwait(false);
            Assert(!writerTask.IsCompleted,
                "an exclusive mutation must wait while build/preview shared leases are active");

            await firstReader.DisposeAsync().ConfigureAwait(false);
            firstReader = null;
            await Task.Delay(100).ConfigureAwait(false);
            Assert(!writerTask.IsCompleted,
                "all shared users must finish before an exclusive mutation begins");

            await secondReader.DisposeAsync().ConfigureAwait(false);
            secondReader = null;
            writer = await writerTask.ConfigureAwait(false);
            Assert(writer.IsExclusive,
                "the queued writer must receive an exclusive lease after readers release");
        }
        finally
        {
            if (firstReader is not null)
            {
                await firstReader.DisposeAsync().ConfigureAwait(false);
            }

            if (secondReader is not null)
            {
                await secondReader.DisposeAsync().ConfigureAwait(false);
            }

            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            DeleteTree(root);
        }
    }

    private static async Task ManagedSharedLeaseScopesOnlyOwnedWorkersAsync()
    {
        var root = CreateTestRoot();
        ArtisticWorkerDirectoryLease? managedLease = null;
        try
        {
            var paths = new ProjectPaths(
                Path.Combine(root, "EverQuest"),
                Path.Combine(root, "Workspace"));
            paths.EnsureWorkspaceDirectories();
            var setup = new ArtisticWorkerSetupService(paths.ToolsPath);
            var nestedManagedWorker = Path.Combine(
                setup.WorkerDirectory,
                ".worker-generations",
                "active-test",
                "worker.bat");
            var managedTools = new ExternalToolPaths(
                null,
                null,
                null,
                null,
                null,
                [],
                nestedManagedWorker);

            managedLease = await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                    paths,
                    managedTools,
                    mayUseArtisticWorker: true)
                .ConfigureAwait(false);
            Assert(managedLease is { IsExclusive: false },
                "a worker anywhere below SpinTexture's managed worker root must take a shared lease on that root");
            Assert(PathGuard.SamePath(managedLease!.WorkerDirectory, setup.WorkerDirectory),
                "a nested managed worker must coordinate on the setup/remove root, not its generation subdirectory");
            await AssertThrowsAsync<IOException>(() => Task.Run(() =>
                setup.ApplyStylePreset("painted-fantasy"))).ConfigureAwait(false);

            await managedLease.DisposeAsync().ConfigureAwait(false);
            managedLease = null;
            setup.ApplyStylePreset("painted-fantasy");

            var customWorkerDirectory = Path.Combine(root, "UserOwnedWorker");
            var customWorkerPath = Path.Combine(customWorkerDirectory, "worker.bat");
            Directory.CreateDirectory(customWorkerDirectory);
            await File.WriteAllTextAsync(customWorkerPath, "@echo custom").ConfigureAwait(false);
            var customTools = managedTools with { ArtisticWorkerPath = customWorkerPath };
            var customLease = await ArtisticWorkerDirectoryLock.AcquireManagedSharedAsync(
                    paths,
                    customTools,
                    mayUseArtisticWorker: true)
                .ConfigureAwait(false);
            Assert(customLease is null,
                "an env/custom worker outside SpinTexture-owned roots must not receive a managed lease");
            Assert(!File.Exists(ArtisticWorkerDirectoryLock.GetLockFilePath(customWorkerDirectory)),
                "managed lease detection must not create coordination files beside user-owned workers");
        }
        finally
        {
            if (managedLease is not null)
            {
                await managedLease.DisposeAsync().ConfigureAwait(false);
            }

            DeleteTree(root);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-artistic-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WritePlaceholderInputAsync(string path, int _)
    {
        await File.WriteAllBytesAsync(path, [0x53, 0x50, 0x49, 0x4E]).ConfigureAwait(false);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
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
            $"Self-test failed: expected {typeof(TException).Name}.");
    }

    private static void AssertFileBytes(byte[] expected, string path, string description)
    {
        Assert(File.Exists(path), description);
        Assert(expected.AsSpan().SequenceEqual(File.ReadAllBytes(path)), description);
    }

    private static void AssertNoPendingGeneration(string workerDirectory)
    {
        var generationsDirectory = Path.Combine(workerDirectory, ".worker-generations");
        Assert(
            !Directory.Exists(generationsDirectory)
                || !Directory.EnumerateDirectories(generationsDirectory).Any(),
            "failed verification must remove the pending worker generation");
    }

    private static void DeleteTree(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }

    private sealed class DelegateNativeProcessRunner(
        Func<NativeProcessCommand, CancellationToken, Task<NativeProcessResult>> run)
        : INativeProcessRunner
    {
        public Task<NativeProcessResult> RunAsync(
            NativeProcessCommand command,
            IProgress<NativeOutputLine>? progress = null,
            CancellationToken cancellationToken = default) =>
            run(command, cancellationToken);
    }
}
