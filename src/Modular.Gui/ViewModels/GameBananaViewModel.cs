using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Gui.Services;
using Modular.Sdk.Backends;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the GameBanana subscribed mods view.
/// </summary>
public partial class GameBananaViewModel : ViewModelBase
{
    private readonly GameBananaBackend? _backend;
    private readonly AppSettings? _settings;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _mods = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _totalMods;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private string _configurationError = string.Empty;

    // Filtered view of mods, fuzzy-ranked when a filter is typed
    public IEnumerable<ModDisplayModel> FilteredMods =>
        string.IsNullOrWhiteSpace(FilterText)
            ? Mods
            : FuzzyMatcher
                .Rank(FilterText, Mods, m => m.Name)
                .Concat(FuzzyMatcher
                    .Rank(FilterText, Mods.Where(m => m.Author != null), m => m.Author!)
                    .Where(m => FuzzyMatcher.Score(FilterText, m.Name) == 0))
                .Distinct();

    // Designer constructor
    public GameBananaViewModel()
    {
        IsConfigured = true;
        Mods.Add(new ModDisplayModel(new Modular.Sdk.Backends.Common.BackendMod
        {
            ModId = "123456",
            Name = "Sample GameBanana Mod",
            BackendId = BackendIds.GameBanana,
            Author = "SampleAuthor"
        }) { Status = ModDownloadStatus.NotDownloaded });
    }

    // DI constructor
    public GameBananaViewModel(
        GameBananaBackend backend,
        AppSettings settings,
        IDialogService dialogService)
    {
        _backend = backend;
        _settings = settings;
        _dialogService = dialogService;

        CheckConfiguration();
    }

    private void CheckConfiguration()
    {
        if (_backend == null || _settings == null)
        {
            IsConfigured = false;
            ConfigurationError = "Backend not initialized.";
            return;
        }

        var errors = _backend.ValidateConfiguration();
        if (errors.Count > 0)
        {
            IsConfigured = false;
            ConfigurationError = string.Join("\n", errors);
        }
        else
        {
            IsConfigured = true;
            ConfigurationError = string.Empty;
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredMods));
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        if (_backend == null)
        {
            StatusMessage = "Backend not initialized";
            return;
        }

        if (!IsConfigured)
        {
            StatusMessage = "Please configure GameBanana user ID in Settings";
            return;
        }

        IsLoading = true;
        StatusMessage = "Fetching subscribed mods from GameBanana...";

        try
        {
            var subscribedMods = await _backend.GetUserModsAsync();

            Mods.Clear();
            foreach (var mod in subscribedMods)
            {
                var displayModel = new ModDisplayModel(mod)
                {
                    Status = ModDownloadStatus.NotDownloaded
                };
                Mods.Add(displayModel);
            }

            TotalMods = Mods.Count;
            StatusMessage = TotalMods == 0
                ? "No subscribed mods found"
                : $"Found {TotalMods} subscribed mod(s)";
            OnPropertyChanged(nameof(FilteredMods));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Error", $"Failed to fetch mods: {ex.Message}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var mod in FilteredMods)
        {
            mod.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var mod in Mods)
        {
            mod.IsSelected = false;
        }
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Mods.Count(m => m.IsSelected);
    }

    public IEnumerable<ModDisplayModel> GetSelectedMods()
    {
        return Mods.Where(m => m.IsSelected);
    }
}
