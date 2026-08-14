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
        return new NativeProcessCommand(
            executablePath,
            [
                "-i", inputDirectory,
                "-o", outputDirectory,
                "-s", WorkerScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-f", "png"
            ],
            Path.GetDirectoryName(Path.GetFullPath(executablePath)),
            DisplayName: "External artistic painted worker",
            InactivityTimeout: WorkerInactivityTimeout);
    }
}
