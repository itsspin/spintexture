namespace SpinTexture.Core.Pipeline;

internal static class AtomicFile
{
    public static string CreateTemporarySiblingPath(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Destination path has no parent directory.");
        var extension = Path.GetExtension(fullPath);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(directory, $".{name}.spintexture-{Guid.NewGuid():N}{extension}");
    }

    public static async Task CopyAndReplaceAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default,
        Action? onCommitted = null)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryPath = CreateTemporarySiblingPath(fullDestination);

        try
        {
            await CopyDurablyAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            await FileIntegrity.EnsureMatchesAsync(
                temporaryPath,
                expectedLength,
                expectedSha256,
                "Temporary transaction copy",
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CommitTemporaryFile(temporaryPath, fullDestination);
            onCommitted?.Invoke();
            await FileIntegrity.EnsureMatchesAsync(
                fullDestination,
                expectedLength,
                expectedSha256,
                "Committed transaction file",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static async Task CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    public static void CommitTemporaryFile(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the original transaction failure; stale temp files are recognizable and recoverable.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original transaction failure.
        }
    }
}
