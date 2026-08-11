namespace SpinTexture.Core.Pipeline;

internal sealed class StagingLibraryLock : IDisposable
{
    private readonly FileStream stream;

    private StagingLibraryLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static StagingLibraryLock Acquire(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        Directory.CreateDirectory(workspacePath);
        var lockPath = Path.Combine(Path.GetFullPath(workspacePath), ".staging-library.lock");
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"PID={Environment.ProcessId}; UTC={DateTimeOffset.UtcNow:O}");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return new StagingLibraryLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another SpinTexture build or staged-pack operation is already using this pack library.",
                exception);
        }
    }

    public void Dispose() => stream.Dispose();
}
