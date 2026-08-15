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

public sealed class WorldExpansionOptionViewModel : ObservableObject
{
    private readonly Action<WorldExpansionOptionViewModel> selectionChanged;
    private bool isSelected;
    private int detectedZoneCount;

    public WorldExpansionOptionViewModel(
        WorldExpansion value,
        string name,
        bool isRecommended,
        Action<WorldExpansionOptionViewModel> selectionChanged)
    {
        Value = value;
        Name = name;
        IsRecommended = isRecommended;
        this.selectionChanged = selectionChanged;
    }

    public WorldExpansion Value { get; }
    public string Name { get; }
    public bool IsRecommended { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(AvailabilityText));
                OnPropertyChanged(nameof(IsSelectionEnabled));
                OnPropertyChanged(nameof(AutomationName));
                selectionChanged(this);
            }
        }
    }

    public int DetectedZoneCount
    {
        get => detectedZoneCount;
        private set
        {
            if (SetProperty(ref detectedZoneCount, value))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(IsSelectionEnabled));
                OnPropertyChanged(nameof(AvailabilityText));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public bool IsAvailable => DetectedZoneCount > 0;

    public bool IsSelectionEnabled => IsAvailable || IsSelected;

    public string AvailabilityText => IsAvailable
        ? $"{DetectedZoneCount:N0} detected zone{(DetectedZoneCount == 1 ? string.Empty : "s")}"
        : IsSelected
            ? "Not found in this analysis; retained for saved recovery"
            : "Not found in this client";

    public string AutomationName =>
        $"{Name}, {AvailabilityText}{(IsRecommended ? ", recommended" : string.Empty)}";

    public void UpdateDetectedZoneCount(int value) =>
        DetectedZoneCount = Math.Max(0, value);

    public void SetSelectedSilently(bool value)
    {
        if (SetProperty(ref isSelected, value, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(AvailabilityText));
            OnPropertyChanged(nameof(IsSelectionEnabled));
            OnPropertyChanged(nameof(AutomationName));
        }
    }
}

public sealed record PaintedThemeOptionViewModel(
    PaintedTheme Value,
    string Name,
    string Description,
    bool IsRecommended = false)
{
    public override string ToString() => Name;
}

public sealed record ArtisticStyleOptionViewModel(
    string Key,
    string Name,
    string Description)
{
    public override string ToString() => Name;
}

public sealed record ArtisticModelTierOptionViewModel(
    string Key,
    string Name,
    string Description)
{
    public override string ToString() => Name;
}

public sealed record LogEntryViewModel(
    string Timestamp,
    string Level,
    string Message);
