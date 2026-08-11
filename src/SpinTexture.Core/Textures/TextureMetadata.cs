namespace SpinTexture.Core.Textures;

public enum TextureFileFormat
{
    Dds,
    Bmp,
    Tga
}

public enum TextureAlphaStatus
{
    None,
    Possible,
    Explicit
}

public sealed record TextureMetadata(
    TextureFileFormat FileFormat,
    string PayloadFormat,
    int Width,
    int Height,
    int MipCount,
    int BitsPerPixel,
    TextureAlphaStatus AlphaStatus,
    bool IsCompressed,
    bool IsTopDown,
    bool IsCubeMap,
    bool IsVolumeTexture,
    int ArraySize,
    string? TexconvFormat,
    bool UsesDx10Header = false)
{
    public bool HasAlpha => AlphaStatus != TextureAlphaStatus.None;
    public bool IsSimpleTwoDimensionalTexture => !IsCubeMap && !IsVolumeTexture && ArraySize == 1;
}
