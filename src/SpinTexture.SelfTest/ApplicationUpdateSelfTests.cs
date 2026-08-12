using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class ApplicationUpdateSelfTests
{
    public static async Task RunAsync(TextWriter output)
    {
        TestReleaseSelection();
        TestArgumentsAndChecksum();
        await TestCheckEndpointAsync().ConfigureAwait(false);
        await TestPreferencesAsync().ConfigureAwait(false);
        await TestPreparedUpdateRollbackAndCommitAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Application update and preferences tests passed.").ConfigureAwait(false);
    }

    private static async Task TestCheckEndpointAsync()
    {
        var json = """
            {
              "tag_name": "v1.2.3",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/itsspin/spintexture/releases/tag/v1.2.3",
              "assets": [
                { "name": "SpinTexture-1.2.3-win-x64.zip", "browser_download_url": "https://example.invalid/update.zip" },
                { "name": "SpinTexture-1.2.3-win-x64.zip.sha256.txt", "browser_download_url": "https://example.invalid/update.sha256.txt" }
              ]
            }
            """;
        using var client = new HttpClient(new StaticJsonHandler(json));
        var service = new ApplicationUpdateService(
            client,
            CreateTempRoot(),
            new Uri("https://example.invalid/latest"));
        var update = await service.CheckAsync(new Version(1, 2, 2)).ConfigureAwait(false);
        Assert(update?.AvailableVersion == new Version(1, 2, 3), "GitHub JSON contract was not parsed.");
    }

    private static void TestReleaseSelection()
    {
        var release = new ApplicationUpdateService.GitHubRelease(
            "v1.2.3",
            Draft: false,
            Prerelease: false,
            "https://github.com/itsspin/spintexture/releases/tag/v1.2.3",
            [
                new("SpinTexture-1.2.3-win-x64.zip", "https://example.invalid/update.zip"),
                new("SpinTexture-1.2.3-win-x64.zip.sha256.txt", "https://example.invalid/update.sha256.txt")
            ]);
        var update = ApplicationUpdateService.ParseRelease(release, new Version(1, 2, 2));
        Assert(update?.AvailableVersion == new Version(1, 2, 3), "New stable release was not selected.");
        Assert(ApplicationUpdateService.ParseRelease(release, new Version(1, 2, 3)) is null,
            "Current release should not prompt.");
        Assert(ApplicationUpdateService.ParseRelease(release with { Prerelease = true }, new Version(1, 0, 0)) is null,
            "Prerelease should not auto-prompt stable users.");
    }

    private static void TestArgumentsAndChecksum()
    {
        Assert(ApplicationUpdateService.ParseChecksum(new string('a', 64) + "  file.zip") == new string('A', 64),
            "Checksum parser rejected a valid SHA-256 file.");
        Assert(!ApplicationUpdateService.TryParseApplyArguments(["--apply-update"], out _),
            "Incomplete update arguments were accepted.");
        Assert(
            ApplicationUpdateService.FormatVersionDisplay("1.2.3+release.sha", null, null) == "v1.2.3",
            "Informational version was not formatted for display.");
        Assert(
            ApplicationUpdateService.FormatVersionDisplay("1.2.3-preview.1+release.sha", null, null)
            == "v1.2.3-preview.1",
            "Prerelease informational version was not formatted for display.");
        Assert(
            ApplicationUpdateService.FormatVersionDisplay("invalid", "2.4.6+product.sha", new Version(9, 9, 9))
            == "v2.4.6",
            "Product version fallback was not formatted for display.");
        Assert(
            ApplicationUpdateService.FormatVersionDisplay(null, null, new Version(3, 5)) == "v3.5.0",
            "Assembly version fallback was not formatted for display.");
        Assert(
            ApplicationUpdateService.FormatVersionDisplay(null, null, new Version(0, 0, 0)) == "Development build",
            "Unversioned builds did not receive a friendly display fallback.");
    }

    private static async Task TestPreferencesAsync()
    {
        var root = CreateTempRoot();
        try
        {
            var install = Path.Combine(root, "EverQuest");
            Directory.CreateDirectory(install);
            await File.WriteAllBytesAsync(Path.Combine(install, "eqgame.exe"), [1]).ConfigureAwait(false);
            var store = new AppPreferencesStore(Path.Combine(root, "settings", "app-settings.json"));
            await store.WriteLastInstallPathAsync(install).ConfigureAwait(false);
            Assert(Path.GetFullPath(install) == store.Read().LastInstallPath, "EQ directory was not remembered.");
            await File.WriteAllTextAsync(Path.Combine(root, "settings", "app-settings.json"), "not-json").ConfigureAwait(false);
            Assert(store.Read() == AppPreferences.Empty, "Corrupt preferences should fail closed to defaults.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestPreparedUpdateRollbackAndCommitAsync()
    {
        var root = CreateTempRoot();
        try
        {
            var destination = Path.Combine(root, "installed");
            var session = Path.Combine(root, "session");
            var payload = Path.Combine(session, "payload");
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(payload);
            // build.ps1 launches this suite through `dotnet <assembly>`, so
            // Environment.ProcessPath can be dotnet.exe with a servicing
            // product-version suffix that is intentionally invalid for a
            // SpinTexture release plan. Exercise the shipped app-host instead.
            var selfTestHost = Path.Combine(AppContext.BaseDirectory, "SpinTexture.SelfTest.exe");
            var currentExe = File.Exists(selfTestHost)
                ? selfTestHost
                : Environment.ProcessPath
                  ?? throw new InvalidOperationException("Self-test executable path is missing.");
            File.Copy(currentExe, Path.Combine(destination, "SpinTexture.exe"));
            File.Copy(currentExe, Path.Combine(payload, "SpinTexture.exe"));
            await File.WriteAllTextAsync(Path.Combine(destination, "old.txt"), "old").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(payload, "new.txt"), "new").ConfigureAwait(false);
            await WriteManifestAsync(destination, ["SpinTexture.exe", "old.txt"]).ConfigureAwait(false);
            await WriteManifestAsync(payload, ["SpinTexture.exe", "new.txt"]).ConfigureAwait(false);
            var version = FileVersionInfo.GetVersionInfo(currentExe).ProductVersion?.Split('+')[0]
                ?? throw new InvalidOperationException("Self-test executable version is missing.");
            var plan = new
            {
                schemaVersion = 1,
                parentProcessId = int.MaxValue,
                parentProcessStartTimeUtcTicks = 1,
                sourceDirectory = payload,
                destinationDirectory = destination,
                executableName = "SpinTexture.exe",
                expectedVersion = version
            };
            var planPath = Path.Combine(session, "update-plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                planPath,
                JsonSerializer.Serialize(plan with { expectedVersion = "9.9.9" })).ConfigureAwait(false);
            await AssertThrowsAsync<InvalidDataException>(() =>
                ApplicationUpdateService.ApplyPreparedUpdateAsync(
                    planPath,
                    restartApplication: false)).ConfigureAwait(false);
            Assert(File.Exists(Path.Combine(destination, "old.txt")), "Failed update did not restore old payload.");
            Assert(!File.Exists(Path.Combine(destination, "new.txt")), "Failed update left new payload installed.");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan)).ConfigureAwait(false);
            await ApplicationUpdateService.ApplyPreparedUpdateAsync(
                planPath,
                restartApplication: false).ConfigureAwait(false);
            Assert(File.Exists(Path.Combine(destination, "new.txt")), "Prepared update did not install new payload.");
            Assert(!File.Exists(Path.Combine(destination, "old.txt")), "Prepared update left obsolete managed payload.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteManifestAsync(string root, IReadOnlyList<string> files)
    {
        var entries = new List<object>();
        foreach (var relative in files)
        {
            var path = Path.Combine(root, relative);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            entries.Add(new { path = relative, bytes = stream.Length, sha256 = hash });
        }

        await File.WriteAllTextAsync(
            Path.Combine(root, "release-manifest.json"),
            JsonSerializer.Serialize(entries)).ConfigureAwait(false);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SpinTexture-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }
}
