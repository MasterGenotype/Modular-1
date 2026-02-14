using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Gui.Models;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the NexusMods mod list view.
/// </summary>
public partial class ModListViewModel : ViewModelBase
{
    private readonly NexusModsBackend? _backend;
    private readonly DownloadDatabase? _database;
    private readonly AppSettings? _settings;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<ModDisplayModel> _mods = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedDomain = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _availableDomains = new();

    [ObservableProperty]
    private bool _domainsLoaded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _totalMods;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private int _updatesAvailable;

    // Filtered view of mods
    public IEnumerable<ModDisplayModel> FilteredMods =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Mods
            : Mods.Where(m =>
                m.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));

    // Designer constructor
    public ModListViewModel()
    {
        // Add sample data for designer
        Mods.Add(new ModDisplayModel(new Modular.Sdk.Backends.Common.BackendMod
        {
            ModId = "1",
            Name = "Sample Mod",
            BackendId = "nexusmods",
            GameDomain = "skyrimspecialedition"
        }) { Status = ModDownloadStatus.Downloaded });
    }

    // DI constructor
    public ModListViewModel(
        NexusModsBackend backend,
        DownloadDatabase database,
        AppSettings settings,
        IDialogService dialogService)
    {
        _backend = backend;
        _database = database;
        _settings = settings;
        _dialogService = dialogService;

        // Load domains on construction
        _ = LoadAvailableDomainsAsync();
    }

    /// <summary>
    /// Loads available game domains from all tracked mods.
    /// </summary>
    private async Task LoadAvailableDomainsAsync()
    {
        if (_backend == null || DomainsLoaded) return;

        try
        {
            // Get ALL tracked mods (no domain filter) to extract unique domains
            var allMods = await _backend.GetUserModsAsync(null);
            var domains = allMods
                .Select(m => m.GameDomain)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            AvailableDomains.Clear();
            foreach (var domain in domains)
            {
                AvailableDomains.Add(domain!);
            }

            // Select first domain if available and none selected
            if (AvailableDomains.Count > 0 && string.IsNullOrEmpty(SelectedDomain))
            {
                SelectedDomain = AvailableDomains[0];
            }

            DomainsLoaded = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load domains: {ex.Message}";
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredMods));
    }

    partial void OnSelectedDomainChanged(string value)
    {
        // Clear and reload when domain changes (only if not empty)
        if (!string.IsNullOrEmpty(value))
        {
            _ = RefreshModsAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        if (_backend == null)
        {
            StatusMessage = "Backend not initialized";
            return;
        }

        IsLoading = true;
        StatusMessage = $"Fetching tracked mods for {SelectedDomain}...";

        try
        {
            var trackedMods = await _backend.GetUserModsAsync(SelectedDomain);

            Mods.Clear();
            foreach (var mod in trackedMods)
            {
                var displayModel = new ModDisplayModel(mod);

                // Check download status
                if (_database != null && int.TryParse(mod.ModId, out var modId))
                {
                    var records = _database.GetRecordsByMod(SelectedDomain, modId);
                    displayModel.Status = records.Any()
                        ? ModDownloadStatus.Downloaded
                        : ModDownloadStatus.NotDownloaded;
                }

                Mods.Add(displayModel);
            }

            TotalMods = Mods.Count;
            StatusMessage = $"Found {TotalMods} tracked mods";
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

    [RelayCommand]
    private void ToggleSelection(ModDisplayModel? mod)
    {
        if (mod != null)
        {
            mod.IsSelected = !mod.IsSelected;
            UpdateSelectedCount();
        }
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Mods.Count(m => m.IsSelected);
    }

    /// <summary>
    /// Gets the selected mods for downloading.
    /// </summary>
    public IEnumerable<ModDisplayModel> GetSelectedMods()
    {
        return Mods.Where(m => m.IsSelected);
    }

    /// <summary>
    /// Gets mods that need to be downloaded (not already downloaded).
    /// </summary>
    public IEnumerable<ModDisplayModel> GetModsToDownload()
    {
        return Mods.Where(m => m.IsSelected && m.Status != ModDownloadStatus.Downloaded);
    }

    /// <summary>
    /// Gets mods that have updates available.
    /// </summary>
    public IEnumerable<ModDisplayModel> GetModsWithUpdates()
    {
        return Mods.Where(m => m.Status == ModDownloadStatus.UpdateAvailable);
    }

    [RelayCommand]
    private void SelectAllUpdates()
    {
        foreach (var mod in Mods.Where(m => m.Status == ModDownloadStatus.UpdateAvailable))
        {
            mod.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_backend == null || _database == null)
        {
            StatusMessage = "Backend or database not initialized";
            return;
        }

        IsLoading = true;
        StatusMessage = "Checking for updates...";
        var updatesFound = 0;

        try
        {
            foreach (var mod in Mods.Where(m => m.Status == ModDownloadStatus.Downloaded))
            {
                if (!int.TryParse(mod.ModId, out var modId))
                    continue;

                // Get downloaded file info
                var records = _database.GetRecordsByMod(SelectedDomain, modId).ToList();
                if (records.Count == 0)
                    continue;

                var latestDownloaded = records.OrderByDescending(r => r.DownloadTime).First();
                mod.DownloadedDate = latestDownloaded.DownloadTime;

                // Get latest files from backend
                var files = await _backend.GetModFilesAsync(
                    mod.ModId,
                    SelectedDomain,
                    new FileFilter { Categories = ["main"] });

                if (files.Count == 0)
                    continue;

                var latestFile = files.OrderByDescending(f => f.UploadedAt).First();
                mod.LatestVersion = latestFile.Version;
                mod.LatestFileId = int.TryParse(latestFile.FileId, out var fid) ? fid : null;

                // Check if update available (file ID changed or newer upload date)
                if (latestFile.UploadedAt > latestDownloaded.DownloadTime ||
                    (mod.LatestFileId.HasValue && mod.LatestFileId.Value != latestDownloaded.FileId))
                {
                    mod.Status = ModDownloadStatus.UpdateAvailable;
                    mod.StatusMessage = $"Update available: {latestFile.Version}";
                    updatesFound++;
                }
            }

            UpdatesAvailable = updatesFound;
            StatusMessage = updatesFound > 0
                ? $"Found {updatesFound} update(s) available"
                : "All mods are up to date";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error checking updates: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Error", $"Failed to check for updates: {ex.Message}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
