using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Gui.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for NexusMods mod search.
/// </summary>
public partial class NexusSearchViewModel : ViewModelBase
{
    private readonly NexusModsBackend? _nexusBackend;
    private readonly ThumbnailService? _thumbnailService;
    private System.Threading.Timer? _debounceTimer;

    // Persistent queue: selected mods survive page changes
    private readonly Dictionary<string, ModDisplayModel> _selectedQueue = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedGame = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _availableGames = new();

    // Full game list for fuzzy matching (domain -> display name)
    private List<(string Domain, string Name)> _allGames = [];

    [ObservableProperty]
    private ModSortOrder _sortOrder = ModSortOrder.Relevance;

    [ObservableProperty]
    private bool _includeAdultContent;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select a backend and game to browse mods";

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _searchResults = new();

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalResults;

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private bool _hasPreviousPage;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private ModDisplayModel? _selectedMod;

    [ObservableProperty]
    private double _detailPanelWidth = 280;

    // --- Image gallery state ---

    /// <summary>All image URLs for the currently selected mod.</summary>
    private List<string> _imageUrls = [];

    /// <summary>Cancellation source for any in-flight image load.</summary>
    private CancellationTokenSource? _imageLoadCts;

    [ObservableProperty]
    private Bitmap? _currentImage;

    [ObservableProperty]
    private int _currentImageIndex;

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private bool _isLoadingImage;

    [ObservableProperty]
    private string _imageLabel = string.Empty;

    public bool HasPreviousImage => CurrentImageIndex > 0;
    public bool HasNextImage => CurrentImageIndex < ImageCount - 1;

    partial void OnSelectedModChanged(ModDisplayModel? value)
    {
        // Cancel any previous image load
        _imageLoadCts?.Cancel();
        _imageUrls = [];
        CurrentImage = null;
        CurrentImageIndex = 0;
        ImageCount = 0;
        ImageLabel = string.Empty;

        if (value != null)
            _ = LoadModImagesAsync(value);
    }

    private async Task LoadModImagesAsync(ModDisplayModel mod)
    {
        if (_nexusBackend == null || _thumbnailService == null) return;
        if (string.IsNullOrEmpty(mod.GameDomain) || string.IsNullOrEmpty(mod.ModId)) return;

        _imageLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _imageLoadCts = cts;

        IsLoadingImage = true;
        try
        {
            // Show thumbnail immediately while fetching the full gallery
            if (mod.Thumbnail != null)
                CurrentImage = mod.Thumbnail;

            var urls = await _nexusBackend.GetModImagesAsync(mod.ModId, mod.GameDomain, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            // If no images from API, fall back to the thumbnail URL
            if (urls.Count == 0 && mod.ThumbnailUrl != null)
                urls = [mod.ThumbnailUrl];

            _imageUrls = urls;
            ImageCount = urls.Count;
            CurrentImageIndex = 0;
            UpdateImageNavProperties();

            if (urls.Count > 0)
                await LoadImageAtIndexAsync(0, cts.Token);
        }
        catch (OperationCanceledException) { /* Selection changed */ }
        catch { /* Failed to load images */ }
        finally { IsLoadingImage = false; }
    }

    private async Task LoadImageAtIndexAsync(int index, CancellationToken ct)
    {
        if (_thumbnailService == null || index < 0 || index >= _imageUrls.Count) return;

        IsLoadingImage = true;
        try
        {
            var url = _imageUrls[index];
            var bitmap = await Task.Run(() => _thumbnailService.GetThumbnailAsync(url, ct), ct);
            if (ct.IsCancellationRequested) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentImage = bitmap;
                CurrentImageIndex = index;
                UpdateImageNavProperties();
            });
        }
        catch (OperationCanceledException) { }
        catch { /* Image load failed */ }
        finally { IsLoadingImage = false; }
    }

    [RelayCommand]
    private async Task NextImageAsync()
    {
        if (CurrentImageIndex >= _imageUrls.Count - 1) return;
        _imageLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _imageLoadCts = cts;
        await LoadImageAtIndexAsync(CurrentImageIndex + 1, cts.Token);
    }

    [RelayCommand]
    private async Task PreviousImageAsync()
    {
        if (CurrentImageIndex <= 0) return;
        _imageLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _imageLoadCts = cts;
        await LoadImageAtIndexAsync(CurrentImageIndex - 1, cts.Token);
    }

    private void UpdateImageNavProperties()
    {
        OnPropertyChanged(nameof(HasPreviousImage));
        OnPropertyChanged(nameof(HasNextImage));
        ImageLabel = ImageCount > 0 ? $"{CurrentImageIndex + 1} / {ImageCount}" : string.Empty;
    }

    // Designer constructor
    public NexusSearchViewModel()
    {
        SearchResults.Add(new ModDisplayModel(new BackendMod
        {
            ModId = "1234",
            Name = "Sample Search Result",
            Author = "TestAuthor",
            BackendId = "nexusmods",
            GameDomain = "skyrimspecialedition",
            EndorsementCount = 1500,
            DownloadCount = 50000
        }));
    }

    // DI constructor
    public NexusSearchViewModel(NexusModsBackend nexusBackend, ThumbnailService thumbnailService)
    {
        _nexusBackend = nexusBackend;
        _thumbnailService = thumbnailService;
        _ = LoadAvailableGamesAsync();
    }

    private async Task LoadAvailableGamesAsync()
    {
        if (_nexusBackend == null) return;
        try
        {
            // Load the full NexusMods game list for autocomplete + fuzzy matching
            _allGames = await _nexusBackend.GetGamesAsync();

            // Populate autocomplete with "domain — Display Name" for easy discovery
            AvailableGames.Clear();
            foreach (var (domain, name) in _allGames.OrderBy(g => g.Name))
                AvailableGames.Add($"{domain} — {name}");
        }
        catch
        {
            // Silently fail — user can type a game domain manually
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _debounceTimer?.Dispose();
        // Debounce typed input; empty triggers an unfiltered listing
        var delay = string.IsNullOrWhiteSpace(value) ? 0 : 400;
        _debounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ExecuteSearchAsync()),
            null, delay, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        await SearchNexusModsAsync();
    }

    /// <summary>
    /// Resolves user input to the best matching NexusMods game domain.
    /// Handles: exact domain, "domain — Name" format from autocomplete, or fuzzy partial match.
    /// </summary>
    private string? ResolveGameDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var trimmed = input.Trim();

        // Handle "domain — Display Name" format from the autocomplete dropdown
        var dashIdx = trimmed.IndexOf(" — ", StringComparison.Ordinal);
        if (dashIdx > 0)
            return trimmed[..dashIdx].Trim();

        // Exact domain match
        var exact = _allGames.FirstOrDefault(g =>
            g.Domain.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact.Domain != null) return exact.Domain;

        // Fuzzy: score each game against the input and pick the best match
        var scored = _allGames
            .Select(g => (g, score: Math.Max(FuzzyMatcher.Score(trimmed, g.Domain),
                                             FuzzyMatcher.Score(trimmed, g.Name))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        if (scored.Count == 0) return trimmed; // Let the API try it as-is

        return scored[0].g.Domain;
    }

    private async Task SearchNexusModsAsync()
    {
        if (_nexusBackend == null) { StatusMessage = "NexusMods backend not available"; return; }

        var resolvedDomain = ResolveGameDomain(SelectedGame);
        if (string.IsNullOrEmpty(resolvedDomain))
        {
            StatusMessage = "Enter a game name or domain (e.g. 'skyrim' or 'skyrimspecialedition')";
            return;
        }

        // Let the user know when fuzzy matching resolved to a different domain
        var rawInput = SelectedGame.Trim();
        var dashIdx = rawInput.IndexOf(" — ", StringComparison.Ordinal);
        var inputDomain = dashIdx > 0 ? rawInput[..dashIdx].Trim() : rawInput;
        var wasFuzzyResolved = !inputDomain.Equals(resolvedDomain, StringComparison.OrdinalIgnoreCase);

        IsLoading = true;
        var domainLabel = wasFuzzyResolved ? $"{resolvedDomain} (matched from '{inputDomain}')" : resolvedDomain;
        StatusMessage = string.IsNullOrWhiteSpace(SearchText)
            ? $"Loading mods for {domainLabel}..."
            : $"Searching {domainLabel} for \"{SearchText}\"...";

        List<ModDisplayModel> modelsToLoad = [];
        try
        {
            var result = await _nexusBackend.SearchModsAsync(new ModSearchQuery
            {
                Terms = SearchText,
                GameDomain = resolvedDomain,
                SortBy = SortOrder,
                Page = CurrentPage,
                PageSize = 20,
                AdultContent = IncludeAdultContent
            });

            // Re-rank results by fuzzy match score when search terms are present,
            // but keep all API results (sort best matches to top, don't discard)
            var ranked = !string.IsNullOrWhiteSpace(SearchText)
                ? result.Mods
                    .OrderByDescending(m => FuzzyMatcher.Score(SearchText, m.Name))
                    .ToList()
                : result.Mods;

            // Persist current selections before clearing results
            foreach (var m in SearchResults.Where(m => m.IsSelected))
            {
                var key = $"{m.Mod.BackendId}:{m.ModId}";
                _selectedQueue[key] = m;
            }
            // Remove deselected mods from the queue (user explicitly unchecked them)
            foreach (var m in SearchResults.Where(m => !m.IsSelected))
            {
                var key = $"{m.Mod.BackendId}:{m.ModId}";
                _selectedQueue.Remove(key);
            }

            SearchResults.Clear();
            foreach (var mod in ranked)
            {
                var display = new ModDisplayModel(mod);
                // Restore selection state from persistent queue
                var queueKey = $"{mod.BackendId}:{mod.ModId}";
                if (_selectedQueue.ContainsKey(queueKey))
                    display.IsSelected = true;
                display.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModDisplayModel.IsSelected)) UpdateSelectedCount(); };
                SearchResults.Add(display);
            }

            modelsToLoad = SearchResults.ToList();

            TotalResults = result.TotalCount;
            HasNextPage = result.HasNextPage;
            HasPreviousPage = CurrentPage > 1;
            StatusMessage = $"Found {result.TotalCount} results (page {result.Page})";
        }
        catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; }
        finally { IsLoading = false; }

        // Load thumbnails after IsLoading=false so the DataGrid is visible
        if (modelsToLoad.Count > 0)
            await LoadThumbnailsAsync(modelsToLoad);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!HasNextPage) return;
        CurrentPage++;
        await ExecuteSearchAsync();
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await ExecuteSearchAsync();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var mod in SearchResults)
            mod.IsSelected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var mod in SearchResults)
            mod.IsSelected = false;
        _selectedQueue.Clear();
        UpdateSelectedCount();
    }

    public void UpdateSelectedCount()
    {
        // Sync current page selections into the persistent queue
        foreach (var m in SearchResults)
        {
            var key = $"{m.Mod.BackendId}:{m.ModId}";
            if (m.IsSelected)
                _selectedQueue[key] = m;
            else
                _selectedQueue.Remove(key);
        }
        SelectedCount = _selectedQueue.Count;
    }

    public IEnumerable<ModDisplayModel> GetSelectedMods()
    {
        // Sync current page state first
        UpdateSelectedCount();
        return _selectedQueue.Values;
    }

    [RelayCommand]
    private void OpenModPage()
    {
        if (SelectedMod?.Url == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SelectedMod.Url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Failed to open browser
        }
    }

    private async Task LoadThumbnailsAsync(List<ModDisplayModel> models)
    {
        if (_thumbnailService == null) return;

        foreach (var m in models.Where(m => m.ThumbnailUrl != null))
        {
            // Load each thumbnail sequentially to avoid overwhelming the UI thread
            // with concurrent dispatches. ThumbnailService handles its own concurrency.
            try
            {
                var bitmap = await Task.Run(() => _thumbnailService.GetThumbnailAsync(m.ThumbnailUrl));
                if (bitmap != null)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        m.Thumbnail = bitmap;
                    });
                }
            }
            catch
            {
                // Thumbnail load failed — leave as placeholder
            }
        }
    }
}
