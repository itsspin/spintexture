using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.Core.Tooling;

/// <summary>
/// Stable identity for the executable/script and style configuration that
/// actually define an external Graphic Painted render. The generated worker's
/// script embeds its pinned model/tool paths, while worker-config.json carries
/// every user-editable diffusion setting.
/// </summary>
internal sealed record ArtisticWorkerIdentity(
    string Fingerprint,
    string? Preset);

internal static class ArtisticWorkerIdentityProvider
{
    private static readonly ConcurrentDictionary<FileCacheKey, Lazy<Task<string>>> FileHashes = new();

    public static async Task<ArtisticWorkerIdentity?> ResolveAsync(
        ExternalToolPaths tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (!tools.HasArtisticWorker)
        {
            return null;
        }

        var workerPath = Path.GetFullPath(tools.ArtisticWorkerPath!);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException(
                "The configured artistic worker is missing.",
                workerPath);
        }

        var configPath = Path.Combine(
            Path.GetDirectoryName(workerPath)
                ?? throw new InvalidDataException("The artistic worker has no parent directory."),
            "worker-config.json");
        var files = new List<(string Role, string Path)>();
        var declaredIdentities = new List<(string Role, string Value)>();

        var workerDirectory = Path.GetDirectoryName(workerPath)!;
        var publishedScript = await TryResolvePublishedScriptAsync(
                workerPath,
                workerDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (publishedScript is not null)
        {
            var shimText = await File.ReadAllTextAsync(workerPath, cancellationToken)
                .ConfigureAwait(false);
            var scriptText = await File.ReadAllTextAsync(publishedScript, cancellationToken)
                .ConfigureAwait(false);
            var publishedPaths = ParsePublishedWorkerPaths(
                scriptText,
                Path.GetDirectoryName(publishedScript)!);
            configPath = publishedPaths.ConfigPath ?? configPath;
            if (publishedPaths.ConfigPath is not null && !File.Exists(configPath))
            {
                throw new InvalidDataException(
                    "The published artistic worker is missing its declared configuration.");
            }
            var generationDirectory = Path.GetDirectoryName(publishedScript)!;
            var manifestPath = Path.Combine(generationDirectory, "worker-identity.json");
            var isManagedGeneration = File.Exists(manifestPath);
            var isRecognizedLegacyGeneration = !isManagedGeneration
                && IsRecognizedLegacyGeneration(workerDirectory, publishedPaths);
            if (isManagedGeneration || isRecognizedLegacyGeneration)
            {
                declaredIdentities.Add((
                    "worker-shim-semantics",
                    HashText(CanonicalizePublishedShimForIdentity(shimText))));
                declaredIdentities.Add((
                    "published-script-semantics",
                    HashText(CanonicalizePublishedScriptForIdentity(scriptText))));
            }
            else
            {
                // An opaque custom wrapper may point at arbitrary renderer
                // dependencies. Its paths are semantic, not a volatile
                // SpinTexture generation ID, so preserve exact script bytes.
                files.Add(("worker", workerPath));
                files.Add(("published-script", publishedScript));
                AddOpaquePublishedDependencies(publishedPaths, files);
            }

            if (isManagedGeneration)
            {
                if (publishedPaths.ConfigPath is null || !File.Exists(configPath))
                {
                    throw new InvalidDataException(
                        "The published artistic worker is missing its declared worker-config.json.");
                }

                var manifest = await ReadManifestAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false);
                ValidateManifest(manifest);
                var declaredSdCli = manifest.RuntimeFiles.Single(item =>
                    item.RelativePath.Replace('\\', '/').EndsWith(
                        "/sd-cli.exe",
                        StringComparison.OrdinalIgnoreCase));
                if (publishedPaths.SdCliPath is null
                    || !PathGuard.SamePath(
                        ResolveUnderRoot(generationDirectory, declaredSdCli.RelativePath),
                        publishedPaths.SdCliPath))
                {
                    throw new InvalidDataException(
                        "The published artistic worker does not invoke the setup-verified sd-cli runtime.");
                }
                foreach (var runtime in manifest.RuntimeFiles.OrderBy(
                             item => item.RelativePath,
                             StringComparer.Ordinal))
                {
                    var runtimePath = ResolveUnderRoot(
                        generationDirectory,
                        runtime.RelativePath);
                    EnsureLength(runtimePath, runtime.Length, "runtime");
                    var runtimeSha = await GetFileHashAsync(runtimePath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!runtimeSha.Equals(runtime.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"The artistic worker runtime no longer matches its setup-verified SHA-256: {runtimePath}");
                    }

                    files.Add(($"runtime:{runtime.RelativePath}", runtimePath));
                    declaredIdentities.Add((
                        $"runtime-manifest:{runtime.RelativePath}",
                        $"{runtime.Length}:{runtime.Sha256.ToLowerInvariant()}"));
                }

                foreach (var component in manifest.Components)
                {
                    var matchingComponentPaths = new[]
                        {
                            publishedPaths.ModelPath,
                            publishedPaths.ControlNetPath
                        }
                        .Where(path => path is not null)
                        .Select(path => path!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(path => Path.GetFileName(path).Equals(
                            component.FileName,
                            StringComparison.Ordinal))
                        .ToArray();
                    if (matchingComponentPaths.Length != 1)
                    {
                        throw new InvalidDataException(
                            $"The published artistic worker does not reference {component.FileName} from its verified identity manifest.");
                    }
                    var componentPath = matchingComponentPaths[0];

                    EnsureLength(componentPath, component.Length, "model component");
                    await ValidatePinnedComponentAsync(
                            componentPath,
                            component,
                            cancellationToken)
                        .ConfigureAwait(false);
                    declaredIdentities.Add((
                        $"component:{component.FileName}",
                        $"{component.Length}:{component.Sha256.ToLowerInvariant()}"));
                }
            }
            else if (isRecognizedLegacyGeneration)
            {
                if (publishedPaths.ConfigPath is not null && !File.Exists(configPath))
                {
                    throw new InvalidDataException(
                        "The published artistic worker is missing its declared configuration.");
                }

                await AddLegacyGeneratedIdentityAsync(
                        workerDirectory,
                        files,
                        declaredIdentities,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            AddRealEsrganIdentity(
                publishedPaths.RealEsrganPath,
                publishedPaths.RealEsrganModelsPath,
                files);
        }
        else
        {
            // A custom executable (or a script that is not a generated
            // published shim) has no volatile generation path to normalize;
            // its exact bytes are its renderer identity.
            files.Add(("worker", workerPath));
            AddRealEsrganIdentity(
                tools.RealEsrganPath,
                tools.RealEsrganModelsPath,
                files);
        }

        if (File.Exists(configPath))
        {
            files.Add(("config", configPath));
        }

        using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.Path);
            var cacheKey = new FileCacheKey(
                Path.GetFullPath(file.Path),
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            var contentHash = await FileHashes.GetOrAdd(
                    cacheKey,
                    static key => new Lazy<Task<string>>(
                        () => HashFileAsync(key.Path),
                        LazyThreadSafetyMode.ExecutionAndPublication))
                .Value
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            Append(identityHash, file.Role);
            Append(identityHash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(identityHash, contentHash);
        }
        foreach (var identity in declaredIdentities.OrderBy(
                     item => item.Role,
                     StringComparer.Ordinal))
        {
            Append(identityHash, identity.Role);
            Append(identityHash, identity.Value);
        }

        string? preset = null;
        if (File.Exists(configPath))
        {
            try
            {
                using var config = JsonDocument.Parse(File.ReadAllText(configPath));
                if (config.RootElement.TryGetProperty("preset", out var presetElement)
                    && presetElement.ValueKind == JsonValueKind.String)
                {
                    preset = presetElement.GetString();
                }
            }
            catch (JsonException)
            {
                // The worker itself will reject malformed configuration. Its
                // exact bytes still fence cache/resume/repair identity here.
            }
        }

        return new ArtisticWorkerIdentity(
            Convert.ToHexStringLower(identityHash.GetHashAndReset()),
            string.IsNullOrWhiteSpace(preset) ? null : preset.Trim());
    }

    private static async Task<string?> TryResolvePublishedScriptAsync(
        string workerPath,
        string workerDirectory,
        CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(workerPath).Equals(".bat", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(workerPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var shim = await File.ReadAllTextAsync(workerPath, cancellationToken)
            .ConfigureAwait(false);
        var match = Regex.Match(
            shim,
            "-File\\s+(?:\"(?<double>[^\"]+)\"|'(?<single>[^']+)'|(?<bare>[^\\s&|<>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            if (Regex.IsMatch(
                    shim,
                    "(?:^|\\s)-File(?:\\s|$)",
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant
                    | RegexOptions.Multiline))
            {
                throw new InvalidDataException(
                    "The artistic worker wrapper contains an unsupported or incomplete PowerShell -File reference.");
            }

            return null;
        }

        var scriptReference = new[] { "double", "single", "bare" }
            .Select(name => match.Groups[name])
            .First(group => group.Success)
            .Value
            .Replace("%~dp0", workerDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        var scriptPath = Path.IsPathFullyQualified(scriptReference)
            ? Path.GetFullPath(scriptReference)
            : Path.GetFullPath(Path.Combine(workerDirectory, scriptReference));
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                "The published artistic worker generation is missing its PowerShell script.",
                scriptPath);
        }

        return scriptPath;
    }

    private static async Task<WorkerIdentityManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<WorkerIdentityManifest>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The artistic worker identity manifest is empty.");
    }

    private static void ValidateManifest(WorkerIdentityManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || manifest.RuntimeFiles is null
            || manifest.RuntimeFiles.Count == 0
            || manifest.RuntimeFiles.Any(item =>
                item is null
                || string.IsNullOrWhiteSpace(item.RelativePath)
                || item.Length <= 0
                || !IsSha256(item.Sha256))
            || manifest.Components is null
            || manifest.Components.Count != 2
            || manifest.Components.Any(item =>
                item is null
                || string.IsNullOrWhiteSpace(item.FileName)
                || item.Length <= 0
                || !IsSha256(item.Sha256)))
        {
            throw new InvalidDataException("The artistic worker identity manifest is invalid.");
        }

        if (manifest.RuntimeFiles
                .GroupBy(
                    item => item.RelativePath.Replace('\\', '/'),
                    StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1)
            || manifest.RuntimeFiles.Count(item =>
                item.RelativePath.Replace('\\', '/').EndsWith(
                    "/sd-cli.exe",
                    StringComparison.OrdinalIgnoreCase)) != 1
            || manifest.Components
                .GroupBy(item => item.FileName, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "The artistic worker identity manifest contains duplicate or ambiguous entries.");
        }

        foreach (var expected in ArtisticWorkerSetupService.Components
                     .Where(component => !component.IsZipArchive))
        {
            var actual = manifest.Components.SingleOrDefault(component =>
                component.FileName.Equals(expected.FileName, StringComparison.Ordinal));
            if (actual is null
                || actual.Length != expected.SizeBytes
                || !actual.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The artistic worker identity manifest does not match pinned component {expected.FileName}.");
            }
        }
    }

    private static PublishedWorkerPaths ParsePublishedWorkerPaths(
        string script,
        string scriptDirectory)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configMatch = Regex.Match(
            script,
            "^\\$config\\s*=\\s*Get-Content\\s+'(?<path>(?:''|[^'])*)'\\s*\\|\\s*ConvertFrom-Json\\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (configMatch.Success)
        {
            paths["config"] = ResolveScriptPath(
                configMatch.Groups["path"].Value.Replace("''", "'", StringComparison.Ordinal),
                scriptDirectory);
        }

        foreach (Match match in Regex.Matches(
                     script,
                     "^\\$(?<role>config|sdCli|model|controlNet|realEsrgan|realEsrganModels)\\s*=\\s*'(?<path>(?:''|[^'])*)'",
                     RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            var path = match.Groups["path"].Value.Replace("''", "'", StringComparison.Ordinal);
            paths[match.Groups["role"].Value] = ResolveScriptPath(path, scriptDirectory);
        }

        paths.TryGetValue("config", out var config);
        paths.TryGetValue("sdCli", out var sdCli);
        paths.TryGetValue("model", out var model);
        paths.TryGetValue("controlNet", out var controlNet);
        paths.TryGetValue("realEsrgan", out var realEsrgan);
        paths.TryGetValue("realEsrganModels", out var realEsrganModels);
        return new PublishedWorkerPaths(
            config,
            sdCli,
            model,
            controlNet,
            realEsrgan,
            realEsrganModels);

        static string ResolveScriptPath(string path, string baseDirectory) =>
            Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    internal static string CanonicalizePublishedShimForIdentity(string shim)
    {
        ArgumentNullException.ThrowIfNull(shim);
        return Regex.Replace(
            shim.Replace("\r\n", "\n", StringComparison.Ordinal),
            "-File\\s+(?:\"[^\"]+\"|'[^']+'|[^\\s&|<>]+)",
            "-File \"<published-script>\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static string CanonicalizePublishedScriptForIdentity(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized = Regex.Replace(
            normalized,
            "^\\$config\\s*=\\s*Get-Content\\s+'(?:(?:'')|[^'])*'\\s*\\|\\s*ConvertFrom-Json\\s*$",
            "$config = Get-Content '<config>' | ConvertFrom-Json",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            "^\\$(?<role>config|sdCli|model|controlNet|realEsrgan|realEsrganModels)\\s*=\\s*'(?:(?:'')|[^'])*'\\s*$",
            static match => $"${match.Groups["role"].Value} = '<{match.Groups["role"].Value}>'",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    private static void AddRealEsrganIdentity(
        string? realEsrganPath,
        string? realEsrganModelsPath,
        ICollection<(string Role, string Path)> files)
    {
        if (realEsrganPath is null || realEsrganModelsPath is null)
        {
            return;
        }

        if (!File.Exists(realEsrganPath))
        {
            throw new InvalidDataException(
                "The Real-ESRGAN executable referenced by the artistic worker is missing.");
        }

        files.Add(("realesrgan-executable", realEsrganPath));
        foreach (var extension in new[] { ".param", ".bin" })
        {
            var modelPath = Path.Combine(
                realEsrganModelsPath,
                RealEsrganCommandBuilder.LegacyDetailModelName + extension);
            if (!File.Exists(modelPath))
            {
                throw new InvalidDataException(
                    $"The {RealEsrganCommandBuilder.LegacyDetailModelName}{extension} model referenced by the artistic worker is missing.");
            }

            files.Add(($"realesrgan-model:{extension}", modelPath));
        }
    }

    private static bool IsRecognizedLegacyGeneration(
        string workerDirectory,
        PublishedWorkerPaths paths)
    {
        var expectedModelsDirectory = Path.Combine(workerDirectory, "models");
        return paths.SdCliPath is not null
            && paths.ModelPath is not null
            && paths.ControlNetPath is not null
            && PathGuard.SamePath(
                paths.SdCliPath,
                Path.Combine(workerDirectory, "sd", "sd-cli.exe"))
            && PathGuard.SamePath(
                paths.ModelPath,
                Path.Combine(
                    expectedModelsDirectory,
                    "DreamShaper_8_pruned.safetensors"))
            && PathGuard.SamePath(
                paths.ControlNetPath,
                Path.Combine(
                    expectedModelsDirectory,
                    "control_v11f1e_sd15_tile_fp16.safetensors"));
    }

    private static void AddOpaquePublishedDependencies(
        PublishedWorkerPaths paths,
        ICollection<(string Role, string Path)> files)
    {
        Add("custom-sd-cli", paths.SdCliPath);
        Add("custom-model", paths.ModelPath);
        Add("custom-control-net", paths.ControlNetPath);
        return;

        void Add(string role, string? path)
        {
            if (path is null)
            {
                return;
            }

            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            {
                throw new InvalidDataException(
                    $"The published artistic worker references a missing {role} dependency: {path}");
            }

            files.Add((role, path));
        }
    }

    private static async Task AddLegacyGeneratedIdentityAsync(
        string workerDirectory,
        ICollection<(string Role, string Path)> files,
        ICollection<(string Role, string Value)> declaredIdentities,
        CancellationToken cancellationToken)
    {
        var runtimePath = Path.Combine(workerDirectory, "sd", "sd-cli.exe");
        var modelsDirectory = Path.Combine(workerDirectory, "models");
        if (!File.Exists(runtimePath) && !Directory.Exists(modelsDirectory))
        {
            return;
        }

        if (!File.Exists(runtimePath) || new FileInfo(runtimePath).Length <= 0)
        {
            throw new InvalidDataException(
                $"The artistic worker legacy runtime is missing or empty: {runtimePath}");
        }
        files.Add(("legacy-runtime", runtimePath));
        foreach (var component in ArtisticWorkerSetupService.Components
                     .Where(component => !component.IsZipArchive))
        {
            var path = Path.Combine(modelsDirectory, component.FileName);
            EnsureLength(path, component.SizeBytes, "legacy model component");
            var actualSha = await GetFileHashAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (!actualSha.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The legacy artistic worker model does not match its pinned SHA-256: {path}");
            }

            declaredIdentities.Add((
                $"legacy-component:{component.FileName}",
                $"{component.SizeBytes}:{component.Sha256.ToLowerInvariant()}"));
        }
    }

    private static async Task ValidatePinnedComponentAsync(
        string path,
        WorkerIdentityComponent component,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (component.LastWriteUtcTicks <= 0
            || info.LastWriteTimeUtc.Ticks == component.LastWriteUtcTicks)
        {
            return;
        }

        // The generation manifest captures the metadata observed immediately
        // after setup SHA-verifies the multi-gigabyte model. Re-read those
        // bytes only if metadata later drifts; identical re-downloads retain
        // the same semantic fingerprint, while in-place model changes fail.
        var actualSha = await GetFileHashAsync(path, cancellationToken).ConfigureAwait(false);
        if (!actualSha.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The artistic worker model no longer matches its setup-verified SHA-256: {path}");
        }
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath)
            || relativePath.Split('/', '\\').Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("The artistic worker identity manifest contains an unsafe path.");
        }

        var fullRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!resolved.StartsWith(
                Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The artistic worker identity manifest escapes its generation directory.");
        }

        return resolved;
    }

    private static void EnsureLength(string path, long minimumLength, string description)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != minimumLength)
        {
            throw new InvalidDataException(
                $"The artistic worker {description} is missing or no longer matches its verified length: {path}");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentHash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexStringLower(contentHash);
    }

    private static async Task<string> GetFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var cacheKey = new FileCacheKey(
            Path.GetFullPath(path),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
        return await FileHashes.GetOrAdd(
                cacheKey,
                static key => new Lazy<Task<string>>(
                    () => HashFileAsync(key.Path),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private sealed record FileCacheKey(string Path, long Length, long LastWriteTicks);

    private sealed record PublishedWorkerPaths(
        string? ConfigPath,
        string? SdCliPath,
        string? ModelPath,
        string? ControlNetPath,
        string? RealEsrganPath,
        string? RealEsrganModelsPath);

    private sealed record WorkerIdentityManifest(
        int SchemaVersion,
        IReadOnlyList<WorkerIdentityRuntimeFile> RuntimeFiles,
        IReadOnlyList<WorkerIdentityComponent> Components);

    private sealed record WorkerIdentityRuntimeFile(
        string RelativePath,
        long Length,
        string Sha256);

    private sealed record WorkerIdentityComponent(
        string FileName,
        long Length,
        string Sha256,
        long LastWriteUtcTicks = 0);
}
