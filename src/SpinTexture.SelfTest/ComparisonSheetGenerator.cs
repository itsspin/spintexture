using System.Text;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;

namespace SpinTexture.SelfTest;

/// <summary>
/// Builds a visual comparison contact sheet for a handful of sample textures:
/// original plus every upscale mode, each at 1x and tiled 2x2 so seam behavior
/// and painted quality can be judged quickly in a browser.
/// </summary>
internal static class ComparisonSheetGenerator
{
    private sealed record ModeColumn(
        string Key,
        string Title,
        TexturePreset Preset,
        PaintedTheme Theme = PaintedTheme.ClassicPainted,
        PaintedStyleSettings? Style = null);

    public static async Task RunAsync(
        string outputDirectory,
        IReadOnlyList<string> texturePaths,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (texturePaths.Count == 0)
        {
            throw new ArgumentException("Provide at least one source texture.", nameof(texturePaths));
        }

        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var modes = new ModeColumn[]
        {
            new("faithful", "Faithful", TexturePreset.Faithful),
            new("texturehd", "Texture HD", TexturePreset.ClassicHd),
            new("materialdetail", "Material Detail", TexturePreset.MaximumDetail),
            new("painted", "Graphic Painted (new)", TexturePreset.Illustrated),
            new("painted-storybook", "Graphic Painted · Light Storybook", TexturePreset.Illustrated, PaintedTheme.LightStorybook),
            new("rustic", "Rustic Painted", TexturePreset.RusticPainted)
        };

        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <meta charset="utf-8">
            <title>SpinTexture mode comparison</title>
            <style>
            body { background:#14161a; color:#dfe3ea; font:13px/1.5 "Segoe UI", sans-serif; margin:24px; }
            h1 { font-size:18px; } h2 { font-size:15px; margin:28px 0 4px; }
            .note { color:#9aa3b2; max-width:72em; }
            .row { display:flex; gap:10px; overflow-x:auto; padding:8px 0; align-items:flex-start; }
            figure { margin:0; flex:0 0 auto; }
            figcaption { font-size:11px; color:#aeb6c4; margin-top:4px; max-width:280px; }
            img { width:280px; height:auto; image-rendering:auto; background:#000;
                  border:1px solid #333a45; border-radius:4px; display:block; }
            .err { color:#e08b8b; }
            </style>
            <h1>SpinTexture upscale mode comparison</h1>
            <p class="note">Top row of each texture: one processed output per mode at fit zoom
            (click an image to open it 1:1). Bottom row: the same output tiled 2&times;2 &mdash;
            any visible seam lines or repeated-pattern artifacts mean the mode broke tiling.</p>
            """);

        var sniffer = new TextureHeaderSniffer();
        var classifier = new TextureSemanticClassifier();

        foreach (var texturePath in texturePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(texturePath);
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var safeName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            await log.WriteLineAsync($"=== {name} ===").ConfigureAwait(false);
            html.Append($"<h2>{System.Net.WebUtility.HtmlEncode(name)}</h2>\n");

            var metadata = await sniffer.ReadFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                html.Append("<p class=\"err\">Unsupported texture header; skipped.</p>\n");
                continue;
            }

            var classification = classifier.Classify(Path.GetFileName(fullPath), metadata);
            if (!classification.CanUseColorUpscaler)
            {
                html.Append($"<p class=\"err\">Not color-safe ({System.Net.WebUtility.HtmlEncode(string.Join("; ", classification.Reasons))}); skipped.</p>\n");
                continue;
            }

            var workRoot = Path.Combine(outputDirectory, $".work-{safeName}");
            Directory.CreateDirectory(workRoot);
            var paths = new ProjectPaths(Path.GetDirectoryName(fullPath)!, Path.Combine(workRoot, "workspace"));
            var tools = new ToolchainDiscovery().Discover(paths);
            if (!tools.IsReady)
            {
                throw new FileNotFoundException(string.Join(" ", tools.Diagnostics));
            }

            var processor = new NativeTextureProcessor(tools);
            var cells = new StringBuilder();
            var tiledCells = new StringBuilder();

            // Original panel first.
            var originalPng = Path.Combine(outputDirectory, $"{safeName}-original.png");
            await processor.CreatePngPreviewAsync(fullPath, originalPng, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await WriteTiledAsync(originalPng, Path.Combine(outputDirectory, $"{safeName}-original-tiled.png"), cancellationToken)
                .ConfigureAwait(false);
            AppendCell(cells, $"{safeName}-original.png", $"Original ({metadata.Width}x{metadata.Height})");
            AppendCell(tiledCells, $"{safeName}-original-tiled.png", "Original tiled 2x2");

            foreach (var mode in modes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(workRoot, $"{safeName}-{mode.Key}{Path.GetExtension(fullPath)}");
                try
                {
                    var result = await processor.ProcessAsync(
                        new NativeTextureProcessRequest(
                            fullPath,
                            destination,
                            Path.Combine(workRoot, $"processing-{mode.Key}"),
                            metadata,
                            classification,
                            new UpscaleOptions(
                                mode.Preset,
                                AssetScope.SelectedZone,
                                MaximumDimension: 2048,
                                GenerateMipMaps: false,
                                InstallAfterBuild: false,
                                SelectedZone: "comparison",
                                PaintedTheme: mode.Theme,
                                PaintedStyle: mode.Style)),
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    var panelPng = Path.Combine(outputDirectory, $"{safeName}-{mode.Key}.png");
                    await processor.CreatePngPreviewAsync(destination, panelPng, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    var tiledPng = Path.Combine(outputDirectory, $"{safeName}-{mode.Key}-tiled.png");
                    await WriteTiledAsync(panelPng, tiledPng, cancellationToken).ConfigureAwait(false);

                    AppendCell(cells, $"{safeName}-{mode.Key}.png", $"{mode.Title} — {result.ProcessingRoute}");
                    AppendCell(tiledCells, $"{safeName}-{mode.Key}-tiled.png", $"{mode.Title} tiled 2x2");
                    await log.WriteLineAsync($"  {mode.Title}: {result.ProcessingRoute}").ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    cells.Append($"<figure><figcaption class=\"err\">{System.Net.WebUtility.HtmlEncode(mode.Title)} failed: {System.Net.WebUtility.HtmlEncode(exception.Message)}</figcaption></figure>\n");
                    await log.WriteLineAsync($"  {mode.Title} FAILED: {exception.Message}").ConfigureAwait(false);
                }
            }

            html.Append("<div class=\"row\">\n").Append(cells).Append("</div>\n");
            html.Append("<div class=\"row\">\n").Append(tiledCells).Append("</div>\n");

            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch (IOException)
            {
                // Leftover work files are harmless; the sheet itself is complete.
            }
        }

        var indexPath = Path.Combine(outputDirectory, "index.html");
        await File.WriteAllTextAsync(indexPath, html.ToString(), cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync($"Contact sheet: {indexPath}").ConfigureAwait(false);
    }

    private static void AppendCell(StringBuilder target, string fileName, string caption)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(caption);
        target.Append($"<figure><a href=\"{fileName}\"><img loading=\"lazy\" src=\"{fileName}\" alt=\"{encoded}\"></a><figcaption>{encoded}</figcaption></figure>\n");
    }

    private static async Task WriteTiledAsync(
        string sourcePngPath,
        string destinationPngPath,
        CancellationToken cancellationToken)
    {
        var source = await TgaPixelBuffer.ReadPngFileAsync(sourcePngPath, cancellationToken).ConfigureAwait(false);
        var width = source.Width;
        var height = source.Height;
        var sourcePixels = source.RgbaPixels;
        var tiled = new byte[width * 2 * height * 2 * 4];
        for (var y = 0; y < height * 2; y++)
        {
            for (var x = 0; x < width * 2; x++)
            {
                var sourceOffset = (((y % height) * width) + (x % width)) * 4;
                var targetOffset = ((y * width * 2) + x) * 4;
                sourcePixels.Span.Slice(sourceOffset, 4).CopyTo(tiled.AsSpan(targetOffset, 4));
            }
        }

        var buffer = TgaPixelBuffer.Read(CreateTgaBytes(width * 2, height * 2, tiled));
        await buffer.WritePngFileAsync(destinationPngPath, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateTgaBytes(int width, int height, byte[] rgba)
    {
        var output = new byte[18 + rgba.Length];
        output[2] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12, 2), checked((ushort)width));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(14, 2), checked((ushort)height));
        output[16] = 32;
        output[17] = 0x28;
        var outputIndex = 18;
        for (var index = 0; index < rgba.Length; index += 4)
        {
            output[outputIndex++] = rgba[index + 2];
            output[outputIndex++] = rgba[index + 1];
            output[outputIndex++] = rgba[index];
            output[outputIndex++] = rgba[index + 3];
        }

        return output;
    }
}
