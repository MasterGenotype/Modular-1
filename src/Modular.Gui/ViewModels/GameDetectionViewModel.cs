using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.GameDetection;
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

public partial class GameDisplayModel : ObservableObject
{
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
