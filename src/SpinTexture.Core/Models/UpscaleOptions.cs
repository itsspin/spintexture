namespace SpinTexture.Core.Models;

public enum TexturePreset
{
    Faithful,
    ClassicHd,
    MaximumDetail,
    Illustrated
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
    IReadOnlyList<TextureOverride>? TextureOverrides = null)
{
    public static UpscaleOptions Recommended => new(
        TexturePreset.ClassicHd,
        AssetScope.WorldOnly,
        2048,
        GenerateMipMaps: true,
        InstallAfterBuild: false);
}
