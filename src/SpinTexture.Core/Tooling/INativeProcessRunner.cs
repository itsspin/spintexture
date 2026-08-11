namespace SpinTexture.Core.Tooling;

public interface INativeProcessRunner
{
    Task<NativeProcessResult> RunAsync(
        NativeProcessCommand command,
        IProgress<NativeOutputLine>? progress = null,
        CancellationToken cancellationToken = default);
}
