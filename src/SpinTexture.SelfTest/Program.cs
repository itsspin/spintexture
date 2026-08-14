using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;
using SpinTexture.Core.Tooling;
using SpinTexture.SelfTest;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2
                && args[0].Equals("--archive-smoke", StringComparison.OrdinalIgnoreCase))
            {
                await ArchiveSelfTests.RunReadOnlySmokeAsync(args[1], Console.Out).ConfigureAwait(false);
                return 0;
            }

            if (args.Length >= 2 && args[0].Equals("--scan", StringComparison.OrdinalIgnoreCase))
            {
                await RunScanAsync(args[1]).ConfigureAwait(false);
                return 0;
            }

            if (args.Length >= 4 && args[0].Equals("--pilot", StringComparison.OrdinalIgnoreCase))
            {
                var preset = args.Length >= 5
                    ? Enum.Parse<TexturePreset>(args[4], ignoreCase: true)
                    : TexturePreset.Faithful;
                var cap = args.Length >= 6 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 1024;
                await RunPilotAsync(args[1], args[2], args[3], preset, cap).ConfigureAwait(false);
                return 0;
            }

            if (args.Length == 4 && args[0].Equals("--extract", StringComparison.OrdinalIgnoreCase))
            {
                await using var archive = await PfsArchive.OpenAsync(args[1]).ConfigureAwait(false);
                var payload = await archive.ReadEntryAsync(args[2]).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
                await File.WriteAllBytesAsync(args[3], payload).ConfigureAwait(false);
                Console.WriteLine($"Extracted {args[2]} ({payload.Length:N0} bytes) to {args[3]}.");
                return 0;
            }

            if (args.Length is 2 or 3
                && args[0].Equals("--list", StringComparison.OrdinalIgnoreCase))
            {
                await using var archive = await PfsArchive.OpenAsync(args[1]).ConfigureAwait(false);
                var filter = args.Length == 3 ? args[2] : null;
                foreach (var entry in archive.Entries
                             .Where(entry => filter is null
                                 || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"{entry.Name}\t{entry.UncompressedSize}");
                }

                return 0;
            }

            if (args.Length >= 3 && args[0].Equals("--compare", StringComparison.OrdinalIgnoreCase))
            {
                await ComparisonSheetGenerator.RunAsync(
                    args[1],
                    args.Skip(2).ToArray(),
                    Console.Out).ConfigureAwait(false);
                return 0;
            }

            if (args.Length == 5 && args[0].Equals("--texture", StringComparison.OrdinalIgnoreCase))
            {
                await RunTextureAsync(
                    args[1],
                    args[2],
                    Enum.Parse<TexturePreset>(args[3], ignoreCase: true),
                    int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                return 0;
            }

            Console.WriteLine("Running SpinTexture safety and fidelity self-tests...");
            await ArchiveSelfTests.RunAsync(Console.Out).ConfigureAwait(false);
            WorldExpansionCatalogSelfTests.Run();
            await TexturePipelineSelfTests.RunAsync(Console.Out).ConfigureAwait(false);
            await ApplicationUpdateSelfTests.RunAsync(Console.Out).ConfigureAwait(false);
            Console.WriteLine("All SpinTexture self-tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunScanAsync(string installPath)
    {
        var progress = new Progress<ProgressUpdate>(update =>
            Console.WriteLine($"[{update.Stage}] {update.Completed:N0}/{update.Total:N0} {update.CurrentItem}"));
        var summary = await new EverQuestTextureScanner().AnalyzeAsync(installPath, progress).ConfigureAwait(false);
        Console.WriteLine($"Archives: {summary.ArchiveCount:N0}");
        Console.WriteLine($"Packed textures: {summary.PackedTextureCount:N0}");
        Console.WriteLine($"Loose textures: {summary.LooseTextureCount:N0}");
        Console.WriteLine($"Zones: {summary.Zones.Count:N0}");
        Console.WriteLine($"Lavastorm discovered: {summary.Zones.Contains("lavastorm", StringComparer.OrdinalIgnoreCase)}");
        foreach (var warning in summary.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }
    }

    private static async Task RunPilotAsync(
        string installPath,
        string zone,
        string workspacePath,
        TexturePreset preset,
        int maximumDimension)
    {
        var progress = new Progress<ProgressUpdate>(update =>
            Console.WriteLine($"[{update.Stage}] {update.Percent,6:0.0}% {update.CurrentItem} — {update.Message}"));
        var options = new UpscaleOptions(
            preset,
            AssetScope.SelectedZone,
            maximumDimension,
            GenerateMipMaps: true,
            InstallAfterBuild: false,
            SelectedZone: zone);
        var result = await new TexturePackWorkflow().BuildAsync(
            new ProjectPaths(installPath, workspacePath),
            options,
            progress).ConfigureAwait(false);
        Console.WriteLine($"Staged build: {result.StagedBuild.BuildDirectory}");
        Console.WriteLine($"Enhanced textures: {result.Report.Statistics.EnhancedTextures:N0}");
        Console.WriteLine($"Preserved textures: {result.Report.Statistics.PreservedTextures:N0}");
        Console.WriteLine($"Preview gallery: {result.PreviewManifestPath ?? "not generated"}");
        Console.WriteLine("Live EverQuest files were not modified.");
    }

    private static async Task RunTextureAsync(
        string sourcePath,
        string destinationPath,
        TexturePreset preset,
        int maximumDimension)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        var metadata = await new TextureHeaderSniffer().ReadFileAsync(sourcePath).ConfigureAwait(false)
            ?? throw new InvalidDataException("The input is not a supported DDS, BMP, or TGA texture.");
        var classification = new TextureSemanticClassifier().Classify(Path.GetFileName(sourcePath), metadata);
        if (!classification.CanUseColorUpscaler)
        {
            throw new InvalidOperationException(
                $"Texture classification is not color-safe: {string.Join("; ", classification.Reasons)}");
        }

        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, $".spintexture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var paths = new ProjectPaths(Path.GetDirectoryName(sourcePath)!, Path.Combine(work, "workspace"));
            var tools = new ToolchainDiscovery().Discover(paths);
            if (!tools.IsReady)
            {
                throw new FileNotFoundException(string.Join(" ", tools.Diagnostics));
            }

            var nativeProgress = new Progress<NativeOutputLine>(line =>
                Console.WriteLine($"[{line.Stream}] {line.Text}"));
            var result = await new NativeTextureProcessor(tools).ProcessAsync(
                new NativeTextureProcessRequest(
                    sourcePath,
                    destinationPath,
                    Path.Combine(work, "processing"),
                    metadata,
                    classification,
                    new UpscaleOptions(
                        preset,
                        AssetScope.SelectedZone,
                        maximumDimension,
                        GenerateMipMaps: true,
                        InstallAfterBuild: false,
                        SelectedZone: "single-texture")),
                nativeProgress,
                cancellationToken: default).ConfigureAwait(false);
            Console.WriteLine($"{sourcePath} -> {destinationPath}");
            Console.WriteLine($"{metadata.Width}x{metadata.Height} -> {result.Dimensions.OutputWidth}x{result.Dimensions.OutputHeight}");
            Console.WriteLine(result.ProcessingRoute);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }
}
