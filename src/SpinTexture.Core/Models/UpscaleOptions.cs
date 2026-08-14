namespace SpinTexture.Core.Models;

public enum TexturePreset
{
    Faithful,
    ClassicHd,
    MaximumDetail,
    Illustrated,
    RusticPainted
}

/// <summary>
/// Selects the finishing palette and shape treatment used by Graphic Painted
/// Fantasy. The zero value is the compatibility default when an older manifest
/// has no theme field; it does not claim that the older payload used the current
/// graphic-painted finishing algorithm.
/// </summary>
public enum PaintedTheme
{
    ClassicPainted = 0,
    LightStorybook = 1,
    DarkGothic = 2,
    ComicInk = 3,
    ZoneAware = 4
}

public enum AssetScope
{
    SelectedZone = 0,
    WorldOnly = 1,
    WorldCharactersAndEquipment = 2,
    AllSafeTextures = 3,
    CharactersAndEquipmentOnly = 4,
    SpellEffectsOnly = 5
}

public sealed record UpscaleOptions(
    TexturePreset Preset,
    AssetScope Scope,
    int MaximumDimension,
    bool GenerateMipMaps,
    bool InstallAfterBuild,
    string? SelectedZone = null,
    IReadOnlyList<TextureOverride>? TextureOverrides = null,
    PaintedTheme PaintedTheme = PaintedTheme.ClassicPainted)
{
    public static UpscaleOptions Recommended => new(
        TexturePreset.ClassicHd,
        AssetScope.WorldOnly,
        2048,
        GenerateMipMaps: true,
        InstallAfterBuild: false);
}
