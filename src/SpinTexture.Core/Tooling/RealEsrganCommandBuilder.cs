using SpinTexture.Core.Models;

namespace SpinTexture.Core.Tooling;

public sealed class RealEsrganCommandBuilder
{
    public const int MinimumOutputScale = 2;
    public const int ModelScale = 4;
    public const string LegacyFaithfulModelName = "realesrnet-x4plus";
    public const string LegacyDetailModelName = "realesrgan-x4plus";
    public const string IllustratedModelName = "realesrgan-x4plus-anime";

    public NativeProcessCommand CreateUpscale(
        string executablePath,
        string modelsPath,
        string inputPath,
        string outputPath,
        TexturePreset preset,
        int tileSize = 0,
        int outputScale = ModelScale,
        string? modelNameOverride = null,
        string workerThreads = "1:2:2")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (tileSize != 0 && tileSize < 32)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), "Real-ESRGAN tiles must be zero (automatic) or at least 32 pixels.");
        }

        if (outputScale is < MinimumOutputScale or > ModelScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputScale),
                $"Real-ESRGAN output scale must be between {MinimumOutputScale} and {ModelScale}.");
        }

        ValidateWorkerThreads(workerThreads);

        var modelName = string.IsNullOrWhiteSpace(modelNameOverride)
            ? ResolveModel(modelsPath, preset).Name
            : modelNameOverride;
        EnsureModelExists(modelsPath, modelName);

        var arguments = new List<string>
        {
            "-i", inputPath,
            "-o", outputPath,
            "-n", modelName,
            "-s", outputScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-t", tileSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-m", modelsPath,
            "-j", workerThreads,
            "-f", "png"
        };

        if (preset == TexturePreset.MaximumDetail)
        {
            arguments.Add("-x");
        }

        return new NativeProcessCommand(
            executablePath,
            arguments,
            Path.GetDirectoryName(Path.GetFullPath(executablePath)),
            DisplayName: "Real-ESRGAN ncnn Vulkan");
    }

    public TextureUpscaleModelSelection ResolveModel(string modelsPath, TexturePreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsPath);
        var modelName = preset switch
        {
            TexturePreset.Faithful => LegacyFaithfulModelName,
            TexturePreset.Illustrated or TexturePreset.RusticPainted => IllustratedModelName,
            _ => LegacyDetailModelName
        };
        return new TextureUpscaleModelSelection(
            modelName,
            IsTextureSpecialized: false);
    }

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
                $"Real-ESRGAN model '{modelName}' is incomplete in {modelsPath}.",
                !File.Exists(parameterPath) ? parameterPath : weightsPath);
        }
    }

    private static void ValidateWorkerThreads(string workerThreads)
    {
        if (workerThreads is not ("1:2:2" or "1:3:3"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerThreads),
                "The validated ncnn worker profiles are 1:2:2 and 1:3:3.");
        }
    }
}
