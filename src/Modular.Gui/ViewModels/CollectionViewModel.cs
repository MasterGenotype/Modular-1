using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Collections;
using Modular.Core.Configuration;

using Modular.Core.Utilities;
using Modular.Gui.Services;
using Modular.Sdk.Collections;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the mod collections management view.
/// Supports both local collection management and online NexusMods collection search.
/// Enforces a maximum of 3 collections per game for storage management.
/// </summary>
public partial class CollectionViewModel : ViewModelBase
{
    private const int MaxCollectionsPerGame = 3;

    private readonly ModCollectionService? _service;
    private readonly ModCollectionRepository? _repository;
    private readonly NexusModsBackend? _backend;
    private readonly IDialogService? _dialogService;
    private readonly AppSettings? _settings;
    private readonly ThumbnailService? _thumbnailService;

    [ObservableProperty]
    private ObservableCollection<ModCollection> _collections = new();

    [ObservableProperty]
    private ModCollection? _selectedCollection;

    [ObservableProperty]
    private ObservableCollection<ModCollectionEntry> _collectionEntries = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select a collection or create a new one";

    [ObservableProperty]
    private string _newCollectionName = string.Empty;

    [ObservableProperty]
    private string _newCollectionGame = string.Empty;

    // --- Online collection search ---

    [ObservableProperty]
    private string _searchGameDomain = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<NexusCollectionDisplayModel> _onlineCollections = new();

    [ObservableProperty]
    private NexusCollectionDisplayModel? _selectedOnlineCollection;

    [ObservableProperty]
    private int _onlineTotalResults;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatusMessage = "Enter a game domain to browse NexusMods collections";

    private const int CollectionPageSize = 20;

    [ObservableProperty]
    private int _currentSearchPage = 1;

    [ObservableProperty]
    private bool _hasNextCollectionPage;

    [ObservableProperty]
    private bool _hasPreviousCollectionPage;

    /// <summary>
    /// Current page results for thumbnail loading.
    /// </summary>
    private List<NexusCollectionDisplayModel> _allOnlineCollections = [];

    // Designer constructor
    public CollectionViewModel()
    {
        var sample = new ModCollection
        {
            Name = "My Skyrim Build",
            GameId = "skyrimspecialedition"
        };
        sample.Entries.Add(new ModCollectionEntry
        {
            ModId = "1234",
            Name = "Sample Mod",
            Author = "TestAuthor",
            Version = "1.0.0"
        });
        Collections.Add(sample);
    }

    // DI constructor
    public CollectionViewModel(
        NexusModsBackend backend,
        IDialogService dialogService,
        ThumbnailService thumbnailService,
        AppSettings? settings = null)
    {
        _backend = backend;
        _dialogService = dialogService;
        _thumbnailService = thumbnailService;
        _settings = settings;
        _repository = new ModCollectionRepository();
        _service = new ModCollectionService(_repository, backend);
        _ = RefreshCollectionsAsync();
    }

    partial void OnSelectedCollectionChanged(ModCollection? value)
    {
        CollectionEntries.Clear();
        if (value != null)
        {
            foreach (var entry in value.Entries)
                CollectionEntries.Add(entry);
            StatusMessage = $"{value.Name} — {value.Entries.Count} mod(s)";
        }
    }

    [RelayCommand]
    private async Task RefreshCollectionsAsync()
    {
        if (_repository == null) return;

        IsLoading = true;
        try
        {
            var list = await _repository.ListAsync();
            Collections.Clear();
            foreach (var c in list)
                Collections.Add(c);
            StatusMessage = $"{list.Count} collection(s) loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load collections: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateCollectionAsync()
    {
        if (_service == null) return;

        if (string.IsNullOrWhiteSpace(NewCollectionName) || string.IsNullOrWhiteSpace(NewCollectionGame))
        {
            StatusMessage = "Enter a name and game domain";
            return;
        }

        // Enforce 3 collections per game limit
        var gameId = NewCollectionGame.Trim();
        var existingForGame = Collections.Count(c =>
            c.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase));

        if (existingForGame >= MaxCollectionsPerGame)
        {
            StatusMessage = $"Maximum {MaxCollectionsPerGame} collections per game reached for '{gameId}'. Delete an existing collection first.";
            if (_dialogService != null)
            {
                await _dialogService.ShowWarningAsync("Collection Limit",
                    $"You can only store up to {MaxCollectionsPerGame} collections per game. " +
                    $"Please delete an existing collection for '{gameId}' before creating a new one.");
            }
            return;
        }

        try
        {
            await _service.CreateAsync(NewCollectionName.Trim(), gameId);
            NewCollectionName = string.Empty;
            NewCollectionGame = string.Empty;
            await RefreshCollectionsAsync();
            StatusMessage = "Collection created";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteCollectionAsync()
    {
        if (_repository == null || SelectedCollection == null) return;

        if (_dialogService != null)
        {
            var confirmed = await _dialogService.ShowConfirmationAsync(
                "Delete Collection",
                $"Are you sure you want to delete the collection '{SelectedCollection.Name}'?");
            if (!confirmed) return;
        }

        var (_, path) = await _repository.FindByNameAsync(SelectedCollection.Name);
        if (path != null && File.Exists(path))
        {
            File.Delete(path);
            await RefreshCollectionsAsync();
            SelectedCollection = null;
            StatusMessage = "Collection deleted";
        }
    }

    [RelayCommand]
    private async Task DownloadCollectionAsync()
    {
        if (_service == null || SelectedCollection == null || _dialogService == null) return;

        var modsDir = _settings?.ModsDirectory;
        if (string.IsNullOrEmpty(modsDir))
        {
            modsDir = await _dialogService.ShowFolderBrowserAsync("Select download directory for collection mods");
            if (string.IsNullOrEmpty(modsDir)) return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Download Collection",
            $"Download all {SelectedCollection.Entries.Count} mod(s) from '{SelectedCollection.Name}' to {modsDir}?");
        if (!confirmed) return;

        IsLoading = true;
        StatusMessage = $"Downloading collection '{SelectedCollection.Name}'...";

        try
        {
            var progress = new Progress<Modular.Sdk.Backends.DownloadProgress>(p =>
            {
                StatusMessage = !string.IsNullOrEmpty(p.Status) ? p.Status : "Downloading...";
            });

            await _service.DownloadCollectionAsync(SelectedCollection, modsDir, progress: progress);
            StatusMessage = $"Collection '{SelectedCollection.Name}' downloaded successfully";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            await _dialogService.ShowErrorAsync("Download Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveEntryAsync(ModCollectionEntry? entry)
    {
        if (_service == null || SelectedCollection == null || entry == null) return;

        await _service.RemoveModAsync(SelectedCollection, entry.ModId);
        CollectionEntries.Remove(entry);
        StatusMessage = $"Removed {entry.Name}";
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (_service == null || SelectedCollection == null) return;

        IsLoading = true;
        StatusMessage = "Checking for updates...";

        try
        {
            var updates = await _service.CheckUpdatesAsync(SelectedCollection);
            StatusMessage = updates.Count > 0
                ? $"{updates.Count} update(s) available"
                : "All mods are up to date";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // --- Online NexusMods collection search ---

    [RelayCommand]
    private async Task SearchOnlineCollectionsAsync()
    {
        CurrentSearchPage = 1;
        await FetchCollectionPageAsync();
    }

    [RelayCommand]
    private async Task NextCollectionPageAsync()
    {
        if (!HasNextCollectionPage) return;
        CurrentSearchPage++;
        await FetchCollectionPageAsync();
    }

    [RelayCommand]
    private async Task PreviousCollectionPageAsync()
    {
        if (!HasPreviousCollectionPage) return;
        CurrentSearchPage--;
        await FetchCollectionPageAsync();
    }

    private async Task FetchCollectionPageAsync()
    {
        if (_backend == null)
        {
            SearchStatusMessage = "NexusMods backend not available";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchGameDomain))
        {
            SearchStatusMessage = "Enter a game domain (e.g. skyrimspecialedition)";
            return;
        }

        IsSearching = true;
        SearchStatusMessage = $"Searching collections for \"{SearchGameDomain}\"...";

        try
        {
            var gameDomain = SearchGameDomain.Trim();
            var searchTerm = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var offset = (CurrentSearchPage - 1) * CollectionPageSize;

            var (collections, totalCount) = await _backend.SearchCollectionsAsync(
                gameDomain, searchTerm: searchTerm, count: CollectionPageSize, offset: offset);

            // Re-rank by fuzzy match score when search text is present,
            // otherwise sort by popularity (endorsements + downloads)
            var models = collections.Select(c => new NexusCollectionDisplayModel(c));
            _allOnlineCollections = !string.IsNullOrWhiteSpace(SearchText)
                ? models
                    .OrderByDescending(c => FuzzyMatcher.Score(SearchText, c.Name))
                    .ThenByDescending(c => c.Endorsements + c.TotalDownloads)
                    .ToList()
                : models
                    .OrderByDescending(c => c.Endorsements)
                    .ThenByDescending(c => c.TotalDownloads)
                    .ToList();

            OnlineCollections.Clear();
            foreach (var c in _allOnlineCollections)
                OnlineCollections.Add(c);

            _ = LoadCollectionThumbnailsAsync(_allOnlineCollections);

            OnlineTotalResults = totalCount;
            HasNextCollectionPage = offset + CollectionPageSize < totalCount;
            HasPreviousCollectionPage = CurrentSearchPage > 1;

            var displayCount = OnlineCollections.Count;
            var pageInfo = totalCount > CollectionPageSize ? $" (page {CurrentSearchPage})" : "";
            SearchStatusMessage = displayCount == 0
                ? $"No collections found for \"{SearchGameDomain}\""
                : $"Found {totalCount} collection(s){pageInfo}";
        }
        catch (Exception ex)
        {
            SearchStatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task LoadCollectionThumbnailsAsync(List<NexusCollectionDisplayModel> models)
    {
        if (_thumbnailService == null) return;

        foreach (var m in models.Where(m => m.TileImageUrl != null))
        {
            try
            {
                var bitmap = await Task.Run(() => _thumbnailService.GetThumbnailAsync(m.TileImageUrl));
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

    [RelayCommand]
    private async Task DownloadOnlineCollectionAsync()
    {
        if (_backend == null || _service == null || _repository == null ||
            SelectedOnlineCollection == null || _dialogService == null) return;

        var info = SelectedOnlineCollection.Info;
        var gameId = info.GameDomain;

        // Enforce 3 collections per game limit
        var existingForGame = Collections.Count(c =>
            c.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase));

        if (existingForGame >= MaxCollectionsPerGame)
        {
            await _dialogService.ShowWarningAsync("Collection Limit",
                $"You can only store up to {MaxCollectionsPerGame} collections per game. " +
                $"Please delete an existing collection for '{gameId}' before downloading a new one.");
            return;
        }

        // Ask for download directory
        var modsDir = _settings?.ModsDirectory;
        if (string.IsNullOrEmpty(modsDir))
        {
            modsDir = await _dialogService.ShowFolderBrowserAsync("Select download directory for collection mods");
            if (string.IsNullOrEmpty(modsDir)) return;
        }

        IsSearching = true;
        SearchStatusMessage = $"Fetching mod list for '{info.Name}'...";

        try
        {
            // 1. Fetch the full mod list from GraphQL
            var detail = await _backend.GetCollectionDetailsAsync(info.Slug, gameId);
            if (detail == null || detail.Entries.Count == 0)
            {
                SearchStatusMessage = $"Could not fetch mod list for '{info.Name}'";
                return;
            }

            // 2. Create a local collection populated with mod entries
            var collection = await _service.CreateAsync(info.Name, gameId);
            foreach (var entry in detail.Entries)
            {
                collection.Entries.Add(new Modular.Sdk.Collections.ModCollectionEntry
                {
                    ModId = entry.ModId,
                    Name = entry.Name,
                    Author = entry.Author,
                    Version = entry.Version,
                    FileId = entry.FileId,
                    FileName = entry.FileName,
                    IsOptional = entry.IsOptional
                });
            }

            // Save the populated collection
            var (_, path) = await _repository.FindByNameAsync(collection.Name);
            if (path != null)
                await _repository.SaveAsync(collection, path);

            await RefreshCollectionsAsync();
            SearchStatusMessage = $"Imported '{info.Name}' with {detail.Entries.Count} mod(s). Downloading...";

            // 3. Download the mods
            var progress = new Progress<Modular.Sdk.Backends.DownloadProgress>(p =>
            {
                SearchStatusMessage = !string.IsNullOrEmpty(p.Status) ? p.Status : "Downloading...";
            });

            await _service.DownloadCollectionAsync(collection, modsDir, progress: progress);
            SearchStatusMessage = $"Collection '{info.Name}' downloaded ({detail.Entries.Count} mods)";
        }
        catch (Exception ex)
        {
            SearchStatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }
}

/// <summary>
/// Display model for NexusMods collections in the search results.
/// </summary>
public partial class NexusCollectionDisplayModel : ObservableObject
{
    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isLoadingThumbnail;

    public NexusCollectionDisplayModel(NexusCollectionInfo info)
    {
        Info = info;
    }

    public NexusCollectionInfo Info { get; }
    public string Name => Info.Name;
    public string? Summary => Info.Summary;
    public string? Description => Info.Description;
    public string? Author => Info.Author;
    public string GameDomain => Info.GameDomain;
    public int ModCount => Info.ModCount;
    public int Endorsements => Info.Endorsements;
    public int TotalDownloads => Info.TotalDownloads;
    public string? TileImageUrl => Info.TileImageUrl;
    public string Url => Info.Url;
}
