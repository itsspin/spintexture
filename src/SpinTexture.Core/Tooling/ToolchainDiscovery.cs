namespace SpinTexture.Core.Tooling;

public sealed record ExternalToolPaths(
    string? TexconvPath,
    string? RealEsrganPath,
    string? RealEsrganModelsPath,
    string? TextureUpscalerPath,
    string? TextureModelsPath,
    IReadOnlyList<string> Diagnostics,
    string? ArtisticWorkerPath = null)
{
    public bool HasArtisticWorker => ArtisticWorkerPath is not null;

    public bool HasDirectXTex => TexconvPath is not null;
    public bool HasRealEsrgan => RealEsrganPath is not null && RealEsrganModelsPath is not null;
    public bool HasTextureUpscaler => TextureUpscalerPath is not null
        && TextureModelsPath is not null
        && UpscaylCommandBuilder.HasTextureSpecializedModel(TextureModelsPath);
    public bool IsReady => HasDirectXTex && HasRealEsrgan;
}

public sealed class ToolchainDiscovery
{
    private const string TexconvEnvironmentVariable = "SPINTEXTURE_TEXCONV";
    private const string RealEsrganEnvironmentVariable = "SPINTEXTURE_REALESRGAN";
    private const string RealEsrganModelsEnvironmentVariable = "SPINTEXTURE_REALESRGAN_MODELS";
    private const string TextureUpscalerEnvironmentVariable = "SPINTEXTURE_UPSCAYL";
    private const string TextureModelsEnvironmentVariable = "SPINTEXTURE_TEXTURE_MODELS";
    private const string ArtisticWorkerEnvironmentVariable = "SPINTEXTURE_ARTISTIC_WORKER";

    public ExternalToolPaths Discover(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var diagnostics = new List<string>();
        var applicationDirectory = AppContext.BaseDirectory;
        var currentDirectory = Directory.GetCurrentDirectory();
        var texconv = FindFile(
            TexconvEnvironmentVariable,
            diagnostics,
            Path.Combine(paths.ToolsPath, "DirectXTex", "texconv.exe"),
            Path.Combine(paths.ToolsPath, "texconv.exe"),
            Path.Combine(applicationDirectory, "Tools", "texconv.exe"),
            Path.Combine(currentDirectory, "Tools", "texconv.exe"),
            Path.Combine(applicationDirectory, "vendor", "texconv.exe"),
            Path.Combine(currentDirectory, "vendor", "texconv.exe"));
        var realEsrgan = FindFile(
            RealEsrganEnvironmentVariable,
            diagnostics,
            Path.Combine(paths.ToolsPath, "RealESRGAN", "realesrgan-ncnn-vulkan.exe"),
            Path.Combine(paths.ToolsPath, "realesrgan-ncnn-vulkan.exe"),
            Path.Combine(applicationDirectory, "Tools", "realesrgan", "realesrgan-ncnn-vulkan.exe"),
            Path.Combine(currentDirectory, "Tools", "realesrgan", "realesrgan-ncnn-vulkan.exe"),
            Path.Combine(applicationDirectory, "vendor", "realesrgan", "realesrgan-ncnn-vulkan.exe"),
            Path.Combine(currentDirectory, "vendor", "realesrgan", "realesrgan-ncnn-vulkan.exe"));

        var modelCandidates = new List<string>();
        AddEnvironmentCandidate(RealEsrganModelsEnvironmentVariable, modelCandidates, diagnostics);
        if (realEsrgan is not null)
        {
            modelCandidates.Add(Path.Combine(Path.GetDirectoryName(realEsrgan)!, "models"));
        }

        modelCandidates.Add(Path.Combine(paths.ToolsPath, "RealESRGAN", "models"));
        modelCandidates.Add(Path.Combine(paths.ToolsPath, "models"));
        modelCandidates.Add(Path.Combine(applicationDirectory, "Tools", "realesrgan", "models"));
        modelCandidates.Add(Path.Combine(currentDirectory, "Tools", "realesrgan", "models"));
        modelCandidates.Add(Path.Combine(applicationDirectory, "vendor", "realesrgan", "models"));
        modelCandidates.Add(Path.Combine(currentDirectory, "vendor", "realesrgan", "models"));

        var models = modelCandidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(IsSupportedModelDirectory);
        var textureUpscaler = FindFile(
            TextureUpscalerEnvironmentVariable,
            diagnostics,
            Path.Combine(paths.ToolsPath, "upscayl", "upscayl-bin.exe"),
            Path.Combine(applicationDirectory, "Tools", "upscayl", "upscayl-bin.exe"),
            Path.Combine(currentDirectory, "Tools", "upscayl", "upscayl-bin.exe"),
            Path.Combine(applicationDirectory, "vendor", "upscayl", "upscayl-bin.exe"),
            Path.Combine(currentDirectory, "vendor", "upscayl", "upscayl-bin.exe"));

        var textureModelCandidates = new List<string>();
        AddEnvironmentCandidate(TextureModelsEnvironmentVariable, textureModelCandidates, diagnostics);
        if (textureUpscaler is not null)
        {
            textureModelCandidates.Add(Path.Combine(Path.GetDirectoryName(textureUpscaler)!, "models"));
        }

        textureModelCandidates.Add(Path.Combine(paths.ToolsPath, "upscayl", "models"));
        textureModelCandidates.Add(Path.Combine(applicationDirectory, "Tools", "upscayl", "models"));
        textureModelCandidates.Add(Path.Combine(currentDirectory, "Tools", "upscayl", "models"));
        textureModelCandidates.Add(Path.Combine(applicationDirectory, "vendor", "upscayl", "models"));
        textureModelCandidates.Add(Path.Combine(currentDirectory, "vendor", "upscayl", "models"));
        var textureModels = textureModelCandidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(UpscaylCommandBuilder.HasTextureSpecializedModel);

        if (texconv is null)
        {
            diagnostics.Add("texconv.exe was not found in the SpinTexture tools folder or SPINTEXTURE_TEXCONV.");
        }

        if (realEsrgan is null)
        {
            diagnostics.Add("realesrgan-ncnn-vulkan.exe was not found in the SpinTexture tools folder or SPINTEXTURE_REALESRGAN.");
        }

        if (models is null)
        {
            diagnostics.Add(
                "A complete models folder containing realesrnet-x4plus, realesrgan-x4plus, and realesrgan-x4plus-anime was not found.");
        }

        if ((textureUpscaler is null) != (textureModels is null))
        {
            diagnostics.Add(
                "Texture HD custom worker is incomplete; the bundled faithful Real-ESRNet fallback will be used.");
        }

        var artisticWorker = FindArtisticWorker(
            ArtisticWorkerEnvironmentVariable,
            diagnostics,
            Path.Combine(paths.ToolsPath, "artistic-worker", "worker.exe"),
            Path.Combine(paths.ToolsPath, "artistic-worker", "worker.bat"),
            Path.Combine(paths.ToolsPath, "artistic-worker", "worker.cmd"),
            Path.Combine(applicationDirectory, "Tools", "artistic-worker", "worker.exe"),
            Path.Combine(applicationDirectory, "Tools", "artistic-worker", "worker.bat"),
            Path.Combine(applicationDirectory, "Tools", "artistic-worker", "worker.cmd"));
        if (artisticWorker is not null)
        {
            diagnostics.Add(
                $"Experimental external artistic painted worker enabled: {artisticWorker}. "
                + "Graphic Painted Fantasy will use it as its reconstruction base; see docs/ARTISTIC_WORKER.md.");
        }

        return new ExternalToolPaths(
            texconv,
            realEsrgan,
            models,
            textureUpscaler,
            textureModels,
            diagnostics,
            artisticWorker);
    }

    private static string? FindFile(
        string environmentVariable,
        ICollection<string> diagnostics,
        params string[] candidates)
    {
        var allCandidates = new List<string>();
        AddEnvironmentCandidate(environmentVariable, allCandidates, diagnostics);
        allCandidates.AddRange(candidates);

        return allCandidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private static string? FindArtisticWorker(
        string environmentVariable,
        ICollection<string> diagnostics,
        params string[] candidates)
    {
        var allCandidates = new List<string>();
        AddEnvironmentCandidate(environmentVariable, allCandidates, diagnostics);
        allCandidates.AddRange(candidates);

        foreach (var candidate in allCandidates.Select(Path.GetFullPath))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(candidate)!;
            var candidateMarker = candidate + ".disabled";
            var setupMarker = Path.Combine(directory, "worker.bat.disabled");
            if (MarkerExists(candidateMarker) || MarkerExists(setupMarker))
            {
                diagnostics.Add(
                    $"Experimental artistic worker remains disabled by its safety marker: {candidate}.");
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool MarkerExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static void AddEnvironmentCandidate(
        string environmentVariable,
        ICollection<string> candidates,
        ICollection<string> diagnostics)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            candidates.Add(Path.GetFullPath(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add($"{environmentVariable} does not contain a valid path.");
        }
    }

    internal static bool IsSupportedModelDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return HasModel(directory, "realesrnet-x4plus")
            && HasModel(directory, "realesrgan-x4plus")
            && HasModel(directory, "realesrgan-x4plus-anime");
    }

    private static bool HasModel(string directory, string modelName) =>
        File.Exists(Path.Combine(directory, $"{modelName}.param"))
        && File.Exists(Path.Combine(directory, $"{modelName}.bin"));
}
