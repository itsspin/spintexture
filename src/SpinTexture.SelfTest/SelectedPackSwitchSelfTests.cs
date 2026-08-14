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

        await TestSameArchiveStyleReplacementAsync(cancellationToken).ConfigureAwait(false);
        await TestFalseLauncherRevertDoesNotRetireRestoreTrackingAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TestSameArchiveStyleReplacementAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-style-replacement-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var original = "verified-vanilla-blackburrow"u8.ToArray();
            var painted = "graphic-painted-blackburrow"u8.ToArray();
            var textureHd = "pbrify-span-v4-blackburrow"u8.ToArray();
            var archivePath = Path.Combine(installPath, "blackburrow.s3d");
            await File.WriteAllBytesAsync(archivePath, original, cancellationToken)
                .ConfigureAwait(false);

            var paintedPack = await CreatePackAsync(
                    paths,
                    "build-painted-style-replacement-test",
                    new UpscaleOptions(
                        TexturePreset.Illustrated,
                        AssetScope.SelectedZone,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "blackburrow",
                        PaintedTheme: PaintedTheme.ClassicPainted),
                    "blackburrow.s3d",
                    painted,
                    () => { },
                    cancellationToken)
                .ConfigureAwait(false);
            var textureHdPack = await CreatePackAsync(
                    paths,
                    "build-texture-hd-style-replacement-test",
                    new UpscaleOptions(
                        TexturePreset.ClassicHd,
                        AssetScope.SelectedZone,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "blackburrow"),
                    "blackburrow.s3d",
                    textureHd,
                    () => { },
                    cancellationToken)
                .ConfigureAwait(false);

            var store = new ManifestStore();
            var transactions = new InstallTransactionService(
                store,
                new AtomicFileOperations(),
                ensureGameStopped: _ => { });
            var health = new InstallHealthService(store);
            var catalog = new StagedPackCatalogService(store);
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: transactions,
                manifestStore: store,
                installHealthService: health,
                stagedPackCatalogService: catalog,
                stagedPackComposer: new StagedPackComposer(catalog, store));

            await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [paintedPack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                painted,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "painted style should be active before explicit restore");

            await workflow.RestoreLatestAsync(paths, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                original,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "explicit vanilla restore must remove every painted byte");

            var textureHdApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [textureHdPack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                textureHd,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "Texture HD install after vanilla restore must contain only Texture HD bytes");
            AssertEqual(
                TexturePreset.ClassicHd,
                (await store.ReadBuildManifestAsync(
                        textureHdApply.Manifest.BuildManifestPath,
                        cancellationToken)
                    .ConfigureAwait(false)).Options.Preset,
                "active replacement manifest style");
            AssertEqual(1, textureHdApply.Manifest.Entries.Count, "active replacement artifact count");

            // Also exercise the normal one-click pack switch. It must restore
            // the managed vanilla source before applying the replacement even
            // though both styles target the exact same archive.
            var directPaintedApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [paintedPack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                painted,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "direct Texture HD to painted replacement");
            Assert(
                !directPaintedApply.ApplyId.Equals(
                    textureHdApply.ApplyId,
                    StringComparison.OrdinalIgnoreCase),
                "a conflicting style replacement must use a new full transaction");

            var directTextureHdApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [textureHdPack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                textureHd,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "direct painted to Texture HD replacement must not retain painted bytes");
            AssertEqual(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(original)),
                directTextureHdApply.Manifest.Entries.Single().OriginalSha256!,
                "replacement transaction must keep vanilla source provenance");
            AssertEqual(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(textureHd)),
                directTextureHdApply.Manifest.Entries.Single().InstalledSha256,
                "replacement transaction must identify only Texture HD output");

            await workflow.RestoreLatestAsync(paths, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                original,
                await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false),
                "final style-replacement restore");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestFalseLauncherRevertDoesNotRetireRestoreTrackingAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-false-launcher-revert-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var originalPaintedTarget = "original-blackburrow-archive"u8.ToArray();
            var paintedOutput = "larger-graphic-painted-blackburrow-archive"u8.ToArray();
            var originalHdTarget = "original-qeynos-archive"u8.ToArray();
            var hdOutput = "larger-pbrify-span-v4-qeynos-archive"u8.ToArray();
            var paintedTargetPath = Path.Combine(installPath, "blackburrow.s3d");
            var hdTargetPath = Path.Combine(installPath, "qeynos.s3d");
            await File.WriteAllBytesAsync(
                    paintedTargetPath,
                    originalPaintedTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(hdTargetPath, originalHdTarget, cancellationToken)
                .ConfigureAwait(false);

            var paintedPack = await CreatePackAsync(
                    paths,
                    "build-painted-false-revert-test",
                    new UpscaleOptions(
                        TexturePreset.Illustrated,
                        AssetScope.SelectedZone,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "blackburrow",
                        PaintedTheme: PaintedTheme.ClassicPainted),
                    "blackburrow.s3d",
                    paintedOutput,
                    () => { },
                    cancellationToken)
                .ConfigureAwait(false);
            var hdPack = await CreatePackAsync(
                    paths,
                    "build-hd-false-revert-test",
                    new UpscaleOptions(
                        TexturePreset.ClassicHd,
                        AssetScope.SelectedZone,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "qeynos"),
                    "qeynos.s3d",
                    hdOutput,
                    () => { },
                    cancellationToken)
                .ConfigureAwait(false);

            var store = new ManifestStore();
            var health = new InstallHealthService(store);
            var catalog = new StagedPackCatalogService(store);
            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                installTransactionService: new InstallTransactionService(
                    store,
                    new AtomicFileOperations(),
                    ensureGameStopped: _ => { }),
                manifestStore: store,
                installHealthService: health,
                stagedPackCatalogService: catalog,
                stagedPackComposer: new StagedPackComposer(catalog, store));

            var paintedApply = await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    [paintedPack.ManifestPath],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Simulate an incomplete or external launcher repair whose bytes
            // happen to have the original archive's length. The fast audit may
            // use that unique length as a status hint, but it must never commit
            // a restore marker without the exact original SHA-256.
            var sameLengthForeignBytes = Enumerable
                .Repeat((byte)'X', originalPaintedTarget.Length)
                .ToArray();
            Assert(
                !sameLengthForeignBytes.AsSpan().SequenceEqual(originalPaintedTarget),
                "same-length tamper fixture must differ from original");
            await File.WriteAllBytesAsync(
                    paintedTargetPath,
                    sameLengthForeignBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            AssertEqual(
                InstallHealthState.RevertedToOriginal,
                (await health.AuditLatestFastAsync(paths, cancellationToken)
                    .ConfigureAwait(false)).State,
                "fast unique-length launcher-revert hint");
            AssertEqual(
                InstallHealthState.MixedOrModified,
                (await health.AuditLatestAsync(paths, cancellationToken)
                    .ConfigureAwait(false)).State,
                "exact launcher-revert verification");

            await AssertThrowsAsync<InvalidOperationException>(
                    () => workflow.ApplySelectedStagedPacksAsync(
                        paths,
                        [hdPack.ManifestPath],
                        cancellationToken: cancellationToken),
                    "same-length modified bytes must block transaction retirement")
                .ConfigureAwait(false);

            var retainedManifest = await store.ReadInstallManifestAsync(
                    paintedApply.InstallManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.Applied,
                retainedManifest.State,
                "failed exact retirement keeps the active restore transaction");
            Assert(
                !File.Exists(Path.Combine(
                    Path.GetDirectoryName(paintedApply.InstallManifestPath)!,
                    "restore-complete.json")),
                "failed exact retirement must not write a restore-complete marker");
            AssertSequenceEqual(
                sameLengthForeignBytes,
                await File.ReadAllBytesAsync(paintedTargetPath, cancellationToken)
                    .ConfigureAwait(false),
                "blocked retirement leaves externally modified archive untouched");
            AssertSequenceEqual(
                originalHdTarget,
                await File.ReadAllBytesAsync(hdTargetPath, cancellationToken)
                    .ConfigureAwait(false),
                "blocked retirement must not install the disjoint Texture HD archive");
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
