using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpinTexture.Core.Services;
using SpinTexture.Core.Models;
using SpinTexture.Core.Textures;

namespace SpinTexture.App.ViewModels;

/// <summary>Both panes of one texture rendered on demand from managed bytes.</summary>
public sealed record OnDemandPreviewPanes(
    DecodedTexturePreview? Original,
    DecodedTexturePreview? Enhanced);

/// <summary>
/// Resolves the original and staged bytes of one pack texture when the build
/// did not capture a full preview image pair for it.
/// </summary>
public delegate Task<OnDemandPreviewPanes> OnDemandPreviewLoader(
    string archivePath,
    string logicalName,
    CancellationToken cancellationToken);

public sealed class PreviewGalleryViewModel : ObservableObject, IDisposable
{
    private const double FitSurfaceSize = 520;
    private const double MinimumZoom = 0.125;
    private const double MaximumZoom = 4;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string manifestDirectory;
    private readonly OnDemandPreviewLoader? onDemandLoader;
    private PreviewGalleryEntryViewModel? selectedEntry;
    private BitmapSource? selectedOriginalImage;
    private BitmapSource? selectedEnhancedImage;
    private CancellationTokenSource? imageLoadCancellation;
    private string searchText = string.Empty;
    private string loadErrorText = string.Empty;
    private string imageStatusText = "Choose a texture to compare";
    private bool isImageLoading;
    private double zoom = 1;

    public PreviewGalleryViewModel(
        string manifestPath,
        bool canApplyChoices = false,
        OnDemandPreviewLoader? onDemandLoader = null)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("A preview manifest path is required.", nameof(manifestPath));
        }

        this.onDemandLoader = onDemandLoader;
        ManifestPath = Path.GetFullPath(manifestPath);
        CanApplyChoices = canApplyChoices;
        manifestDirectory = Path.GetDirectoryName(ManifestPath) ?? Environment.CurrentDirectory;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;
        EntriesView.SortDescriptions.Add(new SortDescription(
            nameof(PreviewGalleryEntryViewModel.ArchivePath),
            ListSortDirection.Ascending));
        EntriesView.SortDescriptions.Add(new SortDescription(
            nameof(PreviewGalleryEntryViewModel.LogicalName),
            ListSortDirection.Ascending));

        ZoomInCommand = new RelayCommand(_ => Zoom += 0.25, _ => Zoom < MaximumZoom);
        ZoomOutCommand = new RelayCommand(_ => Zoom -= 0.25, _ => Zoom > MinimumZoom);
        ResetZoomCommand = new RelayCommand(_ => Zoom = 1, _ => HasSelectedEntry);
        FitZoomCommand = new RelayCommand(_ => Zoom = CalculateFitZoom(), _ => HasSelectedEntry);

        LoadManifest();
    }

    public string ManifestPath { get; }
    public ObservableCollection<PreviewGalleryEntryViewModel> Entries { get; } = [];
    public ICollectionView EntriesView { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetZoomCommand { get; }
    public RelayCommand FitZoomCommand { get; }
    public IReadOnlyList<TextureChoiceOptionViewModel> ChoiceOptions { get; } =
    [
        new("Use pack result", null, null),
        new("Keep original", TextureOverrideAction.PreserveOriginal, null),
        new("Redo: Original Clarity", TextureOverrideAction.Reprocess, TexturePreset.Faithful),
        new("Redo: Texture HD", TextureOverrideAction.Reprocess, TexturePreset.ClassicHd),
        new("Redo: Material Detail", TextureOverrideAction.Reprocess, TexturePreset.MaximumDetail),
        new("Redo: Graphic Painted Fantasy", TextureOverrideAction.Reprocess, TexturePreset.Illustrated),
        new("Redo: Rustic Painted Fantasy", TextureOverrideAction.Reprocess, TexturePreset.RusticPainted)
    ];
    public bool CanApplyChoices { get; }

    public PreviewGalleryEntryViewModel? SelectedEntry
    {
        get => selectedEntry;
        set
        {
            if (!SetProperty(ref selectedEntry, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedEntry));
            // Start with both sides visible. The footer labels this as a
            // downsampled overview and keeps true 1:1 output pixels one click away.
            Zoom = CalculateFitZoom();
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            OnPropertyChanged(nameof(ZoomPercentText));
            OnPropertyChanged(nameof(PixelScaleDescription));
            ResetZoomCommand.RaiseCanExecuteChanged();
            FitZoomCommand.RaiseCanExecuteChanged();
            LoadSelectedImages(value);
        }
    }

    public BitmapSource? SelectedOriginalImage
    {
        get => selectedOriginalImage;
        private set => SetProperty(ref selectedOriginalImage, value);
    }

    public BitmapSource? SelectedEnhancedImage
    {
        get => selectedEnhancedImage;
        private set => SetProperty(ref selectedEnhancedImage, value);
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? string.Empty))
            {
                return;
            }

            EntriesView.Refresh();
            OnPropertyChanged(nameof(VisibleEntryCountText));

            if (SelectedEntry is not null && !EntriesView.Contains(SelectedEntry))
            {
                SelectedEntry = EntriesView.Cast<PreviewGalleryEntryViewModel>().FirstOrDefault();
            }
        }
    }

    public string LoadErrorText
    {
        get => loadErrorText;
        private set
        {
            if (SetProperty(ref loadErrorText, value))
            {
                OnPropertyChanged(nameof(HasLoadError));
            }
        }
    }

    public string ImageStatusText
    {
        get => imageStatusText;
        private set => SetProperty(ref imageStatusText, value);
    }

    public bool IsImageLoading
    {
        get => isImageLoading;
        private set => SetProperty(ref isImageLoading, value);
    }

    public double Zoom
    {
        get => zoom;
        set
        {
            double clamped = Math.Clamp(value, MinimumZoom, MaximumZoom);
            if (!SetProperty(ref zoom, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(ZoomPercentText));
            OnPropertyChanged(nameof(PixelScaleDescription));
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            ZoomInCommand.RaiseCanExecuteChanged();
            ZoomOutCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasEntries => Entries.Count > 0;
    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadErrorText);
    public string EntryCountText => Entries.Count == 1 ? "1 texture" : $"{Entries.Count:N0} textures";
    public string VisibleEntryCountText => string.IsNullOrWhiteSpace(SearchText)
        ? EntryCountText
        : $"{EntriesView.Cast<object>().Count():N0} of {Entries.Count:N0}";
    public double PreviewWidth => Math.Max(1, (SelectedEntry?.EnhancedWidth ?? (int)FitSurfaceSize) * Zoom);
    public double PreviewHeight => Math.Max(1, (SelectedEntry?.EnhancedHeight ?? (int)FitSurfaceSize) * Zoom);
    public string ZoomPercentText => Math.Abs(Zoom - 1) < 0.001
        ? "1:1  |  100% output pixels"
        : $"{Zoom * 100:0.#}% of output pixels";
    public string PixelScaleDescription => Math.Abs(Zoom - 1) < 0.001
        ? "Native output-pixel view - scroll to inspect the full texture"
        : Math.Abs(Zoom - CalculateFitZoom()) < 0.001
            ? $"Fit overview - downsampled to {Zoom * 100:0.#}% for display"
            : $"Scaled inspection view - {Zoom * 100:0.#}% of output pixels";
    public bool HasPendingChoices => CanApplyChoices && Entries.Any(entry => entry.SelectedChoice.Action is not null);
    public string PendingChoiceText => HasPendingChoices
        ? $"Create revised pack ({Entries.Count(entry => entry.SelectedChoice.Action is not null):N0} selected)"
        : "Choose Keep original or a Redo style";

    public IReadOnlyList<TextureOverride> GetTextureOverrides() => Entries
        .Where(entry => entry.SelectedChoice.Action is not null)
        .Select(entry => new TextureOverride(
            entry.ArchivePath,
            entry.LogicalName,
            entry.SelectedChoice.Action!.Value,
            entry.SelectedChoice.Preset))
        .ToArray();

    private void LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                throw new FileNotFoundException("The preview manifest no longer exists.", ManifestPath);
            }

            using FileStream stream = new(
                ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            TexturePreviewManifest? manifest = JsonSerializer.Deserialize<TexturePreviewManifest>(
                stream,
                ManifestJsonOptions);

            if (manifest is null)
            {
                throw new InvalidDataException("The preview manifest is empty or invalid.");
            }

            if (manifest.SchemaVersion != TexturePreviewManifest.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Preview schema {manifest.SchemaVersion} is not supported by this version of SpinTexture.");
            }

            if (string.IsNullOrWhiteSpace(manifest.BuildId) || manifest.Entries is null)
            {
                throw new InvalidDataException("The preview manifest is missing required build information.");
            }

            var indexedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TexturePreviewEntry entry in manifest.Entries
                         .OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                var galleryEntry = new PreviewGalleryEntryViewModel(entry, manifestDirectory);
                if (galleryEntry.HasValidMetadata)
                {
                    galleryEntry.SelectedChoice = ChoiceOptions[0];
                    galleryEntry.ChoiceChanged += OnEntryChoiceChanged;
                    Entries.Add(galleryEntry);
                    indexedKeys.Add(CreateEntryKey(entry.ArchivePath, entry.LogicalName));
                }
            }

            foreach (var entry in manifest.ReviewEntries ?? [])
            {
                if (!indexedKeys.Add(CreateEntryKey(entry.ArchivePath, entry.LogicalName)))
                {
                    continue;
                }

                var galleryEntry = new PreviewGalleryEntryViewModel(entry);
                galleryEntry.SelectedChoice = ChoiceOptions[0];
                galleryEntry.ChoiceChanged += OnEntryChoiceChanged;
                Entries.Add(galleryEntry);
            }

            if (Entries.Count == 0)
            {
                LoadErrorText = "The manifest contains no valid preview entries.";
            }

            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(EntryCountText));
            OnPropertyChanged(nameof(VisibleEntryCountText));
            SelectedEntry = Entries.FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or NotSupportedException
                                           or ArgumentException)
        {
            LoadErrorText = exception.Message;
            ImageStatusText = "Preview manifest could not be loaded";
        }
    }

    private static string CreateEntryKey(string archivePath, string logicalName) =>
        $"{archivePath?.Trim().Replace('\\', '/')}|{logicalName?.Trim()}";

    private bool FilterEntry(object item)
    {
        if (item is not PreviewGalleryEntryViewModel entry || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return entry.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || entry.ArchiveName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || entry.ArchivePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private async void LoadSelectedImages(PreviewGalleryEntryViewModel? entry)
    {
        CancellationTokenSource? previous = imageLoadCancellation;
        var cancellation = new CancellationTokenSource();
        imageLoadCancellation = cancellation;
        previous?.Cancel();
        previous?.Dispose();

        SelectedOriginalImage = null;
        SelectedEnhancedImage = null;

        if (entry is null)
        {
            IsImageLoading = false;
            ImageStatusText = "Choose a texture to compare";
            return;
        }

        IsImageLoading = true;
        ImageStatusText = "Loading full-resolution previews...";

        try
        {
            ImageLoadResult result = await Task.Run(
                () => LoadImagePairAsync(entry, cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            if (!ReferenceEquals(entry, SelectedEntry))
            {
                return;
            }

            SelectedOriginalImage = result.Original;
            SelectedEnhancedImage = result.Enhanced;
            ImageStatusText = result.Status;
        }
        catch (OperationCanceledException)
        {
            // A newer selection owns the image surface now.
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(entry, SelectedEntry))
            {
                ImageStatusText = $"Preview load failed: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(imageLoadCancellation, cancellation))
            {
                IsImageLoading = false;
                imageLoadCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task<ImageLoadResult> LoadImagePairAsync(
        PreviewGalleryEntryViewModel entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource? original = LoadBitmap(entry.OriginalImagePath, decodePixelWidth: null);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource? enhanced = LoadBitmap(entry.EnhancedImagePath, decodePixelWidth: null);
        var renderedOnDemand = false;
        if ((original is null || enhanced is null) && onDemandLoader is not null)
        {
            var panes = await onDemandLoader(
                    entry.ArchivePath,
                    entry.LogicalName,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            original ??= CreateBitmap(panes.Original);
            enhanced ??= CreateBitmap(panes.Enhanced);
            renderedOnDemand = original is not null || enhanced is not null;
        }

        string status = (original, enhanced) switch
        {
            (not null, not null) when renderedOnDemand =>
                "Rendered on demand - original from verified source bytes, enhanced from the staged pack",
            (not null, not null) => "Full-resolution pair loaded - both sides use identical output dimensions",
            (null, not null) when renderedOnDemand =>
                "Enhanced side rendered from the staged pack; the verified original bytes are not reachable right now",
            (null, not null) => "Original preview image is missing",
            (not null, null) => "Enhanced preview image is missing",
            _ when !entry.HasImagePair =>
                "Preview could not be rendered for this texture container - choose Keep original or a Redo style",
            _ => "Both preview images are missing"
        };

        return new ImageLoadResult(original, enhanced, status);
    }

    private static BitmapSource? CreateBitmap(DecodedTexturePreview? decoded)
    {
        if (decoded is null)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(
            decoded.Width,
            decoded.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            decoded.BgraPixels,
            decoded.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private double CalculateFitZoom()
    {
        int width = SelectedEntry?.EnhancedWidth ?? (int)FitSurfaceSize;
        int height = SelectedEntry?.EnhancedHeight ?? (int)FitSurfaceSize;
        int longestEdge = Math.Max(width, height);
        if (longestEdge <= 0)
        {
            return 1;
        }

        // Fit never enlarges a small output; it only creates an overview of a
        // texture that is larger than the original 520 px comparison surface.
        return Math.Clamp(FitSurfaceSize / longestEdge, MinimumZoom, 1);
    }

    internal static BitmapSource? LoadBitmap(string path, int? decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (decodePixelWidth is > 0)
            {
                image.DecodePixelWidth = decodePixelWidth.Value;
            }

            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        imageLoadCancellation?.Cancel();
        imageLoadCancellation?.Dispose();
        imageLoadCancellation = null;
        foreach (var entry in Entries)
        {
            entry.ChoiceChanged -= OnEntryChoiceChanged;
        }
    }

    private void OnEntryChoiceChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingChoices));
        OnPropertyChanged(nameof(PendingChoiceText));
    }

    private sealed record ImageLoadResult(
        BitmapSource? Original,
        BitmapSource? Enhanced,
        string Status);
}

public sealed class PreviewGalleryEntryViewModel : ObservableObject
{
    private TextureChoiceOptionViewModel selectedChoice = TextureChoiceOptionViewModel.Default;
    public PreviewGalleryEntryViewModel(TexturePreviewEntry entry, string manifestDirectory)
    {
        ArchivePath = entry.ArchivePath ?? string.Empty;
        LogicalName = string.IsNullOrWhiteSpace(entry.LogicalName) ? "Unnamed texture" : entry.LogicalName;
        OriginalImagePath = ResolvePath(manifestDirectory, entry.OriginalImagePath);
        EnhancedImagePath = ResolvePath(manifestDirectory, entry.EnhancedImagePath);
        OriginalWidth = entry.OriginalWidth;
        OriginalHeight = entry.OriginalHeight;
        EnhancedWidth = entry.EnhancedWidth;
        EnhancedHeight = entry.EnhancedHeight;
        Thumbnail = PreviewGalleryViewModel.LoadBitmap(EnhancedImagePath, 180)
                    ?? PreviewGalleryViewModel.LoadBitmap(OriginalImagePath, 180);
    }

    public PreviewGalleryEntryViewModel(TextureReviewEntry entry)
    {
        ArchivePath = entry.ArchivePath ?? string.Empty;
        LogicalName = string.IsNullOrWhiteSpace(entry.LogicalName) ? "Unnamed texture" : entry.LogicalName;
        OriginalImagePath = string.Empty;
        EnhancedImagePath = string.Empty;
        OriginalWidth = entry.OriginalWidth;
        OriginalHeight = entry.OriginalHeight;
        EnhancedWidth = entry.EnhancedWidth;
        EnhancedHeight = entry.EnhancedHeight;
    }

    public string ArchivePath { get; }
    public string LogicalName { get; }
    public string OriginalImagePath { get; }
    public string EnhancedImagePath { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public int EnhancedWidth { get; }
    public int EnhancedHeight { get; }
    public BitmapSource? Thumbnail { get; }
    public event EventHandler? ChoiceChanged;
    public TextureChoiceOptionViewModel SelectedChoice
    {
        get => selectedChoice;
        set
        {
            if (SetProperty(ref selectedChoice, value ?? TextureChoiceOptionViewModel.Default))
            {
                ChoiceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasValidMetadata =>
        OriginalWidth > 0
        && OriginalHeight > 0
        && EnhancedWidth > 0
        && EnhancedHeight > 0;
    public bool HasImagePair =>
        !string.IsNullOrWhiteSpace(OriginalImagePath)
        || !string.IsNullOrWhiteSpace(EnhancedImagePath);
    public string PreviewAvailabilityText => HasImagePair
        ? "FULL BEFORE / AFTER"
        : "ON-DEMAND • SELECTABLE";

    public string ArchiveName => string.IsNullOrWhiteSpace(ArchivePath)
        ? "Loose texture"
        : Path.GetFileName(ArchivePath);
    public string OriginalDimensionsText => FormatDimensions(OriginalWidth, OriginalHeight);
    public string EnhancedDimensionsText => FormatDimensions(EnhancedWidth, EnhancedHeight);
    public string ScaleText
    {
        get
        {
            if (OriginalWidth <= 0 || OriginalHeight <= 0 || EnhancedWidth <= 0 || EnhancedHeight <= 0)
            {
                return "Enhanced";
            }

            double scale = Math.Min(
                (double)EnhancedWidth / OriginalWidth,
                (double)EnhancedHeight / OriginalHeight);
            return $"{scale:0.#}x detail pass";
        }
    }

    private static string FormatDimensions(int width, int height) => width > 0 && height > 0
        ? $"{width:N0} x {height:N0}"
        : "Unknown size";

    private static string ResolvePath(string manifestDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            string previewRoot = Path.GetFullPath(manifestDirectory);
            string candidate = Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(manifestDirectory, path));
            string relative = Path.GetRelativePath(previewRoot, candidate);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                // Older preview manifests store absolute image paths. When the
                // verified pack library is relocated, accept only the same
                // generated filename inside the new manifest directory.
                var relocatedCandidate = Path.Combine(previewRoot, Path.GetFileName(path));
                if (File.Exists(relocatedCandidate))
                {
                    return relocatedCandidate;
                }

                return string.Empty;
            }

            return candidate;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

public sealed record TextureChoiceOptionViewModel(
    string Label,
    TextureOverrideAction? Action,
    TexturePreset? Preset)
{
    public static TextureChoiceOptionViewModel Default { get; } = new("Use pack result", null, null);
}
