using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the GameBanana search tab.
/// Searches the GameBanana API and ranks results with fuzzy matching.
/// Filters by GameDomain (numeric game ID) when provided.
/// </summary>
public partial class GameBananaSearchViewModel : ViewModelBase
{
    private readonly GameBananaBackend? _backend;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _gameIdText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Enter a search term to find mods on GameBanana";

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _searchResults = new();

    [ObservableProperty]
    private int _totalResults;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>
    /// Configured game IDs from settings, displayed for quick selection.
    /// </summary>
    public ObservableCollection<string> AvailableGameIds { get; } = new();

    // Designer constructor
    public GameBananaSearchViewModel()
    {
        SearchResults.Add(new ModDisplayModel(new BackendMod
        {
            ModId = "999",
            Name = "Sample GameBanana Search Result",
            Author = "TestAuthor",
            BackendId = "gamebanana",
            GameDomain = "Sample Game"
        }));
    }

    // DI constructor
    public GameBananaSearchViewModel(GameBananaBackend backend, AppSettings settings)
    {
        _backend = backend;

        // Pre-populate available game IDs from configuration
        foreach (var id in settings.GameBananaGameIds)
            AvailableGameIds.Add(id.ToString());

        // Default to first configured game ID if available
        if (AvailableGameIds.Count > 0)
            GameIdText = AvailableGameIds[0];
    }

    partial void OnSearchTextChanged(string value)
    {
        _debounceTimer?.Dispose();
        var delay = string.IsNullOrWhiteSpace(value) ? 0 : 400;
        _debounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ExecuteSearchAsync()),
            null, delay, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        if (_backend == null)
        {
            StatusMessage = "GameBanana backend not available";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchResults.Clear();
            TotalResults = 0;
            StatusMessage = "Enter a search term to find mods on GameBanana";
            return;
        }

        // Parse optional game ID filter
        int? gameId = null;
        if (!string.IsNullOrWhiteSpace(GameIdText) && int.TryParse(GameIdText.Trim(), out var parsedId))
            gameId = parsedId;

        IsLoading = true;
        var gameLabel = gameId.HasValue ? $" (game {gameId.Value})" : "";
        StatusMessage = $"Searching GameBanana for \"{SearchText}\"{gameLabel}...";

        try
        {
            var results = await _backend.SearchModsAsync(SearchText, gameId: gameId, maxResults: 50);

            // Rank by fuzzy score but keep ALL API results — the API already did relevance
            // filtering, so we only reorder; items with score 0 go to the end rather than
            // being discarded (fixes empty-result bug when API matches on description/tags
            // rather than name).
            var scored = results
                .Select(mod => (mod, score: FuzzyMatcher.Score(SearchText, mod.Name)))
                .OrderByDescending(x => x.score)
                .Select(x => x.mod);

            SearchResults.Clear();
            foreach (var mod in scored)
            {
                var display = new ModDisplayModel(mod);
                display.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ModDisplayModel.IsSelected))
                        UpdateSelectedCount();
                };
                SearchResults.Add(display);
            }

            TotalResults = SearchResults.Count;
            StatusMessage = TotalResults == 0
                ? $"No mods found for \"{SearchText}\""
                : $"Found {TotalResults} result(s) on GameBanana";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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
        UpdateSelectedCount();
    }

    public void UpdateSelectedCount()
    {
        SelectedCount = SearchResults.Count(m => m.IsSelected);
    }

    public IEnumerable<ModDisplayModel> GetSelectedMods()
    {
        return SearchResults.Where(m => m.IsSelected);
    }
}
