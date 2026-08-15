using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public sealed record ArtisticWorkerComponent(
    string Name,
    string Url,
    string FileName,
    long SizeBytes,
    string Sha256,
    string License,
    bool IsZipArchive);

public sealed record ArtisticWorkerSetupStatus(
    bool IsInstalled,
    bool IsEnabled,
    string WorkerDirectory,
    string? DisabledReason,
    string? ModelTier = null);

public sealed record ArtisticWorkerConfig(
    int SchemaVersion,
    string Preset,
    string ModelTier,
    string Prompt,
    string NegativePrompt,
    double DenoiseStrength,
    double ControlStrength,
    double CfgScale,
    int Steps,
    long Seed,
    int MaximumDiffusionEdge);

/// <summary>
/// A curated diffusion style recipe. The seed, step count, and resolution
/// bound stay shared so switching styles changes the art direction, not the
/// performance or determinism characteristics.
/// </summary>
public sealed record ArtisticStylePreset(
    string Key,
    string Name,
    string Description,
    string Prompt,
    string NegativePrompt,
    double DenoiseStrength,
    double ControlStrength,
    double CfgScale)
{
    public const string CustomKey = "custom";

    /// <summary>
    /// Maps the recipe to concrete worker settings. Schema 2 is the quality
    /// recipe: DPM++ 2M with the Karras schedule at 28 steps and clip-skip 2
    /// (what the checkpoint was tuned for), with denoise pushed harder
    /// because ControlNet Tile holds every structural line in place.
    /// </summary>
    public ArtisticWorkerConfig ToConfig() => new(
        SchemaVersion: 2,
        Preset: Key,
        ModelTier: ArtisticWorkerSetupService.ModelTierStandard,
        Prompt: Prompt,
        NegativePrompt: NegativePrompt,
        DenoiseStrength: DenoiseStrength,
        ControlStrength: ControlStrength,
        CfgScale: CfgScale,
        Steps: 28,
        Seed: 90210,
        MaximumDiffusionEdge: 1152);
}

/// <summary>
/// One-click installer for the experimental diffusion-based artistic painted
/// worker. Downloads a pinned, SHA-256-verified toolchain — the
/// stable-diffusion.cpp Vulkan build (runs on AMD, NVIDIA, and Intel GPUs
/// exactly like SpinTexture's bundled ncnn workers; no CUDA or Python), a
/// painterly SD 1.5 checkpoint, and the ControlNet Tile model — then
/// generates the worker scripts implementing the documented contract and
/// verifies the install with a deterministic smoke test before the worker is
/// allowed to influence a build.
/// </summary>
public sealed class ArtisticWorkerSetupService
{
    public const string WorkerDirectoryName = "artistic-worker";
    public const long RequiredFreeBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// The single supported model stack (SD 1.5 + ControlNet Tile),
    /// recorded in the config so older files migrate cleanly.
    /// </summary>
    public const string ModelTierStandard = "sd15";

    // Every component is pinned by exact size and SHA-256. The installer
    // refuses bytes that do not match; there is no "latest" channel.
    public static IReadOnlyList<ArtisticWorkerComponent> Components { get; } =
    [
        new(
            "stable-diffusion.cpp (Vulkan, AMD/NVIDIA/Intel)",
            "https://github.com/leejet/stable-diffusion.cpp/releases/download/master-820-de298c2/sd-master-de298c2-bin-win-vulkan-x64.zip",
            "sd-master-de298c2-bin-win-vulkan-x64.zip",
            38_781_335,
            "e9d3072e090eaa0dc91970034ebccee6fafd502c3226480b46bd12ac54f96889",
            "MIT",
            IsZipArchive: true),
        new(
            "DreamShaper 8 painterly checkpoint",
            "https://huggingface.co/Lykon/DreamShaper/resolve/main/DreamShaper_8_pruned.safetensors",
            "DreamShaper_8_pruned.safetensors",
            2_132_625_894,
            "879db523c30d3b9017143d56705015e15a2cb5628762c11d086fed9538abd7fd",
            "CreativeML OpenRAIL-M",
            IsZipArchive: false),
        new(
            "ControlNet v1.1 Tile (fp16)",
            "https://huggingface.co/comfyanonymous/ControlNet-v1-1_fp16_safetensors/resolve/main/control_v11f1e_sd15_tile_fp16.safetensors",
            "control_v11f1e_sd15_tile_fp16.safetensors",
            722_601_104,
            "2f31868eedb243a77932e3c63907a6ba0a2058b6d65b5c27b89ee1b7f618ea33",
            "OpenRAIL",
            IsZipArchive: false)
    ];

    public static long TotalDownloadBytes => Components.Sum(component => component.SizeBytes);

    /// <summary>
    /// Curated art directions for the diffusion repaint. Higher denoise means
    /// the repaint departs further from the reconstructed surface; ControlNet
    /// Tile keeps layout, text, and UV mapping locked in every preset.
    /// </summary>
    public static IReadOnlyList<ArtisticStylePreset> StylePresets { get; } =
    [
        new(
            "painted-fantasy",
            "Painted Fantasy",
            "Bold hand-painted look with confident brush strokes, deep value contrast, and rich saturated color. The balanced default.",
            "masterpiece hand-painted fantasy game texture, rich oil painting, confident visible brush strokes, vivid saturated color, "
            + "deep value contrast, luminous highlights, stylized AAA game art, breathtaking",
            "photorealistic, photo, 3d render, flat, dull, washed out, muddy, lowres, blurry, jpeg artifacts, "
            + "watermark, text, signature, border, frame",
            0.55,
            0.8,
            6.0),
        new(
            "epic-cinematic",
            "Epic Cinematic",
            "Dramatic concept-art energy: cinematic lighting, atmospheric depth, glowing accents, rich color grading. The most transformative look.",
            "epic fantasy concept art, cinematic dramatic lighting, majestic painterly game texture, golden rim light, "
            + "glowing accent highlights, atmospheric depth, rich moody color grading, intricate detailed brushwork, "
            + "awe-inspiring masterpiece",
            "photorealistic, photo, 3d render, flat, dull, washed out, muddy, lowres, blurry, jpeg artifacts, "
            + "watermark, text, signature, border, frame",
            0.58,
            0.78,
            6.5),
        new(
            "dark-oil",
            "Dark Oil Painting",
            "Old-master chiaroscuro: heavy impasto strokes, deep shadow, a muted ominous palette. Made for dungeons and dark cities.",
            "dark fantasy oil painting, chiaroscuro, rembrandt lighting, heavy impasto brush strokes, deep shadow, "
            + "muted earthy palette with jewel-tone accents, ominous atmosphere, museum-quality game texture, masterpiece",
            "photorealistic, photo, 3d render, bright, cheerful, neon, flat, lowres, blurry, jpeg artifacts, "
            + "watermark, text, signature, border, frame",
            0.56,
            0.8,
            6.0),
        new(
            "storybook-watercolor",
            "Storybook Watercolor",
            "Soft washes, gentle ink lines, warm whimsical color. Fits pastoral zones and bright cities.",
            "enchanting storybook watercolor illustration, layered soft color washes, delicate ink outlines, "
            + "warm whimsical fantasy palette, gentle luminous light, hand-painted game texture, charming masterpiece",
            "photorealistic, photo, 3d render, harsh contrast, neon, muddy, lowres, blurry, jpeg artifacts, "
            + "watermark, text, signature, border, frame",
            0.55,
            0.8,
            5.5),
        new(
            "comic-ink",
            "Comic Ink",
            "Bold cel-shaded planes with strong ink lines and vivid flat color. The most graphic, stylized option.",
            "bold comic book art, crisp cel shading, strong clean ink lines, vivid flat color planes, dynamic graphic "
            + "fantasy game texture, high contrast, striking masterpiece",
            "photorealistic, photo, 3d render, soft gradients, muddy, washed out, lowres, blurry, jpeg artifacts, "
            + "watermark, text, signature, border, frame",
            0.62,
            0.75,
            7.0)
    ];

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string toolsDirectory;

    public ArtisticWorkerSetupService(string? toolsDirectory = null)
    {
        this.toolsDirectory = Path.GetFullPath(
            toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "Tools"));
    }

    public string WorkerDirectory => Path.Combine(toolsDirectory, WorkerDirectoryName);

    public ArtisticWorkerSetupStatus GetStatus()
    {
        var workerScript = Path.Combine(WorkerDirectory, "worker.bat");
        var disabledScript = workerScript + ".disabled";
        if (File.Exists(workerScript))
        {
            return new ArtisticWorkerSetupStatus(true, true, WorkerDirectory, null);
        }

        if (File.Exists(disabledScript))
        {
            return new ArtisticWorkerSetupStatus(
                true,
                false,
                WorkerDirectory,
                "The install-time verification did not pass on this PC; the worker is present but disabled.");
        }

        return new ArtisticWorkerSetupStatus(false, false, WorkerDirectory, null);
    }

    /// <summary>
    /// Downloads (or resumes) all pinned components, verifies them, and
    /// generates the worker scripts. Safe to re-run: components that already
    /// match their pinned hash are skipped.
    /// </summary>
    public async Task SetupAsync(
        string realEsrganPath,
        string realEsrganModelsPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realEsrganPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(realEsrganModelsPath);
        Directory.CreateDirectory(WorkerDirectory);
        var driveRoot = Path.GetPathRoot(WorkerDirectory);
        if (driveRoot is not null)
        {
            var free = new DriveInfo(driveRoot).AvailableFreeSpace;
            if (free < RequiredFreeBytes)
            {
                throw new IOException(
                    $"Setting up the artistic worker needs about {RequiredFreeBytes / (1024 * 1024 * 1024):N0} GB free "
                    + $"(downloads plus working room); this drive has {free / (1024.0 * 1024 * 1024):N1} GB.");
            }
        }

        var modelsDirectory = Path.Combine(WorkerDirectory, "models");
        Directory.CreateDirectory(modelsDirectory);
        var components = Components;
        for (var index = 0; index < components.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var component = components[index];
            var destination = component.IsZipArchive
                ? Path.Combine(WorkerDirectory, component.FileName)
                : Path.Combine(modelsDirectory, component.FileName);
            await DownloadComponentAsync(component, destination, index, components.Count, progress, cancellationToken)
                .ConfigureAwait(false);
            if (component.IsZipArchive)
            {
                ExtractRuntime(destination);
            }
        }

        WriteWorkerScripts(realEsrganPath, realEsrganModelsPath);
        progress?.Report(new ProgressUpdate(
            "Artistic worker",
            "All components verified; worker scripts generated.",
            components.Count,
            components.Count,
            "setup"));
    }

    /// <summary>
    /// Runs the freshly installed worker on a generated test image, twice,
    /// and checks the contract: outputs exist, are exactly 4x, and the two
    /// runs are byte-identical (determinism). On failure the worker script is
    /// disabled so it can never affect a build.
    /// </summary>
    public async Task<string?> VerifyAsync(
        Tooling.INativeProcessRunner runner,
        Func<string, int, Task> writeTestImageAsync,
        Func<string, Task<(int Width, int Height)>> readImageSizeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(writeTestImageAsync);
        ArgumentNullException.ThrowIfNull(readImageSizeAsync);
        var workerScript = Path.Combine(WorkerDirectory, "worker.bat");
        if (!File.Exists(workerScript))
        {
            return "The worker script is missing.";
        }

        var verifyRoot = Path.Combine(WorkerDirectory, ".verify");
        try
        {
            if (Directory.Exists(verifyRoot))
            {
                Directory.Delete(verifyRoot, recursive: true);
            }

            const int testSize = 96;
            var inputDirectory = Path.Combine(verifyRoot, "in");
            Directory.CreateDirectory(inputDirectory);
            await writeTestImageAsync(Path.Combine(inputDirectory, "verify.png"), testSize)
                .ConfigureAwait(false);

            var outputs = new List<byte[]>();
            var builder = new Tooling.ArtisticWorkerCommandBuilder();
            for (var run = 0; run < 2; run++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputDirectory = Path.Combine(verifyRoot, $"out-{run}");
                Directory.CreateDirectory(outputDirectory);
                var result = await runner.RunAsync(
                        builder.CreateStylize(workerScript, inputDirectory, outputDirectory),
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return Disable($"The worker exited with code {result.ExitCode}. {Truncate(result.StandardError)}");
                }

                var outputPath = Path.Combine(outputDirectory, "verify.png");
                if (!File.Exists(outputPath))
                {
                    return Disable("The worker did not produce an output image with the input's file name.");
                }

                var (width, height) = await readImageSizeAsync(outputPath).ConfigureAwait(false);
                if (width != testSize * 4 || height != testSize * 4)
                {
                    return Disable(
                        $"The worker produced {width}x{height} instead of the required exact 4x ({testSize * 4}x{testSize * 4}).");
                }

                outputs.Add(await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false));
            }

            if (!outputs[0].AsSpan().SequenceEqual(outputs[1]))
            {
                return Disable(
                    "The worker is not deterministic: two runs on the same input produced different bytes. "
                    + "Repairs and resumed builds must reproduce identical packs.");
            }

            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(verifyRoot))
                {
                    Directory.Delete(verifyRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        string Disable(string reason)
        {
            var disabled = workerScript + ".disabled";
            AtomicFile.TryDelete(disabled);
            File.Move(workerScript, disabled);
            return reason;
        }
    }

    public void Remove()
    {
        if (Directory.Exists(WorkerDirectory))
        {
            Directory.Delete(WorkerDirectory, recursive: true);
        }
    }

    private async Task DownloadComponentAsync(
        ArtisticWorkerComponent component,
        string destination,
        int componentIndex,
        int componentCount,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination)
            && new FileInfo(destination).Length == component.SizeBytes
            && string.Equals(
                await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false),
                component.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new ProgressUpdate(
                "Artistic worker",
                $"{component.Name} is already downloaded and verified.",
                componentIndex + 1,
                componentCount,
                component.FileName));
            return;
        }

        var partialPath = destination + ".partial";
        AtomicFile.TryDelete(partialPath);
        using var response = await Http.GetAsync(
                component.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long downloaded = 0;
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 256,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 256];
            int read;
            var lastReport = DateTimeOffset.MinValue;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                downloaded += read;
                if (downloaded > component.SizeBytes)
                {
                    throw new InvalidDataException(
                        $"{component.Name} is larger than its pinned size; refusing the download.");
                }

                var now = DateTimeOffset.UtcNow;
                if (now - lastReport > TimeSpan.FromMilliseconds(400))
                {
                    lastReport = now;
                    progress?.Report(new ProgressUpdate(
                        "Artistic worker",
                        $"Downloading {component.Name}: {downloaded / (1024.0 * 1024):N0} / {component.SizeBytes / (1024.0 * 1024):N0} MB",
                        componentIndex,
                        componentCount,
                        component.FileName));
                }
            }
        }

        if (downloaded != component.SizeBytes)
        {
            AtomicFile.TryDelete(partialPath);
            throw new InvalidDataException(
                $"{component.Name} download ended at {downloaded:N0} bytes; expected {component.SizeBytes:N0}.");
        }

        var actualSha = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualSha, component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            AtomicFile.TryDelete(partialPath);
            throw new InvalidDataException(
                $"{component.Name} failed SHA-256 verification; the download was discarded. "
                + "Re-run setup; if this repeats, the upstream file changed and SpinTexture needs an updated pin.");
        }

        AtomicFile.TryDelete(destination);
        File.Move(partialPath, destination);
    }

    private void ExtractRuntime(string zipPath)
    {
        var runtimeDirectory = Path.Combine(WorkerDirectory, "sd");
        if (Directory.Exists(runtimeDirectory))
        {
            Directory.Delete(runtimeDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, runtimeDirectory);
        if (!File.Exists(Path.Combine(runtimeDirectory, "sd-cli.exe")))
        {
            throw new InvalidDataException(
                "The stable-diffusion.cpp archive did not contain sd-cli.exe as expected.");
        }
    }

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string ConfigPath => Path.Combine(WorkerDirectory, "worker-config.json");

    public ArtisticWorkerConfig? TryReadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ArtisticWorkerConfig>(
                File.ReadAllText(ConfigPath),
                ConfigJsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the preset key the current config was generated from, or
    /// "custom" when the file was hand-edited past its recipe (any drift in
    /// the style-bearing fields counts).
    /// </summary>
    public string GetActiveStylePresetKey()
    {
        var config = TryReadConfig();
        if (config is null)
        {
            return StylePresets[0].Key;
        }

        var declared = StylePresets.FirstOrDefault(preset =>
            string.Equals(preset.Key, config.Preset, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            return ArtisticStylePreset.CustomKey;
        }

        var expected = declared.ToConfig() with
        {
            SchemaVersion = config.SchemaVersion,
            ModelTier = config.ModelTier,
            Seed = config.Seed,
            Steps = config.Steps,
            MaximumDiffusionEdge = config.MaximumDiffusionEdge
        };
        return expected == config ? declared.Key : ArtisticStylePreset.CustomKey;
    }

    public void ApplyStylePreset(string presetKey)
    {
        var preset = StylePresets.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, presetKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(nameof(presetKey));
        Directory.CreateDirectory(WorkerDirectory);
        // Preserve the user's performance/determinism knobs when they exist.
        var current = TryReadConfig();
        var config = preset.ToConfig();
        if (current is not null)
        {
            // Seed and resolution bound are always the user's; a hand-tuned
            // step count survives only if it was set under the current
            // recipe schema — older recipes' defaults should not pin quality.
            config = config with
            {
                Seed = current.Seed,
                Steps = current.SchemaVersion >= 2 ? current.Steps : config.Steps,
                MaximumDiffusionEdge = current.MaximumDiffusionEdge
            };
        }

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ConfigJsonOptions));
    }

    internal void WriteWorkerScripts(string realEsrganPath, string realEsrganModelsPath)
    {
        // The PowerShell implementation reads image sizes, orchestrates the
        // Real-ESRGAN 4x base pass and the diffusion repaint, and guarantees
        // the exact-4x output contract; the .bat is only a shim so the worker
        // matches SpinTexture's process-invocation contract. A current-schema
        // config survives re-setup so style choices are never reset; configs
        // written by an older recipe schema (or by the retired SDXL tier)
        // are re-mapped onto the active style's current recipe, keeping the
        // user's seed and resolution bound.
        var currentConfig = TryReadConfig();
        if (currentConfig is null)
        {
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(StylePresets[0].ToConfig(), ConfigJsonOptions));
        }
        else if (currentConfig.SchemaVersion < 2
            || !string.Equals(currentConfig.ModelTier, ModelTierStandard, StringComparison.OrdinalIgnoreCase))
        {
            // The declared preset key is the user's style choice; the old
            // recipe's values (including any hand tuning made against it) are
            // superseded by the new recipe they were an approximation of.
            var activePreset = StylePresets.FirstOrDefault(preset =>
                    string.Equals(preset.Key, currentConfig.Preset, StringComparison.OrdinalIgnoreCase))
                ?? StylePresets[0];
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(
                    activePreset.ToConfig() with
                    {
                        Seed = currentConfig.Seed,
                        MaximumDiffusionEdge = currentConfig.MaximumDiffusionEdge
                    },
                    ConfigJsonOptions));
        }

        var script = new StringBuilder();
        script.AppendLine("# Generated by SpinTexture \u2014 experimental artistic painted worker.");
        script.AppendLine("# Contract: worker -i <inDir> -o <outDir> -s 4 -f png; exact 4x PNGs, same names, deterministic.");
        script.AppendLine("param([string]$i, [string]$o, [int]$s = 4, [string]$f = 'png')");
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("Add-Type -AssemblyName System.Drawing");
        script.AppendLine("$culture = [System.Globalization.CultureInfo]::InvariantCulture");
        script.AppendLine("$root = Split-Path -Parent $MyInvocation.MyCommand.Path");
        script.AppendLine("$config = Get-Content (Join-Path $root 'worker-config.json') | ConvertFrom-Json");
        script.AppendLine("$sdCli = Join-Path $root 'sd\\sd-cli.exe'");
        script.AppendLine("$model = Join-Path $root 'models\\DreamShaper_8_pruned.safetensors'");
        script.AppendLine("$controlNet = Join-Path $root 'models\\control_v11f1e_sd15_tile_fp16.safetensors'");
        script.AppendLine($"$realEsrgan = '{realEsrganPath.Replace("'", "''")}'");
        script.AppendLine($"$realEsrganModels = '{realEsrganModelsPath.Replace("'", "''")}'");
        script.AppendLine();
        script.AppendLine("# Native tools (Real-ESRGAN, sd-cli) report progress and GPU info on");
        script.AppendLine("# stderr even when they succeed. Under -ErrorActionPreference Stop that");
        script.AppendLine("# would abort the worker on a success banner, so native output is");
        script.AppendLine("# relayed as plain progress text and success is judged by exit code and");
        script.AppendLine("# the produced files only.");
        script.AppendLine("function Invoke-Native {");
        script.AppendLine("    param([string]$Exe, [string[]]$Arguments)");
        script.AppendLine("    $previous = $ErrorActionPreference");
        script.AppendLine("    $ErrorActionPreference = 'Continue'");
        script.AppendLine("    try {");
        script.AppendLine("        & $Exe @Arguments 2>&1 | ForEach-Object { Write-Host ([string]$_) }");
        script.AppendLine("    } finally {");
        script.AppendLine("        $ErrorActionPreference = $previous");
        script.AppendLine("    }");
        script.AppendLine("    return $LASTEXITCODE");
        script.AppendLine("}");
        script.AppendLine();
        script.AppendLine("$work = Join-Path $root ('work-' + [Guid]::NewGuid().ToString('N'))");
        script.AppendLine("New-Item -ItemType Directory -Path $work | Out-Null");
        script.AppendLine("# Optional per-file art direction from SpinTexture (material and zone");
        script.AppendLine("# aware prompts, coherence-restrained denoise for fluid surfaces).");
        script.AppendLine("$batchMeta = $null");
        script.AppendLine("$batchMetaPath = Join-Path $i 'batch-meta.json'");
        script.AppendLine("if (Test-Path $batchMetaPath) { $batchMeta = Get-Content $batchMetaPath -Raw | ConvertFrom-Json }");
        script.AppendLine("try {");
        script.AppendLine("  $pngFiles = @(Get-ChildItem -Path $i -Filter *.png)");
        script.AppendLine("  $current = 0");
        script.AppendLine("  foreach ($png in $pngFiles) {");
        script.AppendLine("    $current++");
        script.AppendLine("    # Structured progress SpinTexture parses into the build ETA.");
        script.AppendLine("    Write-Host (\"SPINTEXTURE-PROGRESS {0}/{1} {2}\" -f $current, $pngFiles.Count, $png.Name)");
        script.AppendLine("    $prompt = [string]$config.prompt");
        script.AppendLine("    $denoise = [double]$config.denoiseStrength");
        script.AppendLine("    if ($batchMeta -and $batchMeta.files) {");
        script.AppendLine("      $fileEntry = $batchMeta.files.PSObject.Properties[$png.Name]");
        script.AppendLine("      if ($fileEntry) {");
        script.AppendLine("        if ($fileEntry.Value.promptSuffix) { $prompt = $prompt + ', ' + [string]$fileEntry.Value.promptSuffix }");
        script.AppendLine("        if ($fileEntry.Value.denoiseScale) { $denoise = [Math]::Max(0.2, [Math]::Min(1.0, $denoise * [double]$fileEntry.Value.denoiseScale)) }");
        script.AppendLine("      }");
        script.AppendLine("    }");
        script.AppendLine("    $img = [System.Drawing.Image]::FromFile($png.FullName)");
        script.AppendLine("    $w = $img.Width; $h = $img.Height; $img.Dispose()");
        script.AppendLine("    $targetW = $w * 4; $targetH = $h * 4");
        script.AppendLine("    # Base pass: sharp faithful 4x so the diffusion repaint keeps real detail.");
        script.AppendLine("    $up = Join-Path $work ($png.BaseName + '-up.png')");
        script.AppendLine("    $code = Invoke-Native $realEsrgan @('-i', $png.FullName, '-o', $up, '-s', '4', '-n', 'realesrgan-x4plus', '-m', $realEsrganModels, '-f', 'png')");
        script.AppendLine("    if ($code -ne 0 -or -not (Test-Path $up)) { throw \"Real-ESRGAN base pass failed for $($png.Name) (exit $code)\" }");
        script.AppendLine("    # Diffusion repaint at a bounded resolution; ControlNet Tile keeps layout/UVs.");
        script.AppendLine("    $edge = [Math]::Max($targetW, $targetH)");
        script.AppendLine("    $scale = [Math]::Min(1.0, $config.maximumDiffusionEdge / $edge)");
        script.AppendLine("    $sdW = [Math]::Max(512, [Math]::Floor($targetW * $scale / 64) * 64)");
        script.AppendLine("    $sdH = [Math]::Max(512, [Math]::Floor($targetH * $scale / 64) * 64)");
        script.AppendLine("    $painted = Join-Path $work ($png.BaseName + '-painted.png')");
        script.AppendLine("    $sdArguments = @(");
        script.AppendLine("      '-m', $model,");
        script.AppendLine("      '--control-net', $controlNet,");
        script.AppendLine("      '--control-image', $up,");
        script.AppendLine("      '--control-strength', ([double]$config.controlStrength).ToString($culture),");
        script.AppendLine("      '-i', $up,");
        script.AppendLine("      '--strength', $denoise.ToString($culture),");
        script.AppendLine("      # DPM++ 2M on the Karras schedule with clip-skip 2: the settings the");
        script.AppendLine("      # checkpoint is tuned for, and a large quality gain over the defaults.");
        script.AppendLine("      '--sampling-method', 'dpm++2m',");
        script.AppendLine("      '--schedule', 'karras',");
        script.AppendLine("      '--clip-skip', '2',");
        script.AppendLine("      '-p', $prompt,");
        script.AppendLine("      '-n', [string]$config.negativePrompt,");
        script.AppendLine("      '--cfg-scale', ([double]$config.cfgScale).ToString($culture),");
        script.AppendLine("      '--steps', ([int]$config.steps).ToString($culture),");
        script.AppendLine("      '--seed', ([long]$config.seed).ToString($culture),");
        script.AppendLine("      '-W', ([int]$sdW).ToString($culture),");
        script.AppendLine("      '-H', ([int]$sdH).ToString($culture),");
        script.AppendLine("      '--vae-tiling',");
        script.AppendLine("      '-o', $painted)");
        script.AppendLine("    $code = Invoke-Native $sdCli $sdArguments");
        script.AppendLine("    if ($code -ne 0 -or -not (Test-Path $painted)) { throw \"Diffusion repaint failed for $($png.Name) (exit $code)\" }");
        script.AppendLine("    # Contract: the delivered file is exactly 4x the input.");
        script.AppendLine("    $final = Join-Path $o $png.Name");
        script.AppendLine("    if ($sdW -eq $targetW -and $sdH -eq $targetH) {");
        script.AppendLine("      Move-Item -Force $painted $final");
        script.AppendLine("    } else {");
        script.AppendLine("      $src = [System.Drawing.Image]::FromFile($painted)");
        script.AppendLine("      $dst = New-Object System.Drawing.Bitmap($targetW, $targetH)");
        script.AppendLine("      $g = [System.Drawing.Graphics]::FromImage($dst)");
        script.AppendLine("      $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic");
        script.AppendLine("      $g.DrawImage($src, 0, 0, $targetW, $targetH)");
        script.AppendLine("      $g.Dispose(); $src.Dispose()");
        script.AppendLine("      $dst.Save($final, [System.Drawing.Imaging.ImageFormat]::Png)");
        script.AppendLine("      $dst.Dispose()");
        script.AppendLine("      Remove-Item -Force $painted");
        script.AppendLine("    }");
        script.AppendLine("  }");
        script.AppendLine("} finally {");
        script.AppendLine("  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue");
        script.AppendLine("}");
        File.WriteAllText(Path.Combine(WorkerDirectory, "worker.ps1"), script.ToString());

        var shim = "@echo off\r\n"
            + "powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0worker.ps1\" %*\r\n"
            + "exit /b %ERRORLEVEL%\r\n";
        var shimPath = Path.Combine(WorkerDirectory, "worker.bat");
        AtomicFile.TryDelete(shimPath + ".disabled");
        File.WriteAllText(shimPath, shim);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 256,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var digest = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest);
    }

    private static string Truncate(string text) =>
        text.Length <= 400 ? text : text[^400..];

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpinTexture-ArtisticWorkerSetup/1.0");
        return client;
    }
}
