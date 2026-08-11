namespace SpinTexture.Core.Tooling;

public sealed record NativeProcessCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? DisplayName = null);

public enum NativeOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record NativeOutputLine(NativeOutputStream Stream, string Text);

public sealed record NativeProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}
