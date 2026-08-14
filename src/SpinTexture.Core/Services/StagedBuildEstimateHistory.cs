using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public enum HistoricalBuildDurationSource
{
    Unavailable,
    Reported,
    ReportTimestamps,
    BuildDirectoryTimestamps
}

public sealed record HistoricalBuildEstimate(
    string BuildId,
    DateTimeOffset CompletedUtc,
    int SelectedArchives,
    long StagedBytes,
    TimeSpan? Duration,
    HistoricalBuildDurationSource DurationSource);

/// <summary>
/// Reads completed, immutable staged-build metadata to calibrate estimates for
/// an equivalent future build. Payload files are never opened or modified.
/// </summary>
public static class StagedBuildEstimateHistory
{
    private const int OldestSupportedReportSchema = 1;
    private static readonly TimeSpan MaximumCredibleDuration = TimeSpan.FromDays(365);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static HistoricalBuildEstimate? FindLatest(
        ProjectPaths paths,
        UpscaleOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(paths.StagingPath))
        {
            return null;
        }

        HistoricalBuildEstimate? latest = null;
        IEnumerable<string> buildDirectories;
        try
        {
            buildDirectories = Directory.EnumerateDirectories(
                paths.StagingPath,
                "*",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            foreach (var buildDirectory in buildDirectories)
            {
                var candidate = TryReadCandidate(paths, options, buildDirectory);
                if (candidate is not null
                    && (latest is null || candidate.CompletedUtc > latest.CompletedUtc))
                {
                    latest = candidate;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Enumeration can fail after it begins if the workspace is being
            // updated concurrently. Previously inspected candidates remain safe.
        }

        return latest;
    }

    internal static bool OptionsMatch(UpscaleOptions historical, UpscaleOptions requested)
    {
        if (historical.Preset != requested.Preset
            || historical.Scope != requested.Scope
            || historical.MaximumDimension != requested.MaximumDimension
            || historical.GenerateMipMaps != requested.GenerateMipMaps
            || (requested.Preset == TexturePreset.Illustrated
                && historical.PaintedTheme != requested.PaintedTheme))
        {
            return false;
        }

        if (requested.Scope != AssetScope.SelectedZone)
        {
            return true;
        }

        return string.Equals(
            historical.SelectedZone?.Trim(),
            requested.SelectedZone?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static HistoricalBuildEstimate? TryReadCandidate(
        ProjectPaths paths,
        UpscaleOptions requestedOptions,
        string buildDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(buildDirectory, "manifest.json");
            var reportPath = Path.Combine(buildDirectory, "texture-report.json");
            if (!File.Exists(manifestPath) || !File.Exists(reportPath))
            {
                return null;
            }

            var manifest = ReadJson<BuildManifest>(manifestPath);
            var report = ReadJson<TextureBuildReport>(reportPath);
            if (manifest is null
                || report is null
                || manifest.SchemaVersion != BuildManifest.CurrentSchemaVersion
                || report.SchemaVersion is < OldestSupportedReportSchema
                    or > TextureBuildReport.CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(manifest.BuildId)
                || string.IsNullOrWhiteSpace(report.BuildId)
                || string.IsNullOrWhiteSpace(manifest.InstallPath)
                || string.IsNullOrWhiteSpace(report.InstallPath)
                || string.IsNullOrWhiteSpace(report.StagingPath)
                || manifest.Options is null
                || manifest.Entries is not { Count: > 0 }
                || report.Statistics is null
                || report.SelectedArchives <= 0
                || report.CompletedUtc == default
                || report.TexturePipelineRevision != TextureProcessingPipeline.CurrentRevision
                || !manifest.BuildId.Equals(report.BuildId, StringComparison.Ordinal)
                || !DirectoryNameMatchesBuildId(buildDirectory, manifest.BuildId)
                || !SamePath(manifest.InstallPath, paths.InstallPath)
                || !SamePath(report.InstallPath, paths.InstallPath)
                || (!SamePath(report.StagingPath, buildDirectory)
                    && !Path.GetFileName(Path.TrimEndingDirectorySeparator(report.StagingPath))
                        .Equals(manifest.BuildId, StringComparison.Ordinal))
                || !OptionsMatch(manifest.Options, requestedOptions)
                || report.IsIncrementalRepair
                || report.IsSourceMismatchRepair
                || report.WasResumed
                || report.Statistics.ReusedTextures > 0)
            {
                return null;
            }

            long stagedBytes = 0;
            foreach (var entry in manifest.Entries)
            {
                if (entry is null || entry.StagedLength <= 0)
                {
                    return null;
                }

                stagedBytes = checked(stagedBytes + entry.StagedLength);
            }

            if (stagedBytes <= 0)
            {
                return null;
            }

            var (duration, durationSource) = ResolveDuration(report, buildDirectory);
            return new HistoricalBuildEstimate(
                manifest.BuildId,
                report.CompletedUtc,
                report.SelectedArchives,
                stagedBytes,
                duration,
                durationSource);
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException
            or NotSupportedException)
        {
            // A partial, corrupt, foreign, or concurrently changing build is
            // not estimation evidence. Ignore it without touching the pack.
            return null;
        }
    }

    private static (TimeSpan? Duration, HistoricalBuildDurationSource Source) ResolveDuration(
        TextureBuildReport report,
        string buildDirectory)
    {
        if (report.DurationSeconds is > 0
            && double.IsFinite(report.DurationSeconds.Value)
            && report.DurationSeconds.Value <= MaximumCredibleDuration.TotalSeconds)
        {
            var reported = TimeSpan.FromSeconds(report.DurationSeconds.Value);
            if (IsCredible(reported))
            {
                return (reported, HistoricalBuildDurationSource.Reported);
            }
        }

        if (report.StartedUtc is { } startedUtc)
        {
            var timestampDuration = report.CompletedUtc - startedUtc;
            if (IsCredible(timestampDuration))
            {
                return (timestampDuration, HistoricalBuildDurationSource.ReportTimestamps);
            }
        }

        var createdUtc = Directory.GetCreationTimeUtc(buildDirectory);
        if (createdUtc != DateTime.MinValue)
        {
            var directoryDuration = report.CompletedUtc - new DateTimeOffset(createdUtc, TimeSpan.Zero);
            if (IsCredible(directoryDuration))
            {
                return (directoryDuration, HistoricalBuildDurationSource.BuildDirectoryTimestamps);
            }
        }

        return (null, HistoricalBuildDurationSource.Unavailable);
    }

    private static bool IsCredible(TimeSpan duration) =>
        duration > TimeSpan.Zero && duration <= MaximumCredibleDuration;

    private static bool DirectoryNameMatchesBuildId(string buildDirectory, string buildId) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(buildDirectory))
            .Equals(buildId, StringComparison.Ordinal);

    private static bool SamePath(string left, string right)
    {
        var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static T? ReadJson<T>(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
