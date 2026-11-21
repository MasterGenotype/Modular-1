using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Core.Steam;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

public partial class GameDetectionViewModel : ViewModelBase
{
    private readonly SteamGameScanner? _scanner;
    private readonly IDialogService? _dialogService;
    private readonly ModInstallationService? _installService;
    private CancellationTokenSource? _coverCts;

    /// <summary>Games shown as tiles, after the filter.</summary>
    public ObservableCollection<GameDisplayModel> VisibleGames { get; } = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        VisibleGames.Clear();
        foreach (var g in Games.Where(g => string.IsNullOrWhiteSpace(FilterText)
                     || g.DisplayName.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)))
            VisibleGames.Add(g);
    }

    internal Modular.Core.Snapshots.SnapshotManager? Snapshots { get; }
    internal Modular.Core.Backends.NexusMods.NexusModsBackend? Nexus { get; }
    internal Modular.Core.Configuration.AppSettings? Settings { get; }
    internal Modular.Core.Database.ModularDatabase? Database { get; }
    internal Modular.Core.Backends.ModIo.ModIoBackend? ModIo { get; }

    /// <summary>Online lookups for mod lists: Nexus when logged in, mod.io always.</summary>
    internal Modular.Core.ModLists.ModListLookups Lookups => new(
        string.IsNullOrEmpty(Settings?.NexusApiKey) ? null : Nexus, ModIo);

    /// <summary>Mod database access for a game's page (null in the designer).</summary>
    internal ModInstallationService? InstallService => _installService;

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
        ApplyFilter();
    }

    // DI constructor
    public GameDetectionViewModel(SteamGameScanner scanner, IDialogService dialogService, ModInstallationService installService,
        Modular.Core.Snapshots.SnapshotManager snapshots, Modular.Core.Backends.NexusMods.NexusModsBackend nexus,
        Modular.Core.Configuration.AppSettings settings, Modular.Core.Database.ModularDatabase database,
        Modular.Core.Backends.ModIo.ModIoBackend modio)
    {
        ModIo = modio;
        Database = database;
        _scanner = scanner;
        _dialogService = dialogService;
        _installService = installService;
        Snapshots = snapshots;
        Nexus = nexus;
        Settings = settings;
        _ = ScanGamesAsync();
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
                        ShortcutIcon = s.Icon,
                    });
                }
            }

            foreach (var game in results.Where(g => !SteamTools.IsTool(g.AppId, g.DisplayName)).OrderBy(g => g.DisplayName))
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
            ApplyFilter();
            _ = LoadCoversAsync(Games.ToList());
            _ = CheckPinnedGamesAsync(Games.ToList());
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

    /// <summary>
    /// For games Modular holds on a build: badges the ones with a newer build out, and (Steam
    /// closed) renews stale "let Steam launch it" claims so Play keeps working after a patch.
    /// </summary>
    private async Task CheckPinnedGamesAsync(IReadOnlyList<GameDisplayModel> games)
    {
        var service = new Modular.Core.Depots.DepotVersionService();
        var pinned = service.Pins.List().ToDictionary(p => p.AppId);
        var held = games.Where(g => !g.IsShortcut && pinned.ContainsKey((uint)g.AppId)).ToList();
        foreach (var g in held)
            g.IsHeld = true;
        if (held.Count == 0)
            return;
        try
        {
            await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new Modular.Core.Security.DefaultSecretStore()), default);
            var client = new Modular.Core.Depots.SteamDepotClient(session);
            if (!service.SteamRunning)
            {
                var renewed = await service.RenewAsync(client);
                if (renewed.Count > 0)
                    StatusMessage = $"Renewed {renewed.Count} held game(s) after a new patch: Steam sees them as up to date again.";
            }
            foreach (var g in held)
            {
                var latest = await service.CheckLatestAsync((uint)g.AppId, client);
                g.NewerBuildText = latest.NewerThanInstalled
                    ? $"Newer build out: {latest.LatestBuildId?.ToString() ?? "?"}{(latest.LatestDate is { } d ? $" ({d.LocalDateTime:yyyy-MM-dd})" : "")}"
                    : null;
                if (latest.ClaimIsStale)
                    g.NewerBuildText += " · close Steam and open Modular to keep Play working";
            }
        }
        catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException
                                       or InvalidOperationException or TimeoutException)
        {
            // Offline or not logged in: no badges this time.
        }
    }

    /// <summary>Loads each tile's portrait cover: Steam's cached art, or the shortcut's grid image.</summary>
    private async Task LoadCoversAsync(IReadOnlyList<GameDisplayModel> games)
    {
        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var service = SteamShortcutService.ForCurrentUser();
        if (service is null)
            return;
        var missing = new List<GameDisplayModel>();
        foreach (var game in games)
        {
            if (cts.IsCancellationRequested)
                return;
            string? path = game.IsShortcut
                ? service.CurrentArt(game.ShortcutAppId).GetValueOrDefault(Modular.Core.Steam.SteamArtKind.Grid)
                : service.User.FindCachedArt((uint)game.AppId).GetValueOrDefault(Modular.Core.Steam.SteamArtKind.Grid);
            if (path is null || await DecodeAsync(path, 300, cts.Token) is not { } cover)
            {
                missing.Add(game);
                // Until a cover turns up, show the game's icon: the shortcut's, or Steam's.
                var icon = game.IsShortcut
                    ? game.ShortcutIcon
                    : service.User.FindCachedArt((uint)game.AppId).GetValueOrDefault(Modular.Core.Steam.SteamArtKind.Icon);
                if (icon is { Length: > 0 } && File.Exists(icon))
                    game.IconImage = await DecodeAsync(icon, 128, cts.Token);
                continue;
            }
            game.Cover = cover;
        }
        await LoadGridDbCoversAsync(missing, cts.Token);
    }

    private static readonly string CoverCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "thumbnails", "covers");

    /// <summary>
    /// Covers from SteamGridDB for tiles Steam has no art for, shown in Modular only (cached under
    /// thumbnails/covers); a shortcut's own artwork changes only from its shortcut page.
    /// </summary>
    private static async Task LoadGridDbCoversAsync(IReadOnlyList<GameDisplayModel> games, CancellationToken ct)
    {
        if (games.Count == 0)
            return;
        string? key;
        try
        {
            key = await new Modular.Core.Security.DefaultSecretStore().GetAsync(SteamGridDbClient.SecretKey, ct);
        }
        catch
        {
            return;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
        var client = string.IsNullOrWhiteSpace(key) ? null : new SteamGridDbClient(http, key);
        Directory.CreateDirectory(CoverCacheDir);
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested)
                return;
            var cached = Path.Combine(CoverCacheDir, $"{(game.IsShortcut ? "s" : "a")}{game.IdText}.img");
            try
            {
                if (!File.Exists(cached))
                {
                    if (client is null)
                        continue;
                    var match = game.IsShortcut
                        ? (await client.SearchAsync(game.DisplayName, ct)).FirstOrDefault()
                        : await client.GetBySteamAppIdAsync((uint)game.AppId, ct);
                    if (match is null || (await client.GetImagesAsync(match.Id, SteamArtKind.Grid, ct)).FirstOrDefault() is not { } image)
                        continue;
                    await File.WriteAllBytesAsync(cached, await client.DownloadAsync(image.Url, ct), ct);
                }
                game.Cover = await DecodeAsync(cached, 300, ct);
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Keep the icon or name tile.
            }
        }
    }

    private static async Task<Bitmap?> DecodeAsync(string path, int width, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, width);
            }, ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return null;
        }
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

    /// <summary>Portrait cover for the tile; null shows the name on a plain tile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover is not null;

    /// <summary>The shortcut's icon file (shortcuts.vdf "icon"), shown while there's no cover.</summary>
    public string? ShortcutIcon { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconOnly))]
    private Bitmap? _iconImage;

    public bool HasIconOnly => IconImage is not null && Cover is null;

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIconOnly));

    /// <summary>Modular holds this game on a build (pinned).</summary>
    [ObservableProperty]
    private bool _isHeld;

    /// <summary>"Newer build out: 26300000 (2026-12-02)" for a held game; null when it's current.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewerBuild))]
    private string? _newerBuildText;

    public bool HasNewerBuild => NewerBuildText is not null;

    public string StatusText => IsShortcut ? "Steam shortcut" : IsFullyInstalled ? "Installed" : "Partly installed";

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
