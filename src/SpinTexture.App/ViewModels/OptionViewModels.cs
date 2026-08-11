using SpinTexture.Core.Models;

namespace SpinTexture.App.ViewModels;

public sealed record PresetOptionViewModel(
    TexturePreset Value,
    string Name,
    string Eyebrow,
    string Description,
    string Detail,
    string Model,
    string Look,
    string Performance,
    bool IsRecommended = false);

public sealed record ScopeOptionViewModel(
    AssetScope Value,
    string Name,
    string Description)
{
    // The dark ComboBox template uses the selection object directly. Returning
    // the friendly name keeps both the closed field and popup readable even on
    // Windows themes that do not honor DisplayMemberPath in a custom template.
    public override string ToString() => Name;
}

public sealed record LogEntryViewModel(
    string Timestamp,
    string Level,
    string Message);
