namespace SpinTexture.Core.Models;

/// <summary>
/// Expansion-era groups used to select installed world zones. Values are stable
/// flags because build options and staged-pack manifests may persist combinations.
/// </summary>
[Flags]
public enum WorldExpansion
{
    None = 0,
    CurrentEql = 1 << 0,
    Kunark = 1 << 1,
    Velious = 1 << 2,
    Luclin = 1 << 3,
    PlanesOfPower = 1 << 4,
    OtherInstalled = 1 << 5,
    All = CurrentEql | Kunark | Velious | Luclin | PlanesOfPower | OtherInstalled
}
