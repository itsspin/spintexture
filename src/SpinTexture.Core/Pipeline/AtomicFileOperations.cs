namespace SpinTexture.Core.Pipeline;

internal interface IAtomicFileOperations
{
    Task CopyAndReplaceAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default,
        Action? onCommitted = null);
}

internal sealed class AtomicFileOperations : IAtomicFileOperations
{
    public Task CopyAndReplaceAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default,
        Action? onCommitted = null) =>
        AtomicFile.CopyAndReplaceAsync(
            sourcePath,
            destinationPath,
            expectedLength,
            expectedSha256,
            cancellationToken,
            onCommitted);
}
