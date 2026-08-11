namespace SpinTexture.Core.Tooling;

public sealed class NativeProcessException : Exception
{
    public NativeProcessException(string message, NativeProcessResult result)
        : base(message)
    {
        Result = result;
    }

    public NativeProcessResult Result { get; }
}
