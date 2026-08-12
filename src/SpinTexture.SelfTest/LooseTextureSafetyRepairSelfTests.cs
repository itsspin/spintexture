using System.Security.Cryptography;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;

namespace SpinTexture.SelfTest;

internal static class LooseTextureSafetyRepairSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestProtectedCelestialRestoresOriginalAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestUnaffectedLooseArtifactReusesVerifiedBaselineAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestChangedBaselineIsRejectedAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TestProtectedCelestialRestoresOriginalAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("celestial");
        try
        {
            var original = Path.Combine(root, "source", "sunburst.dds");
            var baseline = Path.Combine(root, "baseline", "sunburst.dds");
            var destination = Path.Combine(root, "output", "sunburst.dds");
            await WriteBytesAsync(original, 113, seed: 11, cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(baseline, 409, seed: 29, cancellationToken).ConfigureAwait(false);
            var baselineFingerprint = await FingerprintAsync(baseline, cancellationToken)
                .ConfigureAwait(false);
            var counter = new TextureBuildCounter();
            var builder = new LooseTextureSafetyRepairBuilder(
                baseline,
                baselineFingerprint.Length,
                baselineFingerprint.Sha256,
                counter,
                new ThrowingMaterializer());

            await builder.BuildAsync(
                    Context(
                        $"ActorEffects{Path.DirectorySeparatorChar}sunburst.dds",
                        original,
                        destination),
                    cancellationToken)
                .ConfigureAwait(false);

            AssertBytesEqual(original, destination, "protected celestial original restore");
            var stats = counter.Snapshot();
            AssertEqual(1, stats.PreservedTextures, "protected celestial preserved count");
            AssertEqual(0, stats.ReusedTextures, "protected celestial reuse count");
            AssertEqual(
                1,
                stats.PreservedReasons[CelestialTextureSafetyPolicy.CelestialSpritePreservedReason],
                "protected celestial reason count");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task TestUnaffectedLooseArtifactReusesVerifiedBaselineAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("reuse");
        try
        {
            var original = Path.Combine(root, "source", "ordinary.dds");
            var baseline = Path.Combine(root, "baseline", "ordinary.dds");
            var destination = Path.Combine(root, "output", "ordinary.dds");
            await WriteBytesAsync(original, 127, seed: 7, cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(baseline, 521, seed: 47, cancellationToken).ConfigureAwait(false);
            var fingerprint = await FingerprintAsync(baseline, cancellationToken).ConfigureAwait(false);
            var materializer = new RecordingMaterializer();
            var counter = new TextureBuildCounter();
            var builder = new LooseTextureSafetyRepairBuilder(
                baseline,
                fingerprint.Length,
                fingerprint.Sha256,
                counter,
                materializer);

            await builder.BuildAsync(
                    Context(
                        $"SpellEffects{Path.DirectorySeparatorChar}ordinary.dds",
                        original,
                        destination),
                    cancellationToken)
                .ConfigureAwait(false);

            AssertBytesEqual(baseline, destination, "unaffected exact baseline reuse");
            AssertEqual(1, materializer.CallCount, "unaffected materializer call count");
            var stats = counter.Snapshot();
            AssertEqual(0, stats.PreservedTextures, "unaffected preserved count");
            AssertEqual(1, stats.ReusedTextures, "unaffected reuse count");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task TestChangedBaselineIsRejectedAsync(
        CancellationToken cancellationToken)
    {
        var root = CreateRoot("tamper");
        try
        {
            var original = Path.Combine(root, "source", "ordinary.dds");
            var baseline = Path.Combine(root, "baseline", "ordinary.dds");
            var destination = Path.Combine(root, "output", "ordinary.dds");
            await WriteBytesAsync(original, 131, seed: 13, cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(baseline, 263, seed: 31, cancellationToken).ConfigureAwait(false);
            var fingerprint = await FingerprintAsync(baseline, cancellationToken).ConfigureAwait(false);
            var materializer = new RecordingMaterializer();
            var builder = new LooseTextureSafetyRepairBuilder(
                baseline,
                fingerprint.Length,
                fingerprint.Sha256,
                new TextureBuildCounter(),
                materializer);
            await File.AppendAllTextAsync(baseline, "changed", cancellationToken).ConfigureAwait(false);

            await AssertThrowsAsync<InvalidDataException>(() => builder.BuildAsync(
                Context(
                    $"SpellEffects{Path.DirectorySeparatorChar}ordinary.dds",
                    original,
                    destination),
                cancellationToken)).ConfigureAwait(false);
            AssertEqual(0, materializer.CallCount, "tampered baseline materializer call count");
            Assert(!File.Exists(destination), "tampered baseline must not emit output");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static StagedArtifactBuildContext Context(
        string relativePath,
        string sourcePath,
        string destinationPath) =>
        new(
            relativePath,
            sourcePath,
            destinationPath,
            Path.Combine(Path.GetDirectoryName(destinationPath)!, "work"),
            Path.Combine(Path.GetDirectoryName(destinationPath)!, "previews"),
            new UpscaleOptions(
                TexturePreset.Faithful,
                AssetScope.SpellEffectsOnly,
                MaximumDimension: 2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false,
                SelectedZone: null));

    private static string CreateRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-loose-safety-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteBytesAsync(
        string path,
        int length,
        int seed,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long Length, string Sha256)> FingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return (input.Length, Convert.ToHexStringLower(hash));
    }

    private static void AssertBytesEqual(string expectedPath, string actualPath, string message)
    {
        var expected = File.ReadAllBytes(expectedPath);
        var actual = File.ReadAllBytes(actualPath);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Loose safety repair assertion failed: {message}.");
        }
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
            $"Loose safety repair assertion failed: expected {typeof(TException).Name}.");
    }

    private static void AssertEqual(int expected, int actual, string message)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"Loose safety repair assertion failed for {message}: expected {expected}, got {actual}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Loose safety repair assertion failed: {message}.");
        }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingMaterializer : IStagedPackPayloadMaterializer
    {
        public int CallCount { get; private set; }

        public async Task<StagedPackMaterializationKind> MaterializeAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(
                    destinationPath,
                    await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
            return StagedPackMaterializationKind.VerifiedCopy;
        }
    }

    private sealed class ThrowingMaterializer : IStagedPackPayloadMaterializer
    {
        public Task<StagedPackMaterializationKind> MaterializeAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Protected celestial repair must not reuse the prior staged output.");
    }
}
