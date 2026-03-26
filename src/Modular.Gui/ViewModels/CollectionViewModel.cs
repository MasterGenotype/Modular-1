using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Collections;
using Modular.Core.Configuration;
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
    private ObservableCollection<NexusCollectionDisplayModel> _onlineCollections = new();

    [ObservableProperty]
    private NexusCollectionDisplayModel? _selectedOnlineCollection;

    [ObservableProperty]
    private int _onlineTotalResults;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatusMessage = "Enter a game domain to browse NexusMods collections";

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
        AppSettings? settings = null)
    {
        _backend = backend;
        _dialogService = dialogService;
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
            var (collections, totalCount) = await _backend.SearchCollectionsAsync(
                SearchGameDomain.Trim(), count: 20);

            OnlineCollections.Clear();
            foreach (var c in collections)
            {
                OnlineCollections.Add(new NexusCollectionDisplayModel(c));
            }

            OnlineTotalResults = totalCount;
            SearchStatusMessage = totalCount == 0
                ? $"No collections found for \"{SearchGameDomain}\""
                : $"Found {totalCount} collection(s) for \"{SearchGameDomain}\"";
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

    [RelayCommand]
    private async Task ImportOnlineCollectionAsync()
    {
        if (_service == null || _repository == null || SelectedOnlineCollection == null) return;

        var info = SelectedOnlineCollection.Info;
        var gameId = info.GameDomain;

        // Enforce 3 collections per game limit
        var existingForGame = Collections.Count(c =>
            c.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase));

        if (existingForGame >= MaxCollectionsPerGame)
        {
            StatusMessage = $"Maximum {MaxCollectionsPerGame} collections per game reached for '{gameId}'.";
            if (_dialogService != null)
            {
                await _dialogService.ShowWarningAsync("Collection Limit",
                    $"You can only store up to {MaxCollectionsPerGame} collections per game. " +
                    $"Please delete an existing collection for '{gameId}' before importing a new one.");
            }
            return;
        }

        try
        {
            // Create a local collection record from the online collection info
            await _service.CreateAsync(info.Name, gameId);

            // Refresh and find the newly created collection
            await RefreshCollectionsAsync();

            StatusMessage = $"Imported collection '{info.Name}' ({info.ModCount} mods) for {gameId}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to import collection: {ex.Message}";
        }
    }
}

/// <summary>
/// Display model for NexusMods collections in the search results.
/// </summary>
public class NexusCollectionDisplayModel
{
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
    public string Url => Info.Url;
}
