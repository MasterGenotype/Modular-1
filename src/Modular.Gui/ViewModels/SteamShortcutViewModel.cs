using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Modular.Core.Depots;
using Modular.Core.Security;
using Modular.Core.Steam;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>One image offered for an artwork slot.</summary>
public partial class ArtOptionViewModel : ObservableObject
{
    public ArtOptionViewModel(string source, string label, string? localPath, string? url, string extension)
    {
        Source = source;
        Label = label;
        LocalPath = localPath;
        Url = url;
        Extension = extension;
    }

    /// <summary>"Current", "Steam", "SteamGridDB" or "File".</summary>
    public string Source { get; }
    public string Label { get; }
    public string? LocalPath { get; }
    public string? Url { get; }
    public string Extension { get; }

    [ObservableProperty]
    private Bitmap? _thumb;

    /// <summary>The shortcut already uses this image: choosing it changes nothing.</summary>
    public bool IsCurrent => Source == "Current";
}

/// <summary>One tab of the artwork picker (Grid, Wide, Hero, Logo or Icon).</summary>
public partial class ArtTabViewModel : ObservableObject
{
    public ArtTabViewModel(SteamArtKind kind, string header, string hint, double thumbWidth, double thumbHeight)
    {
        Kind = kind;
        Header = header;
        Hint = hint;
        ThumbWidth = thumbWidth;
        ThumbHeight = thumbHeight;
    }

    public SteamArtKind Kind { get; }
    public string Header { get; }
    public string Hint { get; }
    public double ThumbWidth { get; }
    public double ThumbHeight { get; }

    public ObservableCollection<ArtOptionViewModel> Options { get; } = new();

    [ObservableProperty]
    private ArtOptionViewModel? _selected;

    /// <summary>Removes SteamGridDB results (a new game was picked) but keeps current/Steam/file ones.</summary>
    public void ClearGridDb()
    {
        foreach (var o in Options.Where(o => o.Source == "SteamGridDB").ToList())
            Options.Remove(o);
        if (Selected is { Source: "SteamGridDB" })
            Selected = Options.FirstOrDefault(o => o.IsCurrent);
    }
}

/// <summary>
/// The "Steam shortcut" page for one game: what the shortcut starts, its Proton build, and its
/// library artwork picked per slot from SteamGridDB, Steam's own art, or a file.
/// </summary>
public partial class SteamShortcutViewModel : ViewModelBase
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly SteamShortcutService? _service;
    private readonly GameDetectionViewModel? _owner;
    private readonly ViewModelBase? _back;
    private readonly IDialogService? _dialogs;
    private readonly ISecretStore _secrets = new DefaultSecretStore();
    private readonly uint? _sourceSteamAppId;
    private readonly uint? _existingAppId;
    private SteamGridDbClient? _gridDb;
    private CancellationTokenSource? _gridCts;

    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _exe = "";
    [ObservableProperty] private string _startDir = "";
    [ObservableProperty] private string _launchOptions = "";
    [ObservableProperty] private SteamCompatTool? _selectedCompatTool;
    [ObservableProperty] private ArtTabViewModel? _selectedTab;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsGridKey))]
    private bool _hasGridKey;

    [ObservableProperty] private string _gridSearchText = "";
    [ObservableProperty] private SteamGridDbGame? _selectedGridGame;

    public bool NeedsGridKey => !HasGridKey;

    public ObservableCollection<SteamCompatTool> CompatTools { get; } = new();
    public ObservableCollection<SteamGridDbGame> GridGames { get; } = new();
    public ObservableCollection<ArtTabViewModel> Tabs { get; } = new();

    public string Title { get; }
    public string ApplyText => _existingAppId is null ? "Create shortcut" : "Update shortcut";

    /// <summary>The "None" choice: run the program natively.</summary>
    public static readonly SteamCompatTool NoCompatTool = new("", "None (run natively)");

    // Designer constructor
    public SteamShortcutViewModel()
    {
        Title = "Steam shortcut · Bastion";
        AppName = "Bastion";
        BuildTabs();
    }

    public SteamShortcutViewModel(SteamShortcutService service, SteamShortcutRequest request, GameDisplayModel game,
        GameDetectionViewModel owner, ViewModelBase? back, IDialogService? dialogs)
    {
        _service = service;
        _owner = owner;
        _back = back;
        _dialogs = dialogs;
        _sourceSteamAppId = request.SourceSteamAppId;
        _existingAppId = request.ExistingAppId;
        Title = $"Steam shortcut · {game.DisplayName}";

        AppName = request.AppName;
        Exe = request.Exe;
        StartDir = request.StartDir;
        LaunchOptions = request.LaunchOptions;
        GridSearchText = game.DisplayName;

        CompatTools.Add(NoCompatTool);
        foreach (var t in service.User.ListCompatTools())
            CompatTools.Add(t);
        if (request.CompatTool is { Length: > 0 } tool && CompatTools.All(t => t.InternalName != tool))
            CompatTools.Add(new SteamCompatTool(tool, tool));
        SelectedCompatTool = CompatTools.FirstOrDefault(t => t.InternalName == (request.CompatTool ?? "")) ?? NoCompatTool;

        BuildTabs();
        AddLocalOptions();
        _ = InitGridDbAsync();
    }

    private void BuildTabs()
    {
        Tabs.Add(new ArtTabViewModel(SteamArtKind.Grid, "Grid", "Portrait cover in the library (600×900)", 120, 180));
        Tabs.Add(new ArtTabViewModel(SteamArtKind.Wide, "Wide", "Wide cover for recent games and Big Picture (920×430)", 230, 107));
        Tabs.Add(new ArtTabViewModel(SteamArtKind.Hero, "Hero", "Banner at the top of the game's page (1920×620)", 300, 97));
        Tabs.Add(new ArtTabViewModel(SteamArtKind.Logo, "Logo", "Transparent logo drawn over the hero", 200, 100));
        Tabs.Add(new ArtTabViewModel(SteamArtKind.Icon, "Icon", "Square icon in lists and the taskbar", 80, 80));
        SelectedTab = Tabs[0];
    }

    private void AddLocalOptions()
    {
        if (_service is null)
            return;
        var current = _existingAppId is { } id ? _service.CurrentArt(id) : new Dictionary<SteamArtKind, string>();
        var steamArt = _sourceSteamAppId is { } app ? _service.User.FindCachedArt(app) : new Dictionary<SteamArtKind, string>();
        foreach (var tab in Tabs)
        {
            if (current.TryGetValue(tab.Kind, out var cur))
                tab.Options.Add(WithLocalThumb(new ArtOptionViewModel("Current", "In use now", cur, null, Path.GetExtension(cur)), tab));
            if (steamArt.TryGetValue(tab.Kind, out var steam))
                tab.Options.Add(WithLocalThumb(new ArtOptionViewModel("Steam", "Steam's own", steam, null, Path.GetExtension(steam)), tab));
            tab.Selected = tab.Options.FirstOrDefault(o => o.IsCurrent) ?? tab.Options.FirstOrDefault(o => o.Source == "Steam");
        }
    }

    private static ArtOptionViewModel WithLocalThumb(ArtOptionViewModel option, ArtTabViewModel tab)
    {
        try
        {
            using var fs = File.OpenRead(option.LocalPath!);
            option.Thumb = Bitmap.DecodeToWidth(fs, (int)Math.Max(tab.ThumbWidth * 2, 64));
        }
        catch
        {
            // An unreadable image just shows without a preview.
        }
        return option;
    }

    private async Task InitGridDbAsync()
    {
        var key = await _secrets.GetAsync(SteamGridDbClient.SecretKey);
        HasGridKey = !string.IsNullOrWhiteSpace(key);
        if (!HasGridKey)
            return;
        _gridDb = new SteamGridDbClient(Http, key!);
        try
        {
            if (_sourceSteamAppId is { } app && await _gridDb.GetBySteamAppIdAsync(app) is { } byId)
            {
                GridGames.Clear();
                GridGames.Add(byId);
                SelectedGridGame = byId;
                return;
            }
            await SearchGridAsync();
        }
        catch (Exception ex)
        {
            Status = $"SteamGridDB: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSettings() =>
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Modular.Gui.Messages.NavigateToPageMessage("Settings"));

    [RelayCommand]
    private async Task SearchGridAsync()
    {
        if (_gridDb is null || string.IsNullOrWhiteSpace(GridSearchText))
            return;
        try
        {
            var games = await _gridDb.SearchAsync(GridSearchText.Trim());
            GridGames.Clear();
            foreach (var g in games.Take(20))
                GridGames.Add(g);
            SelectedGridGame = GridGames.FirstOrDefault();
            if (GridGames.Count == 0)
                Status = $"SteamGridDB has no game matching \"{GridSearchText}\".";
        }
        catch (Exception ex)
        {
            Status = $"SteamGridDB: {ex.Message}";
        }
    }

    partial void OnSelectedGridGameChanged(SteamGridDbGame? value)
    {
        if (value is not null)
            _ = LoadGridImagesAsync(value);
    }

    private async Task LoadGridImagesAsync(SteamGridDbGame game)
    {
        if (_gridDb is null)
            return;
        _gridCts?.Cancel();
        var cts = _gridCts = new CancellationTokenSource();
        foreach (var tab in Tabs)
            tab.ClearGridDb();
        Status = $"Loading artwork for {game.Name} from SteamGridDB…";
        using var throttle = new SemaphoreSlim(6);
        try
        {
            var lists = await Task.WhenAll(Tabs.Select(async tab => (tab, images: await _gridDb.GetImagesAsync(game.Id, tab.Kind, cts.Token))));
            var thumbs = new List<Task>();
            foreach (var (tab, images) in lists)
            {
                foreach (var img in images.Take(40))
                {
                    var option = new ArtOptionViewModel("SteamGridDB", $"{img.Width}×{img.Height}{(img.Style is { } st ? $" · {st}" : "")}",
                        null, img.Url, img.Mime == "image/jpeg" ? ".jpg" : ".png");
                    tab.Options.Add(option);
                    thumbs.Add(LoadRemoteThumbAsync(option, img.Thumb, tab, throttle, cts.Token));
                }
                tab.Selected ??= tab.Options.FirstOrDefault();
            }
            Status = $"{lists.Sum(l => l.images.Count)} images on SteamGridDB for {game.Name}.";
            await Task.WhenAll(thumbs);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = $"SteamGridDB: {ex.Message}";
        }
    }

    private static async Task LoadRemoteThumbAsync(ArtOptionViewModel option, string url, ArtTabViewModel tab, SemaphoreSlim throttle, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct);
            using var ms = new MemoryStream(bytes);
            option.Thumb = Bitmap.DecodeToWidth(ms, (int)Math.Max(tab.ThumbWidth * 2, 64));
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Missing preview; the full image may still download fine.
        }
        finally
        {
            throttle.Release();
        }
    }

    [RelayCommand]
    private async Task ChooseFileAsync(ArtTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null || _dialogs is null)
            return;
        var picked = await _dialogs.ShowFileBrowserAsync($"Choose {tab.Header.ToLowerInvariant()} image (PNG or JPEG)");
        if (picked.FirstOrDefault() is not { } path)
            return;
        var option = WithLocalThumb(new ArtOptionViewModel("File", Path.GetFileName(path), path, null, Path.GetExtension(path)), tab);
        tab.Options.Insert(0, option);
        tab.Selected = option;
    }

    [RelayCommand]
    private async Task BrowseExeAsync()
    {
        if (_dialogs is null)
            return;
        var picked = await _dialogs.ShowFileBrowserAsync("Choose the program the shortcut starts");
        if (picked.FirstOrDefault() is { } exe)
        {
            Exe = exe;
            StartDir = Path.GetDirectoryName(exe) ?? StartDir;
        }
    }

    [RelayCommand]
    private void ClearArt(ArtTabViewModel? tab)
    {
        if (tab is not null)
            tab.Selected = tab.Options.FirstOrDefault(o => o.IsCurrent);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_service is null || IsBusy)
            return;
        if (SteamProcess.IsRunning())
        {
            Status = "Steam is running. Close Steam (Steam → Exit), then press the button again. Modular won't close it for you.";
            return;
        }
        IsBusy = true;
        try
        {
            var art = new Dictionary<SteamArtKind, SteamArtChoice>();
            foreach (var tab in Tabs)
            {
                if (tab.Selected is not { IsCurrent: false } choice)
                    continue;
                Status = $"Getting {tab.Header.ToLowerInvariant()} image…";
                var bytes = choice.LocalPath is { } local
                    ? await File.ReadAllBytesAsync(local)
                    : await Http.GetByteArrayAsync(choice.Url!);
                art[tab.Kind] = new SteamArtChoice(bytes, choice.Extension);
            }

            var request = new SteamShortcutRequest(AppName, Exe, StartDir, LaunchOptions,
                SelectedCompatTool is { InternalName.Length: > 0 } t ? t.InternalName : null,
                _sourceSteamAppId, _existingAppId);
            var result = _service.Apply(request, art);
            Status = $"{(result.Created ? "Created" : "Updated")} \"{result.Shortcut.AppName}\" (id {result.Shortcut.AppId}) with {result.ArtWritten.Count} image(s). Start Steam to see it.";
            if (_dialogs is not null)
                await _dialogs.ShowInfoAsync("Steam shortcut", Status);
            if (_owner is not null)
                await _owner.CloseActivePageAsync(rescan: true);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        _gridCts?.Cancel();
        if (_owner is null)
            return;
        if (_back is not null)
            _owner.ActivePage = _back;
        else
            await _owner.CloseActivePageAsync(rescan: false);
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
        return http;
    }
}
