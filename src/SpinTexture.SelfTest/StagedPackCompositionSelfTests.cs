using System.Security.Cryptography;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedPackCompositionSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-pack-composition-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            paths.EnsureWorkspaceDirectories();
            var charactersOptions = new UpscaleOptions(
                TexturePreset.MaximumDetail,
                AssetScope.CharactersAndEquipmentOnly,
                2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false);
            var hateplaneOptions = new UpscaleOptions(
                TexturePreset.ClassicHd,
                AssetScope.SelectedZone,
                2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false,
                SelectedZone: "hateplane");
            var characterManifestPath = await CreatePackAsync(
                    paths,
                    "build-characters",
                    charactersOptions,
                    [
                        new TestArtifact(
                            "global_chr.s3d",
                            "original-character-archive"u8.ToArray(),
                            "enhanced-character-archive"u8.ToArray())
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            var hateplaneManifestPath = await CreatePackAsync(
                    paths,
                    "build-hateplane",
                    hateplaneOptions,
                    [
                        new TestArtifact(
                            "hateplane.s3d",
                            "original-hateplane-zone"u8.ToArray(),
                            "enhanced-hateplane-zone"u8.ToArray()),
                        new TestArtifact(
                            "hateplane_obj.s3d",
                            "original-hateplane-objects"u8.ToArray(),
                            "enhanced-hateplane-objects"u8.ToArray())
                    ],
                    cancellationToken)
                .ConfigureAwait(false);

            var catalog = new StagedPackCatalogService();
            var metadataCatalog = await catalog.DiscoverAsync(
                    paths,
                    StagedPackVerificationMode.Metadata,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(2, metadataCatalog.Count, "metadata catalog pack count");
            Assert(
                metadataCatalog.All(pack =>
                    pack.State == StagedPackValidationState.MetadataValid),
                "metadata catalog should validate both v1 manifests");
            var exactCatalog = await catalog.DiscoverAsync(
                    paths,
                    StagedPackVerificationMode.Exact,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Assert(
                exactCatalog.All(pack => pack.IsReady),
                "exact catalog should hash-verify both packs");
            Assert(
                exactCatalog.All(pack =>
                    pack.Manifest?.SchemaVersion == BuildManifest.CurrentSchemaVersion),
                "catalog should support existing schema-v1 build manifests");

            var constituentFingerprints = await FingerprintConstituentsAsync(
                    paths,
                    [characterManifestPath, hateplaneManifestPath],
                    cancellationToken)
                .ConfigureAwait(false);
            var copyMaterializer = new HardLinkOrCopyStagedPackPayloadMaterializer(
                (_, _) => false);
            var copyComposer = new StagedPackComposer(
                materializer: copyMaterializer);
            var composition = await copyComposer.ComposeAsync(
                    paths,
                    [characterManifestPath, hateplaneManifestPath],
                    cancellationToken: cancellationToken,
                    compositionId: "composite-copy-test")
                .ConfigureAwait(false);
            AssertEqual(3, composition.Manifest.Entries.Count, "disjoint union artifact count");
            AssertEqual(0, composition.HardLinkedArtifacts, "forced-copy hard-link count");
            AssertEqual(3, composition.CopiedArtifacts, "forced-copy artifact count");
            AssertEqual(
                BuildManifest.CurrentSchemaVersion,
                composition.Manifest.SchemaVersion,
                "install-compatible composite manifest schema");
            AssertEqual(2, composition.Composition.Components.Count, "composition provenance component count");
            Assert(
                composition.Composition.Components.Any(component =>
                    component.Options.Preset == TexturePreset.MaximumDetail),
                "composition should retain character-pack options");
            Assert(
                composition.Composition.Components.Any(component =>
                    component.Options.Preset == TexturePreset.ClassicHd),
                "composition should retain Hateplane-pack options");
            Assert(
                File.Exists(composition.CompositionDocumentPath),
                "composition provenance document should be committed");
            var composedInspection = await catalog.InspectAsync(
                    paths,
                    composition.ManifestPath,
                    StagedPackVerificationMode.Exact,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(composedInspection.IsReady, "forced-copy composite should pass exact catalog validation");
            await AssertConstituentsUnchangedAsync(
                    constituentFingerprints,
                    cancellationToken)
                .ConfigureAwait(false);

            // Exercise the production hard-link route. Filesystem policy may
            // require the verified-copy fallback, so assert the complete result
            // rather than assuming hard links are always available.
            var defaultComposition = await new StagedPackComposer().ComposeAsync(
                    paths,
                    [characterManifestPath, hateplaneManifestPath],
                    cancellationToken: cancellationToken,
                    compositionId: "composite-default-materializer-test")
                .ConfigureAwait(false);
            AssertEqual(
                3,
                defaultComposition.HardLinkedArtifacts + defaultComposition.CopiedArtifacts,
                "default materializer artifact count");
            var defaultInspection = await catalog.InspectAsync(
                    paths,
                    defaultComposition.ManifestPath,
                    StagedPackVerificationMode.Exact,
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(defaultInspection.IsReady, "default-materialized composite integrity");
            await AssertConstituentsUnchangedAsync(
                    constituentFingerprints,
                    cancellationToken)
                .ConfigureAwait(false);

            await TestOverlapRulesAsync(paths, copyComposer, cancellationToken)
                .ConfigureAwait(false);
            await TestInvalidPayloadCatalogStatesAsync(paths, catalog, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestOverlapRulesAsync(
        ProjectPaths paths,
        StagedPackComposer composer,
        CancellationToken cancellationToken)
    {
        var options = UpscaleOptions.Recommended;
        var original = "identical-shared-source"u8.ToArray();
        var enhanced = "identical-shared-output"u8.ToArray();
        var first = await CreatePackAsync(
                paths,
                "build-identical-a",
                options,
                [new TestArtifact("shared.s3d", original, enhanced)],
                cancellationToken)
            .ConfigureAwait(false);
        var second = await CreatePackAsync(
                paths,
                "build-identical-b",
                options,
                [new TestArtifact("shared.s3d", original, enhanced)],
                cancellationToken)
            .ConfigureAwait(false);
        var identicalPlan = await composer.PlanAsync(
                paths,
                [first, second, first],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Assert(identicalPlan.CanCompose, "identical overlap should be composable");
        AssertEqual(2, identicalPlan.Components.Count, "duplicate manifest input should be ignored");
        AssertEqual(1, identicalPlan.Entries.Count, "identical overlap should de-duplicate");
        AssertEqual(2, identicalPlan.Entries[0].Origins.Count, "identical overlap provenance");

        var differentOutput = await CreatePackAsync(
                paths,
                "build-output-conflict",
                options,
                [
                    new TestArtifact(
                        "shared.s3d",
                        original,
                        "different-enhanced-output"u8.ToArray())
                ],
                cancellationToken)
            .ConfigureAwait(false);
        var outputConflictPlan = await composer.PlanAsync(
                paths,
                [first, differentOutput],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(1, outputConflictPlan.Conflicts.Count, "output conflict count");
        AssertEqual(
            StagedPackCompositionConflictKind.OutputMismatch,
            outputConflictPlan.Conflicts[0].Kind,
            "same-source different-output conflict kind");
        await AssertCompositionBlockedAsync(
                composer,
                paths,
                [first, differentOutput],
                StagedPackCompositionConflictKind.OutputMismatch,
                "composite-output-conflict-test",
                cancellationToken)
            .ConfigureAwait(false);

        var differentSource = await CreatePackAsync(
                paths,
                "build-source-conflict",
                options,
                [
                    new TestArtifact(
                        "shared.s3d",
                        "different-shared-source"u8.ToArray(),
                        enhanced)
                ],
                cancellationToken)
            .ConfigureAwait(false);
        var sourceConflictPlan = await composer.PlanAsync(
                paths,
                [first, differentSource],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(1, sourceConflictPlan.Conflicts.Count, "source conflict count");
        AssertEqual(
            StagedPackCompositionConflictKind.SourceMismatch,
            sourceConflictPlan.Conflicts[0].Kind,
            "different-source conflict kind");
        await AssertCompositionBlockedAsync(
                composer,
                paths,
                [first, differentSource],
                StagedPackCompositionConflictKind.SourceMismatch,
                "composite-source-conflict-test",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AssertCompositionBlockedAsync(
        StagedPackComposer composer,
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        StagedPackCompositionConflictKind expectedKind,
        string compositionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await composer.ComposeAsync(
                    paths,
                    manifestPaths,
                    cancellationToken: cancellationToken,
                    compositionId: compositionId)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "Self-test failed: conflicting staged packs were composed.");
        }
        catch (StagedPackCompositionException exception)
        {
            Assert(
                exception.Conflicts.Any(conflict => conflict.Kind == expectedKind),
                $"composition should report {expectedKind}");
        }

        Assert(
            !Directory.Exists(Path.Combine(paths.StagingPath, compositionId)),
            "blocked composition should not create an output directory");
    }

    private static async Task TestInvalidPayloadCatalogStatesAsync(
        ProjectPaths paths,
        StagedPackCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var missing = await CreatePackAsync(
                paths,
                "build-missing-payload",
                UpscaleOptions.Recommended,
                [
                    new TestArtifact(
                        "missing.s3d",
                        "missing-source"u8.ToArray(),
                        "missing-output"u8.ToArray())
                ],
                cancellationToken)
            .ConfigureAwait(false);
        var missingPayload = Path.Combine(
            Path.GetDirectoryName(missing)!,
            "payload",
            "missing.s3d");
        File.Delete(missingPayload);
        var missingInfo = await catalog.InspectAsync(
                paths,
                missing,
                StagedPackVerificationMode.Exact,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            StagedPackValidationState.PayloadMissing,
            missingInfo.State,
            "missing payload catalog state");

        var corrupt = await CreatePackAsync(
                paths,
                "build-corrupt-payload",
                UpscaleOptions.Recommended,
                [
                    new TestArtifact(
                        "corrupt.s3d",
                        "corrupt-source"u8.ToArray(),
                        "AAAA"u8.ToArray())
                ],
                cancellationToken)
            .ConfigureAwait(false);
        var corruptPayload = Path.Combine(
            Path.GetDirectoryName(corrupt)!,
            "payload",
            "corrupt.s3d");
        await File.WriteAllBytesAsync(
                corruptPayload,
                "BBBB"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var corruptInfo = await catalog.InspectAsync(
                paths,
                corrupt,
                StagedPackVerificationMode.Exact,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            StagedPackValidationState.PayloadInvalid,
            corruptInfo.State,
            "same-length corrupt payload catalog state");
        AssertEqual(
            StagedPackArtifactValidationState.HashMismatch,
            corruptInfo.Artifacts.Single().State,
            "same-length corrupt payload artifact state");
    }

    private static async Task<string> CreatePackAsync(
        ProjectPaths paths,
        string buildId,
        UpscaleOptions options,
        IReadOnlyList<TestArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        var buildDirectory = Path.Combine(paths.StagingPath, buildId);
        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        Directory.CreateDirectory(payloadDirectory);
        var entries = new List<BuildManifestEntry>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            var payloadPath = Path.Combine(payloadDirectory, artifact.RelativeInstallPath);
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, artifact.Staged, cancellationToken)
                .ConfigureAwait(false);
            entries.Add(new BuildManifestEntry(
                artifact.RelativeInstallPath,
                artifact.Source.LongLength,
                Hash(artifact.Source),
                artifact.Staged.LongLength,
                Hash(artifact.Staged)));
        }

        var manifest = new BuildManifest(
            BuildManifest.CurrentSchemaVersion,
            buildId,
            DateTimeOffset.UtcNow,
            paths.InstallPath,
            options,
            entries.AsReadOnly());
        var manifestPath = Path.Combine(buildDirectory, "manifest.json");
        await new ManifestStore().WriteBuildManifestAsync(
                manifestPath,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
        return manifestPath;
    }

    private static async Task<IReadOnlyDictionary<string, FileFingerprint>>
        FingerprintConstituentsAsync(
            ProjectPaths paths,
            IReadOnlyList<string> manifestPaths,
            CancellationToken cancellationToken)
    {
        var fingerprints = new Dictionary<string, FileFingerprint>(
            StringComparer.OrdinalIgnoreCase);
        var store = new ManifestStore();
        foreach (var manifestPath in manifestPaths)
        {
            fingerprints.Add(
                manifestPath,
                await FileIntegrity.FingerprintAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false));
            var manifest = await store.ReadBuildManifestAsync(manifestPath, cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in manifest.Entries)
            {
                var payload = PathGuard.ResolveUnderRoot(
                    Path.Combine(Path.GetDirectoryName(manifestPath)!, "payload"),
                    entry.RelativeInstallPath);
                fingerprints.Add(
                    payload,
                    await FileIntegrity.FingerprintAsync(payload, cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        return fingerprints;
    }

    private static async Task AssertConstituentsUnchangedAsync(
        IReadOnlyDictionary<string, FileFingerprint> expected,
        CancellationToken cancellationToken)
    {
        foreach (var item in expected)
        {
            var actual = await FileIntegrity.FingerprintAsync(item.Key, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(item.Value, actual, $"immutable constituent {item.Key}");
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

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

    private sealed record TestArtifact(
        string RelativeInstallPath,
        byte[] Source,
        byte[] Staged);
}
