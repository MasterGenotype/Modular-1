using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends.ModIo;
using Modular.Core.Utilities;
using Modular.Gui.Models;
using Modular.Gui.Services;
using Modular.Sdk.Backends;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the mod.io subscribed mods view.
/// </summary>
public partial class ModIoViewModel : ViewModelBase
{
    private readonly ModIoBackend? _backend;
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
    public ModIoViewModel()
    {
        IsConfigured = true;
        Mods.Add(new ModDisplayModel(new Modular.Sdk.Backends.Common.BackendMod
        {
            ModId = "4256380",
            Name = "Sample mod.io Mod",
            BackendId = BackendIds.ModIo,
            Author = "SampleAuthor",
            GameDomain = "6715"
        }) { Status = ModDownloadStatus.NotDownloaded });
    }

    // DI constructor
    public ModIoViewModel(ModIoBackend backend, IDialogService dialogService)
    {
        _backend = backend;
        _dialogService = dialogService;

        CheckConfiguration();
    }

    /// <summary>
    /// Re-reads whether the backend can be used, after the settings change.
    /// </summary>
    public void CheckConfiguration()
    {
        if (_backend == null)
        {
            IsConfigured = false;
            ConfigurationError = "Backend not initialized.";
            return;
        }

        var errors = _backend.ValidateConfiguration();
        IsConfigured = errors.Count == 0;
        ConfigurationError = string.Join("\n", errors);
    }

    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredMods));

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        if (_backend == null)
        {
            StatusMessage = "Backend not initialized";
            return;
        }

        CheckConfiguration();
        if (!IsConfigured)
        {
            StatusMessage = "Please configure mod.io in Settings";
            return;
        }

        IsLoading = true;
        StatusMessage = "Fetching subscribed mods from mod.io...";

        try
        {
            var subscribed = await _backend.GetUserModsAsync();

            Mods.Clear();
            foreach (var mod in subscribed)
            {
                var display = new ModDisplayModel(mod) { Status = ModDownloadStatus.NotDownloaded };
                display.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ModDisplayModel.IsSelected))
                        UpdateSelectedCount();
                };
                Mods.Add(display);
            }

            TotalMods = Mods.Count;
            UpdateSelectedCount();
            StatusMessage = TotalMods == 0
                ? "No subscribed mods found"
                : $"Found {TotalMods} subscribed mod(s)";
            OnPropertyChanged(nameof(FilteredMods));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            if (_dialogService != null)
                await _dialogService.ShowErrorAsync("mod.io", $"Failed to fetch subscriptions: {ex.Message}");
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
            mod.IsSelected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var mod in Mods)
            mod.IsSelected = false;
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount() => SelectedCount = Mods.Count(m => m.IsSelected);

    public IEnumerable<ModDisplayModel> GetSelectedMods() => Mods.Where(m => m.IsSelected);
}
