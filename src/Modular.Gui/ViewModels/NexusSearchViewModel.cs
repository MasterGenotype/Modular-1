using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Gui.Models;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for unified mod search across backends (NexusMods / GameBanana).
/// </summary>
public partial class NexusSearchViewModel : ViewModelBase
{
    private readonly NexusModsBackend? _nexusBackend;
    private readonly GameBananaBackend? _gbBackend;
    private System.Threading.Timer? _debounceTimer;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedBackend = "NexusMods";

    public string[] AvailableBackends { get; } = ["NexusMods", "GameBanana"];

    [ObservableProperty]
    private string _selectedGame = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _availableGames = new();

    // Full game list for fuzzy matching (domain -> display name)
    private List<(string Domain, string Name)> _allGames = [];

    [ObservableProperty]
    private bool _showGameSelector = true;

    [ObservableProperty]
    private ModSortOrder _sortOrder = ModSortOrder.Relevance;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Select a backend and enter search terms";

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
    public NexusSearchViewModel(NexusModsBackend nexusBackend, GameBananaBackend gbBackend)
    {
        _nexusBackend = nexusBackend;
        _gbBackend = gbBackend;
        _ = LoadAvailableGamesAsync();
    }

    partial void OnSelectedBackendChanged(string value)
    {
        // NexusMods requires a game domain; GameBanana does not
        ShowGameSelector = value == "NexusMods";
        SearchResults.Clear();
        CurrentPage = 1;
        TotalResults = 0;
        StatusMessage = $"Search {value} — enter at least 3 characters";
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
        if (value.Length >= 3)
        {
            _debounceTimer = new System.Threading.Timer(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ExecuteSearchAsync()),
                null, 400, System.Threading.Timeout.Infinite);
        }
    }

    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText) || SearchText.Length < 3)
        {
            StatusMessage = "Enter at least 3 characters to search";
            return;
        }

        if (SelectedBackend == "NexusMods")
            await SearchNexusModsAsync();
        else
            await SearchGameBananaAsync();
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

        // Fuzzy: match against domain or display name (contains, case-insensitive)
        var matches = _allGames
            .Where(g =>
                g.Domain.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                g.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) return trimmed; // Let the API try it as-is

        // Prefer exact starts-with on domain, then name, then first contains match
        return matches
            .OrderByDescending(g => g.Domain.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(g => g.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .First().Domain;
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

        IsLoading = true;
        StatusMessage = $"Searching {resolvedDomain} for \"{SearchText}\"...";

        try
        {
            var result = await _nexusBackend.SearchModsAsync(new ModSearchQuery
            {
                Terms = SearchText,
                GameDomain = resolvedDomain,
                SortBy = SortOrder,
                Page = CurrentPage,
                PageSize = 20
            });

            SearchResults.Clear();
            foreach (var mod in result.Mods)
                SearchResults.Add(new ModDisplayModel(mod));

            TotalResults = result.TotalCount;
            HasNextPage = result.HasNextPage;
            HasPreviousPage = CurrentPage > 1;
            StatusMessage = $"Found {result.TotalCount} results (page {result.Page})";
        }
        catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private async Task SearchGameBananaAsync()
    {
        if (_gbBackend == null) { StatusMessage = "GameBanana backend not available"; return; }

        IsLoading = true;
        StatusMessage = $"Searching GameBanana for \"{SearchText}\"...";

        try
        {
            var results = await _gbBackend.SearchModsAsync(SearchText, maxResults: 50);

            SearchResults.Clear();
            foreach (var mod in results)
                SearchResults.Add(new ModDisplayModel(mod));

            TotalResults = SearchResults.Count;
            HasNextPage = false;
            HasPreviousPage = false;
            StatusMessage = $"Found {TotalResults} result(s) on GameBanana";
        }
        catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; }
        finally { IsLoading = false; }
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
}
