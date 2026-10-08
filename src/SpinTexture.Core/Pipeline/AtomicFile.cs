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
        // The temporary file is always a sibling, so this is a same-volume rename
        // (MoveFileEx + MOVEFILE_REPLACE_EXISTING): it either replaces the destination
        // or fails with both names intact. File.Replace without a backup name can fail
        // after deleting the destination (ERROR_UNABLE_TO_MOVE_REPLACEMENT), and every
        // caller then deletes the temporary file, losing the only surviving copy.
        File.Move(temporaryPath, destinationPath, overwrite: true);
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
