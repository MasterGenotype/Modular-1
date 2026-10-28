using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Backends;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the GameBanana search tab.
/// Searches the GameBanana API and ranks results with fuzzy matching.
/// Limits the search to one game when one is picked by name.
/// </summary>
public partial class GameBananaSearchViewModel : ViewModelBase
{
    private readonly GameBananaBackend? _backend;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

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

    /// <summary>The game the search is limited to; none searches every game.</summary>
    public GamePickerViewModel Game { get; }

    // Designer constructor
    public GameBananaSearchViewModel()
    {
        Game = new GamePickerViewModel();
        SearchResults.Add(new ModDisplayModel(new BackendMod
        {
            ModId = "999",
            Name = "Sample GameBanana Search Result",
            Author = "TestAuthor",
            BackendId = BackendIds.GameBanana,
            GameDomain = "Sample Game"
        }));
    }

    // DI constructor
    public GameBananaSearchViewModel(GameBananaBackend backend, AppSettings settings)
    {
        _backend = backend;
        Game = new GamePickerViewModel(
            (name, ct) => backend.SearchGamesAsync(name, ct: ct),
            settings.GameBananaGameIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Game.GameChosen += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
                _ = ExecuteSearchAsync();
        };
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

        IsLoading = true;

        try
        {
            GameMatch? game = null;
            if (Game.HasText)
            {
                StatusMessage = $"Looking up \"{Game.Text.Trim()}\" on GameBanana...";
                game = await Game.ResolveAsync();
                if (game == null)
                {
                    SearchResults.Clear();
                    TotalResults = 0;
                    StatusMessage = $"No GameBanana game is named \"{Game.Text.Trim()}\". Pick one from the list, or clear it to search every game.";
                    return;
                }
            }

            int? gameId = game != null && int.TryParse(game.Id, out var parsedId) ? parsedId : null;
            StatusMessage = $"Searching GameBanana for \"{SearchText}\"{(game != null ? $" in {game.Name}" : "")}...";

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
