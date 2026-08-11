using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class StagedBuildEstimateHistorySelfTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-estimate-history-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var options = new UpscaleOptions(
                TexturePreset.MaximumDetail,
                AssetScope.CharactersAndEquipmentOnly,
                2048,
                GenerateMipMaps: true,
                InstallAfterBuild: false);

            Assert(
                StagedBuildEstimateHistory.FindLatest(paths, options) is null,
                "an absent staging directory must have no historical estimate");

            var legacyCompletedUtc = new DateTimeOffset(2026, 8, 9, 12, 55, 17, TimeSpan.Zero);
            var legacyStartedUtc = legacyCompletedUtc - TimeSpan.FromHours(11.626);
            await CreateBuildAsync(
                    paths,
                    "build-legacy-full",
                    options,
                    legacyCompletedUtc,
                    [125, 875],
                    reportSchemaVersion: 1,
                    durationSeconds: null,
                    startedUtc: null,
                    directoryCreatedUtc: legacyStartedUtc,
                    isIncrementalRepair: false,
                    isSourceMismatchRepair: false,
                    reusedTextures: 0,
                    cancellationToken)
                .ConfigureAwait(false);

            var legacyEstimate = StagedBuildEstimateHistory.FindLatest(paths, options);
            Assert(legacyEstimate is not null, "a valid legacy report should calibrate the estimate");
            AssertEqual(1_000L, legacyEstimate!.StagedBytes, "legacy staged byte total");
            AssertEqual(1_377, legacyEstimate.SelectedArchives, "legacy selected archive count");
            AssertEqual(
                HistoricalBuildDurationSource.BuildDirectoryTimestamps,
                legacyEstimate.DurationSource,
                "legacy duration source");
            Assert(
                legacyEstimate.Duration is { } legacyDuration
                && Math.Abs((legacyDuration - (legacyCompletedUtc - legacyStartedUtc)).TotalSeconds) < 2,
                "legacy directory timestamps should recover the measured build duration");

            Assert(
                StagedBuildEstimateHistory.OptionsMatch(
                    options with { SelectedZone = "ignored", InstallAfterBuild = true },
                    options),
                "install-after-build and an irrelevant non-zone value must not split workload history");
            var zoneOptions = options with
            {
                Scope = AssetScope.SelectedZone,
                SelectedZone = "hateplane"
            };
            Assert(
                StagedBuildEstimateHistory.OptionsMatch(
                    zoneOptions with { SelectedZone = " HATEPLANE " },
                    zoneOptions),
                "selected-zone matching should be trimmed and case-insensitive");
            Assert(
                !StagedBuildEstimateHistory.OptionsMatch(
                    zoneOptions with { SelectedZone = "fearplane" },
                    zoneOptions),
                "different selected zones must not share timing history");

            var reportedCompletedUtc = legacyCompletedUtc.AddDays(1);
            await CreateBuildAsync(
                    paths,
                    "build-reported-full",
                    options,
                    reportedCompletedUtc,
                    [2_000, 3_000],
                    reportSchemaVersion: TextureBuildReport.CurrentSchemaVersion,
                    durationSeconds: 7_200,
                    startedUtc: reportedCompletedUtc - TimeSpan.FromHours(9),
                    directoryCreatedUtc: reportedCompletedUtc - TimeSpan.FromHours(10),
                    isIncrementalRepair: false,
                    isSourceMismatchRepair: false,
                    reusedTextures: 0,
                    cancellationToken)
                .ConfigureAwait(false);

            var reportedEstimate = StagedBuildEstimateHistory.FindLatest(paths, options);
            Assert(reportedEstimate is not null, "the latest equivalent full build should be selected");
            AssertEqual("build-reported-full", reportedEstimate!.BuildId, "latest full build id");
            AssertEqual(5_000L, reportedEstimate.StagedBytes, "latest staged byte total");
            AssertEqual(
                HistoricalBuildDurationSource.Reported,
                reportedEstimate.DurationSource,
                "explicit report duration source");
            AssertEqual(TimeSpan.FromHours(2), reportedEstimate.Duration, "explicit report duration");

            await CreateBuildAsync(
                    paths,
                    "build-newer-repair",
                    options,
                    reportedCompletedUtc.AddHours(1),
                    [6_000],
                    reportSchemaVersion: TextureBuildReport.CurrentSchemaVersion,
                    durationSeconds: 120,
                    startedUtc: reportedCompletedUtc.AddMinutes(58),
                    directoryCreatedUtc: reportedCompletedUtc,
                    isIncrementalRepair: true,
                    isSourceMismatchRepair: false,
                    reusedTextures: 42,
                    cancellationToken)
                .ConfigureAwait(false);

            var afterRepair = StagedBuildEstimateHistory.FindLatest(paths, options);
            AssertEqual(
                "build-reported-full",
                afterRepair?.BuildId,
                "incremental repair timing must not underestimate a fresh build");

            await CreateBuildAsync(
                    paths,
                    "build-newer-source-repair",
                    options,
                    reportedCompletedUtc.AddHours(2),
                    [8_000],
                    reportSchemaVersion: TextureBuildReport.CurrentSchemaVersion,
                    durationSeconds: 30,
                    startedUtc: reportedCompletedUtc.AddHours(2).AddSeconds(-30),
                    directoryCreatedUtc: reportedCompletedUtc.AddHours(2).AddSeconds(-30),
                    isIncrementalRepair: false,
                    isSourceMismatchRepair: true,
                    reusedTextures: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                "build-reported-full",
                StagedBuildEstimateHistory.FindLatest(paths, options)?.BuildId,
                "source-mismatch repair timing must not underestimate a fresh build");

            var corruptDirectory = Path.Combine(paths.StagingPath, "build-corrupt-newest");
            Directory.CreateDirectory(corruptDirectory);
            await File.WriteAllTextAsync(
                    Path.Combine(corruptDirectory, "manifest.json"),
                    "{ definitely-not-json",
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(corruptDirectory, "texture-report.json"),
                    "{}",
                    cancellationToken)
                .ConfigureAwait(false);
            var afterCorrupt = StagedBuildEstimateHistory.FindLatest(paths, options);
            AssertEqual(
                "build-reported-full",
                afterCorrupt?.BuildId,
                "corrupt or partial packs should be ignored safely");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CreateBuildAsync(
        ProjectPaths paths,
        string buildId,
        UpscaleOptions options,
        DateTimeOffset completedUtc,
        IReadOnlyList<long> stagedLengths,
        int reportSchemaVersion,
        double? durationSeconds,
        DateTimeOffset? startedUtc,
        DateTimeOffset directoryCreatedUtc,
        bool isIncrementalRepair,
        bool isSourceMismatchRepair,
        int reusedTextures,
        CancellationToken cancellationToken)
    {
        paths.EnsureWorkspaceDirectories();
        var buildDirectory = Path.Combine(paths.StagingPath, buildId);
        Directory.CreateDirectory(buildDirectory);
        var entries = stagedLengths
            .Select((length, index) => new BuildManifestEntry(
                $"archive-{index}.s3d",
                SourceLength: 100 + index,
                SourceSha256: new string('a', 64),
                StagedLength: length,
                StagedSha256: new string('b', 64)))
            .ToArray();
        var manifest = new BuildManifest(
            BuildManifest.CurrentSchemaVersion,
            buildId,
            completedUtc.AddSeconds(-1),
            paths.InstallPath,
            options,
            entries);
        var statistics = new TextureBuildStatistics(
            DiscoveredTextures: 1,
            EnhancedTextures: 1,
            PreservedTextures: 0,
            SourceTextureBytes: 100,
            EnhancedTextureBytes: 200,
            PreservedReasons: new Dictionary<string, int>(),
            Warnings: [])
        {
            ReusedTextures = reusedTextures
        };
        var report = new TextureBuildReport(
            reportSchemaVersion,
            buildId,
            completedUtc,
            paths.InstallPath,
            buildDirectory,
            SelectedArchives: 1_377,
            statistics)
        {
            StartedUtc = startedUtc,
            DurationSeconds = durationSeconds,
            IsIncrementalRepair = isIncrementalRepair,
            IsSourceMismatchRepair = isSourceMismatchRepair
        };

        await WriteJsonAsync(
                Path.Combine(buildDirectory, "manifest.json"),
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(buildDirectory, "texture-report.json"),
                report,
                cancellationToken)
            .ConfigureAwait(false);
        Directory.SetCreationTimeUtc(buildDirectory, directoryCreatedUtc.UtcDateTime);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}: expected {expected}, actual {actual}");
        }
    }
}
