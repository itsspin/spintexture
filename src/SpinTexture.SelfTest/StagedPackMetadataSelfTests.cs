using System.Text.Json;
using SpinTexture.Core;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

/// <summary>
/// Covers the pack-meta.json sidecar (rename must never affect identity) and
/// leftover-build-debris discovery/cleanup safety.
/// </summary>
internal static class StagedPackMetadataSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-packmeta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var packDirectory = Path.Combine(root, "build-20260101-000000-abc");
            Directory.CreateDirectory(packDirectory);

            TestOptionalTextureReportValidation(packDirectory);

            Assert(
                StagedPackMetadataStore.TryRead(packDirectory) is null,
                "a pack without a sidecar must read as null metadata");

            await StagedPackMetadataStore
                .WriteAsync(packDirectory, "  Freeport Painted v2  ", "my favorite", cancellationToken)
                .ConfigureAwait(false);
            var metadata = StagedPackMetadataStore.TryRead(packDirectory);
            AssertEqual("Freeport Painted v2", metadata?.DisplayName, "display name trims and round-trips");
            AssertEqual("my favorite", metadata?.Notes, "notes round-trip");

            var oversized = new string('x', 500);
            await StagedPackMetadataStore
                .WriteAsync(packDirectory, oversized, null, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                StagedPackUserMetadata.MaximumDisplayNameLength,
                StagedPackMetadataStore.TryRead(packDirectory)?.DisplayName?.Length,
                "display names are bounded");

            await StagedPackMetadataStore
                .WriteAsync(packDirectory, "   ", null, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                StagedPackMetadataStore.TryRead(packDirectory) is null
                && !File.Exists(Path.Combine(packDirectory, StagedPackMetadataStore.FileName)),
                "clearing the name removes the sidecar entirely");

            await File.WriteAllTextAsync(
                Path.Combine(packDirectory, StagedPackMetadataStore.FileName),
                "{ not json",
                cancellationToken).ConfigureAwait(false);
            Assert(
                StagedPackMetadataStore.TryRead(packDirectory) is null,
                "a corrupt sidecar must never fail catalog inspection");

            // Debris discovery: completed packs and checkpointed builds are
            // never debris; unmarked old directories are.
            var installRoot = Path.Combine(root, "install");
            Directory.CreateDirectory(installRoot);
            var paths = new ProjectPaths(installRoot, Path.Combine(root, "workspace"));
            Directory.CreateDirectory(paths.StagingPath);

            var completed = Path.Combine(paths.StagingPath, "build-completed");
            Directory.CreateDirectory(completed);
            await File.WriteAllTextAsync(Path.Combine(completed, "manifest.json"), "{}", cancellationToken)
                .ConfigureAwait(false);
            var resumable = Path.Combine(paths.StagingPath, "build-resumable");
            Directory.CreateDirectory(resumable);
            await File.WriteAllTextAsync(Path.Combine(resumable, "build-checkpoint.json"), "{}", cancellationToken)
                .ConfigureAwait(false);
            var debrisDirectory = Path.Combine(paths.StagingPath, "build-crashed");
            Directory.CreateDirectory(Path.Combine(debrisDirectory, "work"));
            await File.WriteAllTextAsync(
                Path.Combine(debrisDirectory, "work", "partial.tmp"),
                new string('z', 2048),
                cancellationToken).ConfigureAwait(false);

            var freshDebris = StagedPackCatalogService.FindBuildDebris(paths, TimeSpan.FromHours(1));
            Assert(
                freshDebris.Count == 0,
                "recently touched unmarked directories must not be reported as debris");

            var debris = StagedPackCatalogService.FindBuildDebris(paths, TimeSpan.Zero);
            AssertEqual(1, debris.Count, "exactly the unmarked directory is debris");
            AssertEqual("build-crashed", debris[0].Name, "debris identity");
            Assert(debris[0].Bytes >= 2048, "debris size accounts for nested files");

            var cleanup = StagedPackCatalogService.DeleteBuildDebris(paths, debris);
            AssertEqual(1, cleanup.DeletedDirectories, "debris cleanup removes the directory");
            Assert(!Directory.Exists(debrisDirectory), "debris directory is gone");
            Assert(
                Directory.Exists(completed) && Directory.Exists(resumable),
                "completed packs and resumable builds survive cleanup");

            // Re-verification guard: a directory that gained a manifest after
            // discovery must not be deleted.
            var lateCompleted = Path.Combine(paths.StagingPath, "build-late");
            Directory.CreateDirectory(lateCompleted);
            var staleDiscovery = StagedPackCatalogService.FindBuildDebris(paths, TimeSpan.Zero);
            await File.WriteAllTextAsync(Path.Combine(lateCompleted, "manifest.json"), "{}", cancellationToken)
                .ConfigureAwait(false);
            var lateCleanup = StagedPackCatalogService.DeleteBuildDebris(paths, staleDiscovery);
            AssertEqual(0, lateCleanup.DeletedDirectories, "a directory completed after discovery is spared");
            Assert(Directory.Exists(lateCompleted), "late-completed pack survives");

            var changedDebris = Path.Combine(paths.StagingPath, "build-changed");
            Directory.CreateDirectory(changedDebris);
            await File.WriteAllTextAsync(
                Path.Combine(changedDebris, "partial.tmp"),
                "before review",
                cancellationToken).ConfigureAwait(false);
            var changedDiscovery = StagedPackCatalogService.FindBuildDebris(
                paths,
                TimeSpan.Zero);
            var changedFile = Path.Combine(changedDebris, "appeared-after-review.tmp");
            await File.WriteAllTextAsync(
                changedFile,
                "after review",
                cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(changedFile, DateTime.UtcNow.AddMinutes(1));
            var changedCleanup = StagedPackCatalogService.DeleteBuildDebris(
                paths,
                changedDiscovery);
            AssertEqual(0, changedCleanup.DeletedDirectories,
                "leftover changed after review must not be deleted");
            Assert(changedCleanup.Failures.Any(failure =>
                    failure.Contains("changed", StringComparison.OrdinalIgnoreCase)),
                "changed leftover should explain why it was spared");
            Assert(Directory.Exists(changedDebris),
                "changed leftover directory survives for a refreshed review");

            await TestDebrisReparseGuardWhenSupportedAsync(paths, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void TestOptionalTextureReportValidation(string buildDirectory)
    {
        var buildId = Path.GetFileName(buildDirectory);
        var installPath = Path.Combine(Path.GetDirectoryName(buildDirectory)!, "install");
        var statistics = new TextureBuildStatistics(
            DiscoveredTextures: 3,
            EnhancedTextures: 1,
            PreservedTextures: 1,
            SourceTextureBytes: 100,
            EnhancedTextureBytes: 200,
            PreservedReasons: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Protected texture"] = 1
            },
            Warnings: Array.Empty<string>())
        {
            ReusedTextures = 1
        };
        var report = new TextureBuildReport(
            TextureBuildReport.CurrentSchemaVersion,
            buildId,
            DateTimeOffset.UtcNow,
            installPath,
            buildDirectory,
            SelectedArchives: 1,
            statistics);

        Assert(
            TextureBuildReportValidation.IsUsableForStagedPack(
                report,
                buildId,
                installPath,
                buildDirectory),
            "a coherent report matching its staged pack remains usable");

        var semanticNullJson = JsonSerializer.Serialize(report with { Statistics = null! });
        var semanticNullReport = JsonSerializer.Deserialize<TextureBuildReport>(semanticNullJson);
        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                semanticNullReport,
                buildId,
                installPath,
                buildDirectory),
            "valid JSON with null report statistics is ignored instead of taking down pack inspection");

        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                report with
                {
                    Statistics = statistics with
                    {
                        PreservedReasons = null!,
                        Warnings = null!
                    }
                },
                buildId,
                installPath,
                buildDirectory),
            "null report collections are ignored safely");
        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                report with
                {
                    Statistics = statistics with { EnhancedTextures = -1 }
                },
                buildId,
                installPath,
                buildDirectory),
            "negative report counters are ignored safely");
        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                report with { SchemaVersion = TextureBuildReport.CurrentSchemaVersion + 1 },
                buildId,
                installPath,
                buildDirectory),
            "future report schemas are ignored safely");
        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                report with { BuildId = "build-foreign" },
                buildId,
                installPath,
                buildDirectory),
            "foreign report build identity is ignored safely");
        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                report with { InstallPath = Path.Combine(installPath, "other") },
                buildId,
                installPath,
                buildDirectory),
            "foreign report install identity is ignored safely");
        Assert(
            !TextureBuildReportValidation.IsUsableForStagedPack(
                report with
                {
                    StagingPath = Path.Combine(
                        Path.GetDirectoryName(buildDirectory)!,
                        "build-foreign")
                },
                buildId,
                installPath,
                buildDirectory),
            "foreign report staging identity is ignored safely");
    }

    private static async Task TestDebrisReparseGuardWhenSupportedAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-packmeta-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var outsideMarker = Path.Combine(outside, "must-survive.txt");
        await File.WriteAllTextAsync(outsideMarker, "keep", cancellationToken)
            .ConfigureAwait(false);
        var link = Path.Combine(paths.StagingPath, "build-reparse-leftover");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is
                UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
            {
                return;
            }

            try
            {
                _ = StagedPackCatalogService.FindBuildDebris(paths, TimeSpan.Zero);
                throw new InvalidOperationException(
                    "Self-test failed: reparse-point leftover scan should fail closed");
            }
            catch (InvalidDataException)
            {
            }

            Assert(File.Exists(outsideMarker),
                "reparse-point cleanup guard must preserve the external target");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description} (expected {expected}, actual {actual})");
        }
    }
}
