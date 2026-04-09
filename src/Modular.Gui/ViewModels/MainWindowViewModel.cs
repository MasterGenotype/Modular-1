using System.Timers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Modular.Core.Backends;
using Modular.Core.Configuration;
using Modular.Core.RateLimiting;
using Modular.Gui.Messages;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly BackendRegistry? _backendRegistry;
    private readonly AppSettings? _settings;
    private readonly IRateLimiter? _rateLimiter;
    private readonly IDialogService? _dialogService;
    private readonly System.Timers.Timer? _rateLimitTimer;

    [ObservableProperty]
    private string _selectedPage = "NexusMods";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _rateLimitInfo = "Rate Limit: --";

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private string _configurationError = string.Empty;

    [ObservableProperty]
    private bool _showConfigurationWarning;

    [ObservableProperty]
    private bool _showPageContent;

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    // Child ViewModels
    public NexusModsViewModel? NexusModsViewModel { get; }
    public GameBananaPanelViewModel? GameBananaPanelViewModel { get; }
    public DownloadQueueViewModel? DownloadQueueViewModel { get; }
    public SettingsViewModel? SettingsViewModel { get; }
    public LibraryViewModel? LibraryViewModel { get; }
    public GameDetectionViewModel? GameDetectionViewModel { get; }
    public BackupsViewModel? BackupsViewModel { get; }
    public ModManagerViewModel? ModManagerViewModel { get; }

    // Parameterless constructor for designer
    public MainWindowViewModel()
    {
        NexusModsViewModel = new NexusModsViewModel();
        GameBananaPanelViewModel = new GameBananaPanelViewModel();
        DownloadQueueViewModel = new DownloadQueueViewModel();
        SettingsViewModel = new SettingsViewModel();
        LibraryViewModel = new LibraryViewModel();
        GameDetectionViewModel = new GameDetectionViewModel();
        BackupsViewModel = new BackupsViewModel();
        ModManagerViewModel = new ModManagerViewModel();
        CurrentViewModel = NexusModsViewModel;
        CheckConfiguration();
        UpdateVisibility();
    }

    // DI constructor
    public MainWindowViewModel(
        BackendRegistry backendRegistry,
        AppSettings settings,
        IRateLimiter rateLimiter,
        IDialogService dialogService,
        NexusModsViewModel nexusModsViewModel,
        GameBananaPanelViewModel gameBananaPanelViewModel,
        DownloadQueueViewModel downloadQueueViewModel,
        SettingsViewModel settingsViewModel,
        LibraryViewModel libraryViewModel,
        GameDetectionViewModel gameDetectionViewModel,
        BackupsViewModel backupsViewModel,
        ModManagerViewModel modManagerViewModel)
    {
        _backendRegistry = backendRegistry;
        _settings = settings;
        _rateLimiter = rateLimiter;
        _dialogService = dialogService;

        NexusModsViewModel = nexusModsViewModel;
        GameBananaPanelViewModel = gameBananaPanelViewModel;
        DownloadQueueViewModel = downloadQueueViewModel;
        SettingsViewModel = settingsViewModel;
        LibraryViewModel = libraryViewModel;
        GameDetectionViewModel = gameDetectionViewModel;
        BackupsViewModel = backupsViewModel;
        ModManagerViewModel = modManagerViewModel;
        CurrentViewModel = NexusModsViewModel;

        CheckConfiguration();
        UpdateVisibility();
        UpdateRateLimitInfo();

        // Re-check configuration when settings are saved
        WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                CheckConfiguration();
                UpdateVisibility();
            });
        });

        // Clear search selections after all downloads in a batch complete
        WeakReferenceMessenger.Default.Register<DownloadBatchCompletedMessage>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                NexusModsViewModel?.NexusSearchViewModel?.ClearSelection();
                NexusModsViewModel?.ModListViewModel?.SelectNoneCommand.Execute(null);
                GameBananaPanelViewModel?.GameBananaViewModel?.SelectNoneCommand.Execute(null);
                GameBananaPanelViewModel?.GameBananaSearchViewModel?.SelectNoneCommand.Execute(null);
            });
        });

        // Set up timer to update rate limit info periodically
        _rateLimitTimer = new System.Timers.Timer(5000); // Update every 5 seconds
        _rateLimitTimer.Elapsed += OnRateLimitTimerElapsed;
        _rateLimitTimer.Start();
    }

    private void OnRateLimitTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateRateLimitInfo);
    }

    private void UpdateVisibility()
    {
        // Pages that don't require backend configuration
        var isConfigFree = SelectedPage is "Settings" or "Games" or "Mod Manager" or "Backups";
        ShowConfigurationWarning = !IsConfigured && !isConfigFree;
        ShowPageContent = IsConfigured || isConfigFree;
    }

    partial void OnIsConfiguredChanged(bool value) => UpdateVisibility();
    partial void OnSelectedPageChanged(string value) => UpdateVisibility();

    private void CheckConfiguration()
    {
        if (_backendRegistry == null || _settings == null)
        {
            IsConfigured = false;
            ConfigurationError = "Application not fully initialized.";
            return;
        }

        var errors = new List<string>();

        // Check NexusMods configuration
        var nexus = _backendRegistry.Get("nexusmods");
        if (nexus != null)
        {
            errors.AddRange(nexus.ValidateConfiguration());
        }

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

    private void UpdateRateLimitInfo()
    {
        if (_rateLimiter is NexusRateLimiter nexusLimiter)
        {
            RateLimitInfo = $"Daily: {nexusLimiter.DailyRemaining}/{nexusLimiter.DailyLimit} | Hourly: {nexusLimiter.HourlyRemaining}/{nexusLimiter.HourlyLimit}";
        }
    }

    [RelayCommand]
    private void NavigateTo(string page)
    {
        SelectedPage = page;
        StatusText = $"Viewing {page}";

        // Switch to the appropriate ViewModel
        CurrentViewModel = page switch
        {
            "NexusMods" => NexusModsViewModel,
            "GameBanana" => GameBananaPanelViewModel,
            "Downloads" => DownloadQueueViewModel,
            "Library" => LibraryViewModel,
            "Games" => GameDetectionViewModel,
            "Mod Manager" => ModManagerViewModel,
            "Backups" => BackupsViewModel,
            "Settings" => SettingsViewModel,
            _ => NexusModsViewModel
        };
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        NavigateTo("Settings");
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshCurrentViewAsync()
    {
        StatusText = "Refreshing...";
        try
        {
            if (CurrentViewModel is NexusModsViewModel nexusPanel)
            {
                if (nexusPanel.SelectedTabIndex == 0 && nexusPanel.NexusSearchViewModel != null)
                    await nexusPanel.NexusSearchViewModel.ExecuteSearchCommand.ExecuteAsync(null);
                else if (nexusPanel.SelectedTabIndex == 1 && nexusPanel.ModListViewModel != null)
                    await nexusPanel.ModListViewModel.RefreshModsCommand.ExecuteAsync(null);
                else if (nexusPanel.SelectedTabIndex == 2 && nexusPanel.CollectionViewModel != null)
                    await nexusPanel.CollectionViewModel.RefreshCollectionsCommand.ExecuteAsync(null);
            }
            else if (CurrentViewModel is GameBananaPanelViewModel gbPanel)
            {
                if (gbPanel.SelectedTabIndex == 0 && gbPanel.GameBananaViewModel != null)
                    await gbPanel.GameBananaViewModel.RefreshModsCommand.ExecuteAsync(null);
                else if (gbPanel.SelectedTabIndex == 1 && gbPanel.GameBananaSearchViewModel != null)
                    await gbPanel.GameBananaSearchViewModel.ExecuteSearchCommand.ExecuteAsync(null);
            }
            else if (CurrentViewModel is LibraryViewModel library)
            {
                library.RefreshLibraryCommand.Execute(null);
            }
            else if (CurrentViewModel is GameDetectionViewModel gameDetection)
            {
                await gameDetection.ScanGamesCommand.ExecuteAsync(null);
            }
            else if (CurrentViewModel is ModManagerViewModel modManager)
            {
                modManager.ScanForArchivesCommand.Execute(null);
                if (modManager.InstalledModsViewModel != null)
                    await modManager.InstalledModsViewModel.RefreshInstalledCommand.ExecuteAsync(null);
            }
            else if (CurrentViewModel is BackupsViewModel backups)
            {
                if (backups.SnapshotViewModel != null)
                    await backups.SnapshotViewModel.LoadGamesCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (DownloadQueueViewModel == null) return;

        // Unwrap wrapper VMs to get the active child VM
        ViewModelBase? activeVm = CurrentViewModel;
        if (activeVm is NexusModsViewModel nexusPanel)
            activeVm = nexusPanel.SelectedTabIndex == 0
                ? (ViewModelBase?)nexusPanel.NexusSearchViewModel
                : nexusPanel.SelectedTabIndex == 1
                    ? nexusPanel.ModListViewModel
                    : (ViewModelBase?)nexusPanel.CollectionViewModel;
        else if (activeVm is GameBananaPanelViewModel gbPanel)
            activeVm = gbPanel.SelectedTabIndex == 0
                ? (ViewModelBase?)gbPanel.GameBananaViewModel
                : gbPanel.GameBananaSearchViewModel;

        // Get selected mods from current view and queue them for download
        if (activeVm is ModListViewModel modList)
        {
            var selected = modList.GetSelectedMods().ToList();
            if (selected.Count == 0)
            {
                StatusText = "No mods selected";
                return;
            }

            StatusText = $"Fetching files for {selected.Count} mod(s)...";

            var backend = _backendRegistry?.Get("nexusmods") as Modular.Core.Backends.NexusMods.NexusModsBackend;
            if (backend == null)
            {
                StatusText = "Backend not available";
                return;
            }

            var itemsToQueue = new List<(Modular.Sdk.Backends.Common.BackendMod mod, Modular.Sdk.Backends.Common.BackendModFile file)>();

            foreach (var modDisplay in selected)
            {
                try
                {
                    var files = await backend.GetModFilesAsync(
                        modDisplay.ModId,
                        modDisplay.GameDomain,
                        new Modular.Sdk.Backends.Common.FileFilter { Categories = ["main"] });

                    if (files.Count > 0)
                    {
                        var latestFile = files.OrderByDescending(f => f.UploadedAt).First();
                        itemsToQueue.Add((modDisplay.Mod, latestFile));
                    }
                    else
                    {
                        StatusText = $"No files found for {modDisplay.Name}";
                    }
                }
                catch (Exception ex)
                {
                    StatusText = $"Error fetching files for {modDisplay.Name}: {ex.Message}";
                }
            }

            if (itemsToQueue.Count > 0)
            {
                StatusText = $"Queueing {itemsToQueue.Count} file(s) for download...";
                await DownloadQueueViewModel.EnqueueManyAsync(itemsToQueue);
                StatusText = $"Queued {itemsToQueue.Count} file(s) for download";
            }
            else
            {
                StatusText = "No downloadable files found";
            }
        }
        else if (activeVm is NexusSearchViewModel searchVm)
        {
            var selected = searchVm.GetSelectedMods().ToList();
            if (selected.Count == 0)
            {
                StatusText = "No mods selected";
                return;
            }

            StatusText = $"Fetching files for {selected.Count} mod(s)...";

            var nexus = _backendRegistry?.Get("nexusmods") as Modular.Core.Backends.NexusMods.NexusModsBackend;
            if (nexus == null) { StatusText = "NexusMods backend not available"; return; }

            var itemsToQueue = new List<(Modular.Sdk.Backends.Common.BackendMod mod, Modular.Sdk.Backends.Common.BackendModFile file)>();

            foreach (var modDisplay in selected)
            {
                try
                {
                    var files = await nexus.GetModFilesAsync(
                        modDisplay.ModId,
                        modDisplay.GameDomain,
                        new Modular.Sdk.Backends.Common.FileFilter { Categories = ["main"] });

                    if (files.Count > 0)
                    {
                        var latestFile = files.OrderByDescending(f => f.UploadedAt).First();
                        itemsToQueue.Add((modDisplay.Mod, latestFile));
                    }
                }
                catch (Exception ex)
                {
                    StatusText = $"Error fetching files for {modDisplay.Name}: {ex.Message}";
                }
            }

            if (itemsToQueue.Count > 0)
            {
                StatusText = $"Queueing {itemsToQueue.Count} file(s) for download...";
                await DownloadQueueViewModel.EnqueueManyAsync(itemsToQueue);
                StatusText = $"Queued {itemsToQueue.Count} file(s) for download";
            }
            else
            {
                StatusText = "No downloadable files found";
            }
        }
        else if (activeVm is GameBananaViewModel gbList)
        {
            await DownloadGameBananaModsAsync(gbList.GetSelectedMods().ToList());
        }
        else if (activeVm is GameBananaSearchViewModel gbSearch)
        {
            await DownloadGameBananaModsAsync(gbSearch.GetSelectedMods().ToList());
        }
    }

    private async Task DownloadGameBananaModsAsync(List<Models.ModDisplayModel> selected)
    {
        if (DownloadQueueViewModel == null) return;

        if (selected.Count == 0)
        {
            StatusText = "No mods selected";
            return;
        }

        StatusText = $"Fetching files for {selected.Count} mod(s)...";

        var backend = _backendRegistry?.Get("gamebanana") as Modular.Core.Backends.GameBanana.GameBananaBackend;
        if (backend == null)
        {
            StatusText = "Backend not available";
            return;
        }

        var itemsToQueue = new List<(Modular.Sdk.Backends.Common.BackendMod mod, Modular.Sdk.Backends.Common.BackendModFile file)>();

        foreach (var modDisplay in selected)
        {
            try
            {
                var files = await backend.GetModFilesAsync(modDisplay.ModId);
                if (files.Count > 0)
                {
                    var latestFile = files.OrderByDescending(f => f.UploadedAt).First();
                    itemsToQueue.Add((modDisplay.Mod, latestFile));
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error fetching files for {modDisplay.Name}: {ex.Message}";
            }
        }

        if (itemsToQueue.Count > 0)
        {
            StatusText = $"Queueing {itemsToQueue.Count} file(s) for download...";
            await DownloadQueueViewModel.EnqueueManyAsync(itemsToQueue);
            StatusText = $"Queued {itemsToQueue.Count} file(s) for download";
        }
        else
        {
            StatusText = "No downloadable files found";
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        if (CurrentViewModel is DownloadQueueViewModel downloads)
        {
            downloads.CancelAllCommand.Execute(null);
            StatusText = "Operation cancelled";
        }
        else if (CurrentViewModel is NexusModsViewModel nexusPanel &&
                 nexusPanel.ModListViewModel is { IsLoading: true })
        {
            StatusText = "Cannot cancel - operation in progress";
        }
        else
        {
            StatusText = "Nothing to cancel";
        }
    }
}
