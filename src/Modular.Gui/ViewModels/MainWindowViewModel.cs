using System.Timers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Core.Configuration;
using Modular.Core.RateLimiting;
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
    private ViewModelBase? _currentViewModel;

    // Child ViewModels
    public ModListViewModel? ModListViewModel { get; }
    public DownloadQueueViewModel? DownloadQueueViewModel { get; }
    public SettingsViewModel? SettingsViewModel { get; }
    public GameBananaViewModel? GameBananaViewModel { get; }
    public LibraryViewModel? LibraryViewModel { get; }

    // Parameterless constructor for designer
    public MainWindowViewModel()
    {
        ModListViewModel = new ModListViewModel();
        DownloadQueueViewModel = new DownloadQueueViewModel();
        SettingsViewModel = new SettingsViewModel();
        GameBananaViewModel = new GameBananaViewModel();
        LibraryViewModel = new LibraryViewModel();
        CurrentViewModel = ModListViewModel;
        CheckConfiguration();
    }

    // DI constructor
    public MainWindowViewModel(
        BackendRegistry backendRegistry,
        AppSettings settings,
        IRateLimiter rateLimiter,
        IDialogService dialogService,
        ModListViewModel modListViewModel,
        DownloadQueueViewModel downloadQueueViewModel,
        SettingsViewModel settingsViewModel,
        GameBananaViewModel gameBananaViewModel,
        LibraryViewModel libraryViewModel)
    {
        _backendRegistry = backendRegistry;
        _settings = settings;
        _rateLimiter = rateLimiter;
        _dialogService = dialogService;

        ModListViewModel = modListViewModel;
        DownloadQueueViewModel = downloadQueueViewModel;
        SettingsViewModel = settingsViewModel;
        GameBananaViewModel = gameBananaViewModel;
        LibraryViewModel = libraryViewModel;
        CurrentViewModel = ModListViewModel;

        CheckConfiguration();
        UpdateRateLimitInfo();

        // Set up timer to update rate limit info periodically
        _rateLimitTimer = new System.Timers.Timer(5000); // Update every 5 seconds
        _rateLimitTimer.Elapsed += OnRateLimitTimerElapsed;
        _rateLimitTimer.Start();
    }

    private void OnRateLimitTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateRateLimitInfo);
    }

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
            "NexusMods" => ModListViewModel,
            "GameBanana" => GameBananaViewModel,
            "Downloads" => DownloadQueueViewModel,
            "Library" => LibraryViewModel,
            "Settings" => SettingsViewModel,
            _ => ModListViewModel
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
            if (CurrentViewModel is ModListViewModel modList)
            {
                await modList.RefreshModsCommand.ExecuteAsync(null);
            }
            else if (CurrentViewModel is GameBananaViewModel gbList)
            {
                await gbList.RefreshModsCommand.ExecuteAsync(null);
            }
            else if (CurrentViewModel is LibraryViewModel library)
            {
                library.RefreshLibraryCommand.Execute(null);
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
        // Get selected mods from current view and queue them for download
        if (CurrentViewModel is ModListViewModel modList && DownloadQueueViewModel != null)
        {
            var selected = modList.GetSelectedMods().ToList();
            if (selected.Count == 0)
            {
                StatusText = "No mods selected";
                return;
            }

            StatusText = $"Fetching files for {selected.Count} mod(s)...";

            // Get the backend to fetch file info
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
                    // Fetch files for this mod
                    var files = await backend.GetModFilesAsync(
                        modDisplay.ModId,
                        modDisplay.GameDomain,
                        new Modular.Sdk.Backends.Common.FileFilter { Categories = ["main"] });

                    if (files.Count > 0)
                    {
                        // Get the latest main file
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
                NavigateTo("Downloads");
            }
            else
            {
                StatusText = "No downloadable files found";
            }
        }
        else if (CurrentViewModel is GameBananaViewModel gbList && DownloadQueueViewModel != null)
        {
            var selected = gbList.GetSelectedMods().ToList();
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
                NavigateTo("Downloads");
            }
            else
            {
                StatusText = "No downloadable files found";
            }
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
        else if (CurrentViewModel is ModListViewModel modList && modList.IsLoading)
        {
            StatusText = "Cannot cancel - operation in progress";
        }
        else
        {
            StatusText = "Nothing to cancel";
        }
    }
}
