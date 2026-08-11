using System.Security.Cryptography;

namespace SpinTexture.Core.Pipeline;

internal sealed record FileFingerprint(long Length, string Sha256);

internal static class FileIntegrity
{
    public static async Task<FileFingerprint> FingerprintAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new FileFingerprint(stream.Length, Convert.ToHexStringLower(hash));
    }

    public static async Task EnsureMatchesAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} is missing.", path);
        }

        var actual = await FingerprintAsync(path, cancellationToken).ConfigureAwait(false);
        if (actual.Length != expectedLength
            || !string.Equals(actual.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description} failed its SHA-256 integrity check: {path}");
        }
    }
}
