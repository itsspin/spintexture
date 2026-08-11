namespace SpinTexture.Core.Tooling;

public sealed record TextureUpscaleModelSelection(
    string Name,
    bool IsTextureSpecialized);

public sealed class UpscaylCommandBuilder
{
    public const int ModelScale = 4;

    // The first complete .param/.bin pair wins. Supporting aliases keeps local
    // model experiments possible without changing the application binary.
    public static IReadOnlyList<string> TextureSpecializedModelNames { get; } =
    [
        "4x-PBRify_UpscalerSPANV4",
        "pbrify-span-v4",
        "4x-PBRify-UpscalerSPANV4",
        "PBRify_UpscalerSPANV4"
    ];

    public TextureUpscaleModelSelection? FindTextureModel(string modelsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsPath);
        var modelName = TextureSpecializedModelNames.FirstOrDefault(
            candidate => HasModel(modelsPath, candidate));
        return modelName is null
            ? null
            : new TextureUpscaleModelSelection(modelName, IsTextureSpecialized: true);
    }

    public NativeProcessCommand CreateUpscale(
        string executablePath,
        string modelsPath,
        string inputPath,
        string outputPath,
        string modelName,
        int tileSize = 0,
        string workerThreads = "1:2:2")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (!Path.GetFileName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelsPath)))
            .Equals("models", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Upscayl custom-model directory must be named 'models'.",
                nameof(modelsPath));
        }

        if (tileSize != 0 && tileSize < 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileSize),
                "Upscayl tiles must be zero (automatic) or at least 32 pixels.");
        }

        if (workerThreads is not ("1:2:2" or "1:3:3"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerThreads),
                "The validated ncnn worker profiles are 1:2:2 and 1:3:3.");
        }

        EnsureModelExists(modelsPath, modelName);
        return new NativeProcessCommand(
            executablePath,
            [
                "-i", inputPath,
                "-o", outputPath,
                "-m", modelsPath,
                "-n", modelName,
                "-z", ModelScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // Fixed 4x SPAN models must run at their native scale. Asking
                // the worker for 2x/3x corrupts tile assembly; the pipeline
                // performs a seam-safe final resize after cropping instead.
                "-s", ModelScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-t", tileSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-j", workerThreads,
                "-f", "png"
            ],
            Path.GetDirectoryName(Path.GetFullPath(executablePath)),
            DisplayName: "Upscayl ncnn Vulkan game-texture worker");
    }

    public static bool HasTextureSpecializedModel(string modelsPath) =>
        !string.IsNullOrWhiteSpace(modelsPath)
        && TextureSpecializedModelNames.Any(modelName => HasModel(modelsPath, modelName));

    private static bool HasModel(string modelsPath, string modelName) =>
        File.Exists(Path.Combine(modelsPath, $"{modelName}.param"))
        && File.Exists(Path.Combine(modelsPath, $"{modelName}.bin"));

    private static void EnsureModelExists(string modelsPath, string modelName)
    {
        var parameterPath = Path.Combine(modelsPath, $"{modelName}.param");
        var weightsPath = Path.Combine(modelsPath, $"{modelName}.bin");
        if (!File.Exists(parameterPath) || !File.Exists(weightsPath))
        {
            throw new FileNotFoundException(
                $"Upscayl model '{modelName}' is incomplete in {modelsPath}.",
                !File.Exists(parameterPath) ? parameterPath : weightsPath);
        }
    }
}
