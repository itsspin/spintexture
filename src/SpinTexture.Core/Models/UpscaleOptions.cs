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
    PaintedTheme PaintedTheme = PaintedTheme.ClassicPainted,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    WorldExpansion? WorldExpansions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PaintedStyleSettings? PaintedStyle = null)
{
    /// <summary>
    /// The effective painted style: explicit settings when present, otherwise
    /// the defaults. Null stays null in serialized manifests for compatibility.
    /// </summary>
    public PaintedStyleSettings ResolvedPaintedStyle =>
        (PaintedStyle ?? PaintedStyleSettings.Default).Clamped();

    public static UpscaleOptions Recommended => new(
        TexturePreset.ClassicHd,
        AssetScope.WorldOnly,
        2048,
        GenerateMipMaps: true,
        InstallAfterBuild: false);
}

/// <summary>
/// Defines and validates the optional era filter carried by a World build.
/// A null value is the compatibility identity for manifests and recovery
/// checkpoints written before era filtering existed: it deliberately means
/// every discovered World zone plus the shared World archives.
/// </summary>
public static class WorldExpansionSelectionPolicy
{
    public static bool SupportsSelection(AssetScope scope) => scope is
        AssetScope.WorldOnly or AssetScope.WorldCharactersAndEquipment;

    public static WorldExpansion ResolveSelectedOrAll(WorldExpansion? selection) =>
        selection ?? WorldExpansion.All;

    public static void Validate(UpscaleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WorldExpansions is not { } selection)
        {
            return;
        }

        if (!SupportsSelection(options.Scope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "World expansion selection is available only for World builds.");
        }

        if (selection == WorldExpansion.None
            || (selection & ~WorldExpansion.All) != WorldExpansion.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Select at least one supported World expansion.");
        }
    }
}
