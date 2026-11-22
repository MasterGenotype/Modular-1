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

    /// <summary>"Deployed on build 322538 (legacy)", from the deploy's build stamp.</summary>
    [ObservableProperty]
    private string _deployBuildText = "";

    /// <summary>Set when the mods were deployed on another build than the one installed now.</summary>
    [ObservableProperty]
    private string _buildWarning = "";

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
        if (!game.IsShortcut && owner is not null)
            Versions = new GameVersionsViewModel((uint)game.AppId, game.DisplayName, dialogs, owner.Snapshots, owner.InstallService,
                onModsChanged: () => _ = LoadModsAsync());
    }

    /// <summary>The version browser (Steam games only).</summary>
    public GameVersionsViewModel? Versions { get; }

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
            if (!Game.IsShortcut && mods.Count > 0 && await install.Deployment.Mods.GetDeployBuildAsync(key) is { } deployed)
            {
                DeployBuildText = $"Deployed on {deployed.Label} ({deployed.StampedAt.LocalDateTime:yyyy-MM-dd}).";
                BuildWarning = Modular.Core.Depots.BuildStamp.ForApp((uint)Game.AppId) is { } now && !now.SameBuildAs(deployed)
                    ? $"The game is now on {now.Label}, not the build these mods were deployed on. Check they still work, or switch back under Versions."
                    : "";
            }
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
            var lookups = _owner.Lookups;
            var list = await new ModListExporter(_owner.Database!).ExportAsync(key, Game.DisplayName, null, lookups,
                new Progress<string>(m => ActionStatus = $"Reading {m}…"));
            var stem = string.Concat(Game.DisplayName.Select(c => char.IsLetterOrDigit(c) ? c : c is ' ' or '-' ? '-' : '\0').Where(c => c != '\0')).Trim('-');
            var path = Path.Combine(folder, stem + ModList.Extension);
            await File.WriteAllTextAsync(path, list.ToJson());
            var exact = list.Mods.Count(m => m.Source?.FileId is not null);
            ActionStatus = $"Saved {list.Mods.Count} mod(s) to {path}: {exact} download exactly, {list.Mods.Count - exact} need picking by hand"
                + (lookups.HasNexus ? "." : " (log in to Nexus in Settings for exact Nexus files).");
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
            if (ModListImporter.BuildMismatch(list, key) is { } mismatch)
                otherGame += $"\n\n{mismatch}";
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
            var installation = await install.Deployment.ResolveInstallationAsync(key);
            if (installation is null)
            {
                await _dialogs.ShowErrorAsync("Install mod list", $"Modular can't find {Game.DisplayName}'s install.");
                return;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
            using var gate = await InstallGate.EnterAsync($"installing {Game.DisplayName}'s mod list", m => ActionStatus = m);
            var result = await importer.ApplyAsync(list, installation, key,
                _owner.Lookups.ResolveUrlAsync, http,
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
    private async Task CheckUpdatesAsync()
    {
        if (_owner?.InstallService is not { } install || _owner.Database is not { } db || _dialogs is null || IsWorking)
            return;
        var key = await GameKeyAsync();
        if (key is null || Mods.Count == 0)
        {
            ActionStatus = "No installed mods to update.";
            return;
        }
        IsWorking = true;
        try
        {
            var updater = new ModUpdateService(db, install, _owner.Lookups);
            var check = await updater.CheckAsync(key, new Progress<string>(m => ActionStatus = m));
            if (check.Updates.Count == 0)
            {
                ActionStatus = $"Everything is up to date ({check.Checked} mod(s) checked" +
                    (check.NoSource > 0 ? $"; {check.NoSource} installed from local files can't be checked)." : ").");
                return;
            }
            var chosen = await _dialogs.ShowMultiSelectAsync("Mod updates",
                $"{check.Updates.Count} mod(s) have newer files. Ticked ones are downloaded and installed in place: same order, " +
                "same enabled state. A snapshot of the current set is taken first, so you can go back from Backups.",
                check.Updates.Select(u => u.Describe).ToList(),
                Enumerable.Range(0, check.Updates.Count).ToList());
            if (chosen.Count == 0)
            {
                ActionStatus = "No updates installed.";
                return;
            }
            var installation = await install.Deployment.ResolveInstallationAsync(key);
            if (installation is null)
            {
                ActionStatus = $"Modular can't find {Game.DisplayName}'s install.";
                return;
            }
            if (_owner.Snapshots is { } snapshots)
                await snapshots.CreateSnapshotAsync(Game.IsShortcut ? 0 : Game.AppId, Game.DisplayName,
                    Game.IsShortcut ? Game.InstallDirectory : Game.InstallPath, SnapshotTrigger.Manual,
                    name: $"Before updating {chosen.Count} mod(s)", gameKey: key);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
            using var gate = await InstallGate.EnterAsync($"updating {Game.DisplayName}'s mods", m => ActionStatus = m);
            var result = await updater.ApplyAsync(key, installation, chosen.Select(i => check.Updates[i]).ToList(), http,
                _owner.Settings!.ModsDirectory, new Progress<string>(m => ActionStatus = m));
            var report = $"Updated {result.Updated}, failed {result.Failed.Count}.";
            if (result.Failed.Count > 0)
                report += "\n\n" + string.Join("\n", result.Failed.Select(f => $"• {f.Update.Name}: {f.Error}"));
            ActionStatus = report.Split('\n')[0];
            await _dialogs.ShowInfoAsync("Mod updates", report);
            await LoadModsAsync();
        }
        catch (Exception ex)
        {
            ActionStatus = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Shown again from the games list: re-reads the mod list unless an operation here is still running.</summary>
    internal void Reopened()
    {
        if (!IsWorking)
            _ = LoadModsAsync();
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
