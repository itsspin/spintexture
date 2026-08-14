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
    string? DisabledReason);

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
        for (var index = 0; index < Components.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var component = Components[index];
            var destination = component.IsZipArchive
                ? Path.Combine(WorkerDirectory, component.FileName)
                : Path.Combine(modelsDirectory, component.FileName);
            await DownloadComponentAsync(component, destination, index, progress, cancellationToken)
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
            Components.Count,
            Components.Count,
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
                Components.Count,
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
                        Components.Count,
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

    internal void WriteWorkerScripts(string realEsrganPath, string realEsrganModelsPath)
    {
        // The PowerShell implementation reads image sizes, orchestrates the
        // Real-ESRGAN 4x base pass and the diffusion repaint, and guarantees
        // the exact-4x output contract; the .bat is only a shim so the worker
        // matches SpinTexture's process-invocation contract.
        var config = new
        {
            schemaVersion = 1,
            prompt = "masterpiece hand-painted fantasy game texture, oil painting, visible brush strokes, rich saturated color, stylized",
            negativePrompt = "photorealistic, photo, blurry, jpeg artifacts, watermark",
            denoiseStrength = 0.45,
            controlStrength = 0.8,
            cfgScale = 5.5,
            steps = 18,
            seed = 90210,
            maximumDiffusionEdge = 1152
        };
        File.WriteAllText(
            Path.Combine(WorkerDirectory, "worker-config.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        var script = new StringBuilder();
        script.AppendLine("# Generated by SpinTexture — experimental artistic painted worker.");
        script.AppendLine("# Contract: worker -i <inDir> -o <outDir> -s 4 -f png; exact 4x PNGs, same names, deterministic.");
        script.AppendLine("param([string]$i, [string]$o, [int]$s = 4, [string]$f = 'png')");
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("Add-Type -AssemblyName System.Drawing");
        script.AppendLine("$root = Split-Path -Parent $MyInvocation.MyCommand.Path");
        script.AppendLine("$config = Get-Content (Join-Path $root 'worker-config.json') | ConvertFrom-Json");
        script.AppendLine("$sdCli = Join-Path $root 'sd\\sd-cli.exe'");
        script.AppendLine("$model = Join-Path $root 'models\\DreamShaper_8_pruned.safetensors'");
        script.AppendLine("$controlNet = Join-Path $root 'models\\control_v11f1e_sd15_tile_fp16.safetensors'");
        script.AppendLine($"$realEsrgan = '{realEsrganPath.Replace("'", "''")}'");
        script.AppendLine($"$realEsrganModels = '{realEsrganModelsPath.Replace("'", "''")}'");
        script.AppendLine("$work = Join-Path $root ('work-' + [Guid]::NewGuid().ToString('N'))");
        script.AppendLine("New-Item -ItemType Directory -Path $work | Out-Null");
        script.AppendLine("try {");
        script.AppendLine("  foreach ($png in Get-ChildItem -Path $i -Filter *.png) {");
        script.AppendLine("    $img = [System.Drawing.Image]::FromFile($png.FullName)");
        script.AppendLine("    $w = $img.Width; $h = $img.Height; $img.Dispose()");
        script.AppendLine("    $targetW = $w * 4; $targetH = $h * 4");
        script.AppendLine("    # Base pass: sharp faithful 4x so the diffusion repaint keeps real detail.");
        script.AppendLine("    $up = Join-Path $work ($png.BaseName + '-up.png')");
        script.AppendLine("    & $realEsrgan -i $png.FullName -o $up -s 4 -n realesrgan-x4plus -m $realEsrganModels -f png 2>$null");
        script.AppendLine("    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $up)) { throw \"Real-ESRGAN base pass failed for $($png.Name)\" }");
        script.AppendLine("    # Diffusion repaint at a bounded resolution; ControlNet Tile keeps layout/UVs.");
        script.AppendLine("    $edge = [Math]::Max($targetW, $targetH)");
        script.AppendLine("    $scale = [Math]::Min(1.0, $config.maximumDiffusionEdge / $edge)");
        script.AppendLine("    $sdW = [Math]::Max(512, [Math]::Floor($targetW * $scale / 64) * 64)");
        script.AppendLine("    $sdH = [Math]::Max(512, [Math]::Floor($targetH * $scale / 64) * 64)");
        script.AppendLine("    $painted = Join-Path $work ($png.BaseName + '-painted.png')");
        script.AppendLine("    & $sdCli -m $model --control-net $controlNet -i $up --control-image $up `");
        script.AppendLine("      --strength $config.denoiseStrength --control-strength $config.controlStrength `");
        script.AppendLine("      -p $config.prompt -n $config.negativePrompt --cfg-scale $config.cfgScale `");
        script.AppendLine("      --steps $config.steps --seed $config.seed -W $sdW -H $sdH --vae-tiling `");
        script.AppendLine("      -o $painted");
        script.AppendLine("    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $painted)) { throw \"Diffusion repaint failed for $($png.Name)\" }");
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
