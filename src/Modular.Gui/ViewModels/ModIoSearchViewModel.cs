using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.ModIo;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the mod.io search tab. Searches one game at a time, by its mod.io id.
/// </summary>
public partial class ModIoSearchViewModel : ViewModelBase
{
    private const int PageSize = 50;

    private readonly ModIoBackend? _backend;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _gameIdText = string.Empty;

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

    /// <summary>Configured game IDs, offered for quick selection.</summary>
    public ObservableCollection<string> AvailableGameIds { get; } = new();

    // Designer constructor
    public ModIoSearchViewModel()
    {
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
    public ModIoSearchViewModel(ModIoBackend backend, AppSettings settings)
    {
        _backend = backend;

        foreach (var id in settings.ModIoGameIds)
            AvailableGameIds.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

        var game = string.IsNullOrWhiteSpace(GameIdText) ? null : GameIdText.Trim();

        IsLoading = true;
        StatusMessage = $"Searching mod.io for \"{SearchText}\"{(game != null ? $" (game {game})" : "")}...";

        try
        {
            var result = await _backend.SearchModsAsync(new ModSearchQuery
            {
                Terms = SearchText,
                GameDomain = game,
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
