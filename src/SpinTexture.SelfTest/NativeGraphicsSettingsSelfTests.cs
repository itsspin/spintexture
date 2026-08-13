using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpinTexture.Core;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class NativeGraphicsSettingsSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestPresetSwitchingAndRestoreAsync(cancellationToken).ConfigureAwait(false);
        await TestCinematicBloomToggleAsync(cancellationToken).ConfigureAwait(false);
        await TestSwitchBackToBaselineRetiresTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestManagedConflictAndBooleanNormalizationAsync(cancellationToken).ConfigureAwait(false);
        await TestShadowDistanceConflictAsync(cancellationToken).ConfigureAwait(false);
        await TestInterruptedBeforeCommitRecoveryAsync(cancellationToken).ConfigureAwait(false);
        await TestInterruptedPresetSwitchRecoveryAsync(cancellationToken).ConfigureAwait(false);
        await TestInterruptedPresetSwitchWithoutSnapshotFailsClosedAsync(cancellationToken)
            .ConfigureAwait(false);
        await TestLegacyPreBloomRestoreAndMigrationAsync(cancellationToken).ConfigureAwait(false);
        await TestLightingOnlyShadowDistanceMigrationAsync(cancellationToken).ConfigureAwait(false);
        await TestCapabilityAndProcessGatesAsync(cancellationToken).ConfigureAwait(false);
        await TestMalformedInputsFailClosedAsync(cancellationToken).ConfigureAwait(false);
        await TestPostCommitRollbackAsync(cancellationToken).ConfigureAwait(false);
        TestIniRoundTripAndWhitespaceOnlyValue();
    }

    private static async Task TestCinematicBloomToggleAsync(CancellationToken cancellationToken)
    {
        const string originalText =
            "[Defaults]\r\nShadows=FALSE\r\nMultiPassLighting=FALSE\r\n"
            + "PostEffects=FALSE\r\nBloom=TRUE\r\n"
            + "[Options]\r\nShadowClipPlane=40\r\n";
        await using var fixture = await NativeGraphicsFixture.CreateAsync(
                Encoding.UTF8.GetBytes(originalText),
                cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();

        var withoutBloomPlan = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.CinematicNoBloom,
                cancellationToken)
            .ConfigureAwait(false);
        Assert(
            withoutBloomPlan.Changes.Any(change =>
                change.Key == "Bloom" && change.PlannedValue == "FALSE"),
            "Cinematic without Bloom explicitly previews Bloom off");

        var withoutBloom = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.CinematicNoBloom,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            NativeGraphicsPreset.CinematicNoBloom,
            withoutBloom.Status.AppliedPreset,
            "Bloom-off Cinematic identity");
        var withoutBloomText = await fixture.ReadSettingsTextAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertContains(withoutBloomText, "MultiPassLighting=TRUE", "Bloom-off Cinematic keeps advanced lighting");
        AssertContains(withoutBloomText, "PostEffects=TRUE", "Bloom-off Cinematic keeps post effects");
        AssertContains(withoutBloomText, "Bloom=FALSE", "Bloom-off Cinematic disables Bloom");
        AssertContains(withoutBloomText, "ShadowClipPlane=100", "Bloom-off Cinematic keeps maximum shadows");

        var withBloom = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(withoutBloom.TransactionId, withBloom.TransactionId, "Bloom toggle reuses transaction");
        AssertContains(
            await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false),
            "Bloom=TRUE",
            "Bloom can be re-enabled without leaving Cinematic");

        var disabledAgain = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.CinematicNoBloom,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(withBloom.TransactionId, disabledAgain.TransactionId, "reverse Bloom toggle transaction");
        AssertContains(
            await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false),
            "Bloom=FALSE",
            "Bloom can be disabled again");

        await service.RestoreAsync(fixture.Paths, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertSequenceEqual(
            Encoding.UTF8.GetBytes(originalText),
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "Bloom-toggle transaction restores the exact original settings");
    }

    private static async Task TestLegacyPreBloomRestoreAndMigrationAsync(
        CancellationToken cancellationToken)
    {
        var original = Encoding.UTF8.GetBytes(
            "; pre-bloom schema\r\n[Defaults]\r\nShadows=FALSE\r\n"
            + "MultiPassLighting=FALSE\r\nPostEffects=FALSE\r\nBloom=FALSE\r\n");

        await using (var restoreFixture = await NativeGraphicsFixture.CreateAsync(
                         original,
                         cancellationToken)
                     .ConfigureAwait(false))
        {
            var service = restoreFixture.CreateService();
            var applied = await service.ApplyAsync(
                    restoreFixture.Paths,
                    NativeGraphicsPreset.Cinematic,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await ConvertToLegacyPreBloomManifestAsync(
                    restoreFixture,
                    applied.TransactionManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await File.AppendAllTextAsync(
                    restoreFixture.SettingsPath,
                    "Bloom=FALSE\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            var legacyStatus = await service.InspectAsync(
                    restoreFixture.Paths,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(NativeGraphicsSettingsState.Applied, legacyStatus.State,
                "pre-Bloom schema remains inspectable");
            Assert(legacyStatus.CanRestore, "pre-Bloom schema remains restorable");
            await service.RestoreAsync(
                    restoreFixture.Paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var restoredText = await restoreFixture.ReadSettingsTextAsync(cancellationToken)
                .ConfigureAwait(false);
            AssertContains(restoredText, "Shadows=FALSE", "legacy restore restores Shadows");
            AssertContains(restoredText, "MultiPassLighting=FALSE", "legacy restore restores multipass");
            AssertContains(restoredText, "PostEffects=FALSE", "legacy restore restores post effects");
            AssertContains(restoredText, "Bloom=TRUE",
                "legacy restore preserves Bloom as an unmanaged user value");
            AssertContains(restoredText, "Bloom=FALSE",
                "legacy restore ignores even duplicate Bloom values that predate its ownership");
        }

        await using (var migrationFixture = await NativeGraphicsFixture.CreateAsync(
                         original,
                         cancellationToken)
                     .ConfigureAwait(false))
        {
            var service = migrationFixture.CreateService();
            var applied = await service.ApplyAsync(
                    migrationFixture.Paths,
                    NativeGraphicsPreset.Cinematic,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await ConvertToLegacyPreBloomManifestAsync(
                    migrationFixture,
                    applied.TransactionManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var balanced = await service.ApplyAsync(
                    migrationFixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var migratedNode = JsonNode.Parse(
                await File.ReadAllTextAsync(
                        balanced.TransactionManifestPath,
                        cancellationToken)
                    .ConfigureAwait(false))!.AsObject();
            AssertEqual(
                NativeGraphicsSettingsManifest.CurrentSchemaVersion,
                migratedNode["schemaVersion"]!.GetValue<int>(),
                "safe preset switch upgrades the legacy manifest schema");
            var migratedBloom = migratedNode["baselineSettings"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["key"]!.GetValue<string>().Equals(
                    "Bloom",
                    StringComparison.OrdinalIgnoreCase));
            AssertEqual("TRUE", migratedBloom["value"]!.GetValue<string>(),
                "migration captures the current unmanaged Bloom value as baseline");
            var balancedText = await migrationFixture.ReadSettingsTextAsync(cancellationToken)
                .ConfigureAwait(false);
            AssertContains(balancedText, "Bloom=TRUE",
                "Balanced migration preserves current Bloom");
            await service.RestoreAsync(
                    migrationFixture.Paths,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AssertContains(
                await migrationFixture.ReadSettingsTextAsync(cancellationToken)
                    .ConfigureAwait(false),
                "Bloom=TRUE",
                "post-migration restore preserves captured Bloom rather than copying the older backup value");
        }
    }

    private static async Task ConvertToLegacyPreBloomManifestAsync(
        NativeGraphicsFixture fixture,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        // The current Cinematic apply set Bloom and ShadowClipPlane. A pre-Bloom,
        // pre-shadow-distance transaction would have left both outside its
        // ownership. Retain the live Bloom value, but put ShadowClipPlane back at
        // its captured baseline before removing both from schema-1 metadata.
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))!
            .AsObject();
        var shadowBaseline = manifest["baselineSettings"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => node["key"]!.GetValue<string>().Equals(
                "ShadowClipPlane",
                StringComparison.OrdinalIgnoreCase));
        var liveDocument = NativeGraphicsIniDocument.Parse(
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false));
        liveDocument.Set(
            shadowBaseline["section"]!.GetValue<string>(),
            shadowBaseline["key"]!.GetValue<string>(),
            shadowBaseline["exists"]!.GetValue<bool>(),
            shadowBaseline["value"]?.GetValue<string>());
        var liveBytes = liveDocument.ToBytes();
        await File.WriteAllBytesAsync(fixture.SettingsPath, liveBytes, cancellationToken)
            .ConfigureAwait(false);
        var fingerprint = NativeGraphicsIniDocument.Fingerprint(liveBytes);
        manifest["schemaVersion"] = NativeGraphicsSettingsManifest.LegacySchemaVersion;
        RemoveSetting(manifest["baselineSettings"]!.AsArray(), "Bloom");
        RemoveSetting(manifest["appliedSettings"]!.AsArray(), "Bloom");
        RemoveSetting(manifest["baselineSettings"]!.AsArray(), "ShadowClipPlane");
        RemoveSetting(manifest["appliedSettings"]!.AsArray(), "ShadowClipPlane");
        manifest["appliedLength"] = fingerprint.Length;
        manifest["appliedSha256"] = fingerprint.Sha256;
        await File.WriteAllTextAsync(
                manifestPath,
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);

        static void RemoveSetting(JsonArray settings, string key)
        {
            var node = settings.Single(item => item!.AsObject()["key"]!.GetValue<string>()
                .Equals(key, StringComparison.OrdinalIgnoreCase));
            settings.Remove(node);
        }
    }

    private static async Task TestLightingOnlyShadowDistanceMigrationAsync(
        CancellationToken cancellationToken)
    {
        var original = Encoding.UTF8.GetBytes(
            "[Defaults]\r\nShadows=FALSE\r\nMultiPassLighting=FALSE\r\n"
            + "PostEffects=FALSE\r\nBloom=FALSE\r\n"
            + "[Options]\r\nShadowClipPlane=41\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(
                original,
                cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        var balanced = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(
                    balanced.TransactionManifestPath,
                    cancellationToken)
                .ConfigureAwait(false))!.AsObject();
        manifest["schemaVersion"] = NativeGraphicsSettingsManifest.LightingOnlySchemaVersion;
        var shadowBaseline = manifest["baselineSettings"]!.AsArray()
            .Single(node => node!.AsObject()["key"]!.GetValue<string>().Equals(
                "ShadowClipPlane",
                StringComparison.OrdinalIgnoreCase));
        manifest["baselineSettings"]!.AsArray().Remove(shadowBaseline);
        await File.WriteAllTextAsync(
                balanced.TransactionManifestPath,
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);

        var currentDocument = NativeGraphicsIniDocument.Parse(
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false));
        currentDocument.Set("Options", "ShadowClipPlane", true, "58");
        await File.WriteAllBytesAsync(
                fixture.SettingsPath,
                currentDocument.ToBytes(),
                cancellationToken)
            .ConfigureAwait(false);
        await File.AppendAllTextAsync(
                fixture.SettingsPath,
                "[Unrelated]\r\nKeepAfterShadowMigration=Yes\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        var legacyStatus = await service.InspectAsync(fixture.Paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Applied, legacyStatus.State,
            "schema-2 lighting transaction ignores the not-yet-managed shadow distance");
        Assert(legacyStatus.CanRestore,
            "schema-2 lighting transaction remains restorable before migration");

        var cinematic = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var cinematicText = await fixture.ReadSettingsTextAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertContains(cinematicText, "ShadowClipPlane=100",
            "Cinematic enables the verified maximum native shadow distance");

        var migrated = JsonNode.Parse(
            await File.ReadAllTextAsync(
                    cinematic.TransactionManifestPath,
                    cancellationToken)
                .ConfigureAwait(false))!.AsObject();
        AssertEqual(
            NativeGraphicsSettingsManifest.CurrentSchemaVersion,
            migrated["schemaVersion"]!.GetValue<int>(),
            "shadow-distance preset switch upgrades schema 2 to schema 3");
        var migratedShadow = migrated["baselineSettings"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => node["key"]!.GetValue<string>().Equals(
                "ShadowClipPlane",
                StringComparison.OrdinalIgnoreCase));
        AssertEqual("58", migratedShadow["value"]!.GetValue<string>(),
            "migration captures the current unmanaged shadow distance as baseline");

        await service.RestoreAsync(
                fixture.Paths,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var restoredText = await fixture.ReadSettingsTextAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertContains(restoredText, "ShadowClipPlane=58",
            "post-migration restore uses the captured live shadow distance, not the older backup");
        AssertContains(restoredText, "KeepAfterShadowMigration=Yes",
            "post-migration three-way restore preserves unrelated preferences");
    }

    private static async Task TestInterruptedBeforeCommitRecoveryAsync(
        CancellationToken cancellationToken)
    {
        var original = EncodeUtf8Bom(
            "; interrupted transaction\r\n[Defaults]\r\nShadows=FALSE\r\n"
            + "MultiPassLighting=FALSE\r\nPostEffects=FALSE\r\nBloom=FALSE\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(original, cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        var applied = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Model a crash after the durable Preparing manifest was written but
        // before the live atomic replacement: the manifest describes the
        // intended applied values while eqclient.ini is still at baseline.
        await File.WriteAllBytesAsync(fixture.SettingsPath, original, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(applied.TransactionManifestPath, cancellationToken)
                .ConfigureAwait(false))!.AsObject();
        manifest["state"] = "preparing";
        await File.WriteAllTextAsync(
                applied.TransactionManifestPath,
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);

        var interrupted = await service.InspectAsync(fixture.Paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, interrupted.State,
            "interrupted pre-commit state");
        Assert(interrupted.CanRestore, "verified interrupted transaction must remain restorable");
        var restored = await service.RestoreAsync(
                fixture.Paths,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Original, restored.Status.State,
            "interrupted pre-commit restore state");
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "interrupted pre-commit restore preserves exact baseline bytes");
    }

    private static async Task TestInterruptedPresetSwitchRecoveryAsync(
        CancellationToken cancellationToken)
    {
        var original = EncodeUtf8Bom(
            "; interrupted preset switch\r\n"
            + "[Defaults]\r\nShadows=TRUE\r\nMultiPassLighting=FALSE\r\n"
            + "PostEffects=FALSE\r\nBloom=FALSE\r\n"
            + "[Options]\r\nShadowClipPlane=41\r\n"
            + "[Unrelated]\r\nKeepAfterRecovery=Yes\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(original, cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var preSwitchBytes = await File.ReadAllBytesAsync(
                fixture.SettingsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var preSwitchDocument = NativeGraphicsIniDocument.Parse(preSwitchBytes);

        var balanced = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ConvertToInterruptedSwitchManifestAsync(
                balanced.TransactionManifestPath,
                preSwitchDocument,
                includePreApplySnapshot: true,
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                fixture.SettingsPath,
                preSwitchBytes,
                cancellationToken)
            .ConfigureAwait(false);

        var interrupted = await service.InspectAsync(fixture.Paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, interrupted.State,
            "interrupted preset switch state");
        Assert(interrupted.CanRestore,
            "interrupted preset switch retains a verified recovery snapshot");
        await service.RestoreAsync(
                fixture.Paths,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "interrupted Cinematic-to-Balanced switch restores every prior managed deviation");
    }

    private static async Task TestInterruptedPresetSwitchWithoutSnapshotFailsClosedAsync(
        CancellationToken cancellationToken)
    {
        var original = Encoding.UTF8.GetBytes(
            "[Defaults]\r\nShadows=TRUE\r\nMultiPassLighting=FALSE\r\n"
            + "PostEffects=FALSE\r\nBloom=FALSE\r\n"
            + "[Options]\r\nShadowClipPlane=41\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(original, cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var preSwitchBytes = await File.ReadAllBytesAsync(
                fixture.SettingsPath,
                cancellationToken)
            .ConfigureAwait(false);

        var balanced = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ConvertToInterruptedSwitchManifestAsync(
                balanced.TransactionManifestPath,
                NativeGraphicsIniDocument.Parse(preSwitchBytes),
                includePreApplySnapshot: false,
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                fixture.SettingsPath,
                preSwitchBytes,
                cancellationToken)
            .ConfigureAwait(false);

        await AssertThrowsAsync<InvalidOperationException>(
                () => service.RestoreAsync(
                    fixture.Paths,
                    cancellationToken: cancellationToken),
                "an older interrupted switch without a pre-apply snapshot must fail closed")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            preSwitchBytes,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "ambiguous legacy recovery does not overwrite live managed values");
    }

    private static async Task ConvertToInterruptedSwitchManifestAsync(
        string manifestPath,
        NativeGraphicsIniDocument preApplyDocument,
        bool includePreApplySnapshot,
        CancellationToken cancellationToken)
    {
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))!
            .AsObject();
        manifest["state"] = "preparing";
        if (!includePreApplySnapshot)
        {
            manifest["preApplySettings"] = null;
        }
        else
        {
            var snapshot = new JsonArray();
            foreach (var baselineNode in manifest["baselineSettings"]!.AsArray())
            {
                var baseline = baselineNode!.AsObject();
                var section = baseline["section"]!.GetValue<string>();
                var key = baseline["key"]!.GetValue<string>();
                var value = preApplyDocument.Get(section, key);
                snapshot.Add(new JsonObject
                {
                    ["section"] = section,
                    ["key"] = key,
                    ["exists"] = value.Exists,
                    ["value"] = value.Exists ? value.Value : null
                });
            }

            manifest["preApplySettings"] = snapshot;
        }

        await File.WriteAllTextAsync(
                manifestPath,
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TestSwitchBackToBaselineRetiresTransactionAsync(
        CancellationToken cancellationToken)
    {
        var original = Encoding.UTF8.GetBytes(
            "[Defaults]\r\nShadows=TRUE\r\nMultiPassLighting=FALSE\r\nPostEffects=FALSE\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(original, cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        var cinematic = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Applied, cinematic.Status.State,
            "Cinematic baseline-retirement setup");
        AssertContains(
            await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false),
            "Bloom=TRUE",
            "Cinematic adds an originally absent Bloom key");
        var balanced = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Original, balanced.Status.State,
            "switch returning every changed key to baseline retires transaction");
        Assert(
            !(await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false))
            .Contains("Bloom=", StringComparison.OrdinalIgnoreCase),
            "Balanced removes a Bloom key that was absent from the original baseline");
        Assert(
            !(await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false))
            .Contains("ShadowClipPlane=", StringComparison.OrdinalIgnoreCase),
            "Balanced removes a shadow-distance key that was absent from the original baseline");
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "retired switch restores original managed values");
    }

    private static async Task TestPresetSwitchingAndRestoreAsync(
        CancellationToken cancellationToken)
    {
        const string originalText =
            "; preserve this comment\r\n"
            + "[Defaults]\r\n"
            + "MultiPassLighting=FALSE\r\n"
            + "PostEffects=0\r\n"
            + "Bloom=0\r\n"
            + "UnknownSetting=KeepMe\r\n"
            + "\r\n"
            + "[Options]\r\n"
            + "ShadowClipPlane=77\r\n";
        await using var fixture = await NativeGraphicsFixture.CreateAsync(
                EncodeUtf8Bom(originalText),
                cancellationToken)
            .ConfigureAwait(false);

        var service = fixture.CreateService();
        var balancedPlan = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(
            NativeGraphicsSettingsState.Original,
            balancedPlan.Status.State,
            $"initial state ({balancedPlan.Status.Summary})");
        AssertEqual(1, balancedPlan.Changes.Count, "Balanced change count");
        AssertEqual("Shadows", balancedPlan.Changes[0].Key, "Balanced managed key");

        var balanced = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Applied, balanced.Status.State, "Balanced applied state");
        var balancedText = await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false);
        AssertContains(balancedText, "Shadows=TRUE", "Balanced enables shadows");
        AssertContains(balancedText, "MultiPassLighting=FALSE", "Balanced preserves multipass");
        AssertContains(balancedText, "PostEffects=0", "Balanced preserves post effects");
        AssertContains(balancedText, "Bloom=0", "Balanced preserves bloom");
        AssertContains(balancedText, "ShadowClipPlane=77", "shadow distance remains untouched");

        var backupPath = Path.Combine(
            Path.GetDirectoryName(balanced.TransactionManifestPath)!,
            "eqclient.original.ini");
        AssertSequenceEqual(
            fixture.OriginalSettings,
            await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false),
            "exact original backup");

        await File.AppendAllTextAsync(
                fixture.SettingsPath,
                "\r\n[Unrelated]\r\nKeepAfterSwitch=Yes\r\n",
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        var cinematic = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(balanced.TransactionId, cinematic.TransactionId, "preset switch transaction identity");
        var cinematicText = await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false);
        AssertContains(cinematicText, "MultiPassLighting=TRUE", "Cinematic enables multipass");
        AssertContains(cinematicText, "PostEffects=TRUE", "Cinematic enables post effects");
        AssertContains(cinematicText, "Bloom=TRUE", "Cinematic enables bloom");
        AssertContains(cinematicText, "ShadowClipPlane=100",
            "Cinematic enables maximum native shadow distance");

        var balancedAgain = await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(balanced.TransactionId, balancedAgain.TransactionId, "reverse switch transaction identity");
        var switchedBackText = await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false);
        AssertContains(switchedBackText, "MultiPassLighting=FALSE", "reverse switch restores multipass baseline");
        AssertContains(switchedBackText, "PostEffects=0", "reverse switch restores post-effects baseline");
        AssertContains(switchedBackText, "Bloom=0", "reverse switch restores bloom baseline");
        AssertContains(switchedBackText, "ShadowClipPlane=77",
            "Balanced restores and preserves the pre-preset shadow distance");
        AssertContains(switchedBackText, "KeepAfterSwitch=Yes", "reverse switch preserves unrelated changes");

        var restored = await service.RestoreAsync(fixture.Paths, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Original, restored.Status.State, "restored state");
        var restoredText = await fixture.ReadSettingsTextAsync(cancellationToken).ConfigureAwait(false);
        Assert(!restoredText.Contains("Shadows=", StringComparison.OrdinalIgnoreCase),
            "restore removes an originally absent Shadows key");
        AssertContains(restoredText, "KeepAfterSwitch=Yes", "three-way restore preserves unrelated settings");
        AssertContains(restoredText, "ShadowClipPlane=77", "restore never changes shadow distance");

        await using var exactFixture = await NativeGraphicsFixture.CreateAsync(
                fixture.OriginalSettings,
                cancellationToken)
            .ConfigureAwait(false);
        var exactService = exactFixture.CreateService();
        await exactService.ApplyAsync(
                exactFixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await exactService.RestoreAsync(exactFixture.Paths, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AssertSequenceEqual(
            exactFixture.OriginalSettings,
            await File.ReadAllBytesAsync(exactFixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "unchanged applied file restores exact original bytes");
    }

    private static async Task TestManagedConflictAndBooleanNormalizationAsync(
        CancellationToken cancellationToken)
    {
        const string original =
            "[Defaults]\r\nShadows=FALSE\r\nMultiPassLighting=FALSE\r\nPostEffects=FALSE\r\nBloom=FALSE\r\n";
        await using var normalizedFixture = await NativeGraphicsFixture.CreateAsync(
                Encoding.UTF8.GetBytes(original),
                cancellationToken)
            .ConfigureAwait(false);
        var normalizedService = normalizedFixture.CreateService();
        await normalizedService.ApplyAsync(
                normalizedFixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var normalized = (await normalizedFixture.ReadSettingsTextAsync(cancellationToken)
                .ConfigureAwait(false))
            .Replace("=TRUE", "=1", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
                normalizedFixture.SettingsPath,
                normalized,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        var normalizedStatus = await normalizedService.InspectAsync(
                normalizedFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Applied, normalizedStatus.State, "boolean normalization state");
        await normalizedService.RestoreAsync(
                normalizedFixture.Paths,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await using var conflictFixture = await NativeGraphicsFixture.CreateAsync(
                Encoding.UTF8.GetBytes(original),
                cancellationToken)
            .ConfigureAwait(false);
        var conflictService = conflictFixture.CreateService();
        await conflictService.ApplyAsync(
                conflictFixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var conflicted = (await conflictFixture.ReadSettingsTextAsync(cancellationToken)
                .ConfigureAwait(false))
            .Replace("PostEffects=TRUE", "PostEffects=USER-OVERRIDE", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
                conflictFixture.SettingsPath,
                conflicted,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        var beforeRejectedOperations = await File.ReadAllBytesAsync(
                conflictFixture.SettingsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var conflictStatus = await conflictService.InspectAsync(
                conflictFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Modified, conflictStatus.State, "managed conflict state");
        await AssertThrowsAsync<InvalidOperationException>(
                () => conflictService.RestoreAsync(
                    conflictFixture.Paths,
                    cancellationToken: cancellationToken),
                "restore must reject a managed-key conflict")
            .ConfigureAwait(false);
        await AssertThrowsAsync<InvalidOperationException>(
                () => conflictService.ApplyAsync(
                    conflictFixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken),
                "preset switch must reject a managed-key conflict")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            beforeRejectedOperations,
            await File.ReadAllBytesAsync(conflictFixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false),
            "rejected conflict operations do not write eqclient.ini");
    }

    private static async Task TestShadowDistanceConflictAsync(
        CancellationToken cancellationToken)
    {
        var original = Encoding.UTF8.GetBytes(
            "[Defaults]\r\nShadows=FALSE\r\nMultiPassLighting=FALSE\r\n"
            + "PostEffects=FALSE\r\nBloom=FALSE\r\n"
            + "[Options]\r\nShadowClipPlane=41\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(
                original,
                cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var conflicted = (await fixture.ReadSettingsTextAsync(cancellationToken)
                .ConfigureAwait(false))
            .Replace("ShadowClipPlane=100", "ShadowClipPlane=63", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
                fixture.SettingsPath,
                conflicted,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        var beforeRejectedOperations = await File.ReadAllBytesAsync(
                fixture.SettingsPath,
                cancellationToken)
            .ConfigureAwait(false);

        var status = await service.InspectAsync(fixture.Paths, cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Modified, status.State,
            "externally changed managed shadow distance state");
        AssertContains(status.Summary, "ShadowClipPlane",
            "shadow-distance conflict identifies the exact managed key");
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.RestoreAsync(
                    fixture.Paths,
                    cancellationToken: cancellationToken),
                "restore must reject a managed shadow-distance conflict")
            .ConfigureAwait(false);
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken),
                "preset switch must reject a managed shadow-distance conflict")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            beforeRejectedOperations,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false),
            "rejected shadow-distance conflict operations do not write eqclient.ini");
    }

    private static async Task TestCapabilityAndProcessGatesAsync(
        CancellationToken cancellationToken)
    {
        var original = Encoding.UTF8.GetBytes("[Defaults]\r\nShadows=FALSE\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(original, cancellationToken)
            .ConfigureAwait(false);
        var advancedOptionsPath = Path.Combine(
            fixture.Paths.InstallPath,
            "uifiles",
            "default",
            "EQUI_AdvancedDisplayOptionsWindow.xml");
        var controlsWithoutBloom = (await File.ReadAllTextAsync(
                advancedOptionsPath,
                cancellationToken)
            .ConfigureAwait(false))
            .Replace("ADOW_EnableBloomCheckbox", string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
                advancedOptionsPath,
                controlsWithoutBloom,
                cancellationToken)
            .ConfigureAwait(false);
        var service = fixture.CreateService();
        var missingBloom = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Unavailable, missingBloom.Status.State,
            "missing native Bloom control fails closed");
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Cinematic,
                    cancellationToken: cancellationToken),
                "missing Bloom control blocks Cinematic apply")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "missing Bloom control does not write eqclient.ini");

        await fixture.WriteCapabilityAsync(cancellationToken).ConfigureAwait(false);
        var controlsWithoutShadowDistance = (await File.ReadAllTextAsync(
                advancedOptionsPath,
                cancellationToken)
            .ConfigureAwait(false))
            .Replace("ADOW_ShadowClipPlaneSlider", string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
                advancedOptionsPath,
                controlsWithoutShadowDistance,
                cancellationToken)
            .ConfigureAwait(false);
        var missingShadowControl = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Unavailable, missingShadowControl.Status.State,
            "missing native shadow-distance control fails closed");
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Cinematic,
                    cancellationToken: cancellationToken),
                "missing shadow-distance control blocks Cinematic apply")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "missing shadow-distance control does not write eqclient.ini");

        await fixture.WriteCapabilityAsync(cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                fixture.GameExecutablePath,
                "game executable without native distance marker"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var missingShadowRuntime = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Unavailable, missingShadowRuntime.Status.State,
            "missing native shadow-distance runtime marker fails closed");
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Cinematic,
                    cancellationToken: cancellationToken),
                "missing shadow-distance runtime marker blocks Cinematic apply")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "missing shadow-distance runtime marker does not write eqclient.ini");

        await fixture.WriteCapabilityAsync(cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                fixture.GraphicsDllPath,
                "renderer without marker"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var unavailable = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Unavailable, unavailable.Status.State, "capability fail-closed state");
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken),
                "missing renderer marker blocks apply")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "capability failure does not write eqclient.ini");

        await fixture.WriteCapabilityAsync(cancellationToken).ConfigureAwait(false);
        var gated = fixture.CreateService(_ => throw new InvalidOperationException("simulated running client"));
        await AssertThrowsAsync<InvalidOperationException>(
                () => gated.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken),
                "running-client gate blocks apply")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "process gate does not write eqclient.ini");

        await service.ApplyAsync(
                fixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                fixture.GraphicsDllPath,
                "renderer marker removed"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var appliedBeforeBlockedSwitch = await File.ReadAllBytesAsync(
                fixture.SettingsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var blockedSwitchPlan = await service.PlanAsync(
                fixture.Paths,
                NativeGraphicsPreset.Cinematic,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Unavailable, blockedSwitchPlan.Status.State,
            "active switch capability state");
        Assert(blockedSwitchPlan.Status.CanRestore,
            "renderer capability changes must not strand a verified restore point");
        await AssertThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Cinematic,
                    cancellationToken: cancellationToken),
                "active preset switch rechecks capability")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            appliedBeforeBlockedSwitch,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "blocked active switch does not write eqclient.ini");
    }

    private static async Task TestMalformedInputsFailClosedAsync(
        CancellationToken cancellationToken)
    {
        var duplicate = Encoding.UTF8.GetBytes(
            "[Defaults]\r\nShadows=FALSE\r\nShadows=TRUE\r\n");
        await using var duplicateFixture = await NativeGraphicsFixture.CreateAsync(
                duplicate,
                cancellationToken)
            .ConfigureAwait(false);
        var duplicateService = duplicateFixture.CreateService();
        var duplicateStatus = await duplicateService.InspectAsync(
                duplicateFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, duplicateStatus.State,
            "duplicate managed key state");
        await AssertThrowsAsync<InvalidOperationException>(
                () => duplicateService.ApplyAsync(
                    duplicateFixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken),
                "duplicate managed key blocks apply")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            duplicate,
            await File.ReadAllBytesAsync(duplicateFixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false),
            "duplicate managed key does not write eqclient.ini");

        await using var manifestFixture = await NativeGraphicsFixture.CreateAsync(
                Encoding.UTF8.GetBytes("[Defaults]\r\nShadows=FALSE\r\n"),
                cancellationToken)
            .ConfigureAwait(false);
        var manifestService = manifestFixture.CreateService();
        var applied = await manifestService.ApplyAsync(
                manifestFixture.Paths,
                NativeGraphicsPreset.Balanced,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var appliedSettings = await File.ReadAllBytesAsync(
                manifestFixture.SettingsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var originalManifestText = await File.ReadAllTextAsync(
                applied.TransactionManifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        var manifestNode = JsonNode.Parse(originalManifestText)!.AsObject();
        manifestNode["appliedSettings"] = null;
        await File.WriteAllTextAsync(
                applied.TransactionManifestPath,
                manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);
        var malformedStatus = await manifestService.InspectAsync(
                manifestFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, malformedStatus.State,
            "null manifest collection fails closed");
        Assert(!malformedStatus.CanRestore,
            "an invalid manifest must not expose an unverified restore action");
        await AssertThrowsAsync<InvalidOperationException>(
                () => manifestService.RestoreAsync(
                    manifestFixture.Paths,
                    cancellationToken: cancellationToken),
                "malformed manifest blocks restore")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            appliedSettings,
            await File.ReadAllBytesAsync(manifestFixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false),
            "malformed manifest does not write eqclient.ini");

        manifestNode = JsonNode.Parse(originalManifestText)!.AsObject();
        manifestNode["baselineSettings"]!.AsArray()[0] = null;
        await File.WriteAllTextAsync(
                applied.TransactionManifestPath,
                manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);
        malformedStatus = await manifestService.InspectAsync(
                manifestFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, malformedStatus.State,
            "null manifest list element fails closed");

        manifestNode = JsonNode.Parse(originalManifestText)!.AsObject();
        manifestNode["transactionId"] = "native-graphics-wrong-directory";
        await File.WriteAllTextAsync(
                applied.TransactionManifestPath,
                manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);
        malformedStatus = await manifestService.InspectAsync(
                manifestFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, malformedStatus.State,
            "manifest identity mismatch fails closed");

        manifestNode = JsonNode.Parse(originalManifestText)!.AsObject();
        manifestNode["originalSha256"] = "not-a-sha256";
        await File.WriteAllTextAsync(
                applied.TransactionManifestPath,
                manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);
        malformedStatus = await manifestService.InspectAsync(
                manifestFixture.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.RecoveryRequired, malformedStatus.State,
            "invalid manifest fingerprint fails closed");
        AssertSequenceEqual(
            appliedSettings,
            await File.ReadAllBytesAsync(manifestFixture.SettingsPath, cancellationToken)
                .ConfigureAwait(false),
            "all malformed manifest variants leave eqclient.ini untouched");
    }

    private static async Task TestPostCommitRollbackAsync(CancellationToken cancellationToken)
    {
        var original = EncodeUtf8Bom(
            "; rollback sentinel\r\n[Defaults]\r\nShadows=FALSE\r\n");
        await using var fixture = await NativeGraphicsFixture.CreateAsync(original, cancellationToken)
            .ConfigureAwait(false);
        var atomic = new PostCommitFailingAtomicFileOperations(fixture.SettingsPath);
        var processGates = new List<string>();
        var service = new NativeGraphicsSettingsService(atomic, processGates.Add);
        await AssertThrowsAsync<IOException>(
                () => service.ApplyAsync(
                    fixture.Paths,
                    NativeGraphicsPreset.Balanced,
                    cancellationToken: cancellationToken),
                "post-commit failure should surface")
            .ConfigureAwait(false);
        AssertSequenceEqual(
            original,
            await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken).ConfigureAwait(false),
            "post-commit failure rolls back exact live bytes");
        Assert(processGates.Any(operation => operation.Contains(
                "roll back native graphics settings",
                StringComparison.Ordinal)),
            "a post-commit rollback must re-run the stopped-client gate");
        var status = await service.InspectAsync(fixture.Paths, cancellationToken).ConfigureAwait(false);
        AssertEqual(NativeGraphicsSettingsState.Original, status.State, "post-rollback state");
    }

    private static void TestIniRoundTripAndWhitespaceOnlyValue()
    {
        var source = EncodeUtf8Bom(
            "; comment\r\n[Defaults]\r\nShadows=   \r\nUnknown=unchanged\r\n");
        var document = NativeGraphicsIniDocument.Parse(source);
        AssertSequenceEqual(source, document.ToBytes(), "INI no-op round trip");
        document.Set("Defaults", "Shadows", true, "TRUE");
        var updated = Encoding.UTF8.GetString(document.ToBytes().AsSpan(3));
        AssertContains(updated, "Shadows=   TRUE\r\n", "whitespace-only INI value replacement");
        Assert(!updated.Contains("Shadows=   TRUE   ", StringComparison.Ordinal),
            "whitespace-only value must not duplicate overlapping whitespace");
    }

    private static byte[] EncodeUtf8Bom(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var result = new byte[3 + payload.Length];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        payload.CopyTo(result, 3);
        return result;
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}: {message}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertContains(string actual, string expected, string message) =>
        Assert(actual.Contains(expected, StringComparison.Ordinal),
            $"{message}: expected to find '{expected}'");

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}: expected '{expected}', actual '{actual}'");
        }
    }

    private static void AssertSequenceEqual(
        byte[] expected,
        byte[] actual,
        string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class PostCommitFailingAtomicFileOperations(string settingsPath)
        : IAtomicFileOperations
    {
        private readonly AtomicFileOperations inner = new();
        private readonly string settingsPath = Path.GetFullPath(settingsPath);
        private bool failed;

        public Task CopyAndReplaceAsync(
            string sourcePath,
            string destinationPath,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken = default,
            Action? onCommitted = null)
        {
            var failThisCommit = !failed
                && PathGuard.SamePath(destinationPath, settingsPath);
            return inner.CopyAndReplaceAsync(
                sourcePath,
                destinationPath,
                expectedLength,
                expectedSha256,
                cancellationToken,
                () =>
                {
                    onCommitted?.Invoke();
                    if (failThisCommit)
                    {
                        failed = true;
                        throw new IOException("Simulated failure after the live atomic commit.");
                    }
                });
        }
    }

    private sealed class NativeGraphicsFixture : IAsyncDisposable
    {
        private const string CapabilityXml =
            "<Screen>ADOW_ShadowsCheckbox ADOW_AdvancedLightingCheckbox "
            + "ADOW_EnablePostEffectsCheckbox ADOW_EnableBloomCheckbox "
            + "ADOW_ShadowClipPlaneSlider</Screen>";
        private const string CapabilityDll =
            "fake renderer payload: Shadows: Stencil Available; "
            + "MPL: Available; Render: Post Effects Available";
        private const string CapabilityExecutable =
            "fake EQL executable payload: ShadowClipPlane";

        private NativeGraphicsFixture(
            string root,
            ProjectPaths paths,
            string settingsPath,
            string graphicsDllPath,
            string gameExecutablePath,
            byte[] originalSettings)
        {
            Root = root;
            Paths = paths;
            SettingsPath = settingsPath;
            GraphicsDllPath = graphicsDllPath;
            GameExecutablePath = gameExecutablePath;
            OriginalSettings = originalSettings;
        }

        public string Root { get; }
        public ProjectPaths Paths { get; }
        public string SettingsPath { get; }
        public string GraphicsDllPath { get; }
        public string GameExecutablePath { get; }
        public byte[] OriginalSettings { get; }

        public static async Task<NativeGraphicsFixture> CreateAsync(
            byte[] originalSettings,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"spintexture-native-graphics-{Guid.NewGuid():N}");
            var installPath = Path.Combine(root, "EverQuest");
            var workspacePath = Path.Combine(root, "Workspace");
            var settingsPath = Path.Combine(installPath, "eqclient.ini");
            var graphicsDllPath = Path.Combine(installPath, "EQGraphics.dll");
            var gameExecutablePath = Path.Combine(installPath, "eqgame.exe");
            Directory.CreateDirectory(installPath);
            var fixture = new NativeGraphicsFixture(
                root,
                new ProjectPaths(installPath, workspacePath),
                settingsPath,
                graphicsDllPath,
                gameExecutablePath,
                originalSettings.ToArray());
            await File.WriteAllBytesAsync(settingsPath, originalSettings, cancellationToken)
                .ConfigureAwait(false);
            await fixture.WriteCapabilityAsync(cancellationToken).ConfigureAwait(false);
            return fixture;
        }

        public NativeGraphicsSettingsService CreateService(Action<string>? processGate = null) =>
            new(new AtomicFileOperations(), processGate ?? (_ => { }));

        public async Task WriteCapabilityAsync(CancellationToken cancellationToken)
        {
            var uiPath = Path.Combine(
                Paths.InstallPath,
                "uifiles",
                "default",
                "EQUI_AdvancedDisplayOptionsWindow.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(uiPath)!);
            await File.WriteAllTextAsync(uiPath, CapabilityXml, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    GraphicsDllPath,
                    Encoding.ASCII.GetBytes(CapabilityDll),
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    GameExecutablePath,
                    Encoding.ASCII.GetBytes(CapabilityExecutable),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> ReadSettingsTextAsync(CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(SettingsPath, cancellationToken)
                .ConfigureAwait(false);
            var offset = bytes.Length >= 3
                         && bytes[0] == 0xEF
                         && bytes[1] == 0xBB
                         && bytes[2] == 0xBF
                ? 3
                : 0;
            return Encoding.UTF8.GetString(bytes.AsSpan(offset));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
