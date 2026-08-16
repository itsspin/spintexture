using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SpinTexture.Core;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;
using SpinTexture.Core.Services;

namespace SpinTexture.App;

public partial class StagedPackLibraryWindow : UserControl, INotifyPropertyChanged, IDisposable
{
    private readonly ProjectPaths paths;
    private readonly StagedPackCatalogService catalog = new();
    private readonly StagedPackDeletionService deletionService = new();
    private readonly TexturePackWorkflow workflow = new();
    private static readonly JsonSerializerOptions CompositionJsonOptions = CreateCompositionJsonOptions();
    private CancellationTokenSource? operationCancellation;
    private StagedPackRow? selectedPack;
    private bool isBusy;
    private bool closeRequested;
    private bool isInstallAcknowledged;
    private string selectedCategoryFilter = "All packs";
    private string statusText = "Loading completed staged builds...";
    private string selectionText = "No packs selected";
    private string highlightedSelectionText = "No rows highlighted";

    public StagedPackLibraryWindow(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        InitializeComponent();
        paths = WorkspaceLocator.ForInstall(installPath);
        PacksView = CollectionViewSource.GetDefaultView(Packs);
        PacksView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(StagedPackRow.Category)));
        PacksView.Filter = FilterPackByCategory;
        DataContext = this;
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<StagedPackRow> Packs { get; } = [];
    public ICollectionView PacksView { get; }
    public IReadOnlyList<string> CategoryFilters { get; } =
    [
        "All packs",
        "Active install",
        "Zones",
        "Characters & Equipment",
        "Spell Effects",
        "World / Combined",
        "Other / Compositions"
    ];

    public string SelectedCategoryFilter
    {
        get => selectedCategoryFilter;
        set
        {
            if (SetField(ref selectedCategoryFilter, value ?? "All packs"))
            {
                PacksView.Refresh();
            }
        }
    }
    public string PackStorageText => $"Pack storage: {paths.StagingPath}";

    public StagedPackRow? SelectedPack
    {
        get => selectedPack;
        set
        {
            if (SetField(ref selectedPack, value))
            {
                OnPropertyChanged(nameof(CanRepair));
                OnPropertyChanged(nameof(CanRepairCheckedPacks));
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanRename));
                OnPropertyChanged(nameof(CanAddHighlightedToCurrent));
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(CanRepair));
                OnPropertyChanged(nameof(CanRepairCheckedPacks));
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanCheckHighlighted));
                OnPropertyChanged(nameof(CanUncheckHighlighted));
                OnPropertyChanged(nameof(CanAddHighlightedToCurrent));
                OnPropertyChanged(nameof(CanCheckCurrentInstall));
                OnPropertyChanged(nameof(CanCheckAll));
                OnPropertyChanged(nameof(CanClearChecks));
                OnPropertyChanged(nameof(CanRename));
                OnPropertyChanged(nameof(CanCleanupDebris));
                OnPropertyChanged(nameof(IsLibraryEmpty));
                OnPropertyChanged(nameof(CanClose));
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string SelectionText
    {
        get => selectionText;
        private set => SetField(ref selectionText, value);
    }

    public string HighlightedSelectionText
    {
        get => highlightedSelectionText;
        private set => SetField(ref highlightedSelectionText, value);
    }

    public string DeleteButtonText => GetHighlightedPacks() switch
    {
        { Count: 0 } => "Delete Pack...",
        { Count: 1 } packs => $"Delete “{TruncateForButton(packs[0].Title)}”...",
        var packs => $"Delete {packs.Count:N0} Highlighted Packs..."
    };

    private static string TruncateForButton(string title) =>
        title.Length <= 26 ? title : title[..25] + "…";

    public string InstallButtonText
    {
        get
        {
            var count = Packs.Count(pack => pack.IsSelected && pack.CanSelect);
            return count == 0
                ? "Check Packs to Install"
                : count == 1
                    ? "Verify + Install 1 Checked Pack"
                    : $"Verify + Install {count:N0} Checked Packs";
        }
    }

    public bool IsInstallAcknowledged
    {
        get => isInstallAcknowledged;
        set
        {
            if (SetField(ref isInstallAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool CanInstall => !IsBusy
        && IsInstallAcknowledged
        && Packs.Any(pack => pack.IsSelected && pack.CanSelect);
    public bool CanRepair => !IsBusy
        && SelectedPack is { CanRepairAny: true, CanSelect: true };
    public bool CanRepairCheckedPacks => !IsBusy
        && Packs.Any(pack => pack.IsSelected && pack.CanSelect && pack.CanRepairAny);
    public bool CanPreview => !IsBusy
        && SelectedPack?.PreviewManifestPath is not null;
    public bool CanDelete => !IsBusy && GetHighlightedPacks().Count != 0;
    public bool CanRename => !IsBusy && SelectedPack is not null;
    public bool CanCleanupDebris => !IsBusy;
    public bool IsLibraryEmpty => !IsBusy && Packs.Count == 0;
    public bool CanCheckHighlighted => !IsBusy
        && GetHighlightedPacks().Any(pack => pack.CanSelect && !pack.IsSelected);
    public bool CanUncheckHighlighted => !IsBusy
        && GetHighlightedPacks().Any(pack => pack.CanSelect && pack.IsSelected);
    public bool CanAddHighlightedToCurrent => !IsBusy
        && Packs.Any(pack => pack.CanSelect && (pack.IsActive || pack.IsActiveComponent))
        && GetHighlightedPacks().Any(pack => pack.CanSelect);
    public bool CanCheckCurrentInstall => !IsBusy
        && Packs.Any(pack => pack.CanSelect && (pack.IsActive || pack.IsActiveComponent));
    public bool CanCheckAll => !IsBusy && Packs.Any(pack => pack.CanSelect);
    public bool CanClearChecks => !IsBusy
        && Packs.Any(pack => pack.CanSelect && pack.IsSelected);
    public bool CanClose => !IsBusy;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler<PackPreviewRequestedEventArgs>? PreviewRequested;
    public bool CanNavigateAway => !IsBusy;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var checkedPaths = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect)
            .Select(pack => Path.GetFullPath(pack.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RefreshAsync(checkedManifestPaths: checkedPaths).ConfigureAwait(true);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var pack = SelectedPack;
        if (IsBusy || pack is null)
        {
            return;
        }

        var newName = PromptForPackName(pack.AutoTitle, pack.CustomName ?? string.Empty);
        if (newName is null)
        {
            return;
        }

        try
        {
            var existing = StagedPackMetadataStore.TryRead(pack.BuildDirectory);
            await StagedPackMetadataStore
                .WriteAsync(pack.BuildDirectory, newName, existing?.Notes)
                .ConfigureAwait(true);
            StatusText = string.IsNullOrWhiteSpace(newName)
                ? $"Removed the custom name; the pack shows as “{pack.AutoTitle}” again."
                : $"Renamed pack to “{newName.Trim()}”. The name lives in pack-meta.json; pack identity, contents, and installs are unchanged.";
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"The pack name could not be saved: {exception.Message}",
                "Rename pack",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var checkedPaths = Packs
            .Where(candidate => candidate.IsSelected && candidate.CanSelect)
            .Select(candidate => Path.GetFullPath(candidate.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RefreshAsync(pack.ManifestPath, checkedPaths).ConfigureAwait(true);
    }

    private string? PromptForPackName(string autoTitle, string currentName)
    {
        var dialog = new Window
        {
            Title = "Rename staged pack",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Black,
            Foreground = System.Windows.Media.Brushes.White
        };
        var nameBox = new TextBox
        {
            Text = currentName,
            MaxLength = StagedPackUserMetadata.MaximumDisplayNameLength,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var okButton = new Button
        {
            Content = "Save name",
            IsDefault = true,
            Margin = new Thickness(0, 12, 8, 0),
            Padding = new Thickness(12, 4, 12, 4)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 4, 12, 4)
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(new TextBlock
        {
            Text = $"Custom display name for “{autoTitle}”. Leave empty to go back to the automatic name. The name is stored beside the pack and never affects its contents.",
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(nameBox);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        nameBox.SelectAll();
        nameBox.Focus();
        return dialog.ShowDialog() == true ? nameBox.Text : null;
    }

    private async void CleanupDebris_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        IReadOnlyList<StagedPackDebrisInfo> debris;
        try
        {
            debris = await Task.Run(() => StagedPackCatalogService.FindBuildDebris(paths))
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Leftover scan failed: {exception.Message}",
                "Clean up leftovers",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (debris.Count == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "No leftover build folders were found. Every folder in the pack library is a completed pack or a resumable build.",
                "Clean up leftovers",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var totalBytes = debris.Sum(item => item.Bytes);
        var listing = string.Join(
            "\n",
            debris.Take(8).Select(item => $"• {item.Name} · {FormatBytes(item.Bytes)}"));
        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"{debris.Count:N0} leftover folder(s) from interrupted builds use {FormatBytes(totalBytes)}. "
            + "They are not completed packs and cannot be resumed (each is at least a day old and has no manifest or checkpoint).\n\n"
            + listing
            + (debris.Count > 8 ? "\n…" : string.Empty)
            + "\n\nDelete them now? Completed packs, resumable builds, backups, and game files are never touched.",
            "Clean up leftovers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await Task
                .Run(() => StagedPackCatalogService.DeleteBuildDebris(paths, debris))
                .ConfigureAwait(true);
            StatusText = result.Failures.Count == 0
                ? $"Removed {result.DeletedDirectories:N0} leftover folder(s) and reclaimed {FormatBytes(result.ReclaimedBytes)}."
                : $"Removed {result.DeletedDirectories:N0} leftover folder(s); {result.Failures.Count:N0} could not be removed (files may be open).";
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                exception.Message,
                "Clean up leftovers",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<StagedPackRow>().LastOrDefault() is { } focused)
        {
            SelectedPack = focused;
        }
        else if (PackList.SelectedItem is StagedPackRow selected)
        {
            SelectedPack = selected;
        }

        UpdateHighlightedSelection();
        OnPropertyChanged(nameof(DeleteButtonText));
    }

    private bool FilterPackByCategory(object item)
    {
        if (item is not StagedPackRow pack || SelectedCategoryFilter == "All packs")
        {
            return item is StagedPackRow;
        }

        if (SelectedCategoryFilter == "Active install")
        {
            return pack.IsActive || pack.IsActiveComponent;
        }

        return string.Equals(pack.Category, SelectedCategoryFilter, StringComparison.Ordinal);
    }

    private void CheckHighlighted_Click(object sender, RoutedEventArgs e)
    {
        var highlighted = GetHighlightedPacks();
        foreach (var pack in highlighted.Where(pack => pack.CanSelect))
        {
            pack.IsSelected = true;
        }

        StatusText = highlighted.Count == 0
            ? "Highlight one or more pack rows first."
            : $"Added {highlighted.Count(pack => pack.CanSelect):N0} highlighted pack(s) to the install set. No game files were changed.";
        UpdateSelection();
        UpdateHighlightedSelection();
    }

    private void UncheckHighlighted_Click(object sender, RoutedEventArgs e)
    {
        var highlighted = GetHighlightedPacks();
        foreach (var pack in highlighted.Where(pack => pack.CanSelect))
        {
            pack.IsSelected = false;
        }

        StatusText = highlighted.Count == 0
            ? "Highlight one or more pack rows first."
            : $"Removed {highlighted.Count(pack => pack.CanSelect):N0} highlighted pack(s) from the install set. No game files were changed.";
        UpdateSelection();
        UpdateHighlightedSelection();
    }

    private void AddHighlightedToCurrent_Click(object sender, RoutedEventArgs e)
    {
        var highlighted = GetHighlightedPacks()
            .Where(pack => pack.CanSelect)
            .ToHashSet();
        foreach (var pack in Packs.Where(pack => pack.CanSelect))
        {
            if (pack.IsActive || pack.IsActiveComponent || highlighted.Contains(pack))
            {
                pack.IsSelected = true;
            }
        }

        StatusText = highlighted.Count == 0
            ? "Highlight one or more staged packs to add beside the current install."
            : $"Kept the current installed components and added {highlighted.Count:N0} highlighted pack(s). No game files were changed.";
        UpdateSelection();
        UpdateHighlightedSelection();
    }

    private void CheckCurrentInstall_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pack in Packs.Where(pack => pack.CanSelect))
        {
            pack.IsSelected = pack.IsActive || pack.IsActiveComponent;
        }

        StatusText = "The install set now matches the current enhanced install. No game files were changed.";
        UpdateSelection();
        UpdateHighlightedSelection();
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pack in Packs.Where(pack => pack.CanSelect))
        {
            pack.IsSelected = true;
        }

        StatusText = "All eligible source packs are checked. Exact verification and archive-conflict preflight still run before installation.";
        UpdateSelection();
        UpdateHighlightedSelection();
    }

    private void ClearChecks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pack in Packs.Where(pack => pack.CanSelect))
        {
            pack.IsSelected = false;
        }

        StatusText = "The install set was cleared. Staged packs and live game files were not changed.";
        UpdateSelection();
        UpdateHighlightedSelection();
    }

    private async Task RefreshAsync(
        string? selectManifestPath = null,
        IReadOnlySet<string>? checkedManifestPaths = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Reading the staged-pack catalog. No game files are being changed.";
        try
        {
            var activeBuildManifestPath = await FindActiveBuildManifestPathAsync().ConfigureAwait(true);
            var activeState = await ResolveActivePackStateAsync(
                    activeBuildManifestPath,
                    CancellationToken.None)
                .ConfigureAwait(true);
            var discovered = await catalog.DiscoverAsync(
                paths,
                StagedPackVerificationMode.Metadata).ConfigureAwait(true);
            var sourceRepairCandidates = await workflow
                .FindStagedPackSourceRepairCandidatesAsync(
                    paths,
                    discovered.Select(info => info.ManifestPath).ToArray())
                .ConfigureAwait(true);
            foreach (var old in Packs)
            {
                old.PropertyChanged -= OnPackSelectionChanged;
            }

            var rows = discovered
                .Select(info => new StagedPackRow(
                    info,
                    activeBuildManifestPath is not null
                    && SamePath(activeBuildManifestPath, info.ManifestPath),
                    activeState.ComponentManifestPaths.Contains(
                        Path.GetFullPath(info.ManifestPath)),
                    sourceRepairCandidates.Contains(
                        Path.GetFullPath(info.ManifestPath))))
                .OrderBy(row => row.CategoryOrder)
                .ThenByDescending(row => row.CreatedUtc)
                .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            Packs.Clear();
            foreach (var row in rows)
            {
                row.PropertyChanged += OnPackSelectionChanged;
                Packs.Add(row);
            }

            if (checkedManifestPaths is not null)
            {
                foreach (var row in Packs)
                {
                    row.IsSelected = row.CanSelect
                        && checkedManifestPaths.Contains(
                            Path.GetFullPath(row.ManifestPath));
                }
            }

            SelectedPack = selectManifestPath is null
                ? Packs.FirstOrDefault(pack => pack.IsActive)
                  ?? Packs.FirstOrDefault(pack => pack.IsActiveComponent)
                  ?? Packs.FirstOrDefault()
                : Packs.FirstOrDefault(pack => SamePath(pack.ManifestPath, selectManifestPath))
                  ?? Packs.FirstOrDefault();
            PacksView.Refresh();
            UpdateSelection();
            UpdateHighlightedSelection();
            StatusText = Packs.Count == 0
                ? "No completed staged packs were found. Build one from the main window first."
                : $"Found {Packs.Count:N0} completed pack(s) grouped by content. Exact SHA-256 verification runs before install, repair, or deletion."
                  + (activeState.Warning is null ? string.Empty : $" {activeState.Warning}");
        }
        catch (Exception exception)
        {
            StatusText = $"Pack catalog failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            ScheduleDeferredCloseIfRequested();
        }
    }

    private async Task<string?> FindActiveBuildManifestPathAsync()
    {
        var health = await workflow.AuditInstallHealthFastAsync(paths).ConfigureAwait(false);
        if (health.State != InstallHealthState.EnhancedActive
            || health.InstallManifestPath is null)
        {
            return null;
        }

        var install = await new ManifestStore()
            .ReadInstallManifestAsync(health.InstallManifestPath)
            .ConfigureAwait(false);
        return install.BuildManifestPath;
    }

    private async Task<ActivePackState> ResolveActivePackStateAsync(
        string? activeManifestPath,
        CancellationToken cancellationToken)
    {
        var components = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(activeManifestPath))
        {
            return new ActivePackState(components, Warning: null);
        }

        try
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recursionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await ExpandActiveComponentsAsync(
                    Path.GetFullPath(activeManifestPath),
                    components,
                    expanded,
                    recursionStack,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ActivePackState(components, Warning: null);
        }
        catch (Exception exception) when (exception is
            IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new ActivePackState(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                $"The installed combination's source-pack metadata could not be expanded: {exception.Message}");
        }
    }

    private async Task ExpandActiveComponentsAsync(
        string manifestPath,
        ISet<string> leafComponents,
        ISet<string> expanded,
        ISet<string> recursionStack,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeManifestPath = EnsurePathUnderRoot(
            paths.StagingPath,
            manifestPath);
        var buildDirectory = Path.GetDirectoryName(safeManifestPath)
            ?? throw new InvalidDataException(
                "An active staged-pack manifest has no build directory.");
        if (!Path.GetFileName(safeManifestPath).Equals(
                "manifest.json",
                StringComparison.OrdinalIgnoreCase)
            || !SamePath(Path.GetDirectoryName(buildDirectory)!, paths.StagingPath))
        {
            throw new InvalidDataException(
                "The active staged-pack manifest is not a direct managed pack.");
        }

        if (expanded.Contains(safeManifestPath))
        {
            return;
        }

        if (!recursionStack.Add(safeManifestPath))
        {
            throw new InvalidDataException(
                "A staged-pack composition contains a dependency cycle.");
        }

        try
        {
            var compositionPath = Path.Combine(buildDirectory, "composition.json");
            if (!File.Exists(compositionPath))
            {
                leafComponents.Add(safeManifestPath);
                expanded.Add(safeManifestPath);
                return;
            }

            await using var stream = new FileStream(
                compositionPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var composition = await JsonSerializer
                .DeserializeAsync<StagedPackCompositionDocument>(
                    stream,
                    CompositionJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The active staged-pack composition document is empty.");
            if (composition.SchemaVersion != StagedPackCompositionDocument.CurrentSchemaVersion
                || !SamePath(composition.InstallPath, paths.InstallPath)
                || !composition.CompositionId.Equals(
                    Path.GetFileName(buildDirectory),
                    StringComparison.OrdinalIgnoreCase)
                || composition.Components is null
                || composition.Components.Count == 0)
            {
                throw new InvalidDataException(
                    "The active staged-pack composition metadata is invalid.");
            }

            foreach (var component in composition.Components)
            {
                if (component is null)
                {
                    throw new InvalidDataException(
                        "The active composition contains an empty source-pack entry.");
                }

            var componentManifestPath = ResolveUnderRoot(
                paths.StagingPath,
                component.ManifestRelativePath);
            await EnsureFileMatchesAsync(
                    componentManifestPath,
                    component.ManifestLength,
                    component.ManifestSha256,
                    $"Active composition component {component.BuildId}",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExpandActiveComponentsAsync(
                        componentManifestPath,
                        leafComponents,
                        expanded,
                        recursionStack,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            expanded.Add(safeManifestPath);
        }
        finally
        {
            recursionStack.Remove(safeManifestPath);
        }
    }

    private static async Task EnsureFileMatchesAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0
            || expectedSha256 is not { Length: 64 }
            || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{description} has invalid fingerprint metadata.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var observedSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != expectedLength
            || !observedSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description} no longer matches its recorded SHA-256.");
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var selected = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect)
            .Select(pack => pack.ManifestPath)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Install {selected.Length:N0} checked pack(s)?\n\n"
            + "SpinTexture verifies every selected pack. When the selection only adds packs, already-active archives stay untouched and only the new archives are backed up and installed. Selections that remove or replace archives use the verified restore-and-switch path. No AI upscaling is rerun.",
            "Install checked packs",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await InstallCheckedPacksCoreAsync().ConfigureAwait(true);
    }

    private async Task InstallCheckedPacksCoreAsync()
    {
        var selected = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect)
            .Select(pack => pack.ManifestPath)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        await RunOperationAsync(
            "Verifying and installing the checked pack selection...",
            async (progress, token) =>
            {
                await workflow.ApplySelectedStagedPacksAsync(
                    paths,
                    selected,
                    progress,
                    token).ConfigureAwait(false);
                return (string?)null;
            },
            successMessage: "The checked packs are installed and active. Existing staged builds remain reusable.")
            .ConfigureAwait(true);
    }

    /// <summary>
    /// A repair only creates a staged replacement; the game keeps showing the
    /// previously installed files until the checked selection is installed.
    /// Summarize what the repair changed and walk the user straight into that
    /// install so "repaired" reliably means "visible in game".
    /// </summary>
    private async Task OfferInstallAfterRepairAsync(IReadOnlyList<TexturePackBuildResult> repairs)
    {
        if (repairs.Count == 0 || IsBusy)
        {
            return;
        }

        var newlyEnhanced = repairs.Sum(result => result.Report.Statistics.EnhancedTextures);
        var retriedKeptOriginal = repairs.Sum(result =>
            result.Report.Statistics.PreservedReasons.GetValueOrDefault(
                TextureProcessingPipeline.RetriedPreservedOriginalReason));
        var summary = $"Repair changed {newlyEnhanced:N0} texture(s)"
            + (retriedKeptOriginal > 0
                ? $" and kept {retriedKeptOriginal:N0} retried texture(s) on their verified originals."
                : ".");
        StatusText = $"{summary} Install the checked packs to update the game files.";

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"{summary}\n\nThe corrected replacement is staged and checked, but the game still uses the previously installed files until it is installed. Install the checked packs now?",
            "Install repaired packs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            await InstallCheckedPacksCoreAsync().ConfigureAwait(true);
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var pack = SelectedPack;
        if (pack is null || !pack.CanRepairAny)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            BuildRepairPrompt(pack),
            "Repair staged pack",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        TexturePackBuildResult? repairResult = null;
        await RunOperationAsync(
            pack.CanRepairSourceMismatch
                ? "Verifying original-source provenance, then applying current texture safety fixes..."
                : "Hash-verifying prior work and applying only the missing texture safety fixes...",
            async (progress, token) =>
            {
                repairResult = await RepairPackAsync(pack, progress, token).ConfigureAwait(false);
                return repairResult.StagedBuild.ManifestPath;
            },
            successMessage: "Repair complete. The corrected immutable replacement is checked; install the checked packs to update the game files.",
            replaceSelectedManifestPath: pack.ManifestPath)
            .ConfigureAwait(true);
        if (repairResult is not null)
        {
            await OfferInstallAfterRepairAsync([repairResult]).ConfigureAwait(true);
        }
    }

    private async void RepairCheckedPacks_Click(object sender, RoutedEventArgs e)
    {
        var repairQueue = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect && pack.CanRepairAny)
            .OrderByDescending(pack => pack.CanRepairSourceMismatch)
            .ThenBy(pack => pack.CategoryOrder)
            .ThenBy(pack => pack.CreatedUtc)
            .ToArray();
        if (repairQueue.Length == 0 || IsBusy)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Repair {repairQueue.Length:N0} checked pack(s)?\n\n"
            + "SpinTexture will process checked source packs that actually need repair. Source mismatches run first, then current safety fixes such as original-compatible sky/celestial assets and angle-safe cutouts. Each repair creates a new immutable replacement and swaps its checkmark from the old pack to the replacement.\n\n"
            + "The operation stops safely on the first error. Any replacements completed before that point remain staged and checked; no original staged pack is modified.",
            "Repair checked packs",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        var checkedManifestPaths = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect)
            .Select(pack => Path.GetFullPath(pack.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        string? lastReplacementManifestPath = null;
        var completedRepairs = new List<TexturePackBuildResult>();
        var progress = new Progress<ProgressUpdate>(update =>
        {
            var detail = string.IsNullOrWhiteSpace(update.CurrentItem)
                ? update.Message
                : $"{update.Message}  {update.CurrentItem}";
            StatusText = $"Repairing checked pack {completed + 1:N0} of {repairQueue.Length:N0}. {detail}";
        });
        try
        {
            foreach (var pack in repairQueue)
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                StatusText = $"Repairing checked pack {completed + 1:N0} of {repairQueue.Length:N0}: {pack.Title}";
                var result = await RepairPackAsync(
                        pack,
                        progress,
                        operationCancellation.Token)
                    .ConfigureAwait(true);
                lastReplacementManifestPath = result.StagedBuild.ManifestPath;
                completedRepairs.Add(result);
                checkedManifestPaths.Remove(Path.GetFullPath(pack.ManifestPath));
                checkedManifestPaths.Add(Path.GetFullPath(lastReplacementManifestPath));
                completed++;
            }

            IsBusy = false;
            await RefreshAsync(lastReplacementManifestPath, checkedManifestPaths).ConfigureAwait(true);
            StatusText = $"Repaired {completed:N0} checked pack(s). Each corrected replacement is checked; install the checked packs to update the game files.";
            await OfferInstallAfterRepairAsync(completedRepairs).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
            await RefreshAsync(lastReplacementManifestPath, checkedManifestPaths).ConfigureAwait(true);
            StatusText = completed == 0
                ? "Repair canceled safely before any replacement completed."
                : $"Repair canceled after {completed:N0} replacement(s). Completed replacements remain staged and checked.";
        }
        catch (Exception exception)
        {
            IsBusy = false;
            await RefreshAsync(lastReplacementManifestPath, checkedManifestPaths).ConfigureAwait(true);
            StatusText = completed == 0
                ? $"Repair stopped safely: {exception.Message}"
                : $"Repair stopped after {completed:N0} completed replacement(s): {exception.Message}";
            MessageBox.Show(
                Window.GetWindow(this),
                StatusText,
                "Repair checked packs",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            IsBusy = false;
            ScheduleDeferredCloseIfRequested();
        }
    }

    private Task<TexturePackBuildResult> RepairPackAsync(
        StagedPackRow pack,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken) =>
        pack.CanRepairSourceMismatch
            ? workflow.RepairStagedPackSourceMismatchAsync(
                paths,
                pack.ManifestPath,
                progress,
                cancellationToken)
            : workflow.RepairStagedPackAsync(
                paths,
                pack.ManifestPath,
                progress: progress,
                cancellationToken: cancellationToken);

    private static string BuildRepairPrompt(StagedPackRow pack)
    {
        var reason = pack.CanRepairSourceMismatch
            ? "A managed source mismatch was detected. SpinTexture will rebuild only affected archives from verified original bytes, reuse complete unaffected archives, and apply any missing current safety fixes in the same replacement."
            : pack.RepairReasonText;
        return $"Repair {pack.Title}?\n\n{reason}\n\n"
            + "The complete baseline is SHA-256 verified first. Its recorded texture style and painted theme are preserved; SpinTexture stops instead of silently substituting another style. Repair creates a new immutable replacement, leaves this completed staged pack unchanged, and checks the replacement for the next install. If this pack was checked, its checkmark moves to the replacement.";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetHighlightedPacks();
        if (targets.Count == 0 || IsBusy)
        {
            return;
        }

        operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = $"Checking active-install and composition dependencies for {targets.Count:N0} highlighted pack(s)...";
        var checkedManifestPaths = Packs
            .Where(row => row.IsSelected && row.CanSelect)
            .Select(row => Path.GetFullPath(row.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedBuildIds = new List<string>();
        try
        {
            var plans = new List<(StagedPackRow Pack, StagedPackDeletionPlan Plan)>(targets.Count);
            foreach (var target in targets)
            {
                var plan = await deletionService.PlanAsync(
                        paths,
                        target.ManifestPath,
                        operationCancellation.Token)
                    .ConfigureAwait(true);
                plans.Add((target, plan));
            }

            var blocked = plans.Where(item => !item.Plan.CanDelete).ToArray();
            if (blocked.Length != 0)
            {
                var details = string.Join(
                    "\n\n",
                    blocked.Take(5).Select(item => $"{item.Pack.Title}: {item.Plan.Summary}"));
                if (blocked.Length > 5)
                {
                    details += $"\n\n...and {blocked.Length - 5:N0} more blocked pack(s).";
                }

                StatusText = $"Bulk delete blocked safely: {blocked.Length:N0} highlighted pack(s) are active, referenced, or could not be verified.";
                MessageBox.Show(
                    Window.GetWindow(this),
                    "Nothing was deleted because every highlighted pack must pass safety preflight.\n\n" + details,
                    "Highlighted packs cannot be deleted",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var titleList = string.Join(
                "\n",
                plans.Take(10).Select(item => $"• {item.Pack.Title}"));
            if (plans.Count > 10)
            {
                titleList += $"\n• ...and {plans.Count - 10:N0} more";
            }

            var answer = MessageBox.Show(
                Window.GetWindow(this),
                $"Permanently delete {plans.Count:N0} highlighted staged pack(s)?\n\n"
                + titleList
                + "\n\nEvery pack passed active-install, dependency, path, and reparse-point preflight. Only these managed staging directories will be removed. Live game files will not be changed. This cannot be undone; deleted AI output would have to be rebuilt.",
                plans.Count == 1 ? "Delete highlighted pack" : "Delete highlighted packs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = "Deletion canceled. No staged packs were changed.";
                return;
            }

            for (var index = 0; index < plans.Count; index++)
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                var current = plans[index];
                var operationNumber = index + 1;
                var progress = new Progress<ProgressUpdate>(update =>
                {
                    var detail = string.IsNullOrWhiteSpace(update.CurrentItem)
                        ? update.Message
                        : $"{update.Message}  {update.CurrentItem}";
                    StatusText = $"Deleting {operationNumber:N0} of {plans.Count:N0}. {detail}";
                });
                var result = await deletionService.DeleteAsync(
                        paths,
                        current.Pack.ManifestPath,
                        progress,
                        operationCancellation.Token)
                    .ConfigureAwait(true);
                deletedBuildIds.Add(result.BuildId);
                checkedManifestPaths.Remove(Path.GetFullPath(current.Pack.ManifestPath));
            }

            IsBusy = false;
            await RefreshAsync(checkedManifestPaths: checkedManifestPaths).ConfigureAwait(true);
            StatusText = $"Deleted {deletedBuildIds.Count:N0} highlighted staged pack(s). Other completed packs and live game files were not changed.";
        }
        catch (OperationCanceledException)
        {
            if (deletedBuildIds.Count != 0)
            {
                IsBusy = false;
                await RefreshAsync(checkedManifestPaths: checkedManifestPaths).ConfigureAwait(true);
            }

            StatusText = deletedBuildIds.Count == 0
                ? "Deletion canceled safely before an irreversible step began."
                : $"Deletion stopped between packs after {deletedBuildIds.Count:N0} completed deletion(s). Remaining packs and live game files were not changed.";
        }
        catch (Exception exception)
        {
            if (deletedBuildIds.Count != 0)
            {
                IsBusy = false;
                await RefreshAsync(checkedManifestPaths: checkedManifestPaths).ConfigureAwait(true);
            }

            StatusText = deletedBuildIds.Count == 0
                ? $"Delete failed safely: {exception.Message}"
                : $"Delete stopped after {deletedBuildIds.Count:N0} completed deletion(s): {exception.Message}";
            MessageBox.Show(
                Window.GetWindow(this),
                StatusText,
                "Delete highlighted packs",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            IsBusy = false;
            ScheduleDeferredCloseIfRequested();
        }
    }

    private async Task RunOperationAsync(
        string startingStatus,
        Func<IProgress<ProgressUpdate>, CancellationToken, Task<string?>> operation,
        string successMessage,
        string? replaceSelectedManifestPath = null)
    {
        if (IsBusy)
        {
            return;
        }

        operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = startingStatus;
        var checkedManifestPaths = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect)
            .Select(pack => Path.GetFullPath(pack.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var progress = new Progress<ProgressUpdate>(update =>
        {
            StatusText = string.IsNullOrWhiteSpace(update.CurrentItem)
                ? update.Message
                : $"{update.Message}  {update.CurrentItem}";
        });
        try
        {
            var selectManifest = await operation(progress, operationCancellation.Token)
                .ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(replaceSelectedManifestPath))
            {
                checkedManifestPaths.Remove(Path.GetFullPath(replaceSelectedManifestPath));
            }

            if (!string.IsNullOrWhiteSpace(selectManifest))
            {
                checkedManifestPaths.Add(Path.GetFullPath(selectManifest));
            }

            StatusText = successMessage;
            IsBusy = false;
            await RefreshAsync(selectManifest, checkedManifestPaths).ConfigureAwait(true);
            StatusText = successMessage;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation canceled safely. Completed staged packs were not deleted.";
        }
        catch (Exception exception)
        {
            StatusText = $"Operation failed safely: {exception.Message}";
            MessageBox.Show(
                Window.GetWindow(this),
                exception.Message,
                "Staged pack operation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            IsBusy = false;
            ScheduleDeferredCloseIfRequested();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            closeRequested = true;
            operationCancellation?.Cancel();
            StatusText = "Canceling safely. The Build section will return after the current file operation stops.";
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var manifestPath = SelectedPack?.PreviewManifestPath;
        if (manifestPath is null)
        {
            return;
        }

        try
        {
            var baselinePack = SelectedPack
                ?? throw new InvalidOperationException("The selected staged pack is no longer available.");
            var baselineManifestPath = baselinePack.ManifestPath
                ?? throw new InvalidOperationException("The selected staged pack no longer has a build manifest.");
            Func<IReadOnlyList<TextureOverride>, Task>? applyChoices = null;
            if (baselinePack.CanReviseTextures)
            {
                applyChoices = async choices =>
                {
                    TexturePackBuildResult? revisionResult = null;
                    await RunOperationAsync(
                        "Hash-verifying the pack and revising only the reviewed textures...",
                        async (progress, token) =>
                        {
                            revisionResult = await workflow.RepairStagedPackAsync(
                                paths,
                                baselineManifestPath,
                                progress: progress,
                                cancellationToken: token,
                                textureOverrides: choices).ConfigureAwait(false);
                            return revisionResult.StagedBuild.ManifestPath;
                        },
                        "Texture revision complete. Unselected prior work was reused and the new immutable pack is checked; install the checked packs to update the game files.",
                        replaceSelectedManifestPath: baselineManifestPath).ConfigureAwait(true);
                    if (revisionResult is not null)
                    {
                        await OfferInstallAfterRepairAsync([revisionResult]).ConfigureAwait(true);
                    }
                };
            }

            PreviewRequested?.Invoke(
                this,
                new PackPreviewRequestedEventArgs(manifestPath, applyChoices));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                exception.Message,
                "Pack preview",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ScheduleDeferredCloseIfRequested()
    {
        if (closeRequested)
        {
            closeRequested = false;
            _ = Dispatcher.BeginInvoke(new Action(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
        }
    }

    private void OnPackSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StagedPackRow.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        var selected = Packs.Where(pack => pack.IsSelected && pack.CanSelect).ToArray();
        SelectionText = selected.Length == 0
            ? "INSTALL SET · No packs checked"
            : $"INSTALL SET · {selected.Length:N0} pack(s) checked · {selected.Sum(pack => pack.ArtifactCount):N0} source archives · {FormatBytes(selected.Sum(pack => pack.StagedBytes))}";
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(CanCheckHighlighted));
        OnPropertyChanged(nameof(CanUncheckHighlighted));
        OnPropertyChanged(nameof(CanAddHighlightedToCurrent));
        OnPropertyChanged(nameof(CanClearChecks));
        OnPropertyChanged(nameof(CanRepairCheckedPacks));
    }

    private void UpdateHighlightedSelection()
    {
        var highlighted = GetHighlightedPacks();
        HighlightedSelectionText = highlighted.Count switch
        {
            0 => "No rows highlighted",
            1 => $"1 row highlighted · focused details: {SelectedPack?.Title ?? highlighted[0].Title}",
            _ => $"{highlighted.Count:N0} rows highlighted · Ctrl/Shift-click to adjust; focused actions use {SelectedPack?.Title ?? "one row"}"
        };
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanCheckHighlighted));
        OnPropertyChanged(nameof(CanUncheckHighlighted));
        OnPropertyChanged(nameof(CanAddHighlightedToCurrent));
    }

    private IReadOnlyList<StagedPackRow> GetHighlightedPacks()
    {
        if (!IsInitialized || PackList is null)
        {
            return [];
        }

        return PackList.SelectedItems
            .OfType<StagedPackRow>()
            .OrderBy(pack => Packs.IndexOf(pack))
            .ToArray();
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return EnsurePathUnderRoot(root, Path.Combine(root, relativePath));
    }

    private static string EnsurePathUnderRoot(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        if (relative is "." or ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidDataException(
                "A staged-pack component escaped the managed staging workspace.");
        }

        return normalizedCandidate;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatPreset(TexturePreset preset, TextureBuildReport? report)
    {
        var hasCurrentPaintedProfile = (report?.PaintedProfileRevision ?? 0)
            == TextureBuildReport.GetCurrentPaintedProfileRevision(preset);
        return preset switch
        {
            TexturePreset.Faithful => "Original Clarity",
            TexturePreset.ClassicHd => "Texture HD",
            TexturePreset.MaximumDetail => "Material Detail",
            TexturePreset.Illustrated when !hasCurrentPaintedProfile =>
                "Legacy Illustrated / Clean Painted",
            TexturePreset.Illustrated => "Graphic Painted Fantasy",
            TexturePreset.RusticPainted when !hasCurrentPaintedProfile =>
                "Legacy Rustic Painted Fantasy",
            TexturePreset.RusticPainted => "Rustic Painted Fantasy",
            _ => preset.ToString()
        };
    }

    private static string FormatPaintedThemeSuffix(
        UpscaleOptions options,
        TextureBuildReport? report) =>
        options.Preset != TexturePreset.Illustrated
            || (report?.PaintedProfileRevision ?? 0)
                != TextureBuildReport.GetCurrentPaintedProfileRevision(
                    options.Preset)
            ? string.Empty
            : options.PaintedTheme switch
            {
                PaintedTheme.ClassicPainted => " / Classic painted",
                PaintedTheme.LightStorybook => " / Light storybook",
                PaintedTheme.DarkGothic => " / Dark gothic",
                PaintedTheme.ComicInk => " / Comic ink",
                PaintedTheme.ZoneAware => " / Follow each zone",
                _ => string.Empty
            };

    private static string FormatWorldExpansionSelection(
        WorldExpansion? selection,
        bool compact)
    {
        if (selection is null)
        {
            return compact
                ? "All detected zones (legacy)"
                : "all detected world zones (legacy pack)";
        }

        var selectedGroups = WorldExpansionCatalog.OrderedGroups
            .Where(expansion => (selection.Value & expansion) == expansion)
            .ToArray();
        if (selectedGroups.Length == 0)
        {
            return "No world groups";
        }

        if (compact && selectedGroups.Length > 2)
        {
            return selectedGroups.Length == WorldExpansionCatalog.OrderedGroups.Count
                ? "All detected groups"
                : $"{selectedGroups.Length:N0} expansion groups";
        }

        return string.Join(
            " + ",
            selectedGroups.Select(expansion => expansion == WorldExpansion.CurrentEql
                ? "Current EQL"
                : WorldExpansionCatalog.GetDisplayName(expansion)));
    }

    private static JsonSerializerOptions CreateCompositionJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public void Dispose()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = null;
        foreach (var pack in Packs)
        {
            pack.PropertyChanged -= OnPackSelectionChanged;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed record ActivePackState(
        IReadOnlySet<string> ComponentManifestPaths,
        string? Warning);

    public sealed class StagedPackRow : INotifyPropertyChanged
    {
        private bool isSelected;

        public StagedPackRow(
            StagedPackInfo info,
            bool isActive,
            bool isActiveComponent,
            bool canRepairSourceMismatch)
        {
            ManifestPath = info.ManifestPath;
            BuildDirectory = info.BuildDirectory;
            BuildId = info.CandidateBuildId;
            CustomName = info.UserMetadata?.DisplayName;
            IsActive = isActive;
            IsActiveComponent = isActiveComponent && !isActive;
            ArtifactPaths = info.Artifacts
                .Select(artifact => artifact.CanonicalRelativeInstallPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ArtifactCount = info.ArtifactCount;
            StagedBytes = info.StagedBytes;
            var manifest = info.Manifest;
            IsComposition = File.Exists(Path.Combine(info.BuildDirectory, "composition.json"));
            CanSelect = info.IsMetadataValid && !IsComposition;
            isSelected = CanSelect && (IsActive || IsActiveComponent);
            var report = TryReadReport(info.BuildDirectory);
            var isLegacyRepair = report?.IsIncrementalRepair == true;
            var isSourceRepair = report?.IsSourceMismatchRepair == true;
            // The report property keeps its original compatibility name, but
            // revision-based repairs now apply every missing safety rule.
            var isSafetyRepair = report?.IsSafetyRepair == true
                || report?.IsCutoutMipRepair == true;
            var isManualTextureRevision = report?.IsManualTextureRevision == true;
            var visualProfile = manifest is null
                ? string.Empty
                : $"{FormatPreset(manifest.Options.Preset, report)}"
                  + FormatPaintedThemeSuffix(manifest.Options, report);
            var worldSelectionDetail = manifest is not null
                                       && WorldExpansionSelectionPolicy.SupportsSelection(manifest.Options.Scope)
                ? $" · world selection: {FormatWorldExpansionSelection(manifest.Options.WorldExpansions, compact: false)}"
                : string.Empty;
            var visualProfileAndWorldSelection = visualProfile + worldSelectionDetail;
            var scopeTitle = manifest?.Options.Scope switch
            {
                AssetScope.SelectedZone => $"Zone \u00B7 {manifest.Options.SelectedZone ?? "Unknown"}",
                AssetScope.CharactersAndEquipmentOnly => "Characters + Equipment",
                AssetScope.WorldCharactersAndEquipment =>
                    $"World ({FormatWorldExpansionSelection(manifest.Options.WorldExpansions, compact: true)}) + Characters + Equipment",
                AssetScope.WorldOnly =>
                    $"World textures · {FormatWorldExpansionSelection(manifest.Options.WorldExpansions, compact: true)}",
                AssetScope.AllSafeTextures => "All safe textures",
                AssetScope.SpellEffectsOnly => "Spell effects",
                _ => info.CandidateBuildId
            };
            Category = IsComposition
                ? IsActive ? "Installed Combination" : "Other / Compositions"
                : manifest?.Options.Scope switch
                {
                    AssetScope.SelectedZone => "Zones",
                    AssetScope.CharactersAndEquipmentOnly => "Characters & Equipment",
                    AssetScope.SpellEffectsOnly => "Spell Effects",
                    AssetScope.WorldOnly
                        or AssetScope.WorldCharactersAndEquipment
                        or AssetScope.AllSafeTextures => "World / Combined",
                    _ => "Other / Compositions"
                };
            CategoryOrder = Category switch
            {
                "Installed Combination" => 0,
                "Zones" => 1,
                "Characters & Equipment" => 2,
                "Spell Effects" => 3,
                "World / Combined" => 4,
                _ => 4
            };
            CreatedUtc = manifest?.CreatedUtc ?? DateTimeOffset.MinValue;
            AutoTitle = IsComposition
                ? IsActive ? "Installed pack combination" : "Generated pack composition"
                : isSourceRepair
                    ? $"Source repaired \u00B7 {scopeTitle}"
                    : isSafetyRepair
                    ? $"Safety repaired \u00B7 {scopeTitle}"
                    : isManualTextureRevision
                    ? $"Texture revised \u00B7 {scopeTitle}"
                    : isLegacyRepair
                    ? $"Repaired \u00B7 {scopeTitle}"
                    : scopeTitle;
            Title = CustomName is null ? AutoTitle : CustomName;
            Badge = IsActive && IsComposition
                ? "ACTIVE COMBINATION"
                : IsActive
                    ? "ACTIVE"
                    : IsActiveComponent
                        ? "ACTIVE COMPONENT"
                        : IsComposition
                            ? "COMPOSITION"
                            : isSourceRepair
                                ? "SOURCE REPAIRED"
                                : isSafetyRepair
                                ? "SAFETY REPAIRED"
                                : isManualTextureRevision
                                ? "TEXTURE REVISED"
                                : isLegacyRepair
                                ? "REPAIRED"
                                : "STAGED";
            (BadgeBackground, BadgeForeground, CardBorderBrush) = Badge switch
            {
                "ACTIVE" or "ACTIVE COMPONENT" => ("#17352B", "#65D6A6", "#356A57"),
                "ACTIVE COMBINATION" => ("#202E46", "#B9D0FF", "#4B6797"),
                "COMPOSITION" => ("#27253A", "#CEC7FF", "#4E4A73"),
                "SOURCE REPAIRED" => ("#382B18", "#F5C76B", "#785C2F"),
                "SAFETY REPAIRED" => ("#18343A", "#8CE1E8", "#34717B"),
                "TEXTURE REVISED" => ("#32243A", "#D9B6F5", "#674A78"),
                "REPAIRED" => ("#233345", "#BFD9F5", "#405E7D"),
                _ => ("#17352B", "#65D6A6", "#292E36")
            };
            Details = manifest is null
                ? info.CandidateBuildId
                : IsComposition
                    ? $"{ArtifactCount:N0} combined archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                      + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : isSourceRepair && report is not null
                        ? $"{visualProfileAndWorldSelection} \u00B7 {report.ReusedArtifacts:N0} complete archives reused \u00B7 "
                          + $"{report.RebuiltArtifacts:N0} source-contaminated archives rebuilt \u00B7 "
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : isSafetyRepair && report is not null
                        ? $"{visualProfileAndWorldSelection} \u00B7 revision {report.BaselineTexturePipelineRevision:N0}\u2192{report.TexturePipelineRevision:N0} \u00B7 "
                          + $"{report.Statistics.ReusedTextures:N0} prior textures reused \u00B7 "
                          + $"{report.Statistics.EnhancedTextures:N0} affected textures safely updated \u00B7 "
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : isLegacyRepair && report is not null
                        ? $"{visualProfileAndWorldSelection} \u00B7 {report.Statistics.ReusedTextures:N0} prior textures reused \u00B7 "
                          + $"{report.Statistics.EnhancedTextures:N0} newly enhanced \u00B7 "
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        : $"{visualProfileAndWorldSelection} \u00B7 {manifest.Options.MaximumDimension:N0}px \u00B7 "
                          + (report is null
                              ? string.Empty
                              : $"{report.Statistics.EnhancedTextures:N0} enhanced \u00B7 "
                                + (report.Statistics.FallbackTextures > 0
                                    ? $"{report.Statistics.FallbackTextures:N0} validated fallbacks \u00B7 "
                                    : string.Empty)
                                + $"{report.Statistics.PreservedTextures:N0} protected/missing \u00B7 ")
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            StateText = IsComposition
                ? IsActive
                    ? "Installed combination record. Its source packs are checked below; this row cannot be recomposed."
                    : "Generated combination record. Select its source packs instead; this row cannot be nested into another composition."
                : info.Summary;
            ContentsSummary = report is null
                ? $"{Title} contains {ArtifactCount:N0} complete install archive(s). Archive conflicts are checked before composition."
                : isSourceRepair
                    ? $"{Title} contains {ArtifactCount:N0} complete install archive(s). "
                      + $"The source repair reused {report.ReusedArtifacts:N0} verified complete archive outputs and rebuilt "
                      + $"{report.RebuiltArtifacts:N0} affected archives from original bytes. "
                      + "Archive conflicts are checked before composition."
                : isSafetyRepair && report is not null
                    ? $"{Title} contains {ArtifactCount:N0} complete install archive(s), derived from verified baseline "
                      + $"{report.BaselineBuildId ?? "unknown"}. The report records {report.Statistics.EnhancedTextures:N0} "
                      + $"affected textures updated under safety revision {report.TexturePipelineRevision:N0} and "
                      + $"{report.Statistics.ReusedTextures:N0} prior enhanced textures reused. Archive conflicts are checked before composition."
                : $"{Title} contains {ArtifactCount:N0} complete install archive(s). "
                  + $"The report records {report!.Statistics.EnhancedTextures:N0} newly enhanced, "
                  + $"{report!.Statistics.ReusedTextures:N0} reused, and "
                  + $"{report!.Statistics.PreservedTextures:N0} protected or still-missing texture entries. "
                  + (report!.Statistics.FallbackTextures > 0
                      ? $"{report.Statistics.FallbackTextures:N0} textures used a validated safety fallback instead of the requested model route. "
                      : string.Empty)
                  + "Archive conflicts are checked before composition.";
            if (IsComposition)
            {
                var componentSummary = DescribeCompositionComponents(info);
                if (componentSummary is not null)
                {
                    ContentsSummary = componentSummary;
                }
            }

            var isCharacterScope = manifest?.Options.Scope is
                AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment;
            var missingRepairRules = manifest is null
                ? Array.Empty<string>()
                : TextureProcessingPipeline.GetMissingRepairRuleIds(
                    report,
                    manifest.Options.Scope,
                    ArtifactPaths);
            CanRepair = !IsComposition
                && manifest is not null
                && missingRepairRules.Count != 0;
            IsTargetedSafetyRepairCandidate = CanRepair
                && manifest is not null
                && TextureProcessingPipeline.RequiresTargetedSafetyRepair(
                    report,
                    manifest.Options.Scope,
                    ArtifactPaths);
            CanRepairSourceMismatch = !IsComposition
                && canRepairSourceMismatch
                && !isSourceRepair;
            CanRepairAny = CanRepair || CanRepairSourceMismatch;
            var needsCelestialSkySafety = missingRepairRules.Contains(
                TextureProcessingPipeline.CelestialSkySafetyRuleId,
                StringComparer.Ordinal)
                || missingRepairRules.Contains(
                    TextureProcessingPipeline.NativeSkyResourceSafetyRuleId,
                    StringComparer.Ordinal);
            var needsCutoutSafety = missingRepairRules.Contains(
                TextureProcessingPipeline.CutoutMipSafetyRuleId,
                StringComparer.Ordinal);
            var needsTranslucentMaterialSafety = missingRepairRules.Contains(
                TextureProcessingPipeline.LegacyTranslucentMaterialSafetyRuleId,
                StringComparer.Ordinal);
            var needsExpandedCoverage = missingRepairRules.Contains(
                TextureProcessingPipeline.ExpandedClassicCoverageRuleId,
                StringComparer.Ordinal);
            RepairReasonText = BuildRepairReasonText(
                isCharacterScope,
                IsTargetedSafetyRepairCandidate,
                needsCelestialSkySafety,
                needsCutoutSafety,
                needsTranslucentMaterialSafety)
                + (needsExpandedCoverage
                    ? " Expanded coverage also re-attempts previously preserved classic textures (walls, trim, and similar world art) that are enhanceable now; this one-time pass can take a while on large painted packs."
                    : string.Empty);
            RepairStatusText = CanRepairSourceMismatch
                ? "Managed source mismatch detected. Repair verifies original-source provenance first, then applies current texture safety fixes in the same immutable replacement."
                : RepairReasonText;
            RepairBadgeText = CanRepairSourceMismatch
                ? "REPAIR REQUIRED BEFORE INSTALL"
                : "REPAIR AVAILABLE";
            RepairStatusForeground = CanRepairSourceMismatch ? "#FFD5D8" : "#F5DEAB";
            RepairPanelBackground = CanRepairSourceMismatch ? "#451F22" : "#382B18";
            RepairPanelBorderBrush = CanRepairSourceMismatch ? "#8A4149" : "#785C2F";
            SelectionHint = IsComposition
                ? "Generated combinations are not selectable. Check their source packs instead."
                : IsActiveComponent
                    ? "This source pack is part of the installed combination."
                    : "Include this completed pack in the next installed selection.";
            var previewPath = Path.Combine(
                info.BuildDirectory,
                "previews",
                "preview-manifest.json");
            PreviewManifestPath = File.Exists(previewPath)
                ? Path.GetFullPath(previewPath)
                : null;
            CanReviseTextures = !IsComposition
                && manifest is not null
                && !RequiresPipelineRepair(report, manifest.Options.Scope, ArtifactPaths)
                && manifest.Options.Scope is not (AssetScope.AllSafeTextures or AssetScope.SpellEffectsOnly)
                && ArtifactPaths.All(path => EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(path)));
        }

        public string ManifestPath { get; }
        public bool IsActive { get; }
        public bool IsActiveComponent { get; }
        public bool IsComposition { get; }
        public bool CanSelect { get; }
        public bool CanRepair { get; }
        public bool CanReviseTextures { get; }
        public bool IsTargetedSafetyRepairCandidate { get; }
        public bool CanRepairSourceMismatch { get; }
        public bool CanRepairAny { get; }
        public string? PreviewManifestPath { get; }
        public string Title { get; }
        public string AutoTitle { get; }
        public string BuildDirectory { get; }
        public string BuildId { get; }
        public string? CustomName { get; }
        public string Badge { get; }
        public string BadgeBackground { get; }
        public string BadgeForeground { get; }
        public string CardBorderBrush { get; }
        public string Details { get; }
        public string StateText { get; }
        public string RepairStatusText { get; }
        public string RepairStatusForeground { get; }
        public string RepairBadgeText { get; }
        public string RepairPanelBackground { get; }
        public string RepairPanelBorderBrush { get; }
        public string RepairReasonText { get; }
        public string SelectionHint { get; }
        public string Category { get; }
        public int CategoryOrder { get; }
        public DateTimeOffset CreatedUtc { get; }
        public string ContentsSummary { get; }
        public IReadOnlyList<string> ArtifactPaths { get; }
        public int ArtifactCount { get; }
        public long StagedBytes { get; }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (!CanSelect && value)
                {
                    return;
                }

                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string BuildRepairReasonText(
            bool isCharacterScope,
            bool isTargetedSafetyRepair,
            bool needsCelestialSkySafety,
            bool needsCutoutSafety,
            bool needsTranslucentMaterialSafety)
        {
            if (isTargetedSafetyRepair && needsTranslucentMaterialSafety)
            {
                var additional = needsCelestialSkySafety || needsCutoutSafety
                    ? " It also applies the missing sky/celestial and cutout compatibility fixes."
                    : string.Empty;
                return "A water and translucent-material safety fix is available. Repair restores renderer-coupled animated water, glass, and similar blended textures from verified originals while reusing unaffected enhanced textures."
                    + additional;
            }

            if (isTargetedSafetyRepair && needsCelestialSkySafety && needsCutoutSafety)
            {
                return "Current safety fixes are available: restore protected sky, sun, moon, halo, and related celestial assets from verified originals, and regenerate only angle-sensitive enhanced cutouts. Unaffected enhanced textures are reused.";
            }

            if (isTargetedSafetyRepair && needsCelestialSkySafety)
            {
                return "A sky and celestial safety fix is available. Repair restores protected sky, sun, moon, halo, and related renderer-sensitive assets from verified originals while reusing unaffected enhanced textures.";
            }

            if (isTargetedSafetyRepair && needsCutoutSafety)
            {
                return "A cutout safety fix is available. Repair regenerates only affected alpha-tested foliage, armor, and model textures without angle-sensitive generated mip levels while reusing unaffected enhanced textures.";
            }

            if (isTargetedSafetyRepair)
            {
                return "Current texture safety fixes are available. Repair verifies the baseline and updates only affected entries while reusing unaffected enhanced textures.";
            }

            return isCharacterScope
                ? "This legacy pack can be updated without a full rebuild. Repair reuses prior successes and adds current race, armor, control-texture, cutout, and sky/celestial safety coverage."
                : "This pack can be updated with the current safe pipeline without rebuilding unaffected enhanced textures.";
        }

        private static bool RequiresPipelineRepair(
            TextureBuildReport? report,
            AssetScope scope,
            IReadOnlyList<string> artifactPaths) =>
            TextureProcessingPipeline.RequiresRepair(report, scope, artifactPaths);

        private static string? DescribeCompositionComponents(StagedPackInfo info)
        {
            try
            {
                var path = Path.Combine(info.BuildDirectory, "composition.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                var composition = JsonSerializer.Deserialize<StagedPackCompositionDocument>(
                    File.ReadAllText(path),
                    CompositionJsonOptions);
                if (composition is null || composition.Components.Count == 0)
                {
                    return null;
                }

                var stagingRoot = Path.GetDirectoryName(info.BuildDirectory);
                var componentNames = composition.Components
                    .Select(component =>
                    {
                        var displayName = stagingRoot is null
                            ? null
                            : StagedPackMetadataStore
                                .TryRead(Path.Combine(stagingRoot, component.BuildId))?
                                .DisplayName;
                        return $"{displayName ?? component.BuildId} ({component.ArtifactCount:N0} archive(s))";
                    })
                    .ToArray();
                return $"This combination was generated automatically while installing "
                    + $"{composition.Components.Count:N0} checked pack(s) together. It combines: "
                    + string.Join("; ", componentNames)
                    + ". Deleting a source pack is blocked while this combination references it; "
                    + "delete the combination first if it is no longer installed.";
            }
            catch (Exception exception) when (exception is
                IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
            {
                return null;
            }
        }

        private static TextureBuildReport? TryReadReport(string buildDirectory)
        {
            try
            {
                var path = Path.Combine(buildDirectory, "texture-report.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    16 * 1024,
                    FileOptions.SequentialScan);
                return JsonSerializer.Deserialize<TextureBuildReport>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception exception) when (exception is
                IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
            {
                return null;
            }
        }
    }
}

public sealed class PackPreviewRequestedEventArgs(
    string manifestPath,
    Func<IReadOnlyList<TextureOverride>, Task>? applyChoices) : EventArgs
{
    public string ManifestPath { get; } = manifestPath;
    public Func<IReadOnlyList<TextureOverride>, Task>? ApplyChoices { get; } = applyChoices;
}
