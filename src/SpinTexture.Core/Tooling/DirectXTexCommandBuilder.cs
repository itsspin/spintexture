using SpinTexture.Core.Textures;

namespace SpinTexture.Core.Tooling;

public sealed class DirectXTexCommandBuilder
{
    public NativeProcessCommand CreateConvert(
        string texconvPath,
        string inputPath,
        string outputDirectory,
        string outputFileType,
        int? width = null,
        int? height = null,
        bool wrapSampling = false,
        string resizeFilter = "CUBIC")
    {
        ValidateCommon(texconvPath, inputPath, outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFileType);

        var arguments = CommonArguments(outputDirectory);
        arguments.Add("-ft");
        arguments.Add(outputFileType);
        AddDimensions(arguments, width, height, resizeFilter);

        if (wrapSampling)
        {
            arguments.Add("-wrap");
        }

        arguments.Add(inputPath);
        return Command(texconvPath, arguments);
    }

    public NativeProcessCommand CreateEncode(
        string texconvPath,
        string inputPath,
        string outputDirectory,
        TextureMetadata sourceMetadata,
        int outputWidth,
        int outputHeight,
        int mipCount,
        bool preserveAlphaCoverage,
        bool wrapSampling = true,
        string resizeFilter = "CUBIC")
    {
        ValidateCommon(texconvPath, inputPath, outputDirectory);
        ArgumentNullException.ThrowIfNull(sourceMetadata);

        if (outputWidth <= 0 || outputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "Output dimensions must be positive.");
        }

        if (mipCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mipCount), "Mip count must be positive.");
        }

        ValidateResizeFilter(resizeFilter);

        var arguments = CommonArguments(outputDirectory);
        var outputType = sourceMetadata.FileFormat switch
        {
            TextureFileFormat.Dds => "dds",
            TextureFileFormat.Bmp => "bmp",
            TextureFileFormat.Tga => "tga",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceMetadata))
        };

        arguments.Add("-ft");
        arguments.Add(outputType);
        arguments.Add("-w");
        arguments.Add(outputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("-h");
        arguments.Add(outputHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("-if");
        arguments.Add(resizeFilter);
        if (wrapSampling)
        {
            arguments.Add("-wrap");
        }

        if (sourceMetadata.FileFormat == TextureFileFormat.Dds)
        {
            if (sourceMetadata.TexconvFormat is null)
            {
                throw new NotSupportedException($"DDS format {sourceMetadata.PayloadFormat} cannot be encoded safely.");
            }

            arguments.Add("-f");
            arguments.Add(sourceMetadata.TexconvFormat);
            if (!sourceMetadata.UsesDx10Header)
            {
                arguments.Add("-dx9");
            }
        }

        arguments.Add("-m");
        arguments.Add(mipCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (sourceMetadata.HasAlpha)
        {
            arguments.Add("-sepalpha");
            if (preserveAlphaCoverage)
            {
                // The single-dash short form is accepted by every texconv
                // release; the long-form "--keep-coverage" only exists in
                // builds that added long-name switches, and an unrecognized
                // option fails the whole encode.
                arguments.Add("-keepcoverage");
                arguments.Add("0.5");
            }
        }

        arguments.Add(inputPath);
        return Command(texconvPath, arguments);
    }

    private static List<string> CommonArguments(string outputDirectory) =>
        new()
        {
            "-nologo",
            "-y",
            "-o",
            outputDirectory
        };

    private static void AddDimensions(
        List<string> arguments,
        int? width,
        int? height,
        string resizeFilter)
    {
        if (width.HasValue != height.HasValue)
        {
            throw new ArgumentException("Width and height must be specified together.");
        }

        if (!width.HasValue)
        {
            return;
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Output dimensions must be positive.");
        }

        ValidateResizeFilter(resizeFilter);

        arguments.Add("-w");
        arguments.Add(width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("-h");
        arguments.Add(height!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("-if");
        arguments.Add(resizeFilter);
    }

    private static void ValidateResizeFilter(string resizeFilter)
    {
        if (resizeFilter is not ("POINT" or "LINEAR" or "CUBIC" or "FANT" or "BOX" or "TRIANGLE"))
        {
            throw new ArgumentOutOfRangeException(nameof(resizeFilter));
        }
    }

    private static void ValidateCommon(string texconvPath, string inputPath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texconvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
    }

    private static NativeProcessCommand Command(string texconvPath, IReadOnlyList<string> arguments) =>
        new(texconvPath, arguments, Path.GetDirectoryName(Path.GetFullPath(texconvPath)), DisplayName: "DirectXTex texconv");
}
