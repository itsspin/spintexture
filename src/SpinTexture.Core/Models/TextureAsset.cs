namespace SpinTexture.Core.Models;

public enum TextureKind
{
    Color,
    Cutout,
    Normal,
    Mask,
    UserInterface,
    Animated,
    Unsupported,
    Unknown
}

public sealed record TextureAsset(
    string ContainerPath,
    string LogicalName,
    string PayloadFormat,
    long UncompressedBytes,
    int Width,
    int Height,
    int MipCount,
    TextureKind Kind,
    bool IsPacked);
