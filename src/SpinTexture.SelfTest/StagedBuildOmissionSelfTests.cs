using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.SelfTest;

internal static class StagedBuildOmissionSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestRequiredSourceIdenticalOutputIsStrictByDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestAllOptInOmissionsStillFailClosedAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestOptInOmissionKeepsChangedSiblingAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TestRequiredSourceIdenticalOutputIsStrictByDefaultAsync(
        CancellationToken cancellationToken)
    {
        var fixture = await Fixture.CreateAsync("strict", cancellationToken).ConfigureAwait(false);
        try
        {
            var copySource = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    File.ReadAllBytes(context.SourcePath),
                    token));
            await AssertThrowsAsync<InvalidOperationException>(() =>
                new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        fixture.Paths,
                        fixture.Options,
                        [new StagedBuildItem(fixture.RestoredRelativePath, copySource)],
                        "build-required-identical-strict",
                        RequireAllItems: true),
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            Assert(
                !Directory.Exists(Path.Combine(
                    fixture.Paths.StagingPath,
                    "build-required-identical-strict")),
                "strict source-identical failure must clean its transaction");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task TestAllOptInOmissionsStillFailClosedAsync(
        CancellationToken cancellationToken)
    {
        var fixture = await Fixture.CreateAsync("all-omitted", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var copySource = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    File.ReadAllBytes(context.SourcePath),
                    token));
            var exception = await AssertThrowsAsync<InvalidOperationException>(() =>
                new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        fixture.Paths,
                        fixture.Options,
                        [
                            new StagedBuildItem(
                                fixture.RestoredRelativePath,
                                copySource,
                                AllowSourceIdenticalOmission: true)
                        ],
                        "build-all-identical-omitted",
                        RequireAllItems: true),
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            Assert(
                exception.Message.Contains(
                    "no changed install artifacts",
                    StringComparison.OrdinalIgnoreCase),
                "all-omitted build reports that it cannot produce an empty replacement");
            Assert(
                !Directory.Exists(Path.Combine(
                    fixture.Paths.StagingPath,
                    "build-all-identical-omitted")),
                "all-omitted build cleans its empty transaction");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task TestOptInOmissionKeepsChangedSiblingAsync(
        CancellationToken cancellationToken)
    {
        var fixture = await Fixture.CreateAsync("opt-in", cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = new DelegateStagedArtifactBuilder(
                async (context, token) =>
                {
                    var output = context.RelativeInstallPath.Equals(
                        fixture.RestoredRelativePath,
                        StringComparison.OrdinalIgnoreCase)
                        ? await File.ReadAllBytesAsync(context.SourcePath, token).ConfigureAwait(false)
                        : "prior enhanced ordinary loose texture"u8.ToArray();
                    await File.WriteAllBytesAsync(context.DestinationPath, output, token)
                        .ConfigureAwait(false);
                });

            var result = await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        fixture.Paths,
                        fixture.Options,
                        [
                            new StagedBuildItem(
                                fixture.RestoredRelativePath,
                                builder,
                                AllowSourceIdenticalOmission: true),
                            new StagedBuildItem(fixture.ReusedRelativePath, builder)
                        ],
                        "build-required-identical-opt-in",
                        RequireAllItems: true),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            AssertEqual(1, result.Manifest.Entries.Count, "opt-in replacement entry count");
            AssertEqual(
                fixture.ReusedRelativePath,
                result.Manifest.Entries[0].RelativeInstallPath,
                "changed sibling retained in replacement manifest");
            Assert(
                !File.Exists(Path.Combine(
                    result.BuildDirectory,
                    "payload",
                    fixture.RestoredRelativePath)),
                "restored-original artifact omitted from replacement payload");
            Assert(
                File.Exists(Path.Combine(
                    result.BuildDirectory,
                    "payload",
                    fixture.ReusedRelativePath)),
                "changed sibling remains in replacement payload");

            AssertSequenceEqual(
                await File.ReadAllBytesAsync(
                    Path.Combine(fixture.Paths.InstallPath, fixture.RestoredRelativePath),
                    cancellationToken).ConfigureAwait(false),
                fixture.RestoredOriginal,
                "staged build does not mutate restored live source");
            AssertSequenceEqual(
                await File.ReadAllBytesAsync(
                    Path.Combine(fixture.Paths.InstallPath, fixture.ReusedRelativePath),
                    cancellationToken).ConfigureAwait(false),
                fixture.ReusedOriginal,
                "staged build does not mutate sibling live source");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Staged omission assertion failed: expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Staged omission assertion failed: {message}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Staged omission assertion failed for {message}: expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequenceEqual(
        IReadOnlyList<byte> actual,
        IReadOnlyList<byte> expected,
        string message)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException($"Staged omission assertion failed: {message}.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string root,
            ProjectPaths paths,
            UpscaleOptions options,
            string restoredRelativePath,
            string reusedRelativePath,
            byte[] restoredOriginal,
            byte[] reusedOriginal)
        {
            Root = root;
            Paths = paths;
            Options = options;
            RestoredRelativePath = restoredRelativePath;
            ReusedRelativePath = reusedRelativePath;
            RestoredOriginal = restoredOriginal;
            ReusedOriginal = reusedOriginal;
        }

        public string Root { get; }
        public ProjectPaths Paths { get; }
        public UpscaleOptions Options { get; }
        public string RestoredRelativePath { get; }
        public string ReusedRelativePath { get; }
        public byte[] RestoredOriginal { get; }
        public byte[] ReusedOriginal { get; }

        public static async Task<Fixture> CreateAsync(
            string suffix,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"spintexture-staged-omission-{suffix}-{Guid.NewGuid():N}");
            var install = Path.Combine(root, "EverQuest");
            var workspace = Path.Combine(root, "Workspace");
            var restoredRelative = Path.Combine("SpellEffects", "sunburst.dds");
            var reusedRelative = Path.Combine("SpellEffects", "ordinary.dds");
            var restoredOriginal = "verified original celestial texture"u8.ToArray();
            var reusedOriginal = "verified original ordinary texture"u8.ToArray();
            Directory.CreateDirectory(Path.Combine(install, "SpellEffects"));
            await File.WriteAllBytesAsync(
                Path.Combine(install, restoredRelative),
                restoredOriginal,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(install, reusedRelative),
                reusedOriginal,
                cancellationToken).ConfigureAwait(false);

            return new Fixture(
                root,
                new ProjectPaths(install, workspace),
                new UpscaleOptions(
                    TexturePreset.Faithful,
                    AssetScope.SpellEffectsOnly,
                    MaximumDimension: 2048,
                    GenerateMipMaps: true,
                    InstallAfterBuild: false,
                    SelectedZone: null),
                restoredRelative,
                reusedRelative,
                restoredOriginal,
                reusedOriginal);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
