using System.Collections.ObjectModel;
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

    /// <summary>Mods Modular installed for this game, in load order.</summary>
    public ObservableCollection<GameModRow> Mods { get; } = new();

    [ObservableProperty]
    private string _modsSummary = "Looking for installed mods…";

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
        _ = LoadModsAsync();
    }

    public GameDisplayModel Game { get; }

    public string Kind => Game.IsShortcut ? "Non-Steam game" : $"Steam game · App {Game.AppId}";

    public string LocationLabel => Game.IsShortcut ? "Program" : "Install folder";

    public string Location => Game.InstallPath;

    public string SizeText => Game.IsShortcut ? "—" : Game.SizeText;

    public string ShortcutButtonText => Game.IsShortcut ? "Edit this Steam shortcut…" : "Create a Steam shortcut for this game…";

    public bool HasMods => Mods.Count > 0;

    /// <summary>
    /// The key Modular stores this game's mods under: the Steam app id, or for a non-Steam game the
    /// key of the module that claims its folder (e.g. "enshrouded").
    /// </summary>
    private async Task<string?> GameKeyAsync()
    {
        var install = _owner?.InstallService;
        if (install is null)
            return null;
        if (!Game.IsShortcut)
            return Game.AppId.ToString();
        var keys = await install.Deployment.Mods.ListGameKeysAsync();
        var folder = Directory.Exists(Game.InstallDirectory) ? Game.InstallDirectory : Path.GetDirectoryName(Game.InstallPath);
        if (folder is null || install.ModuleRegistry.ResolveByPath(folder)?.Module is not { } module)
            return null;
        return module.GameIds.Prepend(module.ModuleId).FirstOrDefault(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
    }

    private async Task LoadModsAsync()
    {
        try
        {
            var key = await GameKeyAsync();
            if (key is null || _owner?.InstallService is not { } install)
            {
                ModsSummary = "No mods installed through Modular.";
                return;
            }
            var mods = await install.Deployment.Mods.ListAsync(key);
            Mods.Clear();
            foreach (var m in mods)
                Mods.Add(new GameModRow(string.IsNullOrWhiteSpace(m.DisplayName) ? m.ModId : m.DisplayName!, m.Enabled,
                    m.InstallerId ?? "", m.InstalledAtUtc is { } at && DateTimeOffset.TryParse(at, out var t) ? t.LocalDateTime.ToString("yyyy-MM-dd") : ""));
            var enabled = mods.Count(m => m.Enabled);
            ModsSummary = mods.Count == 0
                ? "No mods installed through Modular."
                : $"{mods.Count} mod(s) installed, {enabled} enabled.";
            OnPropertyChanged(nameof(HasMods));
        }
        catch (Exception ex)
        {
            ModsSummary = $"Couldn't read installed mods: {ex.Message}";
        }
    }

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

/// <summary>One installed mod on a game's page.</summary>
public sealed record GameModRow(string Name, bool Enabled, string Installer, string InstalledOn)
{
    public string StateText => Enabled ? "Enabled" : "Disabled";
}
