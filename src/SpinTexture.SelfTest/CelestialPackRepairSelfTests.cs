using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class CelestialPackRepairSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await TestWorldCelestialRepairAsync(cancellationToken).ConfigureAwait(false);
        await TestSpellCelestialRepairAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task TestWorldCelestialRepairAsync(CancellationToken cancellationToken) =>
        TestPackAsync(
            "world",
            AssetScope.WorldOnly,
            "sky.s3d",
            "forest.s3d",
            useHistoricalBackupFallback: true,
            cancellationToken);

    private static Task TestSpellCelestialRepairAsync(CancellationToken cancellationToken) =>
        TestPackAsync(
            "spell",
            AssetScope.SpellEffectsOnly,
            Path.Combine("SpellEffects", "fullmoon01.dds"),
            Path.Combine("SpellEffects", "ordinary.dds"),
            useHistoricalBackupFallback: false,
            cancellationToken);

    private static async Task TestPackAsync(
        string suffix,
        AssetScope scope,
        string protectedRelativePath,
        string ordinaryRelativePath,
        bool useHistoricalBackupFallback,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-celestial-pack-repair-{suffix}-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);
        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, "eqgame.exe"),
                "synthetic-eqgame"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (scope == AssetScope.SpellEffectsOnly)
            {
                // Install validation requires at least one legacy asset
                // archive even though this repair scope itself is loose-only.
                await File.WriteAllBytesAsync(
                    Path.Combine(installPath, "validation.s3d"),
                    "synthetic-validation-archive"u8.ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
            var protectedOriginal = System.Text.Encoding.UTF8.GetBytes(
                $"original::{protectedRelativePath}");
            var ordinaryOriginal = System.Text.Encoding.UTF8.GetBytes(
                $"original::{ordinaryRelativePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.Combine(installPath, protectedRelativePath))!);
            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.Combine(installPath, ordinaryRelativePath))!);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, protectedRelativePath),
                protectedOriginal,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(installPath, ordinaryRelativePath),
                ordinaryOriginal,
                cancellationToken).ConfigureAwait(false);

            var badProtected = System.Text.Encoding.UTF8.GetBytes(
                $"enhanced-bad::{protectedRelativePath}");
            var goodOrdinary = System.Text.Encoding.UTF8.GetBytes(
                $"enhanced-good::{ordinaryRelativePath}");
            var outputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [protectedRelativePath] = badProtected,
                [ordinaryRelativePath] = goodOrdinary
            };
            var builder = new DelegateStagedArtifactBuilder(
                (context, token) => File.WriteAllBytesAsync(
                    context.DestinationPath,
                    outputs[context.RelativeInstallPath],
                    token));
            var baseline = await new StagedBuildService().BuildAsync(
                new StagedBuildRequest(
                    paths,
                    new UpscaleOptions(
                        TexturePreset.Faithful,
                        scope,
                        2048,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false),
                    [
                        new StagedBuildItem(protectedRelativePath, builder),
                        new StagedBuildItem(ordinaryRelativePath, builder)
                    ],
                    $"build-celestial-{suffix}"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (var reportStream = new FileStream(
                Path.Combine(baseline.BuildDirectory, "texture-report.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    reportStream,
                    new TextureBuildReport(
                        TextureBuildReport.CurrentSchemaVersion,
                        baseline.BuildId,
                        DateTimeOffset.UtcNow,
                        installPath,
                        baseline.BuildDirectory,
                        2,
                        new TextureBuildStatistics(
                            2,
                            2,
                            0,
                            2,
                            2,
                            new Dictionary<string, int>(),
                            []))
                    {
                        TexturePipelineRevision = 3
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (useHistoricalBackupFallback)
            {
                var store = new ManifestStore();
                var oldDirectory = Path.Combine(paths.BackupPath, "older-valid-originals");
                Directory.CreateDirectory(Path.Combine(oldDirectory, "payload"));
                var historicalEntries = new List<InstalledArtifact>();
                foreach (var (relativePath, original, installed) in new[]
                {
                    (protectedRelativePath, protectedOriginal, badProtected),
                    (ordinaryRelativePath, ordinaryOriginal, goodOrdinary)
                })
                {
                    var backupRelative = Path.Combine("payload", relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(
                        Path.Combine(oldDirectory, backupRelative))!);
                    await File.WriteAllBytesAsync(
                        Path.Combine(oldDirectory, backupRelative),
                        original,
                        cancellationToken).ConfigureAwait(false);
                    var originalFingerprint = Fingerprint(original);
                    var installedFingerprint = Fingerprint(installed);
                    historicalEntries.Add(new InstalledArtifact(
                        relativePath,
                        true,
                        originalFingerprint.Length,
                        originalFingerprint.Sha256,
                        backupRelative,
                        installedFingerprint.Length,
                        installedFingerprint.Sha256));
                    await File.WriteAllBytesAsync(
                        Path.Combine(installPath, relativePath),
                        installed,
                        cancellationToken).ConfigureAwait(false);
                }

                var oldManifestPath = Path.Combine(oldDirectory, "install-manifest.json");
                await store.WriteInstallManifestAsync(
                    oldManifestPath,
                    new InstallManifest(
                        InstallManifest.CurrentSchemaVersion,
                        "older-valid-originals",
                        DateTimeOffset.UtcNow.AddHours(-1),
                        installPath,
                        "older-build",
                        baseline.ManifestPath,
                        InstallTransactionState.Restored,
                        historicalEntries),
                    cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(oldManifestPath, DateTime.UtcNow.AddMinutes(-5));

                // The newest managed transaction intentionally has no matching
                // entry. Repair must scan older exact backups instead of
                // failing merely because the latest manifest cannot help.
                var newerDirectory = Path.Combine(paths.BackupPath, "newer-unrelated");
                Directory.CreateDirectory(Path.Combine(newerDirectory, "payload"));
                var unrelatedOriginal = "unrelated-original"u8.ToArray();
                var unrelatedInstalled = "unrelated-installed"u8.ToArray();
                await File.WriteAllBytesAsync(
                    Path.Combine(newerDirectory, "payload", "unrelated.s3d"),
                    unrelatedOriginal,
                    cancellationToken).ConfigureAwait(false);
                var unrelatedOriginalFingerprint = Fingerprint(unrelatedOriginal);
                var unrelatedInstalledFingerprint = Fingerprint(unrelatedInstalled);
                var newerManifestPath = Path.Combine(newerDirectory, "install-manifest.json");
                await store.WriteInstallManifestAsync(
                    newerManifestPath,
                    new InstallManifest(
                        InstallManifest.CurrentSchemaVersion,
                        "newer-unrelated",
                        DateTimeOffset.UtcNow,
                        installPath,
                        "newer-build",
                        baseline.ManifestPath,
                        InstallTransactionState.Applied,
                        [
                            new InstalledArtifact(
                                "unrelated.s3d",
                                true,
                                unrelatedOriginalFingerprint.Length,
                                unrelatedOriginalFingerprint.Sha256,
                                Path.Combine("payload", "unrelated.s3d"),
                                unrelatedInstalledFingerprint.Length,
                                unrelatedInstalledFingerprint.Sha256)
                        ]),
                    cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(newerManifestPath, DateTime.UtcNow);
            }

            var workflow = new TexturePackWorkflow(
                clientClosedGuard: () => { },
                manifestStore: new ManifestStore(),
                stagedPackCatalogService: new StagedPackCatalogService());
            var repaired = await workflow.RepairStagedPackAsync(
                paths,
                baseline.ManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            AssertEqual(1, repaired.StagedBuild.Manifest.Entries.Count, "replacement entry count");
            AssertEqual(
                ordinaryRelativePath,
                repaired.StagedBuild.Manifest.Entries[0].RelativeInstallPath,
                "ordinary artifact retained");
            Assert(
                !File.Exists(Path.Combine(
                    repaired.StagedBuild.BuildDirectory,
                    "payload",
                    protectedRelativePath)),
                "protected source-identical artifact omitted");
            AssertSequenceEqual(
                goodOrdinary,
                await File.ReadAllBytesAsync(
                    Path.Combine(
                        repaired.StagedBuild.BuildDirectory,
                        "payload",
                        ordinaryRelativePath),
                    cancellationToken).ConfigureAwait(false),
                "ordinary output exact-reused");
            Assert(repaired.Report.IsSafetyRepair, "safety report flag");
            Assert(
                repaired.Report.AppliedRepairRuleIds.Contains(
                    TextureProcessingPipeline.CelestialSkySafetyRuleId,
                    StringComparer.Ordinal),
                "celestial rule recorded");
            AssertSequenceEqual(
                useHistoricalBackupFallback ? badProtected : protectedOriginal,
                await File.ReadAllBytesAsync(
                    Path.Combine(installPath, protectedRelativePath),
                    cancellationToken).ConfigureAwait(false),
                "staging does not mutate protected live source");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static (long Length, string Sha256) Fingerprint(byte[] bytes) =>
        (bytes.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Celestial pack repair assertion failed: {message}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Celestial pack repair assertion failed for {message}: expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequenceEqual(
        IReadOnlyList<byte> expected,
        IReadOnlyList<byte> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Celestial pack repair assertion failed: {message}.");
        }
    }
}
