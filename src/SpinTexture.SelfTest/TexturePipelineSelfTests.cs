using System.Buffers.Binary;
using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

public static class TexturePipelineSelfTests
{
    public static async Task RunAsync(
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        output ??= TextWriter.Null;
        await CutoutMipPolicySelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Cutout compatibility command and integration tests passed.")
            .ConfigureAwait(false);
        await LegacyTranslucentMaterialSafetySelfTests.RunAsync(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync("Legacy translucent-material safety tests passed.")
            .ConfigureAwait(false);
        await WorldCutoutRepairSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Immutable World/zone cutout revision-repair tests passed.")
            .ConfigureAwait(false);
        TestHeaderSniffingAndClassification();
        TestArtDirectionAndEffectPolicies();
        TestPaintedThemeAccentPreservation();
        await TextureOptionPreviewSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Genuine live option-preview safety and cache tests passed.")
            .ConfigureAwait(false);
        CelestialTextureSafetyPolicySelfTests.Run();
        await LooseTextureSafetyRepairSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await CelestialPackRepairSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await StagedBuildOmissionSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await StagedBuildCheckpointSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        TestTextureProcessingPipelineRevision();
        TestPaintedProfileReportCompatibility();
        await output.WriteLineAsync("Texture metadata tests passed.").ConfigureAwait(false);
        await ControlTextureAndIndexedBmpSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Control-texture and indexed-BMP regression tests passed.")
            .ConfigureAwait(false);
        await RepairReuseSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Missing-only repair reuse tests passed.")
            .ConfigureAwait(false);
        await PaintedRepairSafetySelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Painted repair identity and fail-closed tests passed.")
            .ConfigureAwait(false);
        await StagedPackSourceRepairSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Managed-original build-source and World source-repair tests passed.")
            .ConfigureAwait(false);
        await TestArchiveScopeSelectionAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Archive scope selection tests passed.").ConfigureAwait(false);
        TestPreviewSampling();
        await output.WriteLineAsync("Representative preview sampling tests passed.").ConfigureAwait(false);
        TestCapMathAndCommands();
        await output.WriteLineAsync("Tool command tests passed.").ConfigureAwait(false);
        TestBoundedNativeOutputRetention();
        await TestNativeProcessInactivityGuardAsync(cancellationToken).ConfigureAwait(false);
        TestRestoreSafetyCleanupGuard();
        await output.WriteLineAsync("Bounded diagnostics and restore cleanup-guard tests passed.")
            .ConfigureAwait(false);
        await InstallHealthSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Installed-pack health audit tests passed.").ConfigureAwait(false);
        await StagedPackReinstallSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Staged-pack launcher-repair reinstall tests passed.")
            .ConfigureAwait(false);
        await StagedPackCompositionSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Staged-pack catalog and composition tests passed.")
            .ConfigureAwait(false);
        await StagedPackDeletionSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Guarded staged-pack deletion tests passed.")
            .ConfigureAwait(false);
        await StagedPackStorageSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Verified staged-pack storage migration tests passed.")
            .ConfigureAwait(false);
        await SelectedPackSwitchSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Selected staged-pack switch tests passed.")
            .ConfigureAwait(false);
        await IncrementalPackPromotionSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Incremental staged-pack promotion tests passed.")
            .ConfigureAwait(false);
        await NativeGraphicsSettingsSelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Native graphics settings transaction tests passed.")
            .ConfigureAwait(false);
        await StagedBuildEstimateHistorySelfTests.RunAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Historical staged-build estimator tests passed.")
            .ConfigureAwait(false);
        EnhancedGameLaunchSelfTests.Run();
        await output.WriteLineAsync("Credential-free enhanced game launch tests passed.")
            .ConfigureAwait(false);
        TestTextureModelSelection();
        TestPresetAwareFidelityGate();
        TestPaintedFinalValidationPolicyAndCappedMetrics();
        await output.WriteLineAsync("Texture model discovery and preset fidelity-gate tests passed.").ConfigureAwait(false);
        await TestWrappedTgaAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Seam-safe TGA tests passed.").ConfigureAwait(false);
        await TestPaintedStylizerAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Painterly stylizer regression tests passed.").ConfigureAwait(false);
        if (string.Equals(
                Environment.GetEnvironmentVariable("SPINTEXTURE_SKIP_GPU_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                    "Bundled Vulkan worker smoke test skipped by SPINTEXTURE_SKIP_GPU_SMOKE=1; command and batch tests still passed.")
                .ConfigureAwait(false);
        }
        else
        {
            await TestNativeProcessorSmokeAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync("Bundled texconv/Real-ESRGAN smoke test passed.")
                .ConfigureAwait(false);
        }
        var appliedAndRestored = await TestBuildApplyRestoreAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            appliedAndRestored
                ? "Build/apply/restore transaction tests passed."
                : "Build/no-live-write and running-game safety-gate tests passed; apply/restore was correctly blocked.")
            .ConfigureAwait(false);
        var archivePath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(File.Exists);
        if (archivePath is not null)
        {
            await TestRealArchiveTextureAsync(archivePath, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Read-only real-texture smoke test passed for {archivePath}.").ConfigureAwait(false);
        }

        await output.WriteLineAsync("Texture/tooling/pipeline self-tests passed.").ConfigureAwait(false);
    }

    private static void TestTextureProcessingPipelineRevision()
    {
        static TextureBuildReport Report(int revision, bool isRepair = false) =>
            new(
                TextureBuildReport.CurrentSchemaVersion,
                "revision-test",
                DateTimeOffset.UnixEpoch,
                @"C:\EQ",
                @"C:\staging\revision-test",
                1,
                new TextureBuildStatistics(
                    1,
                    1,
                    0,
                    1,
                    1,
                    new Dictionary<string, int>(),
                    []))
            {
                IsIncrementalRepair = isRepair,
                TexturePipelineRevision = revision
            };

        Assert(
            TextureProcessingPipeline.RequiresRepair(
                report: null,
                AssetScope.CharactersAndEquipmentOnly),
            "a character pack with no report should remain eligible for legacy repair");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 0),
                AssetScope.WorldCharactersAndEquipment),
            "a pre-revision character report should be eligible for legacy repair");
        Assert(
            !TextureProcessingPipeline.RequiresRepair(
                Report(TextureProcessingPipeline.CurrentRevision),
                AssetScope.CharactersAndEquipmentOnly),
            "a current fresh character build should not imply that repair is required");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 0, isRepair: true),
                AssetScope.CharactersAndEquipmentOnly),
            "a revision-0 character pack should advance through the current combined coverage/mip repair");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 1, isRepair: true),
                AssetScope.CharactersAndEquipmentOnly),
            "a revision-1 character pack should receive the cutout compatibility upgrade");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 2),
                AssetScope.CharactersAndEquipmentOnly),
            "a revision-2 character pack should still receive the cutout compatibility upgrade");
        Assert(
            !TextureProcessingPipeline.RequiresRepair(
                Report(revision: 3),
                AssetScope.CharactersAndEquipmentOnly),
            "a revision-3 character-only pack has no environmental celestial work and must not receive a pointless revision-4 repair");
        Assert(
            TextureProcessingPipeline.RequiresCutoutMipUpgrade(
                Report(revision: 0, isRepair: true),
                AssetScope.CharactersAndEquipmentOnly),
            "an already repaired revision-0 character pack should use the targeted cutout path");
        Assert(
            !TextureProcessingPipeline.RequiresCutoutMipUpgrade(
                Report(revision: 0),
                AssetScope.CharactersAndEquipmentOnly),
            "an unrepaired revision-0 character pack should retain the coverage-repair path");
        var sourceRepairedCombined = Report(revision: 0, isRepair: true) with
        {
            IsSourceMismatchRepair = true
        };
        Assert(
            !TextureProcessingPipeline.RequiresCutoutMipUpgrade(
                sourceRepairedCombined,
                AssetScope.WorldCharactersAndEquipment),
            "a source-mismatch repair does not prove revision-0 character coverage");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 1),
                AssetScope.WorldOnly),
            "a World-only pack from before the cutout mip revision should be upgradeable");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 1, isRepair: true),
                AssetScope.WorldCharactersAndEquipment),
            "a repaired combined pack still needs the newer World cutout revision");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 1),
                AssetScope.SelectedZone),
            "a selected-zone pack from before the cutout mip revision should be upgradeable");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 3),
                AssetScope.SelectedZone,
                ["forest.s3d", "forest_obj.s3d"]),
            "an older selected-zone pack should receive the translucent-material safety scan even without sky.s3d");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 3),
                AssetScope.SelectedZone,
                ["forest.s3d", "sky.s3d"]),
            "a revision-3 selected-zone pack containing exact top-level sky.s3d should receive celestial repair");
        Assert(
            !TextureProcessingPipeline.RequiresRepair(
                Report(TextureProcessingPipeline.CurrentRevision),
                AssetScope.WorldOnly),
            "a current World-only pack should not offer another cutout upgrade");
        Assert(
            !TextureProcessingPipeline.RequiresRepair(
                Report(revision: 3),
                AssetScope.AllSafeTextures,
                ["ordinary.s3d", Path.Combine("SpellEffects", "ordinary.dds")]),
            "an all-safe pack without native sky resources remains outside broad mixed-artifact repair");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 3),
                AssetScope.AllSafeTextures,
                [Path.Combine("Resources", "sky", "stars.dds"), "ordinary.s3d"]),
            "an older all-safe pack containing the native star atlas receives exact safety repair");
        Assert(
            TextureProcessingPipeline.RequiresRepair(
                Report(revision: 3),
                AssetScope.AllSafeTextures,
                ["sky.s3d", "ordinary.s3d"]),
            "an older all-safe pack containing legacy sky.s3d receives exact celestial repair");
    }

    private static void TestHeaderSniffingAndClassification()
    {
        var dds = new byte[128];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80), 4);
        "DXT1"u8.CopyTo(dds.AsSpan(84));

        var sniffer = new TextureHeaderSniffer();
        Assert(sniffer.TryRead(dds, out var metadata), "DDS header should be recognized");
        Assert(metadata is not null, "DDS metadata should be populated");
        AssertEqual(256, metadata!.Width, "DDS width");
        AssertEqual("BC1_UNORM", metadata.TexconvFormat!, "DXT1 texconv format");

        var premultipliedDds = (byte[])dds.Clone();
        "DXT2"u8.CopyTo(premultipliedDds.AsSpan(84));
        Assert(sniffer.TryRead(premultipliedDds, out var premultiplied), "DXT2 header should be recognized");
        Assert(premultiplied is not null, "DXT2 metadata should be populated");
        Assert(premultiplied!.TexconvFormat is null, "premultiplied DXT2 must remain byte-preserved");

        var classification = new TextureSemanticClassifier().Classify("lavastorm_rock.dds", metadata);
        AssertEqual(TextureKind.Color, classification.Kind, "world color classification");
        Assert(classification.RequiresSeparateAlphaHandling, "DXT1 possible alpha should be preserved");

        var normal = new TextureSemanticClassifier().Classify("lava_wall_normal.dds", metadata);
        AssertEqual(TextureKind.Normal, normal.Kind, "normal map classification");
        Assert(!normal.CanUseColorUpscaler, "normal maps must bypass the color model");
        AssertEqual(
            TextureKind.Normal,
            new TextureSemanticClassifier().Classify("stone_normal02.dds", metadata).Kind,
            "numbered normal maps should remain protected");
        AssertEqual(
            TextureKind.Normal,
            new TextureSemanticClassifier().Classify("wallet17_01nrml.dds", metadata).Kind,
            "legacy nrml-suffix equipment maps should remain protected");
        AssertEqual(
            TextureKind.Normal,
            new TextureSemanticClassifier().Classify("firedragon_weap_ng.dds", metadata).Kind,
            "compact ng-suffix equipment maps should remain protected");
        AssertEqual(
            TextureKind.Mask,
            new TextureSemanticClassifier().Classify("magic_mask01.dds", metadata).Kind,
            "numbered scalar masks should remain protected");
        AssertEqual(
            TextureKind.Mask,
            new TextureSemanticClassifier().Classify("coverage_neutral.dds", metadata).Kind,
            "equipment coverage maps should remain protected");
        AssertEqual(
            TextureKind.Mask,
            new TextureSemanticClassifier().Classify("wallet_17_cvg.dds", metadata).Kind,
            "compact equipment coverage maps should remain protected");
        AssertEqual(
            TextureKind.Normal,
            new TextureSemanticClassifier().Classify(
                "sp_fearshard_axe_12ngs.dds",
                metadata).Kind,
            "packed normal/gloss/spec maps should remain protected");
        AssertEqual(
            TextureKind.Normal,
            new TextureSemanticClassifier().Classify("paintingsq_10ns.dds", metadata).Kind,
            "compact normal/spec maps should remain protected");
        AssertEqual(
            TextureKind.Unsupported,
            new TextureSemanticClassifier().Classify("sk_env.dds", metadata).Kind,
            "environment lookup maps should remain byte-preserved");
        AssertEqual(
            TextureKind.Color,
            new TextureSemanticClassifier().Classify("longsword.dds", metadata).Kind,
            "compact normal tokens must not match substrings inside ordinary color names");
        AssertEqual(
            TextureKind.UserInterface,
            new TextureSemanticClassifier().Classify(
                "Interface\\ornament01.dds",
                metadata).Kind,
            "loose texture directory context should protect UI assets");
        AssertEqual(
            TextureKind.Animated,
            new TextureSemanticClassifier().Classify(
                "EnvEmitterEffects\\magic_glow01.dds",
                metadata).Kind,
            "loose effect directory context should protect emitter assets");
        AssertEqual(
            TextureKind.UserInterface,
            new TextureSemanticClassifier().Classify("Spells01.tga", metadata).Kind,
            "numbered spell atlases should remain protected");
        AssertEqual(
            TextureKind.Animated,
            new TextureSemanticClassifier().Classify("spely5.tga", metadata).Kind,
            "compact legacy spell effects should remain protected");
        AssertEqual(
            TextureKind.Animated,
            new TextureSemanticClassifier().Classify("smokead.dds", metadata).Kind,
            "compact legacy smoke effects should remain protected");

        var palette = new TextureSemanticClassifier().Classify("lavastorm_palette.dds", metadata);
        AssertEqual(TextureKind.Unsupported, palette.Kind, "packed palette classification");
        Assert(!palette.CanUseColorUpscaler, "packed palettes must bypass the color model");

        Assert(
            PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "lavastorm.s3d",
                "lavastorm_rock.dds",
                classification),
            "opaque terrain materials should use wrap-aware reconstruction");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "lavastorm_obj.s3d",
                "lavastorm_obj_orcflag.dds",
                classification),
            "object archive textures should clamp their borders");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "lavastorm.s3d",
                "lavastorm_nekdoor.dds",
                classification),
            "non-tileable door textures should clamp their borders");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "dke.eqg",
                "dke_skin_c.dds",
                classification,
                isCharacterOrEquipmentArchive: true),
            "direct EQG race-model textures should clamp their borders");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "equipment-01.eqg",
                "weapon_blade_c.dds",
                classification),
            "dedicated equipment textures should clamp their borders");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "lon16.eqg",
                "paintingsq_10ns.dds",
                classification),
            "mixed legacy item packs should remain clamp-only in broader scopes");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "wallet24.eqg",
                "housing_prop_c.dds",
                classification),
            "mixed wallet packs should remain clamp-only in broader scopes");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseWrapSampling(
                "boat_chr.s3d",
                "boatside.dds",
                classification),
            "world-only legacy actor packs should remain clamp-only in broader scopes");

        Assert(
            PfsTextureArchiveBuilder.IsLikelyAnimatedEffectName("lavastorm_lava004.dds"),
            "numbered lava frames should use the temporal-safe model");
        Assert(
            PfsTextureArchiveBuilder.IsLikelyAnimatedEffectName("lavastorm_obj_smke2.dds"),
            "numbered smoke frames should use the temporal-safe model");
        Assert(
            PfsTextureArchiveBuilder.ShouldUseConservativeColorModel("lavastorm_obj_orcflag.dds"),
            "painted flags should use the conservative color model");
        Assert(
            !PfsTextureArchiveBuilder.ShouldUseConservativeColorModel("lavastorm_nerrock.dds"),
            "terrain rock should retain the texture-specialized model");
        Assert(
            !PfsTextureArchiveBuilder.IsLikelyAnimatedEffectName("lavastorm_1floor5.dds"),
            "Lavastorm zone prefixes must not classify numbered floor textures as lava frames");
        Assert(
            !PfsTextureArchiveBuilder.IsLikelyAnimatedEffectName("lavastorm_nwgras1.dds"),
            "Lavastorm zone prefixes must not classify numbered grass textures as lava frames");
    }

    private static async Task TestArchiveScopeSelectionAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-scopes-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "Resources");
        var layers = Path.Combine(resources, "Layers");
        Directory.CreateDirectory(layers);

        try
        {
            var archiveNames = new[]
            {
                "testzone.s3d",
                "testzone_obj.s3d",
                "sro.s3d",
                "sro_obj.s3d",
                "skyfire.s3d",
                "skyfire_obj.s3d",
                "skyfire_chr.s3d",
                "terrain_common.s3d",
                "orc_chr.s3d",
                "globalhuf_chr.s3d",
                "global_chr.s3d",
                "oot_chr.s3d",
                "pre1_chr.s3d",
                "cof_chr.s3d",
                "com_chr.s3d",
                "cub_chr.s3d",
                "global2_chr.s3d",
                "global4_chr.s3d",
                "ivm_chr.s3d",
                "kgo_chr.s3d",
                "liz_chr.s3d",
                "mos_chr.s3d",
                "rif_chr.s3d",
                "gsp_chr.s3d",
                "dke.eqg",
                "dkf.eqg",
                "cdm.eqg",
                "cst.eqg",
                "B09.eqg",
                "sky.eqg",
                "sky.s3d",
                "gequip.s3d",
                "gequip2.s3d",
                "loyequip.s3d",
                "donequip.eqg",
                "holeequip.eqg",
                "obequip_lexit.s3d",
                "dest_sphere_shield.eqg",
                "boat_chr.s3d",
                "box_chr.s3d",
                "och_chr.s3d",
                "ship1_chr.s3d",
                "ship2_chr.s3d",
                "ship3_chr.s3d",
                "tpn_chr.s3d",
                "equipment-01.eqg",
                "kunark_weapons.eqg",
                "primitive_weapons.eqg",
                "psrobebloodthirsty.eqg",
                "lon02.eqg",
                "lon07.eqg",
                "lon11.eqg",
                "lon12.eqg",
                "lon15.eqg",
                "lon16.eqg",
                "it13900.eqg",
                "it100000.eqg",
                "it77777.eqg",
                "wallet01.eqg",
                "wallet07.eqg",
                "wallet10.eqg",
                "wallet11.eqg",
                "wallet12.eqg",
                "wallet13.eqg",
                "wallet14.eqg",
                "wallet15.eqg",
                "wallet16.eqg",
                "wallet18.eqg",
                "wallet19.eqg",
                "wallet20.eqg",
                "wallet22.eqg",
                "wallet25.eqg",
                "wallet32.eqg",
                "wallet34.eqg",
                "wallet37.eqg",
                "wallet41.eqg",
                "wallet23.eqg",
                "wallet26.eqg",
                "wallet27.eqg",
                "wallet28.eqg",
                "wallet29.eqg",
                "wallet30.eqg",
                "wallet33.eqg",
                "bloodiron_green.eqg",
                "bloodiron_red.eqg",
                "blackbook.eqg",
                "wearablealpha.eqg",
                "mixedrole.eqg",
                "it101084.s3d",
                "it23900.eqg",
                "it40001.eqg",
                "wallet24.eqg",
                "global_obj.s3d",
                "globalgdb.eqg",
                "i23.eqg",
                "pel.eqg",
                "unrelated.eqg"
            };
            foreach (var archiveName in archiveNames)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(root, archiveName),
                    [],
                    cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(root, "testzone.emt"),
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(root, "sro.emt"),
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(root, "skyfire.emt"),
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(root, "testzone_chr.txt"),
                "2"
                    + Environment.NewLine
                    + "dke,dke"
                    + Environment.NewLine
                    + "mixedrole,mixedrole"
                    + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(resources, "GlobalLoad_chr.txt"),
                "1" + Environment.NewLine + "orc,orc_chr" + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(resources, "GlobalLoad.txt"),
                string.Join(
                    Environment.NewLine,
                    "2,0,TFFFE,blackbook.eqg,Loading Character Equipment Files",
                    "2,0,TFFFE,mixedrole.eqg,Loading Character Equipment and Furniture Files",
                    "2,0,TFFFE,orc_chr.s3d,Loading Furniture Files",
                    "2,0,TFFFE,it101084,Loading Furniture Files",
                    "1,1,TFFFE,global_obj,Loading Character Equipment Files"),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(resources, "NewArmorAdjData.txt"),
                "DKF,1,1.0" + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(resources, "actortaginfo.txt"),
                string.Join(
                    Environment.NewLine,
                    "13900^1^0^",
                    "100000^0^0^",
                    "77777^3^0^",
                    "101084^-1^0^",
                    "23900^-1^0^",
                    "40001^-1^0^"),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(layers, "WEARABLEALPHA_IT.txt"),
                "IT1^IT2^WEARABLEALPHA" + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);

            var raceRows = new[]
            {
                CreateRaceDataRow("DKF"),
                CreateRaceDataRow("CDM"),
                CreateRaceDataRow("ORC"),
                CreateRaceDataRow("SRO"),
                CreateRaceDataRow("SKY"),
                CreateRaceDataRow("CST"),
                CreateRaceDataRow("B09")
            };
            await File.WriteAllLinesAsync(
                Path.Combine(root, "racedata.txt"),
                raceRows,
                cancellationToken).ConfigureAwait(false);

            var characterOptions = UpscaleOptions.Recommended with
            {
                Scope = AssetScope.CharactersAndEquipmentOnly
            };
            var worldOptions = UpscaleOptions.Recommended with
            {
                Scope = AssetScope.WorldOnly
            };
            var combinedOptions = UpscaleOptions.Recommended with
            {
                Scope = AssetScope.WorldCharactersAndEquipment
            };
            var allOptions = UpscaleOptions.Recommended with
            {
                Scope = AssetScope.AllSafeTextures
            };

            var characterPaths = TexturePackWorkflow.SelectArchives(root, characterOptions);
            var worldPaths = TexturePackWorkflow.SelectArchives(root, worldOptions);
            var combinedPaths = TexturePackWorkflow.SelectArchives(root, combinedOptions);
            var allPaths = TexturePackWorkflow.SelectArchives(root, allOptions);
            var currentWorldPaths = TexturePackWorkflow.SelectArchives(
                root,
                worldOptions with { WorldExpansions = WorldExpansion.CurrentEql });
            var explicitAllWorldPaths = TexturePackWorkflow.SelectArchives(
                root,
                worldOptions with
                {
                    WorldExpansions = WorldExpansion.CurrentEql
                        | WorldExpansion.Kunark
                        | WorldExpansion.OtherInstalled
                });
            var kunarkWorldPaths = TexturePackWorkflow.SelectArchives(
                root,
                worldOptions with { WorldExpansions = WorldExpansion.Kunark });
            var currentCombinedPaths = TexturePackWorkflow.SelectArchives(
                root,
                combinedOptions with { WorldExpansions = WorldExpansion.CurrentEql });
            var explicitCharacterPaths = TexturePackWorkflow
                .ResolveCharacterAndEquipmentArchives(
                    root,
                    combinedOptions with
                    {
                        WorldExpansions = WorldExpansion.CurrentEql
                    });
            var currentCombinedNames = currentCombinedPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingExpansionRejected = false;
            var partiallyMissingExpansionRejected = false;
            try
            {
                _ = TexturePackWorkflow.SelectArchives(
                    root,
                    worldOptions with { WorldExpansions = WorldExpansion.Velious });
            }
            catch (InvalidOperationException)
            {
                missingExpansionRejected = true;
            }
            try
            {
                _ = TexturePackWorkflow.SelectArchives(
                    root,
                    worldOptions with
                    {
                        WorldExpansions = WorldExpansion.CurrentEql | WorldExpansion.Velious
                    });
            }
            catch (InvalidOperationException)
            {
                partiallyMissingExpansionRejected = true;
            }
            var characterNames = characterPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var worldNames = worldPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentWorldNames = currentWorldPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var explicitAllWorldNames = explicitAllWorldPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kunarkWorldNames = kunarkWorldPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var expectedCharacterNames = new HashSet<string>(
                [
                    "globalhuf_chr.s3d",
                    "global_chr.s3d",
                    "oot_chr.s3d",
                    "pre1_chr.s3d",
                    "cof_chr.s3d",
                    "com_chr.s3d",
                    "cub_chr.s3d",
                    "global2_chr.s3d",
                    "global4_chr.s3d",
                    "ivm_chr.s3d",
                    "kgo_chr.s3d",
                    "liz_chr.s3d",
                    "mos_chr.s3d",
                    "rif_chr.s3d",
                    "dke.eqg",
                    "dkf.eqg",
                    "cdm.eqg",
                    "sky.eqg",
                    "gequip.s3d",
                    "gequip2.s3d",
                    "loyequip.s3d",
                    "donequip.eqg",
                    "equipment-01.eqg",
                    "kunark_weapons.eqg",
                    "primitive_weapons.eqg",
                    "psrobebloodthirsty.eqg",
                    "bloodiron_green.eqg",
                    "bloodiron_red.eqg",
                    "lon02.eqg",
                    "lon07.eqg",
                    "lon11.eqg",
                    "lon12.eqg",
                    "lon15.eqg",
                    "lon16.eqg",
                    "it13900.eqg",
                    "it100000.eqg",
                    "it77777.eqg",
                    "wallet01.eqg",
                    "wallet10.eqg",
                    "wallet11.eqg",
                    "wallet12.eqg",
                    "wallet13.eqg",
                    "wallet14.eqg",
                    "wallet15.eqg",
                    "wallet16.eqg",
                    "wallet18.eqg",
                    "wallet19.eqg",
                    "wallet20.eqg",
                    "wallet22.eqg",
                    "wallet24.eqg",
                    "wallet25.eqg",
                    "wallet32.eqg",
                    "wallet34.eqg",
                    "wallet37.eqg",
                    "wallet41.eqg",
                    "wallet23.eqg",
                    "wallet26.eqg",
                    "wallet27.eqg",
                    "wallet28.eqg",
                    "wallet29.eqg",
                    "wallet30.eqg",
                    "wallet33.eqg",
                    "blackbook.eqg",
                    "wearablealpha.eqg"
                ],
                StringComparer.OrdinalIgnoreCase);
            Assert(
                characterNames.SetEquals(expectedCharacterNames),
                "character/equipment scope should resolve only cataloged or dedicated packs");
            Assert(
                !characterNames.Contains("sro.s3d"),
                "a racedata code must not override a discovered world-zone archive");
            Assert(
                worldNames.Contains("sky.s3d") && !worldNames.Contains("sky.eqg"),
                "the shared sky texture pack must not hide the compact SKY race model");
            Assert(
                worldNames.Contains("skyfire_chr.s3d")
                && !explicitAllWorldNames.Contains("skyfire_chr.s3d"),
                "legacy null World selection must preserve the historical sky-prefix partition while explicit expansion groups use the safe boundary");
            Assert(
                currentWorldNames.Contains("sro.s3d")
                && currentWorldNames.Contains("sro_obj.s3d")
                && currentWorldNames.Contains("sky.s3d")
                && !currentWorldNames.Contains("testzone.s3d")
                && !currentWorldNames.Contains("skyfire.s3d")
                && !currentWorldNames.Contains("skyfire_chr.s3d"),
                "Current EQL World selection should retain its zones and shared sky without leaking Other or Kunark sky-prefix archives");
            Assert(
                currentCombinedNames.Contains("skyfire_chr.s3d"),
                "an explicit combined build must retain zone-named character sidecars as character content even when their World era is not selected");
            Assert(
                kunarkWorldNames.Contains("skyfire.s3d")
                && kunarkWorldNames.Contains("skyfire_obj.s3d")
                && kunarkWorldNames.Contains("sky.s3d")
                && !kunarkWorldNames.Contains("sro.s3d"),
                "Kunark World selection should include Skyfire as a zone and never as a shared sky-prefix archive");
            Assert(
                missingExpansionRejected && partiallyMissingExpansionRejected,
                "an explicit selection with any missing expansion must fail instead of producing a shared-only or falsely labeled partial World pack");
            Assert(
                !characterNames.Overlaps(
                    [
                        "it101084.s3d",
                        "it23900.eqg",
                        "it40001.eqg",
                        "wallet07.eqg",
                        "i23.eqg",
                        "pel.eqg",
                        "global_obj.s3d",
                        "globalgdb.eqg",
                        "mixedrole.eqg",
                        "orc_chr.s3d",
                        "holeequip.eqg",
                        "obequip_lexit.s3d",
                        "dest_sphere_shield.eqg",
                        "boat_chr.s3d",
                        "box_chr.s3d",
                        "och_chr.s3d",
                        "ship1_chr.s3d",
                        "ship2_chr.s3d",
                        "ship3_chr.s3d",
                        "tpn_chr.s3d",
                        "gsp_chr.s3d",
                        "cst.eqg",
                        "B09.eqg"
                    ]),
                "explicit world/furniture roles must override every character/equipment source");
            Assert(
                CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet23.eqg",
                    "jack_sword_c.dds"),
                "audited weapons in mixed packs should be eligible");
            Assert(
                !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet23.eqg",
                    "it11892_bowl_c.dds"),
                "non-equipment neighbors in mixed packs must remain untouched");
            Assert(
                CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet23.eqg",
                    "jack_faces_c.dds"),
                "every material referenced by an approved weapon must be eligible");
            Assert(
                CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet28.eqg",
                    "w28brushed_metal.dds")
                && CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet30.eqg",
                    "wood_w30.dds")
                && CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet33.eqg",
                    "rule_mess.dds"),
                "mixed item filters should include every attached-model color dependency");
            Assert(
                CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "lon16.eqg",
                    "sp_fearshard_axe_c.dds")
                && !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "lon16.eqg",
                    "lon16painting01.dds"),
                "mixed LoN packs should include weapons without touching their paintings");
            Assert(
                CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "donequip.eqg",
                    "it10780_katana_1h_sword_c.dds")
                && !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "donequip.eqg",
                    "it10806_potion_red_c.dds"),
                "mixed equipment packs should exclude world-only inventory props");
            Assert(
                !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "global_chr.s3d",
                    "boatbot.bmp")
                && !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "ivm_chr.s3d",
                    "textures\\ivmhe0001.dds")
                && !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "ivm_chr.s3d",
                    "textures/ivmhe0001.dds")
                && !CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "oot_chr.s3d",
                    "ghostship01.bmp")
                && CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "oot_chr.s3d",
                    "orcface01.bmp"),
                "mixed legacy actor packs should exclude path-qualified renderer controls and known world entries");
            Assert(
                CharacterEquipmentArchiveCatalog.IsTextureEntryAllowed(
                    "wallet01.eqg",
                    "soe_wallet_guktan_weapons_1handed_c.dds"),
                "dedicated equipment packs should not receive an entry filter");
            Assert(
                !characterNames.Overlaps(worldNames),
                "character/equipment-only and world-only archive selections must be disjoint");

            var expectedCombined = worldPaths
                .Concat(characterPaths)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert(
                combinedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(expectedCombined),
                "World + characters must be the exact union of the two isolated scopes");
            Assert(
                currentCombinedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(currentWorldPaths.Concat(explicitCharacterPaths)),
                "an expansion-filtered combined build should be the exact union of filtered World and all character/equipment archives");
            AssertEqual(
                allPaths.Count,
                allPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "All safe archive selection should not contain duplicates");
            Assert(
                allPaths.SequenceEqual(
                    allPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
                "archive selection should be deterministically sorted");

            var manifestPath = Path.Combine(root, "manifest", "build-manifest.json");
            var manifest = new BuildManifest(
                BuildManifest.CurrentSchemaVersion,
                "scope-round-trip",
                DateTimeOffset.UtcNow,
                root,
                characterOptions,
                []);
            var manifestStore = new ManifestStore();
            await manifestStore.WriteBuildManifestAsync(
                manifestPath,
                manifest,
                cancellationToken).ConfigureAwait(false);
            var roundTripped = await manifestStore.ReadBuildManifestAsync(
                manifestPath,
                cancellationToken).ConfigureAwait(false);
            AssertEqual(
                AssetScope.CharactersAndEquipmentOnly,
                roundTripped.Options.Scope,
                "character/equipment-only scope manifest round trip");

            var explicitManifestPath = Path.Combine(root, "manifest", "expansion-manifest.json");
            var explicitManifest = new BuildManifest(
                BuildManifest.CurrentSchemaVersion,
                "expansion-round-trip",
                DateTimeOffset.UtcNow,
                root,
                worldOptions with
                {
                    WorldExpansions = WorldExpansion.CurrentEql | WorldExpansion.Kunark
                },
                []);
            await manifestStore.WriteBuildManifestAsync(
                explicitManifestPath,
                explicitManifest,
                cancellationToken).ConfigureAwait(false);
            var explicitRoundTrip = await manifestStore.ReadBuildManifestAsync(
                explicitManifestPath,
                cancellationToken).ConfigureAwait(false);
            Assert(
                explicitRoundTrip.Options.WorldExpansions
                    == (WorldExpansion.CurrentEql | WorldExpansion.Kunark),
                "explicit World expansion manifest should round trip exactly");

            var legacyManifestPath = Path.Combine(root, "manifest", "legacy-manifest.json");
            await manifestStore.WriteBuildManifestAsync(
                legacyManifestPath,
                explicitManifest with
                {
                    SchemaVersion = BuildManifest.MinimumSupportedSchemaVersion,
                    BuildId = "legacy-world",
                    Options = worldOptions with { WorldExpansions = null }
                },
                cancellationToken).ConfigureAwait(false);
            var legacyRoundTrip = await manifestStore.ReadBuildManifestAsync(
                legacyManifestPath,
                cancellationToken).ConfigureAwait(false);
            Assert(
                legacyRoundTrip.Options.WorldExpansions is null,
                "schema-1 World manifests must remain readable as legacy all-detected selections");
            var downgradedSubsetPath = Path.Combine(
                root,
                "manifest",
                "downgraded-subset-manifest.json");
            await manifestStore.WriteBuildManifestAsync(
                downgradedSubsetPath,
                explicitManifest with { SchemaVersion = BuildManifest.MinimumSupportedSchemaVersion },
                cancellationToken).ConfigureAwait(false);
            var downgradedSubsetRejected = false;
            try
            {
                _ = await manifestStore.ReadBuildManifestAsync(
                    downgradedSubsetPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                downgradedSubsetRejected = true;
            }
            Assert(
                downgradedSubsetRejected,
                "an explicit World subset must never masquerade as a schema-1 manifest that older apps would misread");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static string CreateRaceDataRow(string modelCode)
    {
        var fields = Enumerable.Repeat("0", 57).ToArray();
        fields[50] = modelCode;
        return string.Join('^', fields) + '^';
    }

    private static void TestCapMathAndCommands()
    {
        var lavastorm = UpscaleDimensions.Calculate(256, 256, 4096);
        AssertEqual(1024, lavastorm.OutputWidth, "256 x4 width");
        AssertEqual(1024, lavastorm.OutputHeight, "256 x4 height");
        AssertEqual(4d, lavastorm.LinearScale, "x4 linear scale");
        AssertEqual(4, lavastorm.RequiredNeuralScale, "x4 neural scale");

        var capped = UpscaleDimensions.Calculate(1024, 512, 2048);
        AssertEqual(2048, capped.OutputWidth, "maximum dimension cap");
        AssertEqual(1024, capped.OutputHeight, "aspect-preserving capped height");
        AssertEqual(2, capped.RequiredNeuralScale, "x2 cap should request a 2x neural intermediate");

        var triple = UpscaleDimensions.Calculate(1024, 512, 3072);
        AssertEqual(3, triple.RequiredNeuralScale, "x3 cap should request a 3x neural intermediate");

        var fractional = UpscaleDimensions.Calculate(1024, 512, 2560);
        AssertEqual(3, fractional.RequiredNeuralScale, "fractional scales should round the neural intermediate up");

        var unchanged = UpscaleDimensions.Calculate(4096, 2048, 4096);
        AssertEqual(1, unchanged.RequiredNeuralScale, "unchanged textures should not request a neural pass");

        AssertEqual(
            1,
            NativeTextureProcessor.SelectCpuPreparationParallelism(4),
            "four-thread systems should retain one conservative preparation lane");
        AssertEqual(
            2,
            NativeTextureProcessor.SelectCpuPreparationParallelism(8),
            "eight-thread systems should use two preparation lanes");
        AssertEqual(
            4,
            NativeTextureProcessor.SelectCpuPreparationParallelism(16),
            "sixteen-thread systems should use four bounded preparation lanes");
        AssertEqual(
            "1:3:3",
            NativeTextureProcessor.SelectNeuralWorkerThreads(
                processorCount: 16,
                jobCount: 16,
                maximumOutputPixels: 1024 * 1024),
            "many small textures on a high-thread-count system should use the accelerated ncnn queue");
        AssertEqual(
            "1:2:2",
            NativeTextureProcessor.SelectNeuralWorkerThreads(
                processorCount: 16,
                jobCount: 16,
                maximumOutputPixels: 4L * 1024 * 1024),
            "large neural outputs should retain the conservative ncnn queue");
        AssertEqual(
            4,
            NativeTextureProcessor.SelectCpuPostprocessParallelism(
                processorCount: 16,
                availableMemoryBytes: 16L * 1024 * 1024 * 1024,
                maximumOutputPixels: 1024 * 1024),
            "small outputs should use four bounded post-processing lanes on a fast system");
        AssertEqual(
            1,
            NativeTextureProcessor.SelectCpuPostprocessParallelism(
                processorCount: 16,
                availableMemoryBytes: 16L * 1024 * 1024 * 1024,
                maximumOutputPixels: 32L * 1024 * 1024),
            "very large outputs should serialize post-processing to contain memory use");
        AssertEqual(
            1,
            NativeTextureProcessor.SelectCpuPostprocessParallelism(
                processorCount: 16,
                availableMemoryBytes: 1024L * 1024 * 1024,
                maximumOutputPixels: 1024 * 1024),
            "memory-constrained systems should keep one post-processing lane");

        AssertEqual(
            1,
            TextureMipPolicy.Calculate(1024, 1024, generateMipMaps: true, useCutoutFloor: true),
            "1024-square cutout should retain only its enhanced top level");
        AssertEqual(
            1,
            TextureMipPolicy.Calculate(1024, 256, generateMipMaps: true, useCutoutFloor: true),
            "rectangular cutout should retain only its enhanced top level");
        AssertEqual(
            11,
            TextureMipPolicy.Calculate(1024, 1024, generateMipMaps: true, useCutoutFloor: false),
            "ordinary color textures should retain their complete 1x1 mip chain");
        AssertEqual(
            1,
            TextureMipPolicy.Calculate(1024, 1024, generateMipMaps: false, useCutoutFloor: true),
            "disabled mip generation should always request one level");

        var metadata = new TextureMetadata(
            TextureFileFormat.Dds,
            "DDS/DXT1",
            256,
            256,
            1,
            4,
            TextureAlphaStatus.Possible,
            IsCompressed: true,
            IsTopDown: false,
            IsCubeMap: false,
            IsVolumeTexture: false,
            ArraySize: 1,
            TexconvFormat: "BC1_UNORM");
        var command = new DirectXTexCommandBuilder().CreateEncode(
            "C:\\tools\\texconv.exe",
            "C:\\work\\enhanced.tga",
            "C:\\work\\encoded",
            metadata,
            1024,
            1024,
            mipCount: 1,
            preserveAlphaCoverage: true);
        Assert(command.Arguments.Contains("-wrap"), "texconv encode should use wrap sampling");
        Assert(command.Arguments.Contains("--keep-coverage"), "cutout encoding should preserve alpha coverage");
        Assert(command.Arguments.Contains("BC1_UNORM"), "DDS compression should be preserved");
        AssertEqual("1", GetCommandArgument(command.Arguments, "-m")!, "cutout texconv mip count");

        var clampedCommand = new DirectXTexCommandBuilder().CreateEncode(
            "C:\\tools\\texconv.exe",
            "C:\\work\\enhanced.tga",
            "C:\\work\\encoded",
            metadata,
            1024,
            1024,
            mipCount: 11,
            preserveAlphaCoverage: false,
            wrapSampling: false);
        Assert(!clampedCommand.Arguments.Contains("-wrap"), "non-tileable textures must use clamped encoding");
        AssertEqual("11", GetCommandArgument(clampedCommand.Arguments, "-m")!, "color texconv full mip count");

        var singleMipCommand = new DirectXTexCommandBuilder().CreateEncode(
            "C:\\tools\\texconv.exe",
            "C:\\work\\enhanced.tga",
            "C:\\work\\encoded",
            metadata,
            1024,
            1024,
            mipCount: 1,
            preserveAlphaCoverage: false);
        AssertEqual("1", GetCommandArgument(singleMipCommand.Arguments, "-m")!, "disabled texconv mip count");

        var downsampleCommand = new DirectXTexCommandBuilder().CreateEncode(
            "C:\\tools\\texconv.exe",
            "C:\\work\\enhanced.tga",
            "C:\\work\\encoded",
            metadata,
            1024,
            1024,
            mipCount: 11,
            preserveAlphaCoverage: false,
            resizeFilter: "FANT");
        var filterIndex = downsampleCommand.Arguments.ToList().IndexOf("-if");
        Assert(filterIndex >= 0, "texconv encode should declare its resize filter");
        AssertEqual(
            "FANT",
            downsampleCommand.Arguments[filterIndex + 1],
            "neural downsample should select FANT filtering");
    }

    private static string? GetCommandArgument(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index].Equals(name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static void TestBoundedNativeOutputRetention()
    {
        const int capacity = 17;
        var tail = new BoundedTextTail(capacity);
        var lines = new[] { "alpha", "bravo", "charlie", "delta" };
        foreach (var line in lines)
        {
            tail.AppendLine(line);
        }

        var completeOutput = string.Join(Environment.NewLine, lines);
        var expectedTail = completeOutput[^capacity..];
        AssertEqual(expectedTail, tail.ToString(), "native output should retain the exact bounded tail");

        var oversizedLine = new string('x', capacity * 3) + "FINAL";
        tail.AppendLine(oversizedLine);
        AssertEqual(
            oversizedLine[^capacity..],
            tail.ToString(),
            "one oversized native-output line should retain only its exact tail");
        AssertEqual(
            64 * 1024,
            NativeProcessRunner.MaximumRetainedOutputCharacters,
            "native process retained-output budget");
    }

    private static void TestRestoreSafetyCleanupGuard()
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-cleanup-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(root, "apply-test");
        var ownedDirectory = Path.Combine(backupDirectory, "restore-safety-owned");
        var unrelatedDirectory = Path.Combine(backupDirectory, "keep-this-directory");
        var nestedDirectory = Path.Combine(
            backupDirectory,
            "nested",
            "restore-safety-not-an-immediate-child");
        Directory.CreateDirectory(ownedDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(ownedDirectory, "snapshot.bin"), "owned");

        try
        {
            InstallTransactionService.TryDeleteRestoreSafetyDirectory(
                backupDirectory,
                unrelatedDirectory);
            InstallTransactionService.TryDeleteRestoreSafetyDirectory(
                backupDirectory,
                nestedDirectory);
            Assert(Directory.Exists(unrelatedDirectory), "cleanup must preserve unrelated backup directories");
            Assert(Directory.Exists(nestedDirectory), "cleanup must reject nested lookalike directories");

            InstallTransactionService.TryDeleteRestoreSafetyDirectory(
                backupDirectory,
                ownedDirectory);
            Assert(!Directory.Exists(ownedDirectory), "cleanup should remove an exact owned safety directory");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestNativeProcessInactivityGuardAsync(
        CancellationToken cancellationToken)
    {
        var runner = new NativeProcessRunner();
        var ping = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "ping.exe");
        Assert(File.Exists(ping), "Windows ping helper should exist for native inactivity tests");

        var silentCommand = new NativeProcessCommand(
            ping,
            ["-n", "6", "127.0.0.1"],
            DisplayName: "silent self-test worker",
            InactivityTimeout: TimeSpan.FromMilliseconds(150));
        try
        {
            await runner.RunAsync(silentCommand, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "Self-test failed: a silent native worker should hit its inactivity guard.");
        }
        catch (NativeProcessInactivityException exception)
        {
            AssertEqual(
                "silent self-test worker",
                exception.Command.DisplayName!,
                "inactivity exception command");
            Assert(
                exception.InactiveDuration >= TimeSpan.FromMilliseconds(100),
                "inactivity exception should report the quiet interval");
            Assert(
                exception.Message.Contains("seconds", StringComparison.Ordinal)
                && !exception.Message.Contains("0 minutes", StringComparison.Ordinal),
                "sub-minute inactivity diagnostics should report seconds instead of zero minutes");
        }

        var activityLines = new List<string>();
        var activeCommand = new NativeProcessCommand(
            ping,
            ["-n", "7", "127.0.0.1"],
            DisplayName: "active self-test worker",
            InactivityTimeout: TimeSpan.FromSeconds(4));
        var activeResult = await runner.RunAsync(
                activeCommand,
                new InlineProgress<NativeOutputLine>(line => activityLines.Add(line.Text)),
                cancellationToken)
            .ConfigureAwait(false);
        Assert(activeResult.Succeeded, "periodically active native process should complete");
        Assert(
            activityLines.Count >= 7,
            "periodic native output should reset inactivity and be forwarded");

        var noTimeoutResult = await runner.RunAsync(
                new NativeProcessCommand(
                    ping,
                    ["-n", "1", "127.0.0.1"],
                    DisplayName: "default no-timeout self-test worker"),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Assert(noTimeoutResult.Succeeded, "default native command should retain no fixed timeout");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            await runner.RunAsync(
                    new NativeProcessCommand(
                        ping,
                        ["-n", "6", "127.0.0.1"],
                        DisplayName: "cancel self-test worker",
                        InactivityTimeout: TimeSpan.FromMinutes(1)),
                    cancellationToken: cancel.Token)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "Self-test failed: cancellation should stop a native worker.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancellation remains distinct from driver-hang recovery.
        }
    }

    private static void TestTextureModelSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-models-{Guid.NewGuid():N}");
        var legacyModels = Path.Combine(root, "legacy-models");
        var textureModels = Path.Combine(root, "models");
        var discoveryModels = Path.Combine(root, "discovery-models");
        Directory.CreateDirectory(legacyModels);
        Directory.CreateDirectory(textureModels);
        Directory.CreateDirectory(discoveryModels);
        try
        {
            File.WriteAllBytes(Path.Combine(legacyModels, "realesrgan-x4plus.param"), [1]);
            File.WriteAllBytes(Path.Combine(legacyModels, "realesrgan-x4plus.bin"), [1]);
            File.WriteAllBytes(Path.Combine(legacyModels, "realesrnet-x4plus.param"), [1]);
            File.WriteAllBytes(Path.Combine(legacyModels, "realesrnet-x4plus.bin"), [1]);
            File.WriteAllBytes(Path.Combine(legacyModels, "realesrgan-x4plus-anime.param"), [1]);
            File.WriteAllBytes(Path.Combine(legacyModels, "realesrgan-x4plus-anime.bin"), [1]);

            foreach (var modelName in new[]
                     {
                         RealEsrganCommandBuilder.LegacyFaithfulModelName,
                         RealEsrganCommandBuilder.LegacyDetailModelName
                     })
            {
                File.WriteAllBytes(Path.Combine(discoveryModels, $"{modelName}.param"), [1]);
                File.WriteAllBytes(Path.Combine(discoveryModels, $"{modelName}.bin"), [1]);
            }

            Assert(
                !ToolchainDiscovery.IsSupportedModelDirectory(discoveryModels),
                "toolchain discovery must reject a model directory missing the anime model");
            File.WriteAllBytes(
                Path.Combine(discoveryModels, $"{RealEsrganCommandBuilder.IllustratedModelName}.param"),
                [1]);
            File.WriteAllBytes(
                Path.Combine(discoveryModels, $"{RealEsrganCommandBuilder.IllustratedModelName}.bin"),
                [1]);
            Assert(
                ToolchainDiscovery.IsSupportedModelDirectory(discoveryModels),
                "toolchain discovery should accept the complete three-model set");

            var legacyBuilder = new RealEsrganCommandBuilder();
            var fallback = legacyBuilder.ResolveModel(legacyModels, TexturePreset.ClassicHd);
            AssertEqual(
                RealEsrganCommandBuilder.LegacyDetailModelName,
                fallback.Name,
                "Texture HD legacy fallback model");
            Assert(!fallback.IsTextureSpecialized, "legacy model should not be marked texture-specialized");

            var textureBuilder = new UpscaylCommandBuilder();
            Assert(
                textureBuilder.FindTextureModel(textureModels) is null,
                "Texture model should be optional when its files are absent");

            const string textureModel = "4x-PBRify_UpscalerSPANV4";
            File.WriteAllBytes(Path.Combine(textureModels, $"{textureModel}.param"), [1]);
            File.WriteAllBytes(Path.Combine(textureModels, $"{textureModel}.bin"), [1]);
            var specialized = textureBuilder.FindTextureModel(textureModels)
                ?? throw new InvalidOperationException("Self-test failed: texture model was not discovered.");
            AssertEqual(textureModel, specialized.Name, "Texture HD specialized model");
            Assert(specialized.IsTextureSpecialized, "PBRify model should be marked texture-specialized");

            var command = textureBuilder.CreateUpscale(
                Path.Combine(root, "upscayl-bin.exe"),
                textureModels,
                Path.Combine(root, "input.png"),
                Path.Combine(root, "output.png"),
                specialized.Name,
                tileSize: 0);
            Assert(command.Arguments.Contains("-z"), "Upscayl command should declare native model scale");
            Assert(command.Arguments.Contains("-j"), "Upscayl command should configure its processing queues");
            Assert(command.Arguments.Contains("1:2:2"), "Upscayl command should use the validated queue split");
            Assert(command.Arguments.Contains("4"), "Upscayl command should keep fixed SPAN models at native 4x");
            AssertEqual(
                TimeSpan.FromMinutes(45),
                command.InactivityTimeout!.Value,
                "Texture HD worker inactivity guard");

            var acceleratedCommand = textureBuilder.CreateUpscale(
                Path.Combine(root, "upscayl-bin.exe"),
                textureModels,
                Path.Combine(root, "input-directory"),
                Path.Combine(root, "output-directory"),
                specialized.Name,
                tileSize: 0,
                workerThreads: "1:3:3");
            AssertEqual(
                "1:3:3",
                GetCommandArgument(acceleratedCommand.Arguments, "-j")!,
                "Upscayl should accept the validated accelerated small-texture queue");

            var faithfulCommand = legacyBuilder.CreateUpscale(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                legacyModels,
                Path.Combine(root, "input-directory"),
                Path.Combine(root, "output-directory"),
                TexturePreset.Faithful,
                workerThreads: "1:3:3");
            AssertEqual(
                "1:3:3",
                GetCommandArgument(faithfulCommand.Arguments, "-j")!,
                "Real-ESRNet should accept the validated accelerated small-texture queue");
            AssertEqual(
                TimeSpan.FromMinutes(45),
                faithfulCommand.InactivityTimeout!.Value,
                "standard Real-ESRGAN worker inactivity guard");

            var materialCommand = legacyBuilder.CreateUpscale(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                legacyModels,
                Path.Combine(root, "material-input-directory"),
                Path.Combine(root, "material-output-directory"),
                TexturePreset.MaximumDetail);
            Assert(materialCommand.Arguments.Contains("-x"), "Material Detail should retain TTA");
            AssertEqual(
                TimeSpan.FromMinutes(90),
                materialCommand.InactivityTimeout!.Value,
                "Material Detail TTA inactivity guard");

            var faithful = legacyBuilder.ResolveModel(legacyModels, TexturePreset.Faithful);
            AssertEqual(
                RealEsrganCommandBuilder.LegacyFaithfulModelName,
                faithful.Name,
                "Faithful should retain its conservative model");
            var illustrated = legacyBuilder.ResolveModel(legacyModels, TexturePreset.Illustrated);
            AssertEqual(
                RealEsrganCommandBuilder.LegacyDetailModelName,
                illustrated.Name,
                "Illustrated legacy recovery should keep a detail-preserving base for the painterly stylizer");
            var illustratedCommand = legacyBuilder.CreateUpscale(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                legacyModels,
                Path.Combine(root, "input-directory"),
                Path.Combine(root, "output-directory"),
                TexturePreset.Illustrated);
            AssertEqual(
                RealEsrganCommandBuilder.LegacyDetailModelName,
                GetCommandArgument(illustratedCommand.Arguments, "-n")!,
                "Illustrated legacy recovery command model");
            Assert(
                !illustratedCommand.Arguments.Contains("-x"),
                "Illustrated should not silently enable the maximum-detail TTA route");

            var rustic = legacyBuilder.ResolveModel(legacyModels, TexturePreset.RusticPainted);
            AssertEqual(
                RealEsrganCommandBuilder.IllustratedModelName,
                rustic.Name,
                "Rustic Painted should build on the validated illustrated reconstruction model");
            var rusticCommand = legacyBuilder.CreateUpscale(
                Path.Combine(root, "realesrgan-ncnn-vulkan.exe"),
                legacyModels,
                Path.Combine(root, "rustic-input-directory"),
                Path.Combine(root, "rustic-output-directory"),
                TexturePreset.RusticPainted);
            AssertEqual(
                RealEsrganCommandBuilder.IllustratedModelName,
                GetCommandArgument(rusticCommand.Arguments, "-n")!,
                "Rustic Painted command model");
            Assert(
                !rusticCommand.Arguments.Contains("-x"),
                "Rustic Painted should not silently enable the maximum-detail TTA route");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static void TestPresetAwareFidelityGate()
    {
        var visible = new TextureColorStatistics(
            Samples: 64,
            MeanRed: 100,
            MeanGreen: 100,
            MeanBlue: 100,
            MeanLuminance: 100,
            LuminanceStandardDeviation: 20,
            ExtremeLuminanceFraction: 0);
        var stylizedButNotFaithful = new NativeTextureProcessor.TextureFidelityMetrics(
            visible,
            visible,
            MeanLuminanceDrift: 2,
            MaximumMeanChannelDrift: 3,
            RoundTripMeanSquaredError: 200,
            RoundTripLuminancePsnr: 19.12,
            CoarseLuminanceCorrelation: 0.94,
            SourceEdgeEnergy: 1,
            EdgeEnergyRatio: 0.23,
            ExtremeLuminanceGrowth: 0);

        var faithfulRejected = false;
        try
        {
            NativeTextureProcessor.ValidateNeuralOutputForPreset(
                TexturePreset.Faithful,
                stylizedButNotFaithful);
        }
        catch (InvalidDataException)
        {
            faithfulRejected = true;
        }

        Assert(
            faithfulRejected,
            "the regression fixture must remain outside the strict Faithful fidelity budget");
        NativeTextureProcessor.ValidateNeuralOutputForPreset(
            TexturePreset.Illustrated,
            stylizedButNotFaithful);
        var rusticRejected = false;
        try
        {
            NativeTextureProcessor.ValidateNeuralOutputForPreset(
                TexturePreset.RusticPainted,
                stylizedButNotFaithful);
        }
        catch (InvalidDataException)
        {
            rusticRejected = true;
        }

        Assert(
            rusticRejected,
            "Rustic Painted must retain its stricter pre-graphic fidelity budget");

        var catastrophic = stylizedButNotFaithful with
        {
            RoundTripLuminancePsnr = 13,
            CoarseLuminanceCorrelation = 0.10,
            EdgeEnergyRatio = 0.20
        };
        var catastrophicRejected = false;
        try
        {
            NativeTextureProcessor.ValidateNeuralOutputForPreset(
                TexturePreset.Illustrated,
                catastrophic);
        }
        catch (InvalidDataException)
        {
            catastrophicRejected = true;
        }

        Assert(
            catastrophicRejected,
            "graphic painted fidelity gate must reject structurally unrelated output");

        var blank = stylizedButNotFaithful with
        {
            EnhancedStatistics = visible with { Samples = 0 }
        };
        var blankRejected = false;
        try
        {
            NativeTextureProcessor.ValidateNeuralOutputForPreset(
                TexturePreset.RusticPainted,
                blank);
        }
        catch (InvalidDataException)
        {
            blankRejected = true;
        }

        Assert(
            blankRejected,
            "graphic painted fidelity gate must reject output with no visible pixels");
    }

    private static void TestPaintedFinalValidationPolicyAndCappedMetrics()
    {
        var metadata = new TextureMetadata(
            TextureFileFormat.Tga,
            "TGA/TRUE-COLOR/32bpp",
            30,
            20,
            1,
            32,
            TextureAlphaStatus.None,
            IsCompressed: false,
            IsTopDown: true,
            IsCubeMap: false,
            IsVolumeTexture: false,
            ArraySize: 1,
            TexconvFormat: "B8G8R8X8_UNORM");
        var color = new TextureClassification(
            TextureKind.Color,
            ClassificationConfidence.High,
            CanUseColorUpscaler: true,
            RequiresSeparateAlphaHandling: false,
            RequiresReview: false,
            Reasons: []);
        Assert(
            !NativeTextureProcessor.RequiresFinalIllustratedDecodeValidation(
                metadata,
                color,
                preserveAlphaCoverage: false),
            "ordinary opaque Illustrated textures must avoid an extra final texconv decode");
        Assert(
            NativeTextureProcessor.RequiresFinalIllustratedDecodeValidation(
                metadata,
                color with { Kind = TextureKind.Cutout },
                preserveAlphaCoverage: false),
            "semantic cutouts require validation of the exact final encoded bytes");
        Assert(
            NativeTextureProcessor.RequiresFinalIllustratedDecodeValidation(
                metadata with { AlphaStatus = TextureAlphaStatus.Explicit },
                color,
                preserveAlphaCoverage: true),
            "runtime-detected binary-alpha foliage requires final validation even when its name classified as Color");
        Assert(
            NativeTextureProcessor.RequiresFinalIllustratedDecodeValidation(
                metadata with
                {
                    FileFormat = TextureFileFormat.Bmp,
                    PayloadFormat = "BMP/INDEXED8",
                    BitsPerPixel = 8
                },
                color,
                preserveAlphaCoverage: false),
            "8-bit indexed BMP palette mapping requires validation after final encoding");

        var source = CreateSolidTgaBuffer(30, 20, 92, 128, 74);
        var roundedCap = CreateSolidTgaBuffer(103, 69, 92, 128, 74);
        var metrics = NativeTextureProcessor.CalculateFidelityMetrics(source, roundedCap);
        Assert(
            metrics.RoundTripLuminancePsnr >= 100
            && metrics.CoarseLuminanceCorrelation >= 0.999,
            "a rounded aspect-preserving cap must pass deterministic area-reduced fidelity comparison");

        var distortedRejected = false;
        try
        {
            _ = NativeTextureProcessor.CalculateFidelityMetrics(
                source,
                CreateSolidTgaBuffer(103, 75, 92, 128, 74));
        }
        catch (InvalidDataException)
        {
            distortedRejected = true;
        }

        Assert(
            distortedRejected,
            "final painted validation must still reject a real aspect-ratio distortion");

        static TgaPixelBuffer CreateSolidTgaBuffer(
            int width,
            int height,
            byte red,
            byte green,
            byte blue)
        {
            var bytes = new byte[checked(18 + (width * height * 4))];
            bytes[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), checked((ushort)width));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), checked((ushort)height));
            bytes[16] = 32;
            bytes[17] = 0x28;
            for (var offset = 18; offset < bytes.Length; offset += 4)
            {
                bytes[offset] = blue;
                bytes[offset + 1] = green;
                bytes[offset + 2] = red;
                bytes[offset + 3] = byte.MaxValue;
            }

            return TgaPixelBuffer.Read(bytes);
        }
    }

    private static void TestArtDirectionAndEffectPolicies()
    {
        var zoneAwareOptions = new UpscaleOptions(
            TexturePreset.Illustrated,
            AssetScope.WorldOnly,
            2048,
            GenerateMipMaps: true,
            InstallAfterBuild: false,
            PaintedTheme: PaintedTheme.ZoneAware);
        AssertEqual(
            PaintedTheme.DarkGothic,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions,
                @"zones\NERIAKA_2_OBJ.S3D").PaintedTheme,
            "reviewed Neriak object archives should resolve to Dark Gothic");
        AssertEqual(
            PaintedTheme.DarkGothic,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions,
                "neriakb.eqg").PaintedTheme,
            "reviewed Neriak interior archives should resolve to Dark Gothic");
        AssertEqual(
            PaintedTheme.LightStorybook,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions,
                @"zones/felwithea_obj.s3d").PaintedTheme,
            "reviewed bright city archives should resolve to Light Storybook");
        AssertEqual(
            PaintedTheme.LightStorybook,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions,
                "qey2hh1.s3d").PaintedTheme,
            "reviewed West Karana archives should resolve to Light Storybook");
        AssertEqual(
            PaintedTheme.ClassicPainted,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions,
                "neriaka_fake.s3d").PaintedTheme,
            "zone mood selection must never use a partial filename match");
        AssertEqual(
            PaintedTheme.ClassicPainted,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions,
                "plants.s3d").PaintedTheme,
            "shared foliage archives should retain the balanced fallback palette");
        AssertEqual(
            PaintedTheme.ComicInk,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions with { PaintedTheme = PaintedTheme.ComicInk },
                "neriaka.s3d").PaintedTheme,
            "an explicit user theme must override the zone mood map");
        AssertEqual(
            PaintedTheme.ClassicPainted,
            PaintedThemeResolver.ResolveForArtifact(
                zoneAwareOptions with
                {
                    Scope = AssetScope.SelectedZone,
                    SelectedZone = "felwithea"
                },
                "neriaka.s3d").PaintedTheme,
            "a selected-zone/archive identity mismatch must fail to Classic Painted");

        var color = new TextureMetadata(
            TextureFileFormat.Dds,
            "DXT1",
            256,
            256,
            1,
            4,
            TextureAlphaStatus.None,
            IsCompressed: true,
            IsTopDown: false,
            IsCubeMap: false,
            IsVolumeTexture: false,
            1,
            "BC1_UNORM",
            UsesDx10Header: false);
        Assert(
            SpellEffectTexturePolicy.CanEnhance(@"SpellEffects\fire01.dds", color),
            "single-image spell art should be eligible for the explicit effect scope");
        Assert(
            !SpellEffectTexturePolicy.CanEnhance(@"SpellEffects\fire_atlas.dds", color),
            "packed effect atlases must remain protected");
        Assert(
            !SpellEffectTexturePolicy.CanEnhance(@"textures\fire01.dds", color),
            "ordinary loose textures must not leak into the spell-effect scope");

        var normal = color with
        {
            PayloadFormat = "DXT5",
            TexconvFormat = "BC3_UNORM",
            AlphaStatus = TextureAlphaStatus.Explicit
        };
        Assert(
            !SpellEffectTexturePolicy.CanEnhance(@"SpellEffects\fire_n.dds", normal),
            "technical maps in effect folders must remain protected");

        TextureOverridePolicy.ValidateAll(
        [
            new TextureOverride(
                "qeynos.s3d",
                "stone01.dds",
                TextureOverrideAction.Reprocess,
                TexturePreset.RusticPainted)
        ]);
        try
        {
            TextureOverridePolicy.ValidateAll(
            [
                new TextureOverride(
                    "../qeynos.s3d",
                    "stone01.dds",
                    TextureOverrideAction.PreserveOriginal)
            ]);
            throw new InvalidOperationException(
                "Self-test failed: texture choices must reject path traversal.");
        }
        catch (InvalidDataException)
        {
            // Expected.
        }
    }

    private static void TestPaintedThemeAccentPreservation()
    {
        // A dark-zone palette is an art-direction nudge, not a replacement
        // palette. Neriak still contains bright gold, violet, and teal accents
        // that must remain locally recognizable after the Gothic treatment.
        var pixels = new (byte R, byte G, byte B, byte A)[]
        {
            (246, 190, 24, 255), // warm gold trim
            (222, 34, 196, 255), // saturated violet magic/signage
            (22, 218, 184, 173), // teal light with partial alpha
            (72, 63, 78, 255),   // subdued dark-city material
            (193, 77, 211, 0)    // hidden cutout RGB
        };
        var bytes = new byte[18 + (pixels.Length * 4)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)pixels.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 1);
        bytes[16] = 32;
        bytes[17] = 0x28;
        for (var pixel = 0; pixel < pixels.Length; pixel++)
        {
            var offset = 18 + (pixel * 4);
            bytes[offset] = pixels[pixel].B;
            bytes[offset + 1] = pixels[pixel].G;
            bytes[offset + 2] = pixels[pixel].R;
            bytes[offset + 3] = pixels[pixel].A;
        }

        var source = TgaPixelBuffer.Read(bytes);
        var dark = source.ApplyPaintedTheme(PaintedTheme.DarkGothic);
        var sourcePixels = source.RgbaPixels.Span;
        var darkPixels = dark.RgbaPixels.Span;

        AssertSequenceEqual(
            sourcePixels.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            darkPixels.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            "Dark Gothic must preserve opaque, partial, and zero alpha byte-for-byte");
        AssertSequenceEqual(
            sourcePixels.Slice(16, 4),
            darkPixels.Slice(16, 4),
            "Dark Gothic must preserve hidden RGB beneath a fully transparent accent texel");

        for (var pixel = 0; pixel < 3; pixel++)
        {
            var offset = pixel * 4;
            var sourceRed = sourcePixels[offset];
            var sourceGreen = sourcePixels[offset + 1];
            var sourceBlue = sourcePixels[offset + 2];
            var themedRed = darkPixels[offset];
            var themedGreen = darkPixels[offset + 1];
            var themedBlue = darkPixels[offset + 2];
            var sourceChroma = Math.Max(sourceRed, Math.Max(sourceGreen, sourceBlue))
                - Math.Min(sourceRed, Math.Min(sourceGreen, sourceBlue));
            var themedChroma = Math.Max(themedRed, Math.Max(themedGreen, themedBlue))
                - Math.Min(themedRed, Math.Min(themedGreen, themedBlue));
            Assert(
                themedChroma >= sourceChroma * 0.72,
                $"Dark Gothic must retain the saturation of bright accent {pixel + 1}");

            var hueDistance = CircularHueDistance(
                CalculateHue(sourceRed, sourceGreen, sourceBlue),
                CalculateHue(themedRed, themedGreen, themedBlue));
            Assert(
                hueDistance <= 12,
                $"Dark Gothic must retain the local hue identity of bright accent {pixel + 1}");
            Assert(
                Math.Max(themedRed, Math.Max(themedGreen, themedBlue)) >= 160,
                $"Dark Gothic must not crush bright accent {pixel + 1} into the zone's shadows");
        }

        // Exercise the complete deterministic painted finish rather than only
        // its final palette step. Large solid patches keep each central sample
        // outside the edge-aware filter radius and make the contract explicit.
        const int patchSize = 9;
        var patchColors = new (byte R, byte G, byte B)[]
        {
            (246, 190, 24),
            (222, 34, 196),
            (22, 218, 184),
            (72, 63, 78)
        };
        var patchBytes = new byte[18 + (patchSize * patchSize * patchColors.Length * 4)];
        patchBytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(
            patchBytes.AsSpan(12),
            (ushort)(patchSize * patchColors.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(patchBytes.AsSpan(14), patchSize);
        patchBytes[16] = 32;
        patchBytes[17] = 0x28;
        for (var y = 0; y < patchSize; y++)
        {
            for (var patch = 0; patch < patchColors.Length; patch++)
            {
                for (var x = 0; x < patchSize; x++)
                {
                    var pixel = (y * patchSize * patchColors.Length) + (patch * patchSize) + x;
                    var offset = 18 + (pixel * 4);
                    patchBytes[offset] = patchColors[patch].B;
                    patchBytes[offset + 1] = patchColors[patch].G;
                    patchBytes[offset + 2] = patchColors[patch].R;
                    patchBytes[offset + 3] = byte.MaxValue;
                }
            }
        }

        var graphicPatches = TgaPixelBuffer.Read(patchBytes).ApplyPaintedStylization(strength: 1);
        var gothicPatches = graphicPatches.ApplyPaintedTheme(
            PaintedTheme.DarkGothic,
            strength: 0.62);
        var graphicPatchPixels = graphicPatches.RgbaPixels.Span;
        var gothicPatchPixels = gothicPatches.RgbaPixels.Span;
        for (var patch = 0; patch < 3; patch++)
        {
            var centerPixel = ((patchSize / 2) * patchSize * patchColors.Length)
                + (patch * patchSize)
                + (patchSize / 2);
            var offset = centerPixel * 4;
            var baselineRed = graphicPatchPixels[offset];
            var baselineGreen = graphicPatchPixels[offset + 1];
            var baselineBlue = graphicPatchPixels[offset + 2];
            var gothicRed = gothicPatchPixels[offset];
            var gothicGreen = gothicPatchPixels[offset + 1];
            var gothicBlue = gothicPatchPixels[offset + 2];
            var baselineChroma = Math.Max(baselineRed, Math.Max(baselineGreen, baselineBlue))
                - Math.Min(baselineRed, Math.Min(baselineGreen, baselineBlue));
            var gothicChroma = Math.Max(gothicRed, Math.Max(gothicGreen, gothicBlue))
                - Math.Min(gothicRed, Math.Min(gothicGreen, gothicBlue));
            Assert(
                gothicChroma >= baselineChroma * 0.85,
                $"the complete painted pipeline must retain at least 85% chroma for accent patch {patch + 1}");
            Assert(
                CircularHueDistance(
                    CalculateHue(baselineRed, baselineGreen, baselineBlue),
                    CalculateHue(gothicRed, gothicGreen, gothicBlue)) <= 8,
                $"the complete painted pipeline must keep accent patch {patch + 1} within eight hue degrees");
            var baselinePeak = Math.Max(baselineRed, Math.Max(baselineGreen, baselineBlue));
            var gothicPeak = Math.Max(gothicRed, Math.Max(gothicGreen, gothicBlue));
            Assert(
                baselinePeak - gothicPeak <= 12,
                $"the complete painted pipeline must not reduce accent patch {patch + 1}'s peak channel by more than 12");
        }

        var neutralCenterPixel = ((patchSize / 2) * patchSize * patchColors.Length)
            + (3 * patchSize)
            + (patchSize / 2);
        var neutralOffset = neutralCenterPixel * 4;
        var neutralBaselineLuma = (0.2126 * graphicPatchPixels[neutralOffset])
            + (0.7152 * graphicPatchPixels[neutralOffset + 1])
            + (0.0722 * graphicPatchPixels[neutralOffset + 2]);
        var neutralGothicLuma = (0.2126 * gothicPatchPixels[neutralOffset])
            + (0.7152 * gothicPatchPixels[neutralOffset + 1])
            + (0.0722 * gothicPatchPixels[neutralOffset + 2]);
        Assert(
            neutralGothicLuma < neutralBaselineLuma,
            "the complete painted pipeline should still darken subdued neutral city materials");

        // Production theme strength must make the choices visibly different on
        // an ordinary material field, not merely produce different hashes. The
        // fixture mixes warm stone, muted wood, cool masonry, and modest color
        // accents without relying on any bundled game artwork.
        const int materialSize = 32;
        var materialBytes = new byte[18 + (materialSize * materialSize * 4)];
        materialBytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(materialBytes.AsSpan(12), materialSize);
        BinaryPrimitives.WriteUInt16LittleEndian(materialBytes.AsSpan(14), materialSize);
        materialBytes[16] = 32;
        materialBytes[17] = 0x28;
        for (var y = 0; y < materialSize; y++)
        {
            for (var x = 0; x < materialSize; x++)
            {
                var value = 38 + ((x * 9 + y * 5 + ((x / 4 + y / 4) % 2 * 24)) % 150);
                var red = Math.Clamp(value + 18 - (y / 3), 8, 242);
                var green = Math.Clamp(value - 2 + (x / 5), 8, 238);
                var blue = Math.Clamp(value - 24 + ((x + y) % 13), 6, 226);
                var offset = 18 + (((y * materialSize) + x) * 4);
                materialBytes[offset] = (byte)blue;
                materialBytes[offset + 1] = (byte)green;
                materialBytes[offset + 2] = (byte)red;
                materialBytes[offset + 3] = byte.MaxValue;
            }
        }

        var productionBase = TgaPixelBuffer.Read(materialBytes)
            .ApplyPaintedStylization(strength: 0.94, wrapEdges: true);
        var productionLight = productionBase.ApplyPaintedTheme(
            PaintedTheme.LightStorybook,
            strength: 0.78);
        var productionDark = productionBase.ApplyPaintedTheme(
            PaintedTheme.DarkGothic,
            strength: 0.78);
        var productionComic = productionBase.ApplyPaintedTheme(
            PaintedTheme.ComicInk,
            strength: 0.78);
        var lightDarkDelta = MeanAbsoluteVisibleRgbDelta(productionLight, productionDark);
        var lightComicDelta = MeanAbsoluteVisibleRgbDelta(productionLight, productionComic);
        Assert(
            lightDarkDelta >= 7,
            $"Light Storybook and Dark Gothic should be visibly distinct at production strength (delta {lightDarkDelta:0.00})");
        Assert(
            lightComicDelta >= 3.25,
            $"Light Storybook and Comic Ink should be visibly distinct at production strength (delta {lightComicDelta:0.00})");
        Assert(
            productionLight.CalculateVisibleColorStatistics().MeanLuminance
                - productionDark.CalculateVisibleColorStatistics().MeanLuminance >= 5,
            "Light Storybook should average at least five luminance levels above Dark Gothic at production strength");

        var zoneAware = new UpscaleOptions(
            TexturePreset.Illustrated,
            AssetScope.WorldOnly,
            2048,
            GenerateMipMaps: true,
            InstallAfterBuild: false,
            PaintedTheme: PaintedTheme.ZoneAware);
        var neriakOptions = PaintedThemeResolver.ResolveForArtifact(zoneAware, "neriaka.s3d");
        AssertEqual(
            PaintedTheme.DarkGothic,
            neriakOptions.PaintedTheme,
            "reviewed Neriak art direction should use the accent-safe Dark Gothic treatment");
        AssertSequenceEqual(
            darkPixels,
            source.ApplyPaintedTheme(neriakOptions.PaintedTheme).RgbaPixels.Span,
            "Zone-aware Neriak must use the same deterministic accent-safe Dark Gothic treatment");

        var unknownOptions = PaintedThemeResolver.ResolveForArtifact(zoneAware, "unreviewedcity.s3d");
        AssertEqual(
            PaintedTheme.ClassicPainted,
            unknownOptions.PaintedTheme,
            "an unknown zone must fail safely to Classic Painted instead of guessing its palette");
        AssertSequenceEqual(
            sourcePixels,
            source.ApplyPaintedTheme(unknownOptions.PaintedTheme).RgbaPixels.Span,
            "the unknown-zone Classic fallback must not impose an inferred zone palette");

        static double CalculateHue(byte red, byte green, byte blue)
        {
            var r = red / 255.0;
            var g = green / 255.0;
            var b = blue / 255.0;
            var maximum = Math.Max(r, Math.Max(g, b));
            var minimum = Math.Min(r, Math.Min(g, b));
            var delta = maximum - minimum;
            if (delta <= double.Epsilon)
            {
                return 0;
            }

            double hue;
            if (maximum == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (maximum == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }

            return hue < 0 ? hue + 360 : hue;
        }

        static double CircularHueDistance(double left, double right)
        {
            var direct = Math.Abs(left - right);
            return Math.Min(direct, 360 - direct);
        }

        static double MeanAbsoluteVisibleRgbDelta(
            TgaPixelBuffer left,
            TgaPixelBuffer right)
        {
            var leftPixels = left.RgbaPixels.Span;
            var rightPixels = right.RgbaPixels.Span;
            long total = 0;
            long samples = 0;
            for (var offset = 0; offset < leftPixels.Length; offset += 4)
            {
                if (leftPixels[offset + 3] == 0 || rightPixels[offset + 3] == 0)
                {
                    continue;
                }

                total += Math.Abs(leftPixels[offset] - rightPixels[offset]);
                total += Math.Abs(leftPixels[offset + 1] - rightPixels[offset + 1]);
                total += Math.Abs(leftPixels[offset + 2] - rightPixels[offset + 2]);
                samples += 3;
            }

            return samples == 0 ? 0 : (double)total / samples;
        }
    }

    private static void TestPreviewSampling()
    {
        var collectorType = typeof(TexturePreviewManifest).Assembly.GetType(
            "SpinTexture.Core.Services.TexturePreviewCollector",
            throwOnError: true)!;
        var collector = Activator.CreateInstance(collectorType, 24)
            ?? throw new InvalidOperationException("Self-test failed: preview collector could not be created.");
        var tryReserve = collectorType.GetMethod("TryReserve")
            ?? throw new InvalidOperationException("Self-test failed: preview reservation method was not found.");
        var abandon = collectorType.GetMethod("Abandon")
            ?? throw new InvalidOperationException("Self-test failed: preview abandonment method was not found.");

        (bool Accepted, int Index) Reserve(string archive, string logicalName)
        {
            object?[] arguments = [archive, logicalName, -1];
            var accepted = (bool)(tryReserve.Invoke(collector, arguments)
                ?? throw new InvalidOperationException("Self-test failed: preview reservation returned no result."));
            return (accepted, (int)arguments[2]!);
        }

        var lava = Reserve("lavastorm.s3d", "lavastorm_lava001.dds");
        Assert(lava.Accepted, "first animation frame should receive a preview slot");
        Assert(
            !Reserve("lavastorm.s3d", "lavastorm_lava002.dds").Accepted,
            "sequential animation frames should share one preview family");
        Assert(Reserve("lavastorm.s3d", "lavastorm_ngnr1.dds").Accepted, "first numbered rock should be previewed");
        Assert(
            !Reserve("lavastorm.s3d", "lavastorm_ngnr10.dds").Accepted,
            "numbered material variants should share one preview family");
        Assert(
            Reserve("lavastorm_obj.s3d", "lavastorm_lava002.dds").Accepted,
            "separate archives should each be represented");

        foreach (var suffix in new[] { "a", "b", "c", "d", "e", "f" })
        {
            Assert(
                Reserve("lavastorm.s3d", $"unique-material-{suffix}.dds").Accepted,
                "an archive should receive its representative preview share");
        }

        Assert(
            !Reserve("lavastorm.s3d", "ninth-unique-material.dds").Accepted,
            "one archive should not consume more than one third of the gallery");

        abandon.Invoke(collector, [lava.Index]);
        var replacement = Reserve("lavastorm.s3d", "replacement-material.dds");
        Assert(replacement.Accepted, "failed previews should release their family and archive share");
        AssertEqual(lava.Index, replacement.Index, "released preview indices should be reused deterministically");
    }

    private static async Task TestPaintedStylizerAsync(CancellationToken cancellationToken)
    {
        // A strictly periodic fixture makes the wrapped-seam assertions
        // meaningful: opposite edges are true neighbors when the texture tiles.
        const int size = 128;
        var fixtureBytes = new byte[18 + (size * size * 4)];
        fixtureBytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(fixtureBytes.AsSpan(12), size);
        BinaryPrimitives.WriteUInt16LittleEndian(fixtureBytes.AsSpan(14), size);
        fixtureBytes[16] = 32;
        fixtureBytes[17] = 0x28;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Periodic value pattern plus a hard-edged cutout hole so the
                // alpha-preservation contract is exercised on real cutout data.
                var wave = Math.Sin(x * Math.PI * 8 / size) * Math.Cos(y * Math.PI * 6 / size);
                var hash = (uint)((x % 16 * 374761393) + (y % 16 * 668265263));
                hash = (hash ^ (hash >> 13)) * 1274126177u;
                var noise = (hash & 0xFF) / 255.0;
                var value = 96 + (64 * wave) + (48 * noise);
                var inHole = ((x + 32) % size) < 20 && ((y + 48) % size) < 20;
                var offset = 18 + (((y * size) + x) * 4);
                fixtureBytes[offset] = (byte)Math.Clamp(value * 0.55, 0, 255);
                fixtureBytes[offset + 1] = (byte)Math.Clamp(value * 0.85, 0, 255);
                fixtureBytes[offset + 2] = (byte)Math.Clamp(value, 0, 255);
                fixtureBytes[offset + 3] = inHole
                    ? (byte)0
                    : (x % 37) == 0 ? (byte)180 : byte.MaxValue;
            }
        }

        var fixture = TgaPixelBuffer.Read(fixtureBytes);
        var style = new PaintedStyleSettings(0.6, 0.7, 0.5, 0.6, 0.4);
        var painted = fixture.ApplyPaintedStylization(
            strength: 0.78,
            wrapEdges: true,
            neuralScale: 4,
            style);

        AssertSequenceEqual(
            fixture.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            painted.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            "painterly stylization must preserve the alpha plane byte-for-byte, including cutout holes and fringes");
        Assert(
            !fixture.RgbaPixels.Span.SequenceEqual(painted.RgbaPixels.Span),
            "painterly stylization should visibly restyle the color plane");

        var repeat = fixture.ApplyPaintedStylization(
            strength: 0.78,
            wrapEdges: true,
            neuralScale: 4,
            style);
        AssertSequenceEqual(
            painted.RgbaPixels.Span,
            repeat.RgbaPixels.Span,
            "painterly stylization must be deterministic so repairs and resumed builds reproduce identical bytes");

        var identity = fixture.ApplyPaintedStylization(strength: 0, wrapEdges: true, neuralScale: 4, style);
        AssertSequenceEqual(
            fixture.RgbaPixels.Span,
            identity.RgbaPixels.Span,
            "zero painterly strength must be an exact no-op");

        var sizedDown = fixture.ApplyPaintedStylization(
            strength: 0.78,
            wrapEdges: true,
            neuralScale: 4,
            style with { StrokeSize = 0.05 });
        Assert(
            !sizedDown.RgbaPixels.Span.SequenceEqual(painted.RgbaPixels.Span),
            "the stroke-size control must influence the painterly result");

        // Tiling: wrapped-edge neighbor differences must look like interior
        // neighbor differences, or the painted output would show seams in-game.
        var paintedPixels = painted.RgbaPixels.Span;
        double seamDifference = 0;
        double interiorDifference = 0;
        for (var y = 0; y < size; y++)
        {
            seamDifference += RgbDistance(paintedPixels, ((y * size) + 0) * 4, ((y * size) + size - 1) * 4);
            interiorDifference += RgbDistance(paintedPixels, ((y * size) + (size / 2)) * 4, ((y * size) + (size / 2) - 1) * 4);
        }

        for (var x = 0; x < size; x++)
        {
            seamDifference += RgbDistance(paintedPixels, x * 4, (((size - 1) * size) + x) * 4);
            interiorDifference += RgbDistance(paintedPixels, (((size / 2) * size) + x) * 4, ((((size / 2) - 1) * size) + x) * 4);
        }

        Assert(
            seamDifference <= interiorDifference * 2.5,
            $"wrapped painterly output must tile without visible seams (seam {seamDifference:0.0} vs interior {interiorDifference:0.0})");

        // Strength mixing is what the fidelity ladder relies on: a blended
        // candidate must always sit between the input and the stylized result.
        var stylizedFull = fixture.ApplyPaintedStylizationFull(wrapEdges: true, neuralScale: 4, style);
        var half = fixture.BlendTowardStylized(stylizedFull, 0.5);
        var fixturePixels = fixture.RgbaPixels.Span;
        var fullPixels = stylizedFull.RgbaPixels.Span;
        var halfPixels = half.RgbaPixels.Span;
        for (var offset = 0; offset < fixturePixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                int low = Math.Min(fixturePixels[offset + channel], fullPixels[offset + channel]);
                int high = Math.Max(fixturePixels[offset + channel], fullPixels[offset + channel]);
                Assert(
                    halfPixels[offset + channel] >= low - 1 && halfPixels[offset + channel] <= high + 1,
                    "a half-strength painterly blend must stay between the input and the full stylization");
            }
        }

        // Style settings survive the options JSON round-trip and stay absent
        // when null so older manifests remain byte-compatible.
        var styledOptions = new UpscaleOptions(
            TexturePreset.Illustrated,
            AssetScope.WorldOnly,
            2048,
            GenerateMipMaps: true,
            InstallAfterBuild: false,
            PaintedTheme: PaintedTheme.ClassicPainted,
            PaintedStyle: style);
        var serializerOptions = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web);
        var styledJson = System.Text.Json.JsonSerializer.Serialize(styledOptions, serializerOptions);
        Assert(
            styledJson.Contains("paintedStyle", StringComparison.Ordinal)
            && styledJson.Contains("strokeSize", StringComparison.Ordinal),
            "explicit painted style settings must serialize into upscale options");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<UpscaleOptions>(styledJson, serializerOptions);
        AssertEqual(style, roundTrip!.PaintedStyle!, "painted style settings must round-trip through options JSON");
        var defaultJson = System.Text.Json.JsonSerializer.Serialize(
            styledOptions with { PaintedStyle = null },
            serializerOptions);
        Assert(
            !defaultJson.Contains("paintedStyle", StringComparison.Ordinal),
            "default painted style must stay absent from serialized options for manifest compatibility");

        var clamped = new PaintedStyleSettings(double.NaN, 4, -2, 0.5, 1).Clamped();
        AssertEqual(new PaintedStyleSettings(0.5, 1, 0, 0.5, 1), clamped, "painted style clamping");

        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        static double RgbDistance(ReadOnlySpan<byte> pixels, int offsetA, int offsetB) =>
            Math.Abs(pixels[offsetA] - pixels[offsetB])
            + Math.Abs(pixels[offsetA + 1] - pixels[offsetB + 1])
            + Math.Abs(pixels[offsetA + 2] - pixels[offsetB + 2]);
    }

    private static async Task TestWrappedTgaAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[18 + (2 * 2 * 4)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 2);
        bytes[16] = 32;
        bytes[17] = 0x28;
        for (var index = 18; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 7);
        }

        var image = TgaPixelBuffer.Read(bytes);
        var wrapped = image.AddWrappedBorder(1);
        AssertEqual(4, wrapped.Width, "wrapped width");
        AssertEqual(4, wrapped.Height, "wrapped height");
        var clamped = image.AddClampedBorder(1);
        AssertEqual(4, clamped.Width, "clamped width");
        AssertEqual(4, clamped.Height, "clamped height");
        var cropped = wrapped.Crop(1, 1, 2, 2);
        AssertEqual(2, cropped.Width, "cropped width");

        var rusticSourceBytes = new byte[18 + (2 * 2 * 4)];
        rusticSourceBytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(rusticSourceBytes.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(rusticSourceBytes.AsSpan(14), 2);
        rusticSourceBytes[16] = 32;
        rusticSourceBytes[17] = 0x28;
        var rusticColors = new (byte R, byte G, byte B, byte A)[]
        {
            (52, 118, 42, 255),
            (132, 86, 48, 197),
            (64, 82, 132, 83),
            (214, 188, 142, 0)
        };
        for (var pixel = 0; pixel < rusticColors.Length; pixel++)
        {
            var offset = 18 + (pixel * 4);
            rusticSourceBytes[offset] = rusticColors[pixel].B;
            rusticSourceBytes[offset + 1] = rusticColors[pixel].G;
            rusticSourceBytes[offset + 2] = rusticColors[pixel].R;
            rusticSourceBytes[offset + 3] = rusticColors[pixel].A;
        }

        var rusticSource = TgaPixelBuffer.Read(rusticSourceBytes);
        var graphicPainted = rusticSource.ApplyPaintedStylization(strength: 1);
        AssertSequenceEqual(
            rusticSource.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            graphicPainted.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            "graphic painted finishing must preserve alpha byte-for-byte");
        Assert(
            !rusticSource.RgbaPixels.Span.SequenceEqual(graphicPainted.RgbaPixels.Span),
            "graphic painted finishing should visibly consolidate color and value planes");
        var repeatedPainted = rusticSource.ApplyPaintedStylization(strength: 1);
        AssertSequenceEqual(
            graphicPainted.RgbaPixels.Span,
            repeatedPainted.RgbaPixels.Span,
            "painterly stylization must be deterministic for identical inputs");
        var noPaint = rusticSource.ApplyPaintedStylization(strength: 0);
        AssertSequenceEqual(
            rusticSource.RgbaPixels.Span,
            noPaint.RgbaPixels.Span,
            "zero graphic painted strength must be a deterministic no-op");

        var classicTheme = graphicPainted.ApplyPaintedTheme(PaintedTheme.ClassicPainted);
        var lightTheme = graphicPainted.ApplyPaintedTheme(PaintedTheme.LightStorybook);
        var darkTheme = graphicPainted.ApplyPaintedTheme(PaintedTheme.DarkGothic);
        var comicTheme = graphicPainted.ApplyPaintedTheme(PaintedTheme.ComicInk);
        AssertSequenceEqual(
            graphicPainted.RgbaPixels.Span,
            classicTheme.RgbaPixels.Span,
            "Classic Painted must retain the shared graphic-painted result");
        foreach (var (name, themed) in new[]
                 {
                     ("Light Storybook", lightTheme),
                     ("Dark Gothic", darkTheme),
                     ("Comic Ink", comicTheme)
                 })
        {
            AssertSequenceEqual(
                graphicPainted.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
                themed.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
                $"{name} must preserve alpha byte-for-byte");
            Assert(
                !graphicPainted.RgbaPixels.Span.SequenceEqual(themed.RgbaPixels.Span),
                $"{name} should produce a distinct bounded palette treatment");
            AssertSequenceEqual(
                graphicPainted.RgbaPixels.Span.Slice(12, 4),
                themed.RgbaPixels.Span.Slice(12, 4),
                $"{name} must not recolor hidden RGB beneath a fully transparent texel");
        }

        Assert(
            !lightTheme.RgbaPixels.Span.SequenceEqual(darkTheme.RgbaPixels.Span)
            && !lightTheme.RgbaPixels.Span.SequenceEqual(comicTheme.RgbaPixels.Span)
            && !darkTheme.RgbaPixels.Span.SequenceEqual(comicTheme.RgbaPixels.Span),
            "all painted theme outputs should be unique");
        Assert(
            lightTheme.CalculateVisibleColorStatistics().MeanLuminance
            > darkTheme.CalculateVisibleColorStatistics().MeanLuminance,
            "Light Storybook should remain visibly lighter than Dark Gothic");
        var zeroTheme = graphicPainted.ApplyPaintedTheme(
            PaintedTheme.DarkGothic,
            strength: 0);
        AssertSequenceEqual(
            graphicPainted.RgbaPixels.Span,
            zeroTheme.RgbaPixels.Span,
            "zero painted-theme strength must be a deterministic no-op");

        var rusticPainted = rusticSource.ApplyRusticPaintedGrade();
        AssertSequenceEqual(
            rusticSource.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            rusticPainted.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3).ToArray(),
            "rustic painted grading must preserve alpha byte-for-byte");
        Assert(
            !rusticSource.RgbaPixels.Span.SequenceEqual(rusticPainted.RgbaPixels.Span),
            "rustic painted grading should create a visible bounded color change");
        var rusticSourceStats = rusticSource.CalculateVisibleColorStatistics();
        var rusticPaintedStats = rusticPainted.CalculateVisibleColorStatistics();
        Assert(
            Math.Abs(rusticSourceStats.MeanLuminance - rusticPaintedStats.MeanLuminance) <= 10,
            "rustic painted grading must keep mean luminance inside its stylized safety budget");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"spintexture-tga-{Guid.NewGuid():N}.tga");
        var pngPath = Path.Combine(Path.GetTempPath(), $"spintexture-tga-{Guid.NewGuid():N}.png");
        var alphaPath = Path.Combine(Path.GetTempPath(), $"spintexture-alpha-{Guid.NewGuid():N}.tga");
        var dilationPath = Path.Combine(Path.GetTempPath(), $"spintexture-dilation-{Guid.NewGuid():N}.tga");
        try
        {
            await cropped.WriteFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            var roundTrip = await TgaPixelBuffer.ReadFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            AssertEqual(2, roundTrip.Width, "TGA round-trip width");
            AssertEqual(2, roundTrip.Height, "TGA round-trip height");

            await cropped.WritePngFileAsync(pngPath, cancellationToken).ConfigureAwait(false);
            var pngRoundTrip = await TgaPixelBuffer.ReadPngFileAsync(pngPath, cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                cropped.RgbaPixels.Span,
                pngRoundTrip.RgbaPixels.Span,
                "lossless in-process PNG bridge");

            var alphaSourceBytes = new byte[18 + (4 * 4 * 4)];
            alphaSourceBytes[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(alphaSourceBytes.AsSpan(12), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(alphaSourceBytes.AsSpan(14), 4);
            alphaSourceBytes[16] = 32;
            alphaSourceBytes[17] = 0x28;
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var offset = 18 + (((y * 4) + x) * 4);
                    alphaSourceBytes[offset + 3] = x < 2 ? byte.MinValue : byte.MaxValue;
                    if (x >= 2)
                    {
                        alphaSourceBytes[offset + 2] = 200;
                    }
                }
            }

            var binaryAlphaSource = TgaPixelBuffer.Read(alphaSourceBytes);
            Assert(binaryAlphaSource.HasLikelyCutoutAlpha(), "binary foliage alpha should be treated as a cutout");
            await binaryAlphaSource.DilateRgbIntoTransparentPixels()
                .WriteFileAsync(dilationPath, cancellationToken)
                .ConfigureAwait(false);
            var dilatedBytes = await File.ReadAllBytesAsync(dilationPath, cancellationToken).ConfigureAwait(false);
            AssertEqual((byte)200, dilatedBytes[20], "transparent cutout RGB should be edge-dilated");

            var smoothAlphaBytes = (byte[])alphaSourceBytes.Clone();
            for (var pixel = 0; pixel < 16; pixel++)
            {
                smoothAlphaBytes[18 + (pixel * 4) + 3] = (byte)(32 + (pixel * 12));
            }
            var smoothAlphaSource = TgaPixelBuffer.Read(smoothAlphaBytes);
            Assert(
                !smoothAlphaSource.HasLikelyCutoutAlpha(),
                "smooth effect alpha should not use cutout coverage correction");
            Assert(
                smoothAlphaSource.HasSoftTranslucentAlpha(),
                "smooth effect alpha should select the conservative reconstruction route");
            Assert(
                !binaryAlphaSource.HasSoftTranslucentAlpha(),
                "binary foliage alpha should not be mistaken for soft translucency");

            var alphaTargetBytes = new byte[18 + (16 * 16 * 4)];
            alphaTargetBytes[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(alphaTargetBytes.AsSpan(12), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(alphaTargetBytes.AsSpan(14), 16);
            alphaTargetBytes[16] = 32;
            alphaTargetBytes[17] = 0x28;
            var restoredAlpha = TgaPixelBuffer.Read(alphaTargetBytes)
                .WithScaledAlphaFrom(binaryAlphaSource, preserveCoverage: true);
            await restoredAlpha.WriteFileAsync(alphaPath, cancellationToken).ConfigureAwait(false);
            var restoredBytes = await File.ReadAllBytesAsync(alphaPath, cancellationToken).ConfigureAwait(false);
            var restoredAlphaValues = restoredBytes
                .AsSpan(18)
                .ToArray()
                .Where((_, index) => index % 4 == 3)
                .ToArray();
            Assert(restoredAlphaValues.Contains(byte.MinValue), "cutout alpha should retain fully transparent pixels");
            Assert(restoredAlphaValues.Contains(byte.MaxValue), "cutout alpha should retain fully opaque pixels");

            var edgeAlphaSourceBytes = new byte[18 + (2 * 4)];
            edgeAlphaSourceBytes[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(edgeAlphaSourceBytes.AsSpan(12), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(edgeAlphaSourceBytes.AsSpan(14), 1);
            edgeAlphaSourceBytes[16] = 32;
            edgeAlphaSourceBytes[17] = 0x28;
            edgeAlphaSourceBytes[18 + 3] = byte.MinValue;
            edgeAlphaSourceBytes[18 + 4 + 3] = byte.MaxValue;
            var edgeAlphaTargetBytes = new byte[18 + (4 * 4)];
            edgeAlphaTargetBytes[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(edgeAlphaTargetBytes.AsSpan(12), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(edgeAlphaTargetBytes.AsSpan(14), 1);
            edgeAlphaTargetBytes[16] = 32;
            edgeAlphaTargetBytes[17] = 0x28;
            var edgeSource = TgaPixelBuffer.Read(edgeAlphaSourceBytes);
            var edgeTarget = TgaPixelBuffer.Read(edgeAlphaTargetBytes);
            var wrappedAlpha = edgeTarget.WithScaledAlphaFrom(
                edgeSource,
                preserveCoverage: false,
                wrapEdges: true);
            var clampedAlpha = edgeTarget.WithScaledAlphaFrom(
                edgeSource,
                preserveCoverage: false,
                wrapEdges: false);
            Assert(
                wrappedAlpha.RgbaPixels.Span[3] > byte.MinValue,
                "wrapped alpha scaling should sample the opposite edge");
            AssertEqual(
                byte.MinValue,
                clampedAlpha.RgbaPixels.Span[3],
                "clamped alpha scaling must not bleed the opposite edge");
            AssertEqual(
                byte.MaxValue,
                clampedAlpha.RgbaPixels.Span[^1],
                "clamped alpha scaling should retain the final edge");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (File.Exists(pngPath))
            {
                File.Delete(pngPath);
            }

            if (File.Exists(alphaPath))
            {
                File.Delete(alphaPath);
            }

            if (File.Exists(dilationPath))
            {
                File.Delete(dilationPath);
            }
        }
    }

    private static async Task<bool> TestBuildApplyRestoreAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-transaction-{Guid.NewGuid():N}");
        var install = Path.Combine(root, "EverQuest");
        var workspace = Path.Combine(root, "SpinTextureWorkspace");
        Directory.CreateDirectory(install);
        var relativePath = "lavastorm.s3d";
        var livePath = Path.Combine(install, relativePath);
        var unchangedRelativePath = "untouched_obj.s3d";
        var unchangedLivePath = Path.Combine(install, unchangedRelativePath);
        var original = "original-zone-archive"u8.ToArray();
        var enhanced = "enhanced-zone-archive"u8.ToArray();
        var unchanged = "already-final-archive"u8.ToArray();
        await File.WriteAllBytesAsync(livePath, original, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(unchangedLivePath, unchanged, cancellationToken).ConfigureAwait(false);

        try
        {
            var paths = new ProjectPaths(install, workspace);
            var builder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(context.DestinationPath, enhanced, token));
            var unchangedBuilder = new DelegateStagedArtifactBuilder(
                (context, _) =>
                {
                    File.Copy(context.SourcePath, context.DestinationPath);
                    return Task.CompletedTask;
                });
            var build = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    UpscaleOptions.Recommended,
                    [
                        new StagedBuildItem(relativePath, builder),
                        new StagedBuildItem(unchangedRelativePath, unchangedBuilder)
                    ]),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var afterBuild = await File.ReadAllBytesAsync(livePath, cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(original, afterBuild, "build must not write live install");
            AssertEqual(1, build.Manifest.Entries.Count, "byte-identical artifacts should be omitted");
            Assert(!Directory.Exists(Path.Combine(build.BuildDirectory, "work")), "successful build work tree should be removed");

            var failurePaths = new ProjectPaths(install, Path.Combine(root, "FailedWorkspace"));
            var failingBuilder = new DelegateStagedArtifactBuilder(
                (_, _) => throw new InvalidDataException("Deliberate cleanup test failure."));
            try
            {
                await new StagedBuildService().BuildAsync(
                    new StagedBuildRequest(
                        failurePaths,
                        UpscaleOptions.Recommended,
                        [new StagedBuildItem(relativePath, failingBuilder)]),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Self-test failed: deliberate staged build failure did not throw.");
            }
            catch (InvalidDataException)
            {
                Assert(
                    !Directory.EnumerateDirectories(failurePaths.StagingPath).Any(),
                    "failed staged build directory should be removed");
            }

            var productionTransactions = new InstallTransactionService();
            if (EverQuestInstall.IsGameOrLauncherRunning())
            {
                var safetyGateBlockedApply = false;
                try
                {
                    await productionTransactions.ApplyAsync(
                        paths,
                        build.ManifestPath,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    safetyGateBlockedApply = true;
                }

                Assert(
                    safetyGateBlockedApply,
                    "running-game safety gate should block the production apply service");
            }

            var transactions = new InstallTransactionService(
                manifestStore: null,
                atomicFileOperations: new AtomicFileOperations(),
                ensureGameStopped: _ => { });

            const string failedApplyId = "apply-post-commit-failure";
            var applyFailureOperations = new PostCommitFailingAtomicFileOperations(livePath);
            var failingApplyTransactions = new InstallTransactionService(
                manifestStore: null,
                atomicFileOperations: applyFailureOperations,
                ensureGameStopped: _ => { });
            try
            {
                await failingApplyTransactions.ApplyAsync(
                    paths,
                    build.ManifestPath,
                    cancellationToken: cancellationToken,
                    applyId: failedApplyId).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Self-test failed: deliberate post-commit apply failure did not throw.");
            }
            catch (IOException exception) when (
                exception.Message.Contains("post-commit", StringComparison.OrdinalIgnoreCase))
            {
                Assert(
                    applyFailureOperations.FailureInjected,
                    "apply test should inject one failure after the live-file commit");
            }

            var afterFailedApply = await File.ReadAllBytesAsync(livePath, cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                original,
                afterFailedApply,
                "post-commit apply failure should roll back the just-committed artifact");
            var failedApplyManifestPath = Path.Combine(
                paths.BackupPath,
                failedApplyId,
                "install-manifest.json");
            var failedApplyManifest = await new ManifestStore()
                .ReadInstallManifestAsync(failedApplyManifestPath, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.RolledBack,
                failedApplyManifest.State,
                "successful post-commit apply rollback manifest state");

            var applied = await transactions.ApplyAsync(
                paths,
                build.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var afterApply = await File.ReadAllBytesAsync(livePath, cancellationToken).ConfigureAwait(false);
            var unchangedAfterApply = await File.ReadAllBytesAsync(
                unchangedLivePath,
                cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(enhanced, afterApply, "apply should install staged artifact");
            AssertSequenceEqual(
                unchanged,
                unchangedAfterApply,
                "byte-identical artifact should never be installed");
            Assert(
                applied.Manifest.Entries.All(entry =>
                    entry.InstalledLastWriteTimeUtcTicks is > 0),
                "applied manifest should cache verified installed-file timestamps for fast play checks");
            AssertEqual(
                File.GetLastWriteTimeUtc(livePath).Ticks,
                applied.Manifest.Entries.Single().InstalledLastWriteTimeUtcTicks!.Value,
                "applied manifest installed-file timestamp");

            var restoreFailureOperations = new PostCommitFailingAtomicFileOperations(livePath);
            var failingRestoreTransactions = new InstallTransactionService(
                manifestStore: null,
                atomicFileOperations: restoreFailureOperations,
                ensureGameStopped: _ => { });
            try
            {
                await failingRestoreTransactions.RestoreAsync(
                    paths,
                    applied.InstallManifestPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Self-test failed: deliberate post-commit restore failure did not throw.");
            }
            catch (IOException exception) when (
                exception.Message.Contains("post-commit", StringComparison.OrdinalIgnoreCase))
            {
                Assert(
                    restoreFailureOperations.FailureInjected,
                    "restore test should inject one failure after the live-file commit");
            }

            var afterFailedRestore = await File.ReadAllBytesAsync(livePath, cancellationToken)
                .ConfigureAwait(false);
            AssertSequenceEqual(
                enhanced,
                afterFailedRestore,
                "post-commit restore failure should reinstate the enhanced artifact");
            var afterFailedRestoreManifest = await new ManifestStore()
                .ReadInstallManifestAsync(applied.InstallManifestPath, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                InstallTransactionState.Applied,
                afterFailedRestoreManifest.State,
                "failed restore should keep its applied manifest resumable");

            var appliedBackupDirectory = Path.GetDirectoryName(applied.InstallManifestPath)!;
            var safetyDirectoriesBeforeSuccessfulRestore = Directory
                .EnumerateDirectories(
                    appliedBackupDirectory,
                    "restore-safety-*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await transactions.RestoreAsync(
                paths,
                applied.InstallManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var safetyDirectoriesAfterSuccessfulRestore = Directory
                .EnumerateDirectories(
                    appliedBackupDirectory,
                    "restore-safety-*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert(
                safetyDirectoriesAfterSuccessfulRestore.SetEquals(
                    safetyDirectoriesBeforeSuccessfulRestore),
                "successful restore should remove only its newly created safety snapshot");
            var afterRestore = await File.ReadAllBytesAsync(livePath, cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(original, afterRestore, "restore should recover exact original");
            return true;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestNativeProcessorSmokeAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-native-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "lavastorm-rock.tga");
        var destinationPath = Path.Combine(root, "result", "lavastorm-rock-x2.tga");
        var tripleDestinationPath = Path.Combine(root, "result", "lavastorm-rock-x3.tga");
        var batchDestinationPathOne = Path.Combine(root, "result", "lavastorm-rock-batch-1.tga");
        var batchDestinationPathTwo = Path.Combine(root, "result", "lavastorm-rock-batch-2.tga");
        var fallbackDestinationPathOne = Path.Combine(root, "result", "lavastorm-rock-fallback-1.tga");
        var fallbackDestinationPathTwo = Path.Combine(root, "result", "lavastorm-rock-fallback-2.tga");
        var missingOutputDestinationPathOne = Path.Combine(root, "result", "lavastorm-rock-missing-output-1.tga");
        var missingOutputDestinationPathTwo = Path.Combine(root, "result", "lavastorm-rock-missing-output-2.tga");
        var classicDestinationPath = Path.Combine(root, "result", "lavastorm-rock-classic.tga");
        var illustratedDestinationPath = Path.Combine(root, "result", "lavastorm-rock-illustrated.tga");
        var patternedSourcePath = Path.Combine(root, "patterned-rock.tga");
        var cutoutSourcePath = Path.Combine(root, "foliage-card.tga");
        var cutoutDestinationPath = Path.Combine(root, "result", "foliage-card-illustrated.tga");
        var legacyPaintedRecoveryPath = Path.Combine(root, "result", "painted-legacy-recovery.tga");
        var conservativePaintedRecoveryPath = Path.Combine(root, "result", "painted-conservative-recovery.tga");
        var rejectedPaintedCandidatePath = Path.Combine(root, "result", "painted-rejected-candidate.tga");
        var rusticDestinationPath = Path.Combine(root, "result", "lavastorm-rock-rustic-painted.tga");
        // Real profiles can already be deeply nested before per-build and
        // per-texture names are appended. Native processing must isolate its
        // intermediates from those legacy Win32 path-length limits.
        var workPath = Enumerable.Range(0, 12).Aggregate(
            Path.Combine(root, "work"),
            (path, index) => Path.Combine(path, $"deep-staging-segment-{index:D2}"));
        Assert(workPath.Length > 260, "native smoke test should exercise a legacy-long staging path");
        Directory.CreateDirectory(root);

        try
        {
            var source = new byte[18 + (16 * 16 * 4)];
            source[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(12), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(14), 16);
            source[16] = 32;
            source[17] = 0x28;
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var index = 18 + ((y * 16) + x) * 4;
                    // A neutral material swatch keeps this a command/container
                    // smoke test. Adversarial 1px patterns are intentionally
                    // rejected by the production round-trip fidelity gate.
                    source[index] = 128;
                    source[index + 1] = 128;
                    source[index + 2] = 128;
                    source[index + 3] = byte.MaxValue;
                }
            }

            await File.WriteAllBytesAsync(sourcePath, source, cancellationToken).ConfigureAwait(false);
            var cutoutSource = source.ToArray();
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var index = 18 + ((y * 16) + x) * 4;
                    cutoutSource[index] = (byte)(52 + ((x * 7 + y * 3) % 44));
                    cutoutSource[index + 1] = (byte)(104 + ((x * 5 + y * 9) % 72));
                    cutoutSource[index + 2] = (byte)(38 + ((x * 11 + y * 2) % 38));
                    cutoutSource[index + 3] = x is >= 2 and <= 13
                        && y is >= 2 and <= 13
                        && ((x + y) % 5 != 0)
                        ? byte.MaxValue
                        : byte.MinValue;
                }
            }

            var patternedSource = cutoutSource.ToArray();
            for (var offset = 18; offset < patternedSource.Length; offset += 4)
            {
                patternedSource[offset + 3] = byte.MaxValue;
            }

            await File.WriteAllBytesAsync(
                patternedSourcePath,
                patternedSource,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                cutoutSourcePath,
                cutoutSource,
                cancellationToken).ConfigureAwait(false);
            var metadata = new TextureMetadata(
                TextureFileFormat.Tga,
                "TGA/TRUE-COLOR/32bpp",
                16,
                16,
                1,
                32,
                TextureAlphaStatus.None,
                IsCompressed: false,
                IsTopDown: true,
                IsCubeMap: false,
                IsVolumeTexture: false,
                ArraySize: 1,
                TexconvFormat: "B8G8R8X8_UNORM");
            var classification = new TextureSemanticClassifier().Classify("lavastorm-rock.tga", metadata);
            var paths = new ProjectPaths(root, Path.Combine(root, "workspace"));
            var tools = new ToolchainDiscovery().Discover(paths);
            Assert(tools.IsReady, string.Join(" ", tools.Diagnostics));
            var recordingRunner = new RecordingNativeProcessRunner();
            var processor = new NativeTextureProcessor(tools, recordingRunner);
            var commandStart = recordingRunner.RealEsrganScales.Count;
            var result = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    destinationPath,
                    workPath,
                    metadata,
                    classification,
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.SelectedZone,
                        MaximumDimension: 32,
                        GenerateMipMaps: false,
                        InstallAfterBuild: false,
                        SelectedZone: "lavastorm"),
                    WrapPadding: 4),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(32, result.Dimensions.OutputWidth, "native 2x smoke target width");
            var twoXScales = recordingRunner.RealEsrganScales.Skip(commandStart).ToArray();
            AssertEqual(1, twoXScales.Length, "native 2x smoke Real-ESRGAN pass count");
            AssertEqual(2, twoXScales[0], "native 2x smoke Real-ESRGAN scale");

            var outputMetadata = await new TextureHeaderSniffer()
                .ReadFileAsync(destinationPath, cancellationToken)
                .ConfigureAwait(false);
            Assert(outputMetadata is not null, "native 2x smoke output should be a readable TGA");
            AssertEqual(32, outputMetadata!.Width, "native 2x smoke output width");
            AssertEqual(32, outputMetadata.Height, "native 2x smoke output height");

            commandStart = recordingRunner.RealEsrganScales.Count;
            var tripleResult = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    tripleDestinationPath,
                    workPath,
                    metadata,
                    classification,
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.SelectedZone,
                        MaximumDimension: 48,
                        GenerateMipMaps: false,
                        InstallAfterBuild: false,
                        SelectedZone: "lavastorm"),
                    WrapPadding: 4),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(48, tripleResult.Dimensions.OutputWidth, "native 3x smoke target width");
            var threeXScales = recordingRunner.RealEsrganScales.Skip(commandStart).ToArray();
            AssertEqual(1, threeXScales.Length, "native 3x smoke Real-ESRGAN pass count");
            AssertEqual(3, threeXScales[0], "native 3x smoke Real-ESRGAN scale");
            var tripleOutputMetadata = await new TextureHeaderSniffer()
                .ReadFileAsync(tripleDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            Assert(tripleOutputMetadata is not null, "native 3x smoke output should be a readable TGA");
            AssertEqual(48, tripleOutputMetadata!.Width, "native 3x smoke output width");
            AssertEqual(48, tripleOutputMetadata.Height, "native 3x smoke output height");

            commandStart = recordingRunner.RealEsrganScales.Count;
            var batchOutcomes = await processor.ProcessBatchAsync(
                [
                    new NativeTextureProcessRequest(
                        sourcePath,
                        batchDestinationPathOne,
                        workPath,
                        metadata,
                        classification,
                        new UpscaleOptions(
                            TexturePreset.Faithful,
                            AssetScope.SelectedZone,
                            MaximumDimension: 32,
                            GenerateMipMaps: false,
                            InstallAfterBuild: false,
                            SelectedZone: "lavastorm"),
                        WrapPadding: 4),
                    new NativeTextureProcessRequest(
                        sourcePath,
                        batchDestinationPathTwo,
                        workPath,
                        metadata,
                        classification,
                        new UpscaleOptions(
                            TexturePreset.Faithful,
                            AssetScope.SelectedZone,
                            MaximumDimension: 32,
                            GenerateMipMaps: false,
                            InstallAfterBuild: false,
                            SelectedZone: "lavastorm"),
                        WrapPadding: 4)
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(2, batchOutcomes.Count, "native batch outcome count");
            Assert(batchOutcomes.All(outcome => outcome.Succeeded), "native batch should complete every item");
            var batchScales = recordingRunner.RealEsrganScales.Skip(commandStart).ToArray();
            AssertEqual(1, batchScales.Length, "compatible textures should share one neural worker launch");
            AssertEqual(2, batchScales[0], "native batch Real-ESRGAN scale");
            Assert(File.Exists(batchDestinationPathOne), "native batch first output");
            Assert(File.Exists(batchDestinationPathTwo), "native batch second output");

            var fallbackRunner = new FidelityFallbackNativeProcessRunner();
            var fallbackProcessor = new NativeTextureProcessor(tools, fallbackRunner);
            var fallbackOutcomes = await fallbackProcessor.ProcessBatchAsync(
                [
                    new NativeTextureProcessRequest(
                        sourcePath,
                        fallbackDestinationPathOne,
                        workPath,
                        metadata,
                        classification,
                        new UpscaleOptions(
                            TexturePreset.MaximumDetail,
                            AssetScope.CharactersAndEquipmentOnly,
                            MaximumDimension: 32,
                            GenerateMipMaps: false,
                            InstallAfterBuild: false,
                            SelectedZone: null),
                        WrapPadding: 4,
                        WrapEdges: false),
                    new NativeTextureProcessRequest(
                        sourcePath,
                        fallbackDestinationPathTwo,
                        workPath,
                        metadata,
                        classification,
                        new UpscaleOptions(
                            TexturePreset.MaximumDetail,
                            AssetScope.CharactersAndEquipmentOnly,
                            MaximumDimension: 32,
                            GenerateMipMaps: false,
                            InstallAfterBuild: false,
                            SelectedZone: null),
                        WrapPadding: 4,
                        WrapEdges: false)
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Assert(
                fallbackOutcomes.All(outcome => outcome.Succeeded),
                "fidelity-rejected Maximum Detail textures should succeed through Faithful fallback");
            AssertEqual(
                1,
                fallbackRunner.DetailBatchInvocations,
                "compatible Maximum Detail textures should share one primary worker launch");
            AssertEqual(
                1,
                fallbackRunner.FaithfulBatchInvocations,
                "fidelity retries should be collected into one Faithful worker launch");
            Assert(
                fallbackOutcomes.All(outcome => outcome.Result!.ProcessingRoute.Contains(
                    "fidelity fallback",
                    StringComparison.OrdinalIgnoreCase)),
                "batched retry route should be visible in every outcome");

            var missingOutputRunner = new MissingPrimaryOutputFallbackNativeProcessRunner();
            var missingOutputProcessor = new NativeTextureProcessor(tools, missingOutputRunner);
            var missingOutputOutcomes = await missingOutputProcessor.ProcessBatchAsync(
                [
                    new NativeTextureProcessRequest(
                        sourcePath,
                        missingOutputDestinationPathOne,
                        workPath,
                        metadata,
                        classification,
                        new UpscaleOptions(
                            TexturePreset.MaximumDetail,
                            AssetScope.CharactersAndEquipmentOnly,
                            MaximumDimension: 32,
                            GenerateMipMaps: false,
                            InstallAfterBuild: false,
                            SelectedZone: null),
                        WrapPadding: 4,
                        WrapEdges: false),
                    new NativeTextureProcessRequest(
                        sourcePath,
                        missingOutputDestinationPathTwo,
                        workPath,
                        metadata,
                        classification,
                        new UpscaleOptions(
                            TexturePreset.MaximumDetail,
                            AssetScope.CharactersAndEquipmentOnly,
                            MaximumDimension: 32,
                            GenerateMipMaps: false,
                            InstallAfterBuild: false,
                            SelectedZone: null),
                        WrapPadding: 4,
                        WrapEdges: false)
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Assert(
                missingOutputOutcomes.All(outcome => outcome.Succeeded),
                "a structurally invalid Maximum Detail output set should recover through Faithful fallback");
            AssertEqual(
                1,
                missingOutputRunner.DetailBatchInvocations,
                "an invalid primary output set should not recursively relaunch the detail worker");
            AssertEqual(
                1,
                missingOutputRunner.FaithfulBatchInvocations,
                "the structurally invalid primary batch should retry once as a compatible Faithful batch");
            Assert(
                missingOutputOutcomes.All(outcome => outcome.Result!.ProcessingRoute.Contains(
                    "fidelity fallback",
                    StringComparison.OrdinalIgnoreCase)),
                "structural primary-output fallback should remain visible in every outcome");

            commandStart = recordingRunner.RealEsrganScales.Count;
            var textureCommandStart = recordingRunner.UpscaylScales.Count;
            var classicResult = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    classicDestinationPath,
                    workPath,
                    metadata,
                    classification,
                    new UpscaleOptions(
                        TexturePreset.ClassicHd,
                        AssetScope.SelectedZone,
                        MaximumDimension: 64,
                        GenerateMipMaps: false,
                        InstallAfterBuild: false,
                        SelectedZone: "lavastorm"),
                    WrapPadding: 4),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(64, classicResult.Dimensions.OutputWidth, "Classic HD smoke target width");
            var classicScales = recordingRunner.RealEsrganScales.Skip(commandStart).ToArray();
            var textureScales = recordingRunner.UpscaylScales.Skip(textureCommandStart).ToArray();
            if (tools.HasTextureUpscaler)
            {
                AssertEqual(0, classicScales.Length, "Texture HD should bypass the legacy Real-ESRGAN worker");
                AssertEqual(1, textureScales.Length, "Texture HD specialized worker pass count");
                AssertEqual(4, textureScales[0], "Texture HD SPAN model must run at its native scale");
                Assert(
                    classicResult.ProcessingRoute.Contains("PBRify", StringComparison.OrdinalIgnoreCase),
                    "Texture HD route should report the specialized PBRify model");
            }
            else
            {
                AssertEqual(1, classicScales.Length, "Texture HD fallback Real-ESRGAN pass count");
                AssertEqual(4, classicScales[0], "Texture HD fallback Real-ESRGAN scale");
                AssertEqual(0, textureScales.Length, "Texture HD fallback should not invoke Upscayl");
            }
            var classicOutputMetadata = await new TextureHeaderSniffer()
                .ReadFileAsync(classicDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            Assert(classicOutputMetadata is not null, "Classic HD output should be a readable TGA");
            AssertEqual(64, classicOutputMetadata!.Width, "Classic HD smoke output width");

            commandStart = recordingRunner.RealEsrganScales.Count;
            textureCommandStart = recordingRunner.UpscaylScales.Count;
            var illustratedResult = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    illustratedDestinationPath,
                    workPath,
                    metadata,
                    classification,
                    new UpscaleOptions(
                        TexturePreset.Illustrated,
                        AssetScope.SelectedZone,
                        MaximumDimension: 64,
                        GenerateMipMaps: false,
                        InstallAfterBuild: false,
                        SelectedZone: "lavastorm",
                        PaintedTheme: PaintedTheme.DarkGothic),
                    WrapPadding: 4),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(64, illustratedResult.Dimensions.OutputWidth, "Illustrated smoke target width");
            var illustratedLegacyScales = recordingRunner.RealEsrganScales
                .Skip(commandStart)
                .ToArray();
            var illustratedTextureScales = recordingRunner.UpscaylScales
                .Skip(textureCommandStart)
                .ToArray();
            if (tools.HasTextureUpscaler)
            {
                AssertEqual(
                    0,
                    illustratedLegacyScales.Length,
                    "Graphic Painted should not start with the legacy anime model when PBRify is available");
                AssertEqual(
                    1,
                    illustratedTextureScales.Length,
                    "Graphic Painted should use one PBRify reconstruction pass");
                Assert(
                    illustratedResult.ProcessingRoute.Contains(
                        "PBRify",
                        StringComparison.OrdinalIgnoreCase),
                    "Graphic Painted should report its PBRify reconstruction base");
            }
            else
            {
                AssertEqual(
                    1,
                    illustratedLegacyScales.Length,
                    "Graphic Painted should retain one bundled illustrated recovery pass when PBRify is unavailable");
                AssertEqual(
                    0,
                    illustratedTextureScales.Length,
                    "Graphic Painted must not report an unavailable PBRify pass");
            }
            Assert(
                illustratedResult.ProcessingRoute.Contains(
                    "illustrated",
                    StringComparison.OrdinalIgnoreCase),
                "Illustrated smoke should exercise the bundled illustrated model without an unreported route change");
            Assert(
                illustratedResult.ProcessingRoute.Contains(
                    "dark gothic",
                    StringComparison.OrdinalIgnoreCase),
                "Illustrated smoke should report the concrete painted theme that actually ran");
            var illustratedMetadata = await new TextureHeaderSniffer()
                .ReadFileAsync(illustratedDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            Assert(illustratedMetadata is not null, "Illustrated output should be a readable TGA");
            AssertEqual(64, illustratedMetadata!.Width, "Illustrated smoke output width");

            var cutoutMetadata = metadata with
            {
                AlphaStatus = TextureAlphaStatus.Explicit,
                TexconvFormat = "B8G8R8A8_UNORM"
            };
            // Some old DDS payloads have generic names and classify as Color,
            // while runtime alpha inspection correctly identifies their binary
            // foliage cards. Exercise that exact safety path.
            var ambiguousCutoutClassification = classification with
            {
                Kind = TextureKind.Color,
                RequiresSeparateAlphaHandling = true
            };
            commandStart = recordingRunner.RealEsrganScales.Count;
            textureCommandStart = recordingRunner.UpscaylScales.Count;
            var cutoutResult = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    cutoutSourcePath,
                    cutoutDestinationPath,
                    workPath,
                    cutoutMetadata,
                    ambiguousCutoutClassification,
                    new UpscaleOptions(
                        TexturePreset.Illustrated,
                        AssetScope.SelectedZone,
                        MaximumDimension: 64,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "lavastorm",
                        PaintedTheme: PaintedTheme.ClassicPainted),
                    WrapPadding: 4),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(64, cutoutResult.Dimensions.OutputWidth, "Illustrated foliage target width");
            AssertEqual(
                1,
                cutoutResult.ExpectedMipCount,
                "runtime-detected binary-alpha foliage must retain the exact single-top-level cutout policy");
            Assert(
                cutoutResult.ProcessingRoute.Contains(
                    "illustrated",
                    StringComparison.OrdinalIgnoreCase)
                && cutoutResult.ProcessingRoute.Contains(
                    "finishing",
                    StringComparison.OrdinalIgnoreCase),
                "runtime-detected foliage should remain on a truthful painted route");
            if (tools.HasTextureUpscaler)
            {
                AssertEqual(
                    0,
                    recordingRunner.RealEsrganScales.Skip(commandStart).Count(),
                    "Illustrated foliage should retain the PBRify reconstruction when it passes validation");
                AssertEqual(
                    1,
                    recordingRunner.UpscaylScales.Skip(textureCommandStart).Count(),
                    "Illustrated foliage should use one PBRify reconstruction pass");
            }

            var cutoutPixels = await TgaPixelBuffer
                .ReadFileAsync(cutoutDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            var cutoutAlpha = cutoutPixels.RgbaPixels.Span
                .ToArray()
                .Where((_, index) => index % 4 == 3)
                .ToArray();
            var sourceCutoutAlpha = cutoutSource
                .Skip(18)
                .Where((_, index) => index % 4 == 3)
                .ToArray();
            var sourceCoverage = sourceCutoutAlpha.Count(alpha => alpha >= 128)
                / (double)sourceCutoutAlpha.Length;
            var outputCoverage = cutoutAlpha.Count(alpha => alpha >= 128)
                / (double)cutoutAlpha.Length;
            Assert(
                cutoutAlpha.Any(alpha => alpha == byte.MinValue)
                && cutoutAlpha.Any(alpha => alpha == byte.MaxValue)
                && Math.Abs(outputCoverage - sourceCoverage) <= 0.01,
                "Illustrated foliage must preserve its alpha-tested silhouette coverage after final encoding");

            if (tools.HasTextureUpscaler)
            {
                NativeTextureProcessRequest CreatePaintedRequest(
                    string inputPath,
                    string outputPath,
                    TextureMetadata requestMetadata,
                    TextureClassification requestClassification,
                    bool generateMipMaps = false) => new(
                        inputPath,
                        outputPath,
                        workPath,
                        requestMetadata,
                        requestClassification,
                        new UpscaleOptions(
                            TexturePreset.Illustrated,
                            AssetScope.SelectedZone,
                            MaximumDimension: 64,
                            GenerateMipMaps: generateMipMaps,
                            InstallAfterBuild: false,
                            SelectedZone: "lavastorm",
                            PaintedTheme: PaintedTheme.ClassicPainted),
                        WrapPadding: 4);

                var legacyRecoveryRunner = new PaintedRecoveryNativeProcessRunner(
                    corruptSpecialized: true,
                    corruptLegacyIllustrated: false,
                    corruptFaithful: false,
                    corruptFinalValidation: false);
                var legacyRecoveryProcessor = new NativeTextureProcessor(
                    tools,
                    legacyRecoveryRunner);
                var legacyRecovery = await legacyRecoveryProcessor.ProcessAsync(
                    CreatePaintedRequest(
                        patternedSourcePath,
                        legacyPaintedRecoveryPath,
                        metadata,
                        classification),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                AssertEqual(
                    1,
                    legacyRecoveryRunner.SpecializedInvocations,
                    "a rejected PBRify result should run the specialized worker once");
                AssertEqual(
                    1,
                    legacyRecoveryRunner.LegacyIllustratedInvocations,
                    "a rejected PBRify result should retry once with the bundled illustrated model");
                AssertEqual(
                    0,
                    legacyRecoveryRunner.FaithfulInvocations,
                    "a safe legacy illustrated recovery should not fall through to the faithful base");
                Assert(
                    legacyRecovery.ProcessingRoute.Contains(
                        "legacy illustrated reconstruction",
                        StringComparison.OrdinalIgnoreCase)
                    && legacyRecovery.ProcessingRoute.Contains(
                        "finishing",
                        StringComparison.OrdinalIgnoreCase)
                    && !legacyRecovery.ProcessingRoute.Contains(
                        "fallback",
                        StringComparison.OrdinalIgnoreCase),
                    "legacy recovery must retain a truthful requested-profile route that repair accepts");

                var conservativeRecoveryRunner = new PaintedRecoveryNativeProcessRunner(
                    corruptSpecialized: true,
                    corruptLegacyIllustrated: true,
                    corruptFaithful: false,
                    corruptFinalValidation: false);
                var conservativeRecoveryProcessor = new NativeTextureProcessor(
                    tools,
                    conservativeRecoveryRunner);
                var conservativeRecovery = await conservativeRecoveryProcessor.ProcessAsync(
                    CreatePaintedRequest(
                        patternedSourcePath,
                        conservativePaintedRecoveryPath,
                        metadata,
                        classification),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                AssertEqual(
                    1,
                    conservativeRecoveryRunner.SpecializedInvocations,
                    "conservative painted recovery should begin with one PBRify attempt");
                AssertEqual(
                    1,
                    conservativeRecoveryRunner.LegacyIllustratedInvocations,
                    "conservative painted recovery should try the legacy illustrated base once");
                AssertEqual(
                    1,
                    conservativeRecoveryRunner.FaithfulInvocations,
                    "conservative painted recovery should terminate with one faithful reconstruction base");
                Assert(
                    conservativeRecovery.ProcessingRoute.Contains(
                        "conservative illustrated recovery",
                        StringComparison.OrdinalIgnoreCase)
                    && conservativeRecovery.ProcessingRoute.Contains(
                        RealEsrganCommandBuilder.LegacyFaithfulModelName,
                        StringComparison.OrdinalIgnoreCase)
                    && conservativeRecovery.ProcessingRoute.Contains(
                        "finishing",
                        StringComparison.OrdinalIgnoreCase)
                    && !conservativeRecovery.ProcessingRoute.Contains(
                        "fallback",
                        StringComparison.OrdinalIgnoreCase),
                    "faithful-base recovery must still be styled and reported as the requested Illustrated profile");

                var rejectedCandidateRunner = new PaintedRecoveryNativeProcessRunner(
                    corruptSpecialized: false,
                    corruptLegacyIllustrated: false,
                    corruptFaithful: false,
                    corruptFinalValidation: true);
                var rejectedCandidateProcessor = new NativeTextureProcessor(
                    tools,
                    rejectedCandidateRunner);
                var rejectedOutcomes = await rejectedCandidateProcessor.ProcessBatchAsync(
                    [
                        CreatePaintedRequest(
                            cutoutSourcePath,
                            rejectedPaintedCandidatePath,
                            cutoutMetadata,
                            ambiguousCutoutClassification,
                            generateMipMaps: true)
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                AssertEqual(1, rejectedOutcomes.Count, "painted rejection outcome count");
                Assert(
                    !rejectedOutcomes[0].Succeeded
                    && rejectedOutcomes[0].Error is InvalidDataException,
                    "all painted bases and adaptive strengths failing final validation must return an error");
                AssertEqual(
                    1,
                    rejectedCandidateRunner.SpecializedInvocations,
                    "final encoded rejection should try PBRify exactly once");
                AssertEqual(
                    1,
                    rejectedCandidateRunner.LegacyIllustratedInvocations,
                    "final encoded rejection should try the legacy illustrated base exactly once");
                AssertEqual(
                    1,
                    rejectedCandidateRunner.FaithfulInvocations,
                    "final encoded rejection should terminate after one conservative faithful-base attempt");
                Assert(
                    rejectedCandidateRunner.FinalValidationInvocations >= 4,
                    "the first safe reconstruction base should exhaust all four bounded finish strengths before failing closed "
                    + $"(observed {rejectedCandidateRunner.FinalValidationInvocations} final validation attempts)");
                Assert(
                    !File.Exists(rejectedPaintedCandidatePath),
                    "a candidate rejected after final encoding must never be published to the requested destination");
            }

            commandStart = recordingRunner.RealEsrganScales.Count;
            textureCommandStart = recordingRunner.UpscaylScales.Count;
            var rusticResult = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    rusticDestinationPath,
                    workPath,
                    metadata,
                    classification,
                    new UpscaleOptions(
                        TexturePreset.RusticPainted,
                        AssetScope.SelectedZone,
                        MaximumDimension: 64,
                        GenerateMipMaps: false,
                        InstallAfterBuild: false,
                        SelectedZone: "lavastorm"),
                    WrapPadding: 4),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(64, rusticResult.Dimensions.OutputWidth, "Rustic Painted smoke target width");
            AssertEqual(
                1,
                recordingRunner.RealEsrganScales.Skip(commandStart).Count(),
                "Rustic Painted revision one should retain its legacy reconstruction model");
            AssertEqual(
                0,
                recordingRunner.UpscaylScales.Skip(textureCommandStart).Count(),
                "Graphic Painted's PBRify change must not alter Rustic Painted revision one");
            Assert(
                rusticResult.ProcessingRoute.Contains(
                    "graphic painted",
                    StringComparison.OrdinalIgnoreCase)
                && rusticResult.ProcessingRoute.Contains(
                    "rustic",
                    StringComparison.OrdinalIgnoreCase),
                "Rustic Painted smoke should exercise the graphic-painted model route plus bounded rustic grading");
            var rusticMetadata = await new TextureHeaderSniffer()
                .ReadFileAsync(rusticDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            Assert(rusticMetadata is not null, "Rustic Painted output should be a readable TGA");
            AssertEqual(64, rusticMetadata!.Width, "Rustic Painted smoke output width");
            var illustratedPixels = await TgaPixelBuffer
                .ReadFileAsync(illustratedDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            var classicPixels = await TgaPixelBuffer
                .ReadFileAsync(classicDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            var rusticPixels = await TgaPixelBuffer
                .ReadFileAsync(rusticDestinationPath, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                !classicPixels.RgbaPixels.Span.SequenceEqual(
                    illustratedPixels.RgbaPixels.Span),
                "Graphic Painted must apply a visible illustrated finish instead of duplicating Texture HD's PBRify pixels");
            Assert(
                !illustratedPixels.RgbaPixels.Span.SequenceEqual(rusticPixels.RgbaPixels.Span),
                "Rustic Painted output should be visibly distinct from ungraded Illustrated output");
            Assert(
                rusticPixels.RgbaPixels.Span.ToArray().Where((_, index) => index % 4 == 3)
                    .All(alpha => alpha == byte.MaxValue),
                "Rustic Painted native route should retain fully opaque alpha exactly");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task TestRealArchiveTextureAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var originalArchiveHash = Convert.ToHexStringLower(
            await System.Security.Cryptography.SHA256.HashDataAsync(
                new MemoryStream(await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false));
        await using var archive = await PfsArchive.OpenAsync(
            archivePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var sniffer = new TextureHeaderSniffer();
        PfsArchiveEntry? selected = null;
        TextureMetadata? metadata = null;

        foreach (var candidate in archive.Entries.Where(entry => entry.Content.Kind == PfsContentKind.Dds))
        {
            var payload = await archive.ReadEntryAsync(candidate.Name, cancellationToken).ConfigureAwait(false);
            if (sniffer.TryRead(payload, out var candidateMetadata)
                && candidateMetadata is not null
                && candidateMetadata.TexconvFormat == "BC1_UNORM")
            {
                selected = candidate;
                metadata = candidateMetadata;
                break;
            }
        }

        Assert(selected is not null && metadata is not null, "real archive should contain a BC1/DXT1 texture");
        var root = Path.Combine(Path.GetTempPath(), $"spintexture-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var sourcePath = Path.Combine(root, "source.dds");
            var outputPath = Path.Combine(root, "output.dds");
            await File.WriteAllBytesAsync(
                sourcePath,
                await archive.ReadEntryAsync(selected!.Name, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            var classification = new TextureSemanticClassifier().Classify(selected.Name, metadata!);
            Assert(classification.CanUseColorUpscaler, "selected real texture should use the color route");
            var maximumDimension = Math.Min(
                2048,
                checked(Math.Max(metadata!.Width, metadata.Height) * RealEsrganCommandBuilder.ModelScale));
            var tools = new ToolchainDiscovery().Discover(
                new ProjectPaths(Path.GetDirectoryName(archivePath)!, Path.Combine(root, "workspace")));
            var processor = new NativeTextureProcessor(tools);
            var result = await processor.ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    outputPath,
                    Path.Combine(root, "work"),
                    metadata,
                    classification,
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        AssetScope.SelectedZone,
                        maximumDimension,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: Path.GetFileNameWithoutExtension(archivePath))),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var outputMetadata = await sniffer.ReadFileAsync(outputPath, cancellationToken).ConfigureAwait(false);
            Assert(outputMetadata is not null, "real DXT1 output should be readable");
            AssertEqual("BC1_UNORM", outputMetadata!.TexconvFormat!, "real DXT1 output compression");
            AssertEqual(result.Dimensions.OutputWidth, outputMetadata.Width, "real DXT1 output width");
            Assert(outputMetadata.MipCount > 1, "real DXT1 output should have a mip chain");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }

        var archiveHashAfter = Convert.ToHexStringLower(
            await System.Security.Cryptography.SHA256.HashDataAsync(
                new MemoryStream(await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false));
        AssertEqual(originalArchiveHash, archiveHashAfter, "real archive must remain byte-identical");
    }

    private sealed class RecordingNativeProcessRunner : INativeProcessRunner
    {
        private readonly NativeProcessRunner inner = new();
        private readonly List<NativeProcessCommand> commands = [];

        public IReadOnlyList<int> RealEsrganScales => Snapshot()
            .Where(command => Path.GetFileName(command.ExecutablePath).Equals(
                "realesrgan-ncnn-vulkan.exe",
                StringComparison.OrdinalIgnoreCase))
            .Select(command =>
            {
                var scaleIndex = command.Arguments.ToList().IndexOf("-s");
                Assert(scaleIndex >= 0 && scaleIndex + 1 < command.Arguments.Count, "Real-ESRGAN command should include a scale");
                return int.Parse(
                    command.Arguments[scaleIndex + 1],
                    System.Globalization.CultureInfo.InvariantCulture);
            })
            .ToArray();

        public IReadOnlyList<int> UpscaylScales => Snapshot()
            .Where(command => Path.GetFileName(command.ExecutablePath).Equals(
                "upscayl-bin.exe",
                StringComparison.OrdinalIgnoreCase))
            .Select(command =>
            {
                var scaleIndex = command.Arguments.ToList().IndexOf("-s");
                Assert(scaleIndex >= 0 && scaleIndex + 1 < command.Arguments.Count, "Upscayl command should include a scale");
                return int.Parse(
                    command.Arguments[scaleIndex + 1],
                    System.Globalization.CultureInfo.InvariantCulture);
            })
            .ToArray();

        public Task<NativeProcessResult> RunAsync(
            NativeProcessCommand command,
            IProgress<NativeOutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            lock (commands)
            {
                commands.Add(command);
            }

            return inner.RunAsync(command, progress, cancellationToken);
        }

        private NativeProcessCommand[] Snapshot()
        {
            lock (commands)
            {
                return commands.ToArray();
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class PaintedRecoveryNativeProcessRunner(
        bool corruptSpecialized,
        bool corruptLegacyIllustrated,
        bool corruptFaithful,
        bool corruptFinalValidation) : INativeProcessRunner
    {
        private readonly NativeProcessRunner inner = new();
        private int specializedInvocations;
        private int legacyIllustratedInvocations;
        private int faithfulInvocations;
        private int finalValidationInvocations;

        public int SpecializedInvocations => Volatile.Read(ref specializedInvocations);
        public int LegacyIllustratedInvocations => Volatile.Read(ref legacyIllustratedInvocations);
        public int FaithfulInvocations => Volatile.Read(ref faithfulInvocations);
        public int FinalValidationInvocations => Volatile.Read(ref finalValidationInvocations);

        public async Task<NativeProcessResult> RunAsync(
            NativeProcessCommand command,
            IProgress<NativeOutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var executable = Path.GetFileName(command.ExecutablePath);
            var modelName = GetArgument(command.Arguments, "-n");
            var isSpecialized = executable.Equals(
                "upscayl-bin.exe",
                StringComparison.OrdinalIgnoreCase);
            var isLegacyIllustrated = executable.Equals(
                    "realesrgan-ncnn-vulkan.exe",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    modelName,
                    RealEsrganCommandBuilder.LegacyDetailModelName,
                    StringComparison.OrdinalIgnoreCase);
            var isFaithful = executable.Equals(
                    "realesrgan-ncnn-vulkan.exe",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    modelName,
                    RealEsrganCommandBuilder.LegacyFaithfulModelName,
                    StringComparison.OrdinalIgnoreCase);
            var outputDirectory = GetArgument(command.Arguments, "-o");
            var isFinalValidation = executable.Equals(
                    "texconv.exe",
                    StringComparison.OrdinalIgnoreCase)
                && outputDirectory?.Contains(
                    "illustrated-final-validation",
                    StringComparison.OrdinalIgnoreCase) == true;

            if (isSpecialized)
            {
                Interlocked.Increment(ref specializedInvocations);
            }
            else if (isLegacyIllustrated)
            {
                Interlocked.Increment(ref legacyIllustratedInvocations);
            }
            else if (isFaithful)
            {
                Interlocked.Increment(ref faithfulInvocations);
            }

            if (isFinalValidation)
            {
                Interlocked.Increment(ref finalValidationInvocations);
            }

            var result = await inner.RunAsync(command, progress, cancellationToken)
                .ConfigureAwait(false);
            if ((isSpecialized && corruptSpecialized)
                || (isLegacyIllustrated && corruptLegacyIllustrated)
                || (isFaithful && corruptFaithful))
            {
                var workerOutput = outputDirectory
                    ?? throw new InvalidOperationException(
                        "Neural command did not include an output path.");
                var outputs = Directory.Exists(workerOutput)
                    ? Directory.EnumerateFiles(
                        workerOutput,
                        "*.png",
                        SearchOption.TopDirectoryOnly).ToArray()
                    : [workerOutput];
                foreach (var output in outputs)
                {
                    await ReplacePngWithBlackAsync(output, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (isFinalValidation && corruptFinalValidation)
            {
                foreach (var output in Directory.EnumerateFiles(
                             outputDirectory!,
                             "*.tga",
                             SearchOption.TopDirectoryOnly))
                {
                    await ReplaceTgaWithBlackAsync(output, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return result;
        }

        private static string? GetArgument(IReadOnlyList<string> arguments, string name)
        {
            for (var index = 0; index + 1 < arguments.Count; index++)
            {
                if (arguments[index].Equals(name, StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            return null;
        }

        private static async Task ReplacePngWithBlackAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var source = await TgaPixelBuffer.ReadPngFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var black = CreateOpaqueBlackTga(source.Width, source.Height);
            await TgaPixelBuffer.Read(black)
                .WritePngFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task ReplaceTgaWithBlackAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var source = await TgaPixelBuffer.ReadFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    path,
                    CreateOpaqueBlackTga(source.Width, source.Height),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static byte[] CreateOpaqueBlackTga(int width, int height)
        {
            var tga = new byte[checked(18 + (width * height * 4))];
            tga[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(
                tga.AsSpan(12, 2),
                checked((ushort)width));
            BinaryPrimitives.WriteUInt16LittleEndian(
                tga.AsSpan(14, 2),
                checked((ushort)height));
            tga[16] = 32;
            tga[17] = 0x28;
            for (var offset = 18; offset < tga.Length; offset += 4)
            {
                tga[offset + 3] = byte.MaxValue;
            }

            return tga;
        }
    }

    private sealed class FidelityFallbackNativeProcessRunner : INativeProcessRunner
    {
        private readonly NativeProcessRunner inner = new();
        private int detailBatchInvocations;
        private int faithfulBatchInvocations;

        public int DetailBatchInvocations => Volatile.Read(ref detailBatchInvocations);
        public int FaithfulBatchInvocations => Volatile.Read(ref faithfulBatchInvocations);

        public async Task<NativeProcessResult> RunAsync(
            NativeProcessCommand command,
            IProgress<NativeOutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var executable = Path.GetFileName(command.ExecutablePath);
            var modelName = GetArgument(command.Arguments, "-n");
            var isDetailBatch = executable.Equals(
                                    "realesrgan-ncnn-vulkan.exe",
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    modelName,
                                    RealEsrganCommandBuilder.LegacyDetailModelName,
                                    StringComparison.OrdinalIgnoreCase);
            var isFaithfulBatch = executable.Equals(
                                      "realesrgan-ncnn-vulkan.exe",
                                      StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(
                                      modelName,
                                      RealEsrganCommandBuilder.LegacyFaithfulModelName,
                                      StringComparison.OrdinalIgnoreCase);
            if (isDetailBatch)
            {
                Interlocked.Increment(ref detailBatchInvocations);
            }
            else if (isFaithfulBatch)
            {
                Interlocked.Increment(ref faithfulBatchInvocations);
            }

            var result = await inner.RunAsync(command, progress, cancellationToken)
                .ConfigureAwait(false);
            if (isDetailBatch)
            {
                var outputPath = GetArgument(command.Arguments, "-o")
                    ?? throw new InvalidOperationException("Real-ESRGAN command did not include an output path.");
                var outputs = Directory.Exists(outputPath)
                    ? Directory.EnumerateFiles(outputPath, "*.png", SearchOption.TopDirectoryOnly).ToArray()
                    : [outputPath];
                foreach (var output in outputs)
                {
                    await ReplacePngWithBlackAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        private static string? GetArgument(IReadOnlyList<string> arguments, string name)
        {
            for (var index = 0; index + 1 < arguments.Count; index++)
            {
                if (arguments[index].Equals(name, StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            return null;
        }

        private static async Task ReplacePngWithBlackAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var source = await TgaPixelBuffer.ReadPngFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var tga = new byte[checked(18 + (source.Width * source.Height * 4))];
            tga[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(
                tga.AsSpan(12, 2),
                checked((ushort)source.Width));
            BinaryPrimitives.WriteUInt16LittleEndian(
                tga.AsSpan(14, 2),
                checked((ushort)source.Height));
            tga[16] = 32;
            tga[17] = 0x28;
            for (var offset = 18; offset < tga.Length; offset += 4)
            {
                tga[offset + 3] = byte.MaxValue;
            }

            await TgaPixelBuffer.Read(tga)
                .WritePngFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class MissingPrimaryOutputFallbackNativeProcessRunner : INativeProcessRunner
    {
        private readonly NativeProcessRunner inner = new();
        private int detailBatchInvocations;
        private int faithfulBatchInvocations;

        public int DetailBatchInvocations => Volatile.Read(ref detailBatchInvocations);
        public int FaithfulBatchInvocations => Volatile.Read(ref faithfulBatchInvocations);

        public async Task<NativeProcessResult> RunAsync(
            NativeProcessCommand command,
            IProgress<NativeOutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var executable = Path.GetFileName(command.ExecutablePath);
            var modelName = GetArgument(command.Arguments, "-n");
            var isDetailBatch = executable.Equals(
                                    "realesrgan-ncnn-vulkan.exe",
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    modelName,
                                    RealEsrganCommandBuilder.LegacyDetailModelName,
                                    StringComparison.OrdinalIgnoreCase);
            var isFaithfulBatch = executable.Equals(
                                      "realesrgan-ncnn-vulkan.exe",
                                      StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(
                                      modelName,
                                      RealEsrganCommandBuilder.LegacyFaithfulModelName,
                                      StringComparison.OrdinalIgnoreCase);
            if (isDetailBatch)
            {
                Interlocked.Increment(ref detailBatchInvocations);
            }
            else if (isFaithfulBatch)
            {
                Interlocked.Increment(ref faithfulBatchInvocations);
            }

            var result = await inner.RunAsync(command, progress, cancellationToken)
                .ConfigureAwait(false);
            if (isDetailBatch)
            {
                var outputDirectory = GetArgument(command.Arguments, "-o")
                    ?? throw new InvalidOperationException("Real-ESRGAN command did not include an output path.");
                var output = Directory.EnumerateFiles(
                        outputDirectory,
                        "*.png",
                        SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .First();
                File.Delete(output);
            }

            return result;
        }

        private static string? GetArgument(IReadOnlyList<string> arguments, string name)
        {
            for (var index = 0; index + 1 < arguments.Count; index++)
            {
                if (arguments[index].Equals(name, StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            return null;
        }
    }

    private sealed class PostCommitFailingAtomicFileOperations(string targetPath)
        : IAtomicFileOperations
    {
        private readonly IAtomicFileOperations inner = new AtomicFileOperations();
        private readonly string fullTargetPath = Path.GetFullPath(targetPath);
        private int failuresRemaining = 1;

        public bool FailureInjected => Volatile.Read(ref failuresRemaining) == 0;

        public async Task CopyAndReplaceAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default,
            Action? onCommitted = null)
        {
            await inner.CopyAndReplaceAsync(
                sourcePath,
                destinationPath,
                expectedLength,
                expectedSha256,
                cancellationToken,
                onCommitted).ConfigureAwait(false);

            if (Path.GetFullPath(destinationPath)
                    .Equals(fullTargetPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref failuresRemaining, 0, 1) == 1)
            {
                throw new IOException("Deliberate post-commit transaction verification failure.");
            }
        }
    }

    private static void TestPaintedProfileReportCompatibility()
    {
        AssertEqual(
            3,
            TextureBuildReport.CurrentIllustratedProfileRevision,
            "Graphic Painted profile revision should advance independently to revision three");
        AssertEqual(
            1,
            TextureBuildReport.CurrentRusticPaintedProfileRevision,
            "Rustic Painted should retain its revision-one profile identity");
        AssertEqual(
            TextureBuildReport.CurrentIllustratedProfileRevision,
            TexturePackWorkflow.GetFreshPaintedProfileRevision(TexturePreset.Illustrated),
            "fresh Graphic Painted builds should record the current painted profile");
        AssertEqual(
            TextureBuildReport.CurrentRusticPaintedProfileRevision,
            TexturePackWorkflow.GetFreshPaintedProfileRevision(TexturePreset.RusticPainted),
            "fresh Rustic Painted builds should record the current painted profile");
        AssertEqual(
            0,
            TexturePackWorkflow.GetFreshPaintedProfileRevision(TexturePreset.MaximumDetail),
            "non-painted builds must not claim painted-profile provenance");
        AssertEqual(
            TexturePackWorkflow.FreshBuildResumeOperationKey,
            TexturePackWorkflow.GetFreshBuildResumeOperationKey(TexturePreset.MaximumDetail),
            "non-painted fresh builds should retain the base resume operation key");
        AssertEqual(
            TexturePackWorkflow.FreshBuildResumeOperationKey,
            TexturePackWorkflow.GetFreshBuildResumeOperationKey(TexturePreset.RusticPainted),
            "unchanged Rustic Painted builds should retain the base resume operation key");
        AssertEqual(
            $"{TexturePackWorkflow.FreshBuildResumeOperationKey}-illustrated-3",
            TexturePackWorkflow.GetFreshBuildResumeOperationKey(TexturePreset.Illustrated),
            "Graphic Painted revision three should use a fenced resume operation key");

        var report = new TextureBuildReport(
            TextureBuildReport.CurrentSchemaVersion,
            "painted-profile-test",
            DateTimeOffset.UnixEpoch,
            @"C:\EQ",
            @"C:\staging\painted-profile-test",
            1,
            new TextureBuildStatistics(
                1,
                1,
                0,
                1,
                1,
                new Dictionary<string, int>(),
                []))
        {
            PaintedProfileRevision = TextureBuildReport.CurrentIllustratedProfileRevision
        };
        var json = JsonSerializer.Serialize(report);
        var current = JsonSerializer.Deserialize<TextureBuildReport>(json);
        AssertEqual(
            TextureBuildReport.CurrentIllustratedProfileRevision,
            current?.PaintedProfileRevision ?? -1,
            "current painted profile revision should round-trip through the report");

        using var document = JsonDocument.Parse(json);
        using var legacyStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(legacyStream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals(nameof(TextureBuildReport.PaintedProfileRevision)))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        var legacy = JsonSerializer.Deserialize<TextureBuildReport>(legacyStream.ToArray());
        AssertEqual(
            0,
            legacy?.PaintedProfileRevision ?? -1,
            "reports written before painted profile provenance should deserialize as legacy revision zero");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }

    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(root, recursive: true);
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
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }
}
