using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Core.Backends.ModIo;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the mod.io search tab. Searches one game at a time, picked by name.
/// </summary>
public partial class ModIoSearchViewModel : ViewModelBase
{
    private const int PageSize = 50;

    private readonly ModIoBackend? _backend;
    private readonly Services.TrackingCoordinator? _tracking;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Enter a search term to find mods on mod.io";

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _searchResults = new();

    [ObservableProperty]
    private int _totalResults;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>The game to search; mod.io searches one game at a time.</summary>
    public GamePickerViewModel Game { get; }

    // Designer constructor
    public ModIoSearchViewModel()
    {
        Game = new GamePickerViewModel();
        SearchResults.Add(new ModDisplayModel(new BackendMod
        {
            ModId = "4256380",
            Name = "Sample mod.io Search Result",
            Author = "TestAuthor",
            BackendId = BackendIds.ModIo,
            GameDomain = "6715"
        }));
    }

    // DI constructor
    public ModIoSearchViewModel(ModIoBackend backend, AppSettings settings, Services.TrackingCoordinator tracking)
    {
        _backend = backend;
        _tracking = tracking;
        Game = new GamePickerViewModel(
            (name, ct) => backend.SearchGamesAsync(name, ct: ct),
            settings.ModIoGameIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
            StatusMessage = "mod.io backend not available";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchResults.Clear();
            TotalResults = 0;
            StatusMessage = "Enter a search term to find mods on mod.io";
            return;
        }

        if (!Game.HasText)
        {
            SearchResults.Clear();
            TotalResults = 0;
            StatusMessage = "Pick a game to search: mod.io searches one game at a time";
            return;
        }

        IsLoading = true;

        try
        {
            StatusMessage = $"Looking up \"{Game.Text.Trim()}\" on mod.io...";
            var game = await Game.ResolveAsync();
            if (game == null)
            {
                SearchResults.Clear();
                TotalResults = 0;
                StatusMessage = $"No mod.io game is named \"{Game.Text.Trim()}\". Pick one from the list.";
                return;
            }

            StatusMessage = $"Searching mod.io for \"{SearchText}\" in {game.Name}...";

            var result = await _backend.SearchModsAsync(new ModSearchQuery
            {
                Terms = SearchText,
                GameDomain = game.Id,
                PageSize = PageSize
            });

            // mod.io ranks by popularity; float the closest name matches to the top
            // without dropping anything the API matched on description or tags.
            var ranked = result.Mods
                .Select(mod => (mod, score: FuzzyMatcher.Score(SearchText, mod.Name)))
                .OrderByDescending(x => x.score)
                .Select(x => x.mod);

            SearchResults.Clear();
            foreach (var mod in ranked)
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
            UpdateSelectedCount();
            StatusMessage = TotalResults == 0
                ? $"No mods found for \"{SearchText}\""
                : result.TotalCount > TotalResults
                    ? $"Showing {TotalResults} of {result.TotalCount} results on mod.io"
                    : $"Found {TotalResults} result(s) on mod.io";
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
    private async Task SubscribeSelectedAsync()
    {
        if (_backend == null || _tracking == null)
            return;

        var selected = GetSelectedMods().Select(m => (m.ModId, (string?)m.GameDomain)).ToList();
        IsLoading = true;
        try
        {
            StatusMessage = await _tracking.ApplyAsync(_backend, selected, track: true);
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

    public void UpdateSelectedCount() => SelectedCount = SearchResults.Count(m => m.IsSelected);

    public IEnumerable<ModDisplayModel> GetSelectedMods() => SearchResults.Where(m => m.IsSelected);
}
