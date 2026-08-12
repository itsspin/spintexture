namespace SpinTexture.Core.Tooling;

public sealed record NativeProcessCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? DisplayName = null,
    // Native Vulkan workers can survive a display-driver reset without ever
    // exiting or producing another line of output. This is an inactivity
    // guard, not a fixed runtime limit: any worker output restarts the clock,
    // so legitimately long texture batches can continue indefinitely.
    TimeSpan? InactivityTimeout = null);

public enum NativeOutputStream
{
    StandardOutput,
    StandardError
}

public enum NativeTextureWorkPhase
{
    ToolOutput,
    CpuPreparation,
    GpuInference,
    Encoding
}

public sealed record NativeOutputLine(
    NativeOutputStream Stream,
    string Text,
    NativeTextureWorkPhase Phase = NativeTextureWorkPhase.ToolOutput,
    int Completed = 0,
    int Total = 0);

public sealed record NativeProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}
