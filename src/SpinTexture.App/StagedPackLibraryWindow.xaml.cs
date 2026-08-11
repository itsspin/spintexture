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

public partial class StagedPackLibraryWindow : Window, INotifyPropertyChanged
{
    private readonly ProjectPaths paths;
    private readonly StagedPackCatalogService catalog = new();
    private readonly StagedPackDeletionService deletionService = new();
    private readonly TexturePackWorkflow workflow = new();
    private static readonly JsonSerializerOptions CompositionJsonOptions = CreateCompositionJsonOptions();
    private CancellationTokenSource? operationCancellation;
    private StagedPackRow? selectedPack;
    private PreviewGalleryWindow? previewWindow;
    private bool isBusy;
    private bool closeRequested;
    private bool isInstallAcknowledged;
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
        DataContext = this;
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        Closing += OnClosing;
    }

    public ObservableCollection<StagedPackRow> Packs { get; } = [];
    public ICollectionView PacksView { get; }

    public StagedPackRow? SelectedPack
    {
        get => selectedPack;
        set
        {
            if (SetField(ref selectedPack, value))
            {
                OnPropertyChanged(nameof(CanRepair));
                OnPropertyChanged(nameof(CanRepairSourceMismatch));
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanDelete));
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
                OnPropertyChanged(nameof(CanRepairSourceMismatch));
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanCheckHighlighted));
                OnPropertyChanged(nameof(CanUncheckHighlighted));
                OnPropertyChanged(nameof(CanAddHighlightedToCurrent));
                OnPropertyChanged(nameof(CanCheckCurrentInstall));
                OnPropertyChanged(nameof(CanCheckAll));
                OnPropertyChanged(nameof(CanClearChecks));
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
        && SelectedPack is { CanRepair: true, CanSelect: true };
    public bool CanRepairSourceMismatch => !IsBusy
        && SelectedPack is { CanRepairSourceMismatch: true, CanSelect: true };
    public bool CanPreview => !IsBusy
        && SelectedPack?.PreviewManifestPath is not null;
    public bool CanDelete => !IsBusy && GetHighlightedPacks().Count != 0;
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

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var checkedPaths = Packs
            .Where(pack => pack.IsSelected && pack.CanSelect)
            .Select(pack => Path.GetFullPath(pack.ManifestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RefreshAsync(checkedManifestPaths: checkedPaths).ConfigureAwait(true);
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
            this,
            $"Install {selected.Length:N0} checked pack(s)?\n\n"
            + "SpinTexture verifies every selected pack. When the selection only adds packs, already-active archives stay untouched and only the new archives are backed up and installed. Selections that remove or replace archives use the verified restore-and-switch path. No AI upscaling is rerun.",
            "Install checked packs",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
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

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var pack = SelectedPack;
        if (pack is null || !pack.CanRepair)
        {
            return;
        }

        var prompt = pack.IsCutoutMipRepairCandidate
            ? "Upgrade this pack's foliage/cutout mipmaps?\n\n"
              + "SpinTexture will SHA-256 verify the complete baseline and create a new immutable replacement. Only previously enhanced alpha-tested textures that use the retired full mip chain are reprocessed. Prior enhanced opaque textures are reused, source-identical entries are left original, and the existing staged pack is never modified."
            : "Salvage this legacy character pack?\n\n"
              + "Every texture already enhanced successfully will be reused byte-for-byte. Only unchanged eligible textures are retried with the current safe pipeline; newly supported classic indexed character and armor BMPs use the palette-stable Classic HD route. The completed original pack is never modified, and packs built with the current pipeline do not need this second pass.";
        var answer = MessageBox.Show(
            this,
            prompt,
            pack.IsCutoutMipRepairCandidate
                ? "Upgrade cutout mipmaps"
                : "Repair legacy pack",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunOperationAsync(
            pack.IsCutoutMipRepairCandidate
                ? "Hash-verifying the baseline and upgrading only stale enhanced cutouts..."
                : "Hash-verifying prior work and repairing only missing textures...",
            async (progress, token) =>
            {
                var result = await workflow.RepairStagedPackAsync(
                    paths,
                    pack.ManifestPath,
                    TexturePreset.Faithful,
                    progress,
                    token).ConfigureAwait(false);
                return result.StagedBuild.ManifestPath;
            },
            successMessage: pack.IsCutoutMipRepairCandidate
                ? "Cutout mip upgrade complete. Opaque successes were reused, only stale enhanced cutouts were regenerated, and the new replacement is checked with your other selected packs."
                : "Repair pack complete. Prior successes were reused; the repaired replacement is checked with your other selected packs.",
            replaceSelectedManifestPath: pack.ManifestPath)
            .ConfigureAwait(true);
    }

    private async void RepairSourceMismatch_Click(object sender, RoutedEventArgs e)
    {
        var pack = SelectedPack;
        if (pack is null || !pack.CanRepairSourceMismatch)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Repair this pack's source mismatch?\n\n"
            + "SpinTexture detected archives that were accidentally built from a previously installed enhanced pack. It will create a new immutable replacement: complete unaffected archives are reused, while only archives with verified managed provenance are rebuilt from their original bytes. No texture inside an affected archive is reused, the existing pack is never modified, and an unknown client-version change is blocked instead of guessed.",
            "Repair source mismatch",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunOperationAsync(
            "Verifying original-source provenance and rebuilding only affected archives...",
            async (progress, token) =>
            {
                var result = await workflow.RepairStagedPackSourceMismatchAsync(
                    paths,
                    pack.ManifestPath,
                    progress,
                    token).ConfigureAwait(false);
                return result.StagedBuild.ManifestPath;
            },
            successMessage: "Source mismatch repaired. Unaffected archive outputs were reused; affected archives were rebuilt from verified originals. The new replacement is checked with your other selected packs.",
            replaceSelectedManifestPath: pack.ManifestPath)
            .ConfigureAwait(true);
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
                    this,
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
                this,
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
                this,
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
                this,
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
        if (!IsBusy)
        {
            Close();
        }
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
            if (previewWindow is { IsVisible: true }
                && string.Equals(
                    previewWindow.ManifestPath,
                    Path.GetFullPath(manifestPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                previewWindow.Activate();
                return;
            }

            previewWindow?.Close();
            var window = new PreviewGalleryWindow(manifestPath) { Owner = this };
            previewWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(previewWindow, window))
                {
                    previewWindow = null;
                }
            };
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Pack preview",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsBusy)
        {
            return;
        }

        e.Cancel = true;
        closeRequested = true;
        operationCancellation?.Cancel();
        StatusText = "Canceling safely. This window will close after the current file operation stops.";
    }

    private void ScheduleDeferredCloseIfRequested()
    {
        if (closeRequested)
        {
            _ = Dispatcher.BeginInvoke(new Action(Close));
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
        OnPropertyChanged(nameof(CanCheckHighlighted));
        OnPropertyChanged(nameof(CanUncheckHighlighted));
        OnPropertyChanged(nameof(CanAddHighlightedToCurrent));
        OnPropertyChanged(nameof(CanClearChecks));
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

    private static JsonSerializerOptions CreateCompositionJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
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
            var isCutoutMipRepair = report?.IsCutoutMipRepair == true;
            var scopeTitle = manifest?.Options.Scope switch
            {
                AssetScope.SelectedZone => $"Zone \u00B7 {manifest.Options.SelectedZone ?? "Unknown"}",
                AssetScope.CharactersAndEquipmentOnly => "Characters + Equipment",
                AssetScope.WorldCharactersAndEquipment => "World + Characters + Equipment",
                AssetScope.WorldOnly => "World textures",
                AssetScope.AllSafeTextures => "All safe textures",
                _ => info.CandidateBuildId
            };
            Category = IsComposition
                ? IsActive ? "Installed Combination" : "Other / Compositions"
                : manifest?.Options.Scope switch
                {
                    AssetScope.SelectedZone => "Zones",
                    AssetScope.CharactersAndEquipmentOnly => "Characters & Equipment",
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
                "World / Combined" => 3,
                _ => 4
            };
            CreatedUtc = manifest?.CreatedUtc ?? DateTimeOffset.MinValue;
            Title = IsComposition
                ? IsActive ? "Installed pack combination" : "Generated pack composition"
                : isSourceRepair
                    ? $"Source repaired \u00B7 {scopeTitle}"
                    : isCutoutMipRepair
                    ? $"Cutout upgraded \u00B7 {scopeTitle}"
                    : isLegacyRepair
                    ? $"Repaired \u00B7 {scopeTitle}"
                    : scopeTitle;
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
                                : isCutoutMipRepair
                                ? "CUTOUT UPGRADED"
                                : isLegacyRepair
                                ? "REPAIRED"
                                : "STAGED";
            (BadgeBackground, BadgeForeground, CardBorderBrush) = Badge switch
            {
                "ACTIVE" or "ACTIVE COMPONENT" => ("#17352B", "#65D6A6", "#356A57"),
                "ACTIVE COMBINATION" => ("#202E46", "#B9D0FF", "#4B6797"),
                "COMPOSITION" => ("#27253A", "#CEC7FF", "#4E4A73"),
                "SOURCE REPAIRED" => ("#382B18", "#F5C76B", "#785C2F"),
                "CUTOUT UPGRADED" => ("#18343A", "#8CE1E8", "#34717B"),
                "REPAIRED" => ("#233345", "#BFD9F5", "#405E7D"),
                _ => ("#17352B", "#65D6A6", "#292E36")
            };
            Details = manifest is null
                ? info.CandidateBuildId
                : IsComposition
                    ? $"{ArtifactCount:N0} combined archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                      + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : isSourceRepair && report is not null
                        ? $"{report.ReusedArtifacts:N0} complete archives reused \u00B7 "
                          + $"{report.RebuiltArtifacts:N0} source-contaminated archives rebuilt \u00B7 "
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : isCutoutMipRepair && report is not null
                        ? $"revision {report.BaselineTexturePipelineRevision:N0}\u2192{report.TexturePipelineRevision:N0} \u00B7 "
                          + $"{report.Statistics.ReusedTextures:N0} prior textures reused \u00B7 "
                          + $"{report.Statistics.EnhancedTextures:N0} stale cutouts regenerated \u00B7 "
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : isLegacyRepair && report is not null
                        ? $"{report.Statistics.ReusedTextures:N0} prior textures reused \u00B7 "
                          + $"{report.Statistics.EnhancedTextures:N0} newly enhanced \u00B7 "
                          + $"{ArtifactCount:N0} archives \u00B7 {FormatBytes(StagedBytes)} \u00B7 "
                          + manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        : $"{manifest.Options.Preset} \u00B7 {manifest.Options.MaximumDimension:N0}px \u00B7 "
                          + (report is null
                              ? string.Empty
                              : $"{report.Statistics.EnhancedTextures:N0} enhanced \u00B7 {report.Statistics.PreservedTextures:N0} protected/missing \u00B7 ")
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
                : isCutoutMipRepair && report is not null
                    ? $"{Title} contains {ArtifactCount:N0} complete install archive(s), derived from verified baseline "
                      + $"{report.BaselineBuildId ?? "unknown"}. The report records {report.Statistics.EnhancedTextures:N0} "
                      + $"stale enhanced cutouts regenerated under pipeline revision {report.TexturePipelineRevision:N0} and "
                      + $"{report.Statistics.ReusedTextures:N0} prior enhanced textures reused. Archive conflicts are checked before composition."
                : $"{Title} contains {ArtifactCount:N0} complete install archive(s). "
                  + $"The report records {report!.Statistics.EnhancedTextures:N0} newly enhanced, "
                  + $"{report!.Statistics.ReusedTextures:N0} reused, and "
                  + $"{report!.Statistics.PreservedTextures:N0} protected or still-missing texture entries. "
                  + "Archive conflicts are checked before composition.";
            var isCharacterScope = manifest?.Options.Scope is
                AssetScope.CharactersAndEquipmentOnly
                or AssetScope.WorldCharactersAndEquipment;
            CanRepair = !IsComposition
                && manifest is not null
                && RequiresPipelineRepair(report, manifest.Options.Scope);
            IsCutoutMipRepairCandidate = CanRepair
                && manifest is not null
                && TextureProcessingPipeline.RequiresCutoutMipUpgrade(
                    report,
                    manifest.Options.Scope);
            CanRepairSourceMismatch = !IsComposition
                && canRepairSourceMismatch
                && !isSourceRepair;
            RepairStatusText = IsCutoutMipRepairCandidate
                ? "One-time pipeline upgrade available: verify the full baseline, reuse opaque successes, and regenerate only previously enhanced alpha-tested foliage, armor, and model textures with bounded 4\u00D74 terminal mips."
                : isCharacterScope
                ? CanRepair
                    ? "Legacy pack: reuse prior output and add the current race, armor, and protected control-texture coverage."
                    : "Character/equipment first-pass safety and coverage are current; no routine second pass is required."
                : string.Empty;
            RepairStatusForeground = CanRepair ? "#F5C76B" : "#65D6A6";
            SourceRepairStatusText = CanRepairSourceMismatch
                ? "Managed source mismatch detected: reuse unaffected complete archives and rebuild only the affected archives from verified originals."
                : string.Empty;
            RepairButtonText = IsCutoutMipRepairCandidate
                ? "Upgrade Cutout Mips"
                : "Repair Legacy Pack";
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
        }

        public string ManifestPath { get; }
        public bool IsActive { get; }
        public bool IsActiveComponent { get; }
        public bool IsComposition { get; }
        public bool CanSelect { get; }
        public bool CanRepair { get; }
        public bool IsCutoutMipRepairCandidate { get; }
        public bool CanRepairSourceMismatch { get; }
        public string? PreviewManifestPath { get; }
        public string Title { get; }
        public string Badge { get; }
        public string BadgeBackground { get; }
        public string BadgeForeground { get; }
        public string CardBorderBrush { get; }
        public string Details { get; }
        public string StateText { get; }
        public string RepairStatusText { get; }
        public string RepairStatusForeground { get; }
        public string RepairButtonText { get; }
        public string SourceRepairStatusText { get; }
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

        private static bool RequiresPipelineRepair(
            TextureBuildReport? report,
            AssetScope scope) =>
            TextureProcessingPipeline.RequiresRepair(report, scope);

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
