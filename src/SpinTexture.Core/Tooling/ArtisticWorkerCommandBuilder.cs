namespace SpinTexture.Core.Tooling;

/// <summary>
/// Invokes the optional user-provided artistic painted worker (for example a
/// Stable-Diffusion img2img pipeline wrapped in a script). Contract: the
/// worker is called as
/// <c>worker -i &lt;inputDir&gt; -o &lt;outputDir&gt; -s 4 -f png</c>, must
/// write one PNG per input PNG with the identical file name, exactly 4x the
/// input dimensions, and must be deterministic (fixed seed) so repairs and
/// resumed builds reproduce identical packs. See docs/ARTISTIC_WORKER.md.
/// </summary>
public sealed class ArtisticWorkerCommandBuilder
{
    public const int WorkerScale = 4;

    // Diffusion pipelines can legitimately sit silent on one image for a long
    // time; the inactivity guard only catches a truly hung worker.
    internal static readonly TimeSpan WorkerInactivityTimeout = TimeSpan.FromHours(2);

    public NativeProcessCommand CreateStylize(
        string executablePath,
        string inputDirectory,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var contractArguments = new List<string>
        {
            "-i", inputDirectory,
            "-o", outputDirectory,
            "-s", WorkerScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "png"
        };

        // Batch scripts cannot be started directly with UseShellExecute=false;
        // route them through the command interpreter explicitly. The script is
        // invoked by bare relative name with the working directory set to its
        // folder: cmd's /c quote-stripping mangles a command line that starts
        // with a quoted absolute path whenever the install lives in a
        // directory containing spaces (for example "SpinTexture (1)"), while a
        // command that does not start with a quote is passed through intact.
        var isBatchScript = Path.GetExtension(executablePath) is { } extension
            && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase));
        var fileName = isBatchScript
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : executablePath;
        var arguments = isBatchScript
            ? new List<string> { "/d", "/c", $".\\{Path.GetFileName(executablePath)}" }
                .Concat(contractArguments)
                .ToList()
            : contractArguments;
        return new NativeProcessCommand(
            fileName,
            arguments,
            Path.GetDirectoryName(Path.GetFullPath(executablePath)),
            DisplayName: "External artistic painted worker",
            InactivityTimeout: WorkerInactivityTimeout);
    }
}
