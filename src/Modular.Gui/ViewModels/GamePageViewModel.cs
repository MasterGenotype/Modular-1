using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.ModLists;
using Modular.Core.Snapshots;
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

    /// <summary>Progress of a snapshot, export or import.</summary>
    [ObservableProperty]
    private string _actionStatus = "";

    [ObservableProperty]
    private bool _isWorking;

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
    private async Task TakeSnapshotAsync()
    {
        if (_owner?.Snapshots is not { } snapshots || _dialogs is null || IsWorking)
            return;
        var name = await _dialogs.ShowInputAsync("Take snapshot",
            $"A snapshot records {Game.DisplayName}'s installed mods so you can restore this set later (Backups page). Name it:",
            $"{Game.DisplayName} {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (name is null)
            return;
        IsWorking = true;
        try
        {
            var key = await GameKeyAsync();
            var snapshot = await snapshots.CreateSnapshotAsync(Game.IsShortcut ? 0 : Game.AppId, Game.DisplayName,
                Game.IsShortcut ? Game.InstallDirectory : Game.InstallPath, SnapshotTrigger.Manual, name: name, gameKey: key);
            ActionStatus = $"Snapshot \"{name}\" saved with {snapshot.ModCount} mod(s). Restore it from Backups.";
        }
        catch (Exception ex)
        {
            ActionStatus = $"Snapshot failed: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task ExportModListAsync()
    {
        if (_owner?.InstallService is not { } install || _dialogs is null || IsWorking)
            return;
        var key = await GameKeyAsync();
        if (key is null || Mods.Count == 0)
        {
            ActionStatus = "No installed mods to export.";
            return;
        }
        var folder = await _dialogs.ShowFolderBrowserAsync("Save the mod list in…",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (folder is null)
            return;
        IsWorking = true;
        try
        {
            Func<string, string, CancellationToken, Task<IReadOnlyList<RemoteModFile>>>? listFiles = null;
            if (_owner.Nexus is { } nexus && !string.IsNullOrEmpty(_owner.Settings?.NexusApiKey))
            {
                listFiles = async (domain, modId, ct) => (await nexus.GetModFilesAsync(modId, domain, ct: ct))
                    .Select(f => new RemoteModFile(f.FileId, f.FileName, f.SizeBytes, f.Version,
                        f.UploadedAt is { } u ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)) : null)).ToList();
            }
            var list = await new ModListExporter(_owner.Database!).ExportAsync(key, Game.DisplayName, null, listFiles,
                new Progress<string>(m => ActionStatus = $"Reading {m}…"));
            var stem = string.Concat(Game.DisplayName.Select(c => char.IsLetterOrDigit(c) ? c : c is ' ' or '-' ? '-' : '\0').Where(c => c != '\0')).Trim('-');
            var path = Path.Combine(folder, stem + ModList.Extension);
            await File.WriteAllTextAsync(path, list.ToJson());
            var exact = list.Mods.Count(m => m.Source?.FileId is not null);
            ActionStatus = $"Saved {list.Mods.Count} mod(s) to {path}: {exact} download exactly, {list.Mods.Count - exact} need picking by hand"
                + (listFiles is null ? " (log in to Nexus in Settings for exact files)." : ".");
        }
        catch (Exception ex)
        {
            ActionStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task ImportModListAsync()
    {
        if (_owner?.InstallService is not { } install || _dialogs is null || IsWorking)
            return;
        var picked = (await _dialogs.ShowFileBrowserAsync("Choose a mod list (.modular-modlist.json)")).FirstOrDefault();
        if (picked is null)
            return;
        IsWorking = true;
        try
        {
            var list = ModList.Load(picked);
            var key = await GameKeyAsync() ?? list.Game.Key;
            var importer = new ModListImporter(install);
            var plan = await importer.PlanAsync(list, key);
            int Count(ModListAction a) => plan.Count(p => p.Action == a);
            var otherGame = !string.Equals(list.Game.Key, key, StringComparison.OrdinalIgnoreCase)
                ? $"\n\nThis list was made for {list.Game.Name ?? list.Game.Key}, not {Game.DisplayName}." : "";
            var message = $"{list.Mods.Count} mod(s) in the list:\n" +
                $"• {Count(ModListAction.Download)} to download and install\n" +
                $"• {Count(ModListAction.AlreadyInstalled)} already installed\n" +
                $"• {Count(ModListAction.PickFile) + Count(ModListAction.Manual)} you'll need to get by hand (listed afterwards)" +
                otherGame + (list.Notes.Count > 0 ? "\n\n" + string.Join("\n", list.Notes) : "");
            if (Count(ModListAction.Download) == 0)
            {
                await _dialogs.ShowInfoAsync("Install mod list", message + "\n\nNothing to download.");
                return;
            }
            if (!await _dialogs.ShowConfirmationAsync("Install mod list", message + "\n\nDownload and install now?"))
                return;
            if (_owner.Nexus is not { } nexus || string.IsNullOrEmpty(_owner.Settings?.NexusApiKey))
            {
                await _dialogs.ShowErrorAsync("Install mod list", "Log in to NexusMods in Settings first.");
                return;
            }
            var installation = await install.Deployment.ResolveInstallationAsync(key);
            if (installation is null)
            {
                await _dialogs.ShowErrorAsync("Install mod list", $"Modular can't find {Game.DisplayName}'s install.");
                return;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
            var result = await importer.ApplyAsync(list, installation, key,
                (src, ct) => nexus.ResolveDownloadUrlAsync(src.ModId, src.FileId!, src.Game, ct), http,
                _owner.Settings!.ModsDirectory, new Progress<string>(m => ActionStatus = m));
            var report = $"Installed {result.Installed}, already there {result.Skipped}, failed {result.Failed.Count}.";
            if (result.Failed.Count > 0)
                report += "\n\nFailed:\n" + string.Join("\n", result.Failed.Select(f => $"• {f.Entry.Name}: {f.Error}"));
            if (result.NeedsUser.Count > 0)
                report += "\n\nGet these by hand:\n" + string.Join("\n", result.NeedsUser.Select(n => $"• {n.Entry.Name}: {n.Detail}"));
            ActionStatus = report.Split('\n')[0];
            await _dialogs.ShowInfoAsync("Install mod list", report);
            await LoadModsAsync();
        }
        catch (Exception ex)
        {
            ActionStatus = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
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
