using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public sealed record ApplicationUpdateInfo(
    Version CurrentVersion,
    Version AvailableVersion,
    Uri ReleasePage,
    Uri ArchiveDownload,
    Uri ChecksumDownload,
    string ArchiveName);

public sealed class ApplicationUpdateService
{
    public const string ApplyUpdateArgument = "--apply-update";
    private const string LatestReleaseEndpoint = "https://api.github.com/repos/itsspin/spintexture/releases/latest";
    private const long MaximumArchiveBytes = 512L * 1024L * 1024L;
    private const long MaximumExtractedBytes = 1024L * 1024L * 1024L;
    private static readonly Regex StableVersionPattern = new(
        "^v?(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DisplayVersionPattern = new(
        "^v?(?<version>[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly string updateRoot;
    private readonly Uri latestReleaseEndpoint;

    public ApplicationUpdateService(
        HttpClient? client = null,
        string? updateRoot = null,
        Uri? latestReleaseEndpoint = null)
    {
        this.client = client ?? CreateClient();
        this.updateRoot = updateRoot is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpinTexture",
                "Updates")
            : Path.GetFullPath(updateRoot);
        this.latestReleaseEndpoint = latestReleaseEndpoint ?? new Uri(LatestReleaseEndpoint);
    }

    public async Task<ApplicationUpdateInfo?> CheckAsync(
        Version? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        currentVersion ??= GetCurrentVersion();
        using var response = await client.GetAsync(latestReleaseEndpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
        return ParseRelease(release, currentVersion);
    }

    public static string GetCurrentVersionDisplay()
    {
        Assembly? entryAssembly = null;
        string? informationalVersion = null;
        string? productVersion = null;
        Version? assemblyVersion = null;

        try
        {
            entryAssembly = Assembly.GetEntryAssembly();
            informationalVersion = entryAssembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            assemblyVersion = entryAssembly?.GetName().Version;
        }
        catch
        {
            // Version text must never prevent the application from opening.
        }

        try
        {
            var executablePath = entryAssembly is not null && !string.IsNullOrWhiteSpace(entryAssembly.Location)
                ? entryAssembly.Location
                : Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            }
        }
        catch
        {
            // Assembly metadata below remains a safe fallback for single-file releases.
        }

        return FormatVersionDisplay(informationalVersion, productVersion, GetCurrentVersion(assemblyVersion));
    }

    public static Version GetCurrentVersion()
    {
        try
        {
            return GetCurrentVersion(Assembly.GetEntryAssembly()?.GetName().Version);
        }
        catch
        {
            return new Version(0, 0, 0);
        }
    }

    internal static string FormatVersionDisplay(
        string? informationalVersion,
        string? productVersion,
        Version? assemblyVersion)
    {
        foreach (var candidate in new[] { informationalVersion, productVersion })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = candidate.Trim().Split('+', 2)[0];
            var match = DisplayVersionPattern.Match(normalized);
            if (match.Success)
            {
                return $"v{match.Groups["version"].Value}";
            }
        }

        if (assemblyVersion is not null
            && (assemblyVersion.Major != 0 || assemblyVersion.Minor != 0 || assemblyVersion.Build > 0))
        {
            var build = assemblyVersion.Build < 0 ? 0 : assemblyVersion.Build;
            return $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{build}";
        }

        return "Development build";
    }

    private static Version GetCurrentVersion(Version? version)
    {
        return version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public async Task<string> DownloadAndPrepareAsync(
        ApplicationUpdateInfo update,
        string installDirectory,
        string executableName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        var normalizedInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        if (!Directory.Exists(normalizedInstall))
        {
            throw new DirectoryNotFoundException("The SpinTexture installation directory no longer exists.");
        }

        Directory.CreateDirectory(updateRoot);
        var sessionRoot = Path.Combine(updateRoot, $"v{update.AvailableVersion}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionRoot);
        try
        {
            var archivePath = Path.Combine(sessionRoot, update.ArchiveName);
            var checksumPath = archivePath + ".sha256.txt";
            await DownloadFileAsync(update.ChecksumDownload, checksumPath, null, cancellationToken).ConfigureAwait(false);
            await DownloadFileAsync(update.ArchiveDownload, archivePath, progress, cancellationToken).ConfigureAwait(false);
            var expectedHash = ParseChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false));
            var archiveInfo = new FileInfo(archivePath);
            if (archiveInfo.Length is <= 0 or > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The downloaded update archive has an unexpected size.");
            }

            var actualHash = (await FileIntegrity.FingerprintAsync(archivePath, cancellationToken)
                .ConfigureAwait(false)).Sha256;
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded update failed its published SHA-256 check.");
            }

            var payloadRoot = Path.Combine(sessionRoot, "payload");
            Directory.CreateDirectory(payloadRoot);
            ExtractSafely(archivePath, payloadRoot);
            await ValidatePayloadAsync(payloadRoot, executableName, cancellationToken).ConfigureAwait(false);

            var helperPath = Path.Combine(sessionRoot, "SpinTexture.UpdateHost.exe");
            File.Copy(Path.Combine(payloadRoot, executableName), helperPath, overwrite: false);
            var plan = new UpdateApplyPlan(
                SchemaVersion: 1,
                ParentProcessId: Environment.ProcessId,
                ParentProcessStartTimeUtcTicks: Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                SourceDirectory: payloadRoot,
                DestinationDirectory: normalizedInstall,
                ExecutableName: executableName,
                ExpectedVersion: update.AvailableVersion.ToString(3));
            var planPath = Path.Combine(sessionRoot, "update-plan.json");
            await File.WriteAllTextAsync(
                planPath,
                JsonSerializer.Serialize(plan, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            return planPath;
        }
        catch
        {
            TryDeleteDirectory(sessionRoot);
            throw;
        }
    }

    public static bool TryParseApplyArguments(
        IReadOnlyList<string>? arguments,
        out string planPath)
    {
        planPath = string.Empty;
        if (arguments is not { Count: 2 }
            || !arguments[0].Equals(ApplyUpdateArgument, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[1]))
        {
            return false;
        }

        try
        {
            planPath = Path.GetFullPath(arguments[1]);
            return File.Exists(planPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static ProcessStartInfo CreateApplyStartInfo(string planPath)
    {
        var fullPlanPath = Path.GetFullPath(planPath);
        var sessionRoot = Path.GetDirectoryName(fullPlanPath)
            ?? throw new InvalidOperationException("The update plan has no parent directory.");
        var helperPath = Path.Combine(sessionRoot, "SpinTexture.UpdateHost.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("The prepared updater helper is missing.", helperPath);
        }

        var startInfo = new ProcessStartInfo(helperPath)
        {
            WorkingDirectory = sessionRoot,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(ApplyUpdateArgument);
        startInfo.ArgumentList.Add(fullPlanPath);
        return startInfo;
    }

    public static async Task ApplyPreparedUpdateAsync(
        string planPath,
        CancellationToken cancellationToken = default,
        bool restartApplication = true)
    {
        var fullPlanPath = Path.GetFullPath(planPath);
        var sessionRoot = Path.GetDirectoryName(fullPlanPath)
            ?? throw new InvalidOperationException("The update plan has no parent directory.");
        var plan = JsonSerializer.Deserialize<UpdateApplyPlan>(
            await File.ReadAllTextAsync(fullPlanPath, cancellationToken).ConfigureAwait(false),
            JsonOptions)
            ?? throw new InvalidDataException("The update plan is empty.");
        ValidatePlan(plan, sessionRoot);
        await WaitForExitAsync(plan, cancellationToken).ConfigureAwait(false);

        var backupRoot = Path.Combine(sessionRoot, "rollback");
        Directory.CreateDirectory(backupRoot);
        var payloadManifest = await ReadManifestAsync(plan.SourceDirectory, cancellationToken).ConfigureAwait(false);
        var installedManifest = await ReadManifestAsync(plan.DestinationDirectory, cancellationToken).ConfigureAwait(false);
        var oldFiles = installedManifest.Select(entry => entry.Path)
            .Append("release-manifest.json")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var newFiles = payloadManifest.Select(entry => entry.Path)
            .Append("release-manifest.json")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var committed = false;
        var copiedNewFiles = new List<string>();
        try
        {
            foreach (var relativePath in oldFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResolveUnderRoot(plan.DestinationDirectory, relativePath);
                var backup = ResolveUnderRoot(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(source, backup);
            }

            foreach (var relativePath in newFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResolveUnderRoot(plan.SourceDirectory, relativePath);
                var destination = ResolveUnderRoot(plan.DestinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
                copiedNewFiles.Add(relativePath);
            }

            await ValidatePayloadAsync(plan.DestinationDirectory, plan.ExecutableName, cancellationToken)
                .ConfigureAwait(false);
            var installedExecutablePath = Path.Combine(plan.DestinationDirectory, plan.ExecutableName);
            var actualVersion = FileVersionInfo.GetVersionInfo(installedExecutablePath).ProductVersion;
            if (string.IsNullOrWhiteSpace(actualVersion)
                || !actualVersion.StartsWith(plan.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installed update does not report the expected version.");
            }

            committed = true;
        }
        finally
        {
            if (!committed)
            {
                foreach (var relativePath in copiedNewFiles.AsEnumerable().Reverse())
                {
                    var path = ResolveUnderRoot(plan.DestinationDirectory, relativePath);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }

                foreach (var relativePath in EnumerateRelativeFiles(backupRoot))
                {
                    var source = ResolveUnderRoot(backupRoot, relativePath);
                    var destination = ResolveUnderRoot(plan.DestinationDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(source, destination);
                }
            }
        }

        var executablePath = Path.Combine(plan.DestinationDirectory, plan.ExecutableName);
        if (restartApplication)
        {
            Process.Start(new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = plan.DestinationDirectory,
                UseShellExecute = true
            });
        }
    }

    public void CleanupCompletedUpdates()
    {
        if (!Directory.Exists(updateRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(updateRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    TryDeleteDirectory(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cleanup is best-effort and never blocks startup or an active update.
            }
        }
    }

    internal static ApplicationUpdateInfo? ParseRelease(GitHubRelease release, Version currentVersion)
    {
        if (release.Draft || release.Prerelease)
        {
            return null;
        }

        var match = StableVersionPattern.Match(release.TagName ?? string.Empty);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var availableVersion))
        {
            throw new InvalidDataException("The latest GitHub release tag is not a stable semantic version.");
        }

        if (availableVersion <= currentVersion)
        {
            return null;
        }

        var archiveName = $"SpinTexture-{availableVersion.ToString(3)}-win-x64.zip";
        var archive = release.Assets.SingleOrDefault(asset =>
            asset.Name.Equals(archiveName, StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.SingleOrDefault(asset =>
            asset.Name.Equals(archiveName + ".sha256.txt", StringComparison.OrdinalIgnoreCase));
        if (archive is null || checksum is null)
        {
            throw new InvalidDataException("The latest GitHub release is missing its Windows archive or checksum.");
        }

        return new ApplicationUpdateInfo(
            currentVersion,
            availableVersion,
            new Uri(release.HtmlUrl, UriKind.Absolute),
            new Uri(archive.DownloadUrl, UriKind.Absolute),
            new Uri(checksum.DownloadUrl, UriKind.Absolute),
            archiveName);
    }

    internal static string ParseChecksum(string text)
    {
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The published checksum file is invalid.");
        }

        return token.ToUpperInvariant();
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The update download is larger than the allowed release size.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 128];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The update download exceeded the allowed release size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (length is > 0)
            {
                progress?.Report(total * 100d / length.Value);
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ExtractSafely(string archivePath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes)
            {
                throw new InvalidDataException("The update expands beyond the allowed release size.");
            }

            var destination = ResolveUnderRoot(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static async Task ValidatePayloadAsync(
        string root,
        string executableName,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(root, cancellationToken).ConfigureAwait(false);
        if (!manifest.Any(entry => entry.Path.Equals(executableName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The release manifest does not contain SpinTexture.exe.");
        }

        var actualFiles = EnumerateRelativeFiles(root)
            .Where(path => !path.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedFiles = manifest.Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!actualFiles.SequenceEqual(expectedFiles, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update payload contains missing or unexpected files.");
        }

        foreach (var entry in manifest)
        {
            var path = ResolveUnderRoot(root, entry.Path);
            await FileIntegrity.EnsureMatchesAsync(
                path,
                entry.Bytes,
                entry.Sha256,
                "Release payload",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<ReleaseManifestEntry>> ReadManifestAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(root, "release-manifest.json");
        var manifest = JsonSerializer.Deserialize<List<ReleaseManifestEntry>>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            JsonOptions)
            ?? throw new InvalidDataException("The release manifest is empty.");
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest)
        {
            if (string.IsNullOrWhiteSpace(entry.Path)
                || entry.Bytes < 0
                || string.IsNullOrWhiteSpace(entry.Sha256)
                || entry.Sha256.Length != 64
                || !entry.Sha256.All(Uri.IsHexDigit)
                || !distinct.Add(entry.Path))
            {
                throw new InvalidDataException("The release manifest contains an invalid entry.");
            }

            _ = ResolveUnderRoot(root, entry.Path);
        }

        return manifest;
    }

    private static void ValidatePlan(UpdateApplyPlan plan, string sessionRoot)
    {
        if (plan.SchemaVersion != 1
            || plan.ParentProcessId <= 0
            || plan.ParentProcessStartTimeUtcTicks <= 0
            || string.IsNullOrWhiteSpace(plan.ExecutableName)
            || Path.GetFileName(plan.ExecutableName) != plan.ExecutableName
            || !StableVersionPattern.IsMatch(plan.ExpectedVersion))
        {
            throw new InvalidDataException("The prepared update plan is invalid.");
        }

        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.SourceDirectory));
        var expectedSource = Path.Combine(sessionRoot, "payload");
        if (!source.Equals(expectedSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update payload path is invalid.");
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.DestinationDirectory));
        if (!Directory.Exists(destination)
            || !File.Exists(Path.Combine(destination, plan.ExecutableName)))
        {
            throw new InvalidDataException("The destination is not a complete SpinTexture installation.");
        }
    }

    private static async Task WaitForExitAsync(UpdateApplyPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(plan.ParentProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != plan.ParentProcessStartTimeUtcTicks)
            {
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The parent already exited.
        }
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("An update path must be relative.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!resolved.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An update path escapes its destination.");
        }

        return resolved;
    }

    private static IEnumerable<string> EnumerateRelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path));

    private static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        result.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpinTexture", "1.0"));
        result.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return result;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

    internal sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
    private sealed record ReleaseManifestEntry(string Path, long Bytes, string Sha256);
    private sealed record UpdateApplyPlan(
        int SchemaVersion,
        int ParentProcessId,
        long ParentProcessStartTimeUtcTicks,
        string SourceDirectory,
        string DestinationDirectory,
        string ExecutableName,
        string ExpectedVersion);
}
