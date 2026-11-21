using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Steam;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>One game's own page, opened from the Games list.</summary>
public partial class GamePageViewModel : ViewModelBase
{
    private readonly GameDetectionViewModel? _owner;
    private readonly IDialogService? _dialogs;
    private readonly SteamShortcutService? _shortcuts;

    [ObservableProperty]
    private Bitmap? _hero;

    [ObservableProperty]
    private Bitmap? _logo;

    [ObservableProperty]
    private Bitmap? _grid;

    // Designer constructor
    public GamePageViewModel() : this(new GameDisplayModel { DisplayName = "Bastion", AppId = 107100, InstallPath = "/games/Bastion" }, null, null)
    {
    }

    public GamePageViewModel(GameDisplayModel game, GameDetectionViewModel? owner, IDialogService? dialogs)
    {
        Game = game;
        _owner = owner;
        _dialogs = dialogs;
        _shortcuts = owner is null ? null : SteamShortcutService.ForCurrentUser();
        LoadArt();
    }

    public GameDisplayModel Game { get; }

    public string Kind => Game.IsShortcut ? "Non-Steam game" : $"Steam game · App {Game.AppId}";

    public string Location => Game.IsShortcut ? Game.InstallPath : Game.InstallPath;

    public string ShortcutButtonText => Game.IsShortcut ? "Edit Steam shortcut…" : "Create Steam shortcut…";

    private void LoadArt()
    {
        if (_shortcuts is null)
            return;
        IReadOnlyDictionary<SteamArtKind, string> art = Game.IsShortcut
            ? _shortcuts.CurrentArt(Game.ShortcutAppId)
            : _shortcuts.User.FindCachedArt((uint)Game.AppId);
        Hero = Load(art, SteamArtKind.Hero);
        Logo = Load(art, SteamArtKind.Logo);
        Grid = Load(art, SteamArtKind.Grid);
    }

    private static Bitmap? Load(IReadOnlyDictionary<SteamArtKind, string> art, SteamArtKind kind)
    {
        if (!art.TryGetValue(kind, out var path))
            return null;
        try
        {
            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (_owner is not null)
            await _owner.CloseActivePageAsync(rescan: false);
    }

    [RelayCommand]
    private async Task OpenShortcutAsync()
    {
        if (_owner is null)
            return;
        if (_shortcuts is null)
        {
            if (_dialogs is not null)
                await _dialogs.ShowErrorAsync("Steam not found", "Modular couldn't find a Steam user folder (userdata) on this machine.");
            return;
        }
        SteamShortcutRequest request;
        if (Game.IsShortcut)
        {
            var existing = _shortcuts.ListShortcuts().FirstOrDefault(s => s.AppId == Game.ShortcutAppId);
            request = existing is not null ? _shortcuts.FromShortcut(existing) : _shortcuts.PrefillForExe(Game.InstallPath);
        }
        else
        {
            request = _shortcuts.PrefillForSteamGame((uint)Game.AppId, Game.DisplayName, Game.InstallPath, Game.LibraryRoot);
        }
        _owner.ActivePage = new SteamShortcutViewModel(_shortcuts, request, Game, _owner, back: this, _dialogs);
    }
}
