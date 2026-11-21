using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.GameDetection;
using Modular.Core.Steam;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

public partial class GameDetectionViewModel : ViewModelBase
{
    private readonly SteamGameScanner? _scanner;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<GameDisplayModel> _games = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Click Scan to detect installed games";

    [ObservableProperty]
    private int _totalGames;

    /// <summary>A game's own page (or its Steam shortcut page) shown in place of the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private ViewModelBase? _activePage;

    public bool ShowList => ActivePage is null;

    [ObservableProperty]
    private GameDisplayModel? _selectedGame;

    // Designer constructor
    public GameDetectionViewModel()
    {
        Games.Add(new GameDisplayModel
        {
            AppId = 570,
            DisplayName = "Dota 2",
            InstallPath = "/home/user/.steam/steamapps/common/dota 2 beta",
            SizeOnDisk = 32_000_000_000,
            IsFullyInstalled = true
        });
        Games.Add(new GameDisplayModel
        {
            AppId = 730,
            DisplayName = "Counter-Strike 2",
            InstallPath = "/home/user/.steam/steamapps/common/Counter-Strike Global Offensive",
            SizeOnDisk = 28_000_000_000,
            IsFullyInstalled = true
        });
        TotalGames = 2;
    }

    // DI constructor
    public GameDetectionViewModel(SteamGameScanner scanner, IDialogService dialogService)
    {
        _scanner = scanner;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task ScanGamesAsync()
    {
        if (_scanner == null)
        {
            StatusMessage = "Game scanner not initialized";
            return;
        }

        IsLoading = true;
        StatusMessage = "Scanning Steam libraries...";
        Games.Clear();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await _scanner.ScanAllAsync(cts.Token);

            var shortcuts = SteamShortcutService.ForCurrentUser();
            if (shortcuts is not null)
            {
                foreach (var s in shortcuts.ListShortcuts().Where(s => s.Exe.Length > 0))
                {
                    Games.Add(new GameDisplayModel
                    {
                        ShortcutAppId = s.AppId,
                        DisplayName = s.AppName,
                        InstallPath = s.Exe,
                        InstallDirectory = s.StartDir,
                        IsFullyInstalled = true,
                        IsShortcut = true,
                    });
                }
            }

            foreach (var game in results.OrderBy(g => g.DisplayName))
            {
                Games.Add(new GameDisplayModel
                {
                    AppId = game.AppId,
                    DisplayName = game.DisplayName,
                    InstallDirectory = game.InstallDirectory,
                    InstallPath = game.InstallPath,
                    LibraryRoot = game.LibraryRoot,
                    SizeOnDisk = game.SizeOnDisk,
                    IsFullyInstalled = game.IsFullyInstalled
                });
            }

            var sorted = Games.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            Games.Clear();
            foreach (var g in sorted)
                Games.Add(g);
            TotalGames = Games.Count;
            StatusMessage = $"Found {TotalGames} game(s)";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan timed out";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Scan Error", $"Failed to scan Steam libraries: {ex.Message}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public partial class GameDetectionViewModel
{
    [RelayCommand]
    private void OpenGame(GameDisplayModel? game)
    {
        game ??= SelectedGame;
        if (game is null)
            return;
        ActivePage = new GamePageViewModel(game, this, _dialogService);
    }

    [RelayCommand]
    private async Task AddNonSteamGameAsync()
    {
        if (_dialogService is null)
            return;
        var picked = await _dialogService.ShowFileBrowserAsync("Choose the game's program (.exe, script or binary)");
        if (picked.FirstOrDefault() is not { } exe)
            return;
        if (SteamShortcutService.ForCurrentUser() is not { } service)
        {
            await _dialogService.ShowErrorAsync("Steam not found", "Modular couldn't find a Steam user folder (userdata) on this machine.");
            return;
        }
        var game = new GameDisplayModel
        {
            DisplayName = Path.GetFileNameWithoutExtension(exe),
            InstallPath = exe,
            InstallDirectory = Path.GetDirectoryName(exe) ?? "",
            IsShortcut = true,
            IsFullyInstalled = true,
        };
        ActivePage = new SteamShortcutViewModel(service, service.PrefillForExe(exe), game, this, back: null, _dialogService);
    }

    /// <summary>Back to the list; rescans so a new shortcut shows up.</summary>
    internal async Task CloseActivePageAsync(bool rescan)
    {
        ActivePage = null;
        if (rescan)
            await ScanGamesAsync();
    }
}

public partial class GameDisplayModel : ObservableObject
{
    /// <summary>A non-Steam game from shortcuts.vdf rather than a Steam install.</summary>
    [ObservableProperty]
    private bool _isShortcut;

    /// <summary>The shortcut's Steam id (unsigned), when <see cref="IsShortcut"/>.</summary>
    [ObservableProperty]
    private uint _shortcutAppId;

    public string IdText => IsShortcut ? ShortcutAppId.ToString() : AppId.ToString();

    public string SourceText => IsShortcut ? "Non-Steam" : "Steam";

    [ObservableProperty]
    private int _appId;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _installDirectory = string.Empty;

    [ObservableProperty]
    private string _installPath = string.Empty;

    [ObservableProperty]
    private string _libraryRoot = string.Empty;

    [ObservableProperty]
    private long _sizeOnDisk;

    [ObservableProperty]
    private bool _isFullyInstalled;

    public string SizeText => FormatBytes(SizeOnDisk);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
