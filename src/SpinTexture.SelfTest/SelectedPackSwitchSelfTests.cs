using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class SelectedPackSwitchSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-selected-pack-switch-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var originalCharacters = "original-character-archive"u8.ToArray();
            var enhancedCharacters = "enhanced-character-archive-with-crisp-textures"u8.ToArray();
            var originalHateplane = "original-hateplane-zone-archive"u8.ToArray();
            var enhancedHateplane = "enhanced-hateplane-zone-archive-with-crisp-textures"u8.ToArray();
            var characterPath = Path.Combine(installPath, "global_chr.s3d");
            var hateplanePath = Path.Combine(installPath, "hateplane.s3d");
            await File.WriteAllBytesAsync(characterPath, originalCharacters, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(hateplanePath, originalHateplane, cancellationToken)
                .ConfigureAwait(false);

            var builderInvocationCount = 0;
            var characterPack = await CreatePackAsync(
                    paths,
                    "build-character-switch-test",
                    new UpscaleOptions(
                        TexturePreset.MaximumDetail,
                        AssetScope.CharactersAndEquipmentOnly,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false),
                    "global_chr.s3d",
                    enhancedCharacters,
                    () => Interlocked.Increment(ref builderInvocationCount),
                    cancellationToken)
                .ConfigureAwait(false);
            var hateplanePack = await CreatePackAsync(
                    paths,
                    "build-hateplane-switch-test",
                    new UpscaleOptions(
                        TexturePreset.ClassicHd,
                        AssetScope.SelectedZone,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "hateplane"),
                    "hateplane.s3d",
                    enhancedHateplane,
                    () => Interlocked.Increment(ref builderInvocationCount),
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(2, builderInvocationCount, "initial staged builders");

            var constituentFingerprints = await FingerprintPacksAsync(
                    [characterPack.ManifestPath, hateplanePack.ManifestPath],
                    cancellationToken)
                .ConfigureAwait(false);
            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var health = new InstallHealthService(store);
            var catalog = new StagedPackCatalogService(store);
            var composer = new StagedPackComposer(
                catalog,
                store,
                new HardLinkOrCopyStagedPackPayloadMaterializer((_, _) => false));
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: health,
                stagedPackCatalogService: catalog,
                stagedPackComposer: composer);

            var characterApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [characterPack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                enhancedCharacters,
                await File.ReadAllBytesAsync(characterPath, cancellationToken).ConfigureAwait(false),
                "character-only active archive");
            AssertSequenceEqual(
                originalHateplane,
                await File.ReadAllBytesAsync(hateplanePath, cancellationToken).ConfigureAwait(false),
                "disjoint zone remains original during character-only install");

            // A changed disjoint archive must be detected before the active pack
            // is restored, so a failed switch cannot unnecessarily disable it.
            var externallyChangedHateplane = "launcher-patched-hateplane-archive"u8.ToArray();
            await File.WriteAllBytesAsync(
                    hateplanePath,
                    externallyChangedHateplane,
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertThrowsAsync<InvalidOperationException>(
                    () => workflow.ApplySelectedStagedPacksAsync(
                        paths,
                        [characterPack.ManifestPath, hateplanePack.ManifestPath],
                        cancellationToken: cancellationToken),
                    "changed disjoint source should block a pack switch")
                .ConfigureAwait(false);
            AssertSequenceEqual(
                enhancedCharacters,
                await File.ReadAllBytesAsync(characterPath, cancellationToken).ConfigureAwait(false),
                "blocked switch must leave the active character pack installed");
            var healthAfterBlockedSwitch = await workflow
                .AuditInstallHealthAsync(paths, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallHealthState.EnhancedActive,
                healthAfterBlockedSwitch.State,
                "blocked switch active-pack health");
            AssertEqual(
                characterApply.ApplyId,
                healthAfterBlockedSwitch.ApplyId!,
                "blocked switch active transaction identity");

            await File.WriteAllBytesAsync(hateplanePath, originalHateplane, cancellationToken)
                .ConfigureAwait(false);
            var combinedApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [characterPack.ManifestPath, hateplanePack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(
                !PathGuard.SamePath(
                    combinedApply.Manifest.BuildManifestPath,
                    characterPack.ManifestPath),
                "disjoint checked packs should install through a composite manifest");
            AssertSequenceEqual(
                enhancedCharacters,
                await File.ReadAllBytesAsync(characterPath, cancellationToken).ConfigureAwait(false),
                "combined install character archive");
            AssertSequenceEqual(
                enhancedHateplane,
                await File.ReadAllBytesAsync(hateplanePath, cancellationToken).ConfigureAwait(false),
                "combined install Hateplane archive");

            var zoneOnlyApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [hateplanePack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                originalCharacters,
                await File.ReadAllBytesAsync(characterPath, cancellationToken).ConfigureAwait(false),
                "switching to zone-only should restore excluded character archive");
            AssertSequenceEqual(
                enhancedHateplane,
                await File.ReadAllBytesAsync(hateplanePath, cancellationToken).ConfigureAwait(false),
                "switching to zone-only should retain enhanced Hateplane through staged reuse");

            var repeatedZoneApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [hateplanePack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                zoneOnlyApply.ApplyId,
                repeatedZoneApply.ApplyId,
                "reapplying the same selected pack should be an idempotent no-op");
            AssertEqual(
                2,
                builderInvocationCount,
                "composition and switching must never rerun staged artifact builders");
            await AssertFingerprintsUnchangedAsync(
                    constituentFingerprints,
                    cancellationToken)
                .ConfigureAwait(false);

            await workflow.RestoreLatestAsync(paths, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                originalCharacters,
                await File.ReadAllBytesAsync(characterPath, cancellationToken).ConfigureAwait(false),
                "final restore character archive");
            AssertSequenceEqual(
                originalHateplane,
                await File.ReadAllBytesAsync(hateplanePath, cancellationToken).ConfigureAwait(false),
                "final restore Hateplane archive");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static Task<StagedBuildResult> CreatePackAsync(
        ProjectPaths paths,
        string buildId,
        UpscaleOptions options,
        string relativeInstallPath,
        byte[] stagedBytes,
        Action onBuild,
        CancellationToken cancellationToken)
    {
        var builder = new DelegateStagedArtifactBuilder(
            async (context, token) =>
            {
                onBuild();
                await File.WriteAllBytesAsync(context.DestinationPath, stagedBytes, token)
                    .ConfigureAwait(false);
            });
        return new StagedBuildService().BuildAsync(
            new StagedBuildRequest(
                paths,
                options,
                [new StagedBuildItem(relativeInstallPath, builder)],
                buildId),
            cancellationToken: cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, FileFingerprint>> FingerprintPacksAsync(
        IReadOnlyList<string> manifestPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileFingerprint>(
            StringComparer.OrdinalIgnoreCase);
        var store = new ManifestStore();
        foreach (var manifestPath in manifestPaths)
        {
            result.Add(
                manifestPath,
                await FileIntegrity.FingerprintAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false));
            var manifest = await store.ReadBuildManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in manifest.Entries)
            {
                var payloadPath = PathGuard.ResolveUnderRoot(
                    Path.Combine(Path.GetDirectoryName(manifestPath)!, "payload"),
                    entry.RelativeInstallPath);
                result.Add(
                    payloadPath,
                    await FileIntegrity.FingerprintAsync(payloadPath, cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        return result;
    }

    private static async Task AssertFingerprintsUnchangedAsync(
        IReadOnlyDictionary<string, FileFingerprint> expected,
        CancellationToken cancellationToken)
    {
        foreach (var item in expected)
        {
            var observed = await FileIntegrity.FingerprintAsync(item.Key, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(item.Value, observed, $"immutable staged constituent {item.Key}");
        }
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string description)
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
            $"Self-test failed: {description} did not throw {typeof(TException).Name}.");
    }

    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(root, recursive: true);
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description}; expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequenceEqual(
        byte[] expected,
        byte[] actual,
        string description)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }
}
