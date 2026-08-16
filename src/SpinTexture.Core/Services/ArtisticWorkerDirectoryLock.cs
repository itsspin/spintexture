using SpinTexture.Core.Tooling;

namespace SpinTexture.Core.Services;

/// <summary>
/// Coordinates access to a managed artistic-worker directory across
/// SpinTexture processes. Shared leases are intended for build/preview use;
/// exclusive leases protect setup, publication, configuration, and removal.
/// </summary>
public static class ArtisticWorkerDirectoryLock
{
    private const int RetryDelayMilliseconds = 50;

    public static string GetLockFilePath(string workerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerDirectory);
        var fullWorkerDirectory = Path.GetFullPath(workerDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDirectory = Path.GetDirectoryName(fullWorkerDirectory);
        var workerDirectoryName = Path.GetFileName(fullWorkerDirectory);
        if (string.IsNullOrWhiteSpace(parentDirectory)
            || string.IsNullOrWhiteSpace(workerDirectoryName))
        {
            throw new ArgumentException(
                "The artistic worker directory must have a parent directory.",
                nameof(workerDirectory));
        }

        // The lock must not live below WorkerDirectory: Remove deliberately
        // deletes that entire tree while retaining its exclusive lease.
        return Path.Combine(parentDirectory, $".{workerDirectoryName}.access.lock");
    }

    public static Task<ArtisticWorkerDirectoryLease> AcquireSharedAsync(
        string workerDirectory,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(workerDirectory, isExclusive: false, cancellationToken);

    public static Task<ArtisticWorkerDirectoryLease> AcquireExclusiveAsync(
        string workerDirectory,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(workerDirectory, isExclusive: true, cancellationToken);

    internal static async Task<ArtisticWorkerDirectoryLease?> AcquireManagedSharedAsync(
        ProjectPaths paths,
        ExternalToolPaths tools,
        bool mayUseArtisticWorker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tools);
        var workerDirectory = mayUseArtisticWorker
            ? GetManagedWorkerDirectory(paths, tools)
            : null;
        return workerDirectory is null
            ? null
            : await AcquireSharedAsync(workerDirectory, cancellationToken).ConfigureAwait(false);
    }

    internal static string? GetManagedWorkerDirectory(
        ProjectPaths paths,
        ExternalToolPaths tools)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tools);
        if (!tools.HasArtisticWorker)
        {
            return null;
        }

        var workerPath = Path.GetFullPath(tools.ArtisticWorkerPath!);

        var managedCandidates = new[]
        {
            Path.Combine(paths.ToolsPath, ArtisticWorkerSetupService.WorkerDirectoryName),
            Path.Combine(
                AppContext.BaseDirectory,
                "Tools",
                ArtisticWorkerSetupService.WorkerDirectoryName)
        };
        return managedCandidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(candidate => IsPathContainedBy(candidate, workerPath));
    }

    public static ArtisticWorkerDirectoryLease AcquireExclusive(
        string workerDirectory,
        TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = timeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(timeout);
        try
        {
            return AcquireExclusiveAsync(workerDirectory, timeoutCancellation.Token)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException exception) when (timeoutCancellation.IsCancellationRequested)
        {
            throw new IOException(
                "The artistic worker is busy in another SpinTexture process. "
                + "Wait for its build, preview, setup, or removal to finish and try again.",
                exception);
        }
    }

    private static async Task<ArtisticWorkerDirectoryLease> AcquireAsync(
        string workerDirectory,
        bool isExclusive,
        CancellationToken cancellationToken)
    {
        var fullWorkerDirectory = Path.GetFullPath(workerDirectory);
        var lockFilePath = GetLockFilePath(fullWorkerDirectory);
        var lockDirectory = Path.GetDirectoryName(lockFilePath)
            ?? throw new InvalidOperationException("The artistic-worker lock path has no parent directory.");
        Directory.CreateDirectory(lockDirectory);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileStream stream;
                if (isExclusive)
                {
                    stream = new FileStream(
                        lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.None);
                }
                else
                {
                    EnsureLockFileExists(lockFilePath);
                    stream = new FileStream(
                        lockFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1,
                        FileOptions.None);
                }

                return new ArtisticWorkerDirectoryLease(
                    fullWorkerDirectory,
                    lockFilePath,
                    isExclusive,
                    stream);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureLockFileExists(string lockFilePath)
    {
        if (File.Exists(lockFilePath))
        {
            return;
        }

        // This short-lived, fully shared handle only bootstraps the persistent
        // lock file. Acquisition is retried if an exclusive owner wins between
        // this close and the shared open above.
        using var _ = new FileStream(
            lockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.None);
    }

    private static bool IsLockContention(IOException exception)
    {
        // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION. Do not turn other
        // I/O failures (bad paths, full disks, ACL problems) into endless waits.
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is 32 or 33;
    }

    private static bool IsPathContainedBy(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}

/// <summary>
/// An open-file lease. Keep the instance alive for the complete operation;
/// disposing it is the atomic release visible to other processes.
/// </summary>
public sealed class ArtisticWorkerDirectoryLease : IDisposable, IAsyncDisposable
{
    private FileStream? stream;

    internal ArtisticWorkerDirectoryLease(
        string workerDirectory,
        string lockFilePath,
        bool isExclusive,
        FileStream stream)
    {
        WorkerDirectory = workerDirectory;
        LockFilePath = lockFilePath;
        IsExclusive = isExclusive;
        this.stream = stream;
    }

    public string WorkerDirectory { get; }

    public string LockFilePath { get; }

    public bool IsExclusive { get; }

    public bool IsDisposed => Volatile.Read(ref stream) is null;

    internal void EnsureExclusiveFor(string workerDirectory)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!IsExclusive
            || !string.Equals(
                Path.GetFullPath(workerDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                WorkerDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "An exclusive lease for this artistic-worker directory is required.");
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref stream, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        var ownedStream = Interlocked.Exchange(ref stream, null);
        if (ownedStream is not null)
        {
            await ownedStream.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
