namespace SpinTexture.Core.Pipeline;

internal sealed class TransactionLock : IDisposable
{
    private readonly FileStream _stream;

    private TransactionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static TransactionLock Acquire(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        Directory.CreateDirectory(workspacePath);
        var lockPath = Path.Combine(Path.GetFullPath(workspacePath), ".install-transaction.lock");

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"PID={Environment.ProcessId}; UTC={DateTimeOffset.UtcNow:O}");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return new TransactionLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another SpinTexture apply or restore transaction is already using this workspace.",
                exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
