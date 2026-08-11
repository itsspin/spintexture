namespace SpinTexture.Core.Archives;

public sealed class PfsArchiveException : IOException
{
    public PfsArchiveException(string message)
        : base(message)
    {
    }

    public PfsArchiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
