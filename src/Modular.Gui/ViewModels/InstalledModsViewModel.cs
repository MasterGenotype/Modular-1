using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Modular.Core.Dependencies;
using Modular.Core.Deployment;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Gui.Messages;
using Modular.Gui.Services;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.ViewModels;

public partial class InstalledModsViewModel : ViewModelBase
{
    private readonly ModInstallationService? _installService;
    private readonly IDialogService? _dialogService;
    private readonly MissingDependencyService? _dependencyService;
    private readonly SteamGameScanner? _gameScanner;

    // Every installed changeset, and the key of the game option each one is listed under.
    private readonly List<ChangesetRecord> _allRecords = new();
    private readonly Dictionary<string, string> _gameKeyOf = new(StringComparer.Ordinal);

    // The records behind the rows, which carry what a dependency check needs.
    private readonly Dictionary<string, ChangesetRecord> _records = new(StringComparer.Ordinal);

    [ObservableProperty]
    private ObservableCollection<ChangesetDisplayModel> _changesets = new();

    [ObservableProperty]
    private ObservableCollection<GameFilterOption> _games = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReapply))]
    private GameFilterOption? _selectedGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReapply))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _totalInstalled;

    /// <summary>Whether the selected game has mods whose deployment can be re-applied.</summary>
    public bool CanReapply => !IsLoading && SelectedGame is { IsAllGames: false, ModCount: > 0 };

    // Designer constructor
    public InstalledModsViewModel()
    {
        var game = new GameFilterOption("489830", "Skyrim Special Edition", 1);
        Games.Add(GameFilterOption.AllGames(1));
        Games.Add(game);
        SelectedGame = game;

        Changesets.Add(new ChangesetDisplayModel
        {
            ChangesetId = "abc123",
            ModId = "SkyUI",
            Game = game.DisplayName,
            TargetDirectory = "/home/user/.steam/steamapps/common/Skyrim",
            CreatedAt = "2026-03-20 14:30",
            State = "Active"
        });
        TotalInstalled = 1;
    }

    // DI constructor
    public InstalledModsViewModel(
        ModInstallationService installService,
        IDialogService dialogService,
        MissingDependencyService dependencyService,
        SteamGameScanner gameScanner)
    {
        _installService = installService;
        _dialogService = dialogService;
        _dependencyService = dependencyService;
        _gameScanner = gameScanner;

        _ = RefreshInstalledAsync();
    }

    partial void OnSelectedGameChanged(GameFilterOption? value) => ApplyGameFilter();

    [RelayCommand]
    private async Task RefreshInstalledAsync()
    {
        if (_installService == null)
        {
            StatusMessage = "Installation service not available";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading installed mods...";

        try
        {
            var installed = await _installService.ListInstalledAsync();
            var scanned = await ScanGamesAsync();

            _allRecords.Clear();
            _allRecords.AddRange(installed);
            _gameKeyOf.Clear();

            // The games listed on the Games page, then any game that has mods but was not
            // found by the scan (a non-Steam install, or one whose library is offline).
            var names = scanned.ToDictionary(
                g => g.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                g => g.DisplayName,
                StringComparer.OrdinalIgnoreCase);

            foreach (var record in installed)
                _gameKeyOf[record.ChangesetId] = AssignGameKey(record, scanned);

            foreach (var key in _gameKeyOf.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (!names.ContainsKey(key))
                    names[key] = await DescribeUnscannedGameAsync(key, installed);
            }

            var previous = SelectedGame?.Key;
            var options = names
                .Select(n => new GameFilterOption(
                    n.Key,
                    n.Value,
                    _gameKeyOf.Values.Count(k => string.Equals(k, n.Key, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(o => o.ModCount > 0)
                .ThenBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Games.Clear();
            Games.Add(GameFilterOption.AllGames(installed.Count));
            foreach (var option in options)
                Games.Add(option);

            // Keep the game the user was looking at; otherwise open on the game with the
            // most mods, since the tab is about one game at a time.
            SelectedGame =
                Games.FirstOrDefault(g => previous != null && g.Key == previous) ??
                options.Where(o => o.ModCount > 0).MaxBy(o => o.ModCount) ??
                Games[0];

            ApplyGameFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // The Steam games the Games page would list. A scan failure only costs the names.
    private async Task<List<SteamGameInstall>> ScanGamesAsync()
    {
        if (_gameScanner == null)
            return [];

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await _gameScanner.ScanAllAsync(cts.Token);
        }
        catch (Exception)
        {
            return [];
        }
    }

    // A managed changeset names its game; a legacy one is placed by where it installed to.
    private static string AssignGameKey(ChangesetRecord record, IReadOnlyList<SteamGameInstall> scanned)
    {
        if (!string.IsNullOrEmpty(record.GameKey))
            return record.GameKey;

        if (!string.IsNullOrEmpty(record.TargetDirectory))
        {
            var target = Path.GetFullPath(record.TargetDirectory);
            var game = scanned.FirstOrDefault(g =>
                !string.IsNullOrEmpty(g.InstallPath) && IsUnder(target, g.InstallPath));

            if (game != null)
                return game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return GameFilterOption.UnknownKey;
    }

    private static bool IsUnder(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Names a game the Steam scan did not find, by resolving one of its mods' installs.
    private async Task<string> DescribeUnscannedGameAsync(string key, IReadOnlyList<ChangesetRecord> installed)
    {
        if (key == GameFilterOption.UnknownKey)
            return "Unknown game";

        var sample = installed.FirstOrDefault(r =>
            string.Equals(_gameKeyOf.GetValueOrDefault(r.ChangesetId), key, StringComparison.OrdinalIgnoreCase));

        if (sample != null && _installService != null)
        {
            try
            {
                var resolved = await _installService.ResolveGameAsync(sample);
                if (!string.IsNullOrEmpty(resolved?.Installation.DisplayName))
                    return resolved.Value.Installation.DisplayName!;
            }
            catch (Exception)
            {
                // Fall back to the key.
            }
        }

        return key;
    }

    private void ApplyGameFilter()
    {
        Changesets.Clear();
        _records.Clear();

        var selected = SelectedGame;
        var names = Games.Where(g => !g.IsAllGames)
            .ToDictionary(g => g.Key!, g => g.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (var record in _allRecords)
        {
            var key = _gameKeyOf.GetValueOrDefault(record.ChangesetId, GameFilterOption.UnknownKey);
            if (selected is { IsAllGames: false } &&
                !string.Equals(key, selected.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _records[record.ChangesetId] = record;
            Changesets.Add(new ChangesetDisplayModel
            {
                ChangesetId = record.ChangesetId,
                ModId = record.ModId ?? "Unknown",
                Game = names.GetValueOrDefault(key, key),
                TargetDirectory = record.TargetDirectory ?? "Unknown",
                ArchivePath = record.ArchivePath ?? "",
                CreatedAt = record.CreatedAtUtc,
                State = record.State.ToString()
            });
        }

        TotalInstalled = Changesets.Count;
        StatusMessage = selected is { IsAllGames: false }
            ? $"{TotalInstalled} installed mod(s) for {selected.DisplayName}"
            : $"{TotalInstalled} installed mod(s)";
    }

    // Drops removed changesets from the full list and the game counts, so the selector
    // stays honest without a rescan.
    private void ForgetChangesets(IReadOnlyCollection<string> changesetIds)
    {
        if (changesetIds.Count == 0)
            return;

        _allRecords.RemoveAll(r => changesetIds.Contains(r.ChangesetId));
        foreach (var id in changesetIds)
            _gameKeyOf.Remove(id);

        foreach (var game in Games)
        {
            game.ModCount = game.IsAllGames
                ? _allRecords.Count
                : _gameKeyOf.Values.Count(k => string.Equals(k, game.Key, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(CanReapply));
    }

    /// <summary>
    /// Re-applies the selected game's whole mod installation: every enabled mod is laid
    /// down again from staging, in load order, and the game's load-order files rewritten.
    /// Files that are already in place are left as they are; missing or changed ones are
    /// put back.
    /// </summary>
    [RelayCommand]
    private async Task ReapplyGameAsync()
    {
        if (_installService == null || _dialogService == null || SelectedGame is not { IsAllGames: false } game)
            return;

        var records = _allRecords
            .Where(r => string.Equals(_gameKeyOf.GetValueOrDefault(r.ChangesetId), game.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var managed = records.Where(r => r.ManagedBy == ChangesetOwnership.Deployment).ToList();
        var legacy = records.Count - managed.Count;

        if (managed.Count == 0)
        {
            await _dialogService.ShowWarningAsync(
                "Nothing to Re-apply",
                $"None of the {records.Count} mod(s) installed for {game.DisplayName} were installed " +
                "through staging, so there is no deployment to re-apply. Reinstall them from their archives.");
            return;
        }

        var message =
            $"Re-apply every enabled mod for {game.DisplayName} from staging, in load order?\n\n" +
            "Files already in place are kept. Missing or changed files are put back, and any " +
            "changed file is preserved before it is replaced.";
        if (legacy > 0)
            message += $"\n\n{legacy} mod(s) installed without staging are not part of the deployment and are left as they are.";

        if (!await _dialogService.ShowConfirmationAsync("Re-apply Mods", message))
            return;

        IsLoading = true;
        StatusMessage = $"Re-applying mods for {game.DisplayName}...";

        try
        {
            var resolved = await _installService.ResolveGameAsync(managed[0]);
            if (resolved == null)
            {
                StatusMessage = $"Could not find {game.DisplayName}";
                await _dialogService.ShowErrorAsync(
                    "Re-apply Failed",
                    $"{game.DisplayName} could not be found on disk. Check that it is still installed.");
                return;
            }

            var installation = resolved.Value.Installation;
            var result = await Task.Run(() => _installService.Deployment.DeployAsync(installation));

            if (!result.Success)
            {
                StatusMessage = $"Re-apply failed: {result.Error}";
                await _dialogService.ShowErrorAsync("Re-apply Failed", result.Error ?? "Unknown error");
                return;
            }

            StatusMessage =
                $"Re-applied {game.DisplayName}: {result.Placed.Count} file(s) placed, " +
                $"{result.Unchanged.Count} already in place, {result.Removed.Count} removed";

            var notes = DescribeDeploymentNotes(result);
            if (notes.Count > 0)
                await _dialogService.ShowWarningAsync("Re-applied with Notes", string.Join("\n", notes));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            await _dialogService.ShowErrorAsync("Re-apply Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static List<string> DescribeDeploymentNotes(DeploymentResult result)
    {
        var notes = new List<string>();

        if (result.Displaced.Count > 0)
            notes.Add($"{result.Displaced.Count} file(s) had been changed outside Modular and were preserved before being replaced.");
        if (result.FellBackToCopy.Count > 0)
            notes.Add($"{result.FellBackToCopy.Count} file(s) were copied because the chosen deployment method was unavailable.");
        if (result.StaleBackupsSkipped.Count > 0)
            notes.Add($"{result.StaleBackupsSkipped.Count} vanilla backup(s) predate a game update and were not restored.");

        if (result.Warnings.Count > 0)
        {
            if (notes.Count > 0)
                notes.Add(string.Empty);
            notes.AddRange(result.Warnings);
        }

        return notes;
    }

    [RelayCommand]
    private async Task RemoveAllAsync()
    {
        if (_installService == null || _dialogService == null || Changesets.Count == 0)
            return;

        var scope = SelectedGame is { IsAllGames: false } game ? $" for {game.DisplayName}" : string.Empty;
        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Remove All Mods",
            $"Are you sure you want to uninstall all {Changesets.Count} installed mod(s){scope}?\n\nThis action cannot be undone.");

        if (!confirmed) return;

        IsLoading = true;
        var total = Changesets.Count;
        var removed = 0;
        var failed = 0;
        var removedIds = new List<string>();

        var snapshot = Changesets.ToList();
        foreach (var changeset in snapshot)
        {
            StatusMessage = $"Uninstalling {changeset.ModId} ({removed + failed + 1}/{total})...";

            try
            {
                var result = await _installService.UninstallAsync(changeset.ChangesetId);
                if (result.Success)
                {
                    Changesets.Remove(changeset);
                    removedIds.Add(changeset.ChangesetId);
                    removed++;
                }
                else
                {
                    failed++;
                }
            }
            catch
            {
                failed++;
            }
        }

        ForgetChangesets(removedIds);
        TotalInstalled = Changesets.Count;
        IsLoading = false;

        if (failed == 0)
            StatusMessage = $"Successfully removed all {removed} mod(s)";
        else
            StatusMessage = $"Removed {removed} mod(s), {failed} failed";
    }

    /// <summary>
    /// Finds and queues what one installed mod needs and does not have.
    /// </summary>
    [RelayCommand]
    private async Task FetchMissingDependenciesAsync(ChangesetDisplayModel? changeset)
    {
        if (changeset == null || !_records.TryGetValue(changeset.ChangesetId, out var record))
            return;

        await FetchDependenciesAsync([record], changeset.ModId, includeUnmanaged: false);
    }

    /// <summary>
    /// Finds and queues what every installed mod needs and does not have.
    /// </summary>
    [RelayCommand]
    private async Task FetchAllMissingDependenciesAsync()
    {
        var scope = SelectedGame is { IsAllGames: false } game
            ? $"all installed {game.DisplayName} mods"
            : "all installed mods";
        await FetchDependenciesAsync(_records.Values.ToList(), scope, includeUnmanaged: true);
    }

    private async Task FetchDependenciesAsync(
        IReadOnlyList<ChangesetRecord> targets, string scope, bool includeUnmanaged)
    {
        if (_installService == null || _dialogService == null || _dependencyService == null)
        {
            StatusMessage = "Dependency lookup not available";
            return;
        }

        if (targets.Count == 0)
        {
            StatusMessage = "No installed mods to check";
            return;
        }

        IsLoading = true;
        StatusMessage = $"Checking dependencies of {scope}...";

        try
        {
            var installed = await _installService.ListInstalledAsync();
            var report = await _dependencyService.FindAsync(targets, installed, includeUnmanaged);

            if (report.Items.Count == 0)
            {
                StatusMessage = DescribeNothingFound(report);

                var notes = report.Errors.Concat(DescribeConflicts(report)).ToList();
                if (notes.Count > 0)
                    await _dialogService.ShowWarningAsync("Dependency Check", string.Join("\n", notes));
                return;
            }

            // What can be queued: mods with a backend id, and declared dependencies a name
            // search found candidates for. The rest can only be reported.
            var selectable = report.Items
                .Where(i => i.Kind == MissingDependencyKind.Downloadable ||
                            (i.Kind == MissingDependencyKind.Unidentified && i.Candidates.Count > 0))
                .ToList();
            var manual = report.Items.Except(selectable).ToList();

            var summary = DescribeReport(report, selectable.Count, manual);

            if (selectable.Count == 0)
            {
                StatusMessage = $"{manual.Count} missing dependency(ies), none downloadable here";
                await _dialogService.ShowWarningAsync("Missing Dependencies", summary);
                return;
            }

            var chosen = await _dialogService.ShowMultiSelectAsync(
                "Missing Dependencies",
                summary,
                selectable.Select(DescribeItem).ToList(),
                selectable
                    .Select((item, index) => (item, index))
                    .Where(x => x.item.Kind == MissingDependencyKind.Downloadable && !x.item.Optional)
                    .Select(x => x.index)
                    .ToList());

            if (chosen.Count == 0)
            {
                StatusMessage = "No dependencies queued";
                return;
            }

            var toQueue = new List<(BackendMod Mod, BackendModFile File)>();
            var toInstall = new List<string>();
            var skipped = new List<string>();

            foreach (var item in chosen.Select(i => selectable[i]))
            {
                // Already downloaded: it only needs installing.
                if (item.DownloadedArchives.Count > 0)
                {
                    var archives = await ChooseDownloadedAsync(item);
                    if (archives.Count == 0)
                        skipped.Add(item.Name);
                    else
                        toInstall.AddRange(archives);
                    continue;
                }

                StatusMessage = $"Looking up files for {item.Name}...";

                try
                {
                    var picked = await ChooseFileAsync(item);
                    if (picked == null)
                        skipped.Add(item.Name);
                    else
                        toQueue.Add(picked.Value);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{item.Name} ({ex.Message})");
                }
            }

            if (toInstall.Count > 0)
                WeakReferenceMessenger.Default.Send(new SendToInstallerMessage(toInstall, navigate: false));

            // Downloaded files join the installer's list as they finish.
            if (toQueue.Count > 0)
                WeakReferenceMessenger.Default.Send(new EnqueueDownloadsMessage(toQueue, addToInstaller: true));

            var outcome = new List<string>();
            if (toQueue.Count > 0)
                outcome.Add($"queued {toQueue.Count} file(s) for download");
            if (toInstall.Count > 0)
                outcome.Add($"added {toInstall.Count} already-downloaded file(s) to the install list");
            if (skipped.Count > 0)
                outcome.Add($"skipped {skipped.Count}");

            StatusMessage = "Dependencies: " + string.Join(", ", outcome) +
                            (toQueue.Count > 0 ? ". Downloads are added to the install list when they finish." : ".");

            if (skipped.Count > 0)
            {
                await _dialogService.ShowWarningAsync(
                    "Dependencies Skipped",
                    "Nothing was queued for:\n\n" + string.Join("\n", skipped));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            await _dialogService.ShowErrorAsync("Dependency Check Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Settles which mod and which file to download for one missing dependency, asking
    // only when the answer is in doubt. Null means the user backed out or nothing fits.
    private async Task<(BackendMod Mod, BackendModFile File)?> ChooseFileAsync(MissingDependency item)
    {
        var backendId = item.BackendId;
        var modId = item.ModId;
        var gameDomain = item.GameDomain;

        if (item.Kind == MissingDependencyKind.Unidentified)
        {
            var index = await _dialogService!.ShowListPickerAsync(
                "Confirm Match",
                $"'{item.Name}' is named by a mod file, not by a mod page, so it was searched " +
                "for by name. Pick the mod it refers to, or cancel to skip it.",
                item.Candidates.Select(c =>
                    string.IsNullOrEmpty(c.Author) ? c.Name : $"{c.Name} — {c.Author}").ToList());

            if (index < 0 || index >= item.Candidates.Count)
                return null;

            var candidate = item.Candidates[index];
            backendId = candidate.BackendId;
            modId = candidate.ModId;
            gameDomain = candidate.GameDomain ?? gameDomain;
        }

        if (string.IsNullOrEmpty(backendId) || string.IsNullOrEmpty(modId))
            return null;

        var (mod, files) = await _dependencyService!.GetFilesAsync(backendId, modId, gameDomain);
        if (files.Count == 0)
            throw new InvalidOperationException("no downloadable files");

        var file = MissingDependencyService.PickDefaultFile(files);
        if (file == null)
        {
            var message = $"'{mod.Name}' has {files.Count} files. Pick the one to download.";
            if (item.Notes.Count > 0)
                message += "\n\nThe requiring mod's author says:\n" + string.Join("\n", item.Notes);

            var index = await _dialogService!.ShowListPickerAsync(
                "Choose File", message, files.Select(DescribeFile).ToList());

            if (index < 0 || index >= files.Count)
                return null;

            file = files[index];
        }

        return (mod, file);
    }

    // Settles which of a dependency's already-downloaded archives to install. A mod page
    // often carries several files, and not all of them need be wanted.
    private async Task<IReadOnlyList<string>> ChooseDownloadedAsync(MissingDependency item)
    {
        if (item.DownloadedArchives.Count == 1)
            return item.DownloadedArchives;

        var message = $"'{item.Name}' is already downloaded, as {item.DownloadedArchives.Count} files. " +
                      "Tick the ones to add to the install list.";
        if (item.Notes.Count > 0)
            message += "\n\nThe requiring mod's author says:\n" + string.Join("\n", item.Notes);

        var picked = await _dialogService!.ShowMultiSelectAsync(
            "Choose Files",
            message,
            item.DownloadedArchives.Select(a => Path.GetFileName(a)).ToList());

        return picked
            .Where(i => i >= 0 && i < item.DownloadedArchives.Count)
            .Select(i => item.DownloadedArchives[i])
            .ToList();
    }

    private static string DescribeItem(MissingDependency item)
    {
        var text = item.Kind == MissingDependencyKind.Unidentified
            ? $"{item.Name} (match by name)"
            : item.Name;

        if (item.DownloadedArchives.Count > 0)
            text += " (already downloaded)";
        if (item.Optional)
            text += " (optional)";

        if (item.RequiredBy.Count > 0)
            text += $" — needed by {string.Join(", ", item.RequiredBy)}";

        return text;
    }

    private static string DescribeFile(BackendModFile file)
    {
        var text = file.DisplayName ?? file.FileName;

        if (!string.IsNullOrEmpty(file.Category))
            text += $" [{file.Category}]";
        if (!string.IsNullOrEmpty(file.Version))
            text += $" v{file.Version}";

        return text;
    }

    private static string DescribeReport(
        MissingDependencyReport report, int selectableCount, IReadOnlyList<MissingDependency> manual)
    {
        var lines = new List<string>
        {
            selectableCount > 0
                ? $"{selectableCount} missing dependency(ies) can be fetched. Ticked items are downloaded " +
                  "and added to the install list; ones already downloaded are added without downloading " +
                  "again. Optional items and items matched by name are unticked until you confirm them."
                : "None of the missing dependencies can be downloaded from here."
        };

        if (manual.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("To get yourself:");
            lines.AddRange(manual.Select(m =>
                $"  {m.Name}" +
                (string.IsNullOrEmpty(m.Url) ? string.Empty : $" — {m.Url}") +
                (m.RequiredBy.Count > 0 ? $" (needed by {string.Join(", ", m.RequiredBy)})" : string.Empty)));
        }

        if (report.SatisfiedElsewhere.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Taken as installed, because a mod of the same name is in the game's mod folder: " +
                      string.Join(", ", report.SatisfiedElsewhere));
        }

        var conflicts = DescribeConflicts(report);
        if (conflicts.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(conflicts);
        }

        if (report.Untraceable.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{report.Untraceable.Count} mod(s) could not be traced to a mod page, " +
                      "so their listed requirements are unknown.");
        }

        if (report.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(report.Errors);
        }

        return string.Join("\n", lines);
    }

    private static List<string> DescribeConflicts(MissingDependencyReport report) =>
        report.Conflicts
            .Select(c => $"Conflict: '{c.DeclaredBy}' says it cannot be used with '{c.ConflictsWith}'.")
            .ToList();

    private static string DescribeNothingFound(MissingDependencyReport report)
    {
        if (report.Errors.Count > 0)
            return "Dependency check incomplete";

        return report.Untraceable.Count == 0
            ? $"No missing dependencies ({report.ModsChecked} mod(s) checked)"
            : $"No missing dependencies found ({report.ModsChecked} checked, " +
              $"{report.Untraceable.Count} not traceable to a mod page)";
    }

    [RelayCommand]
    private async Task UninstallModAsync(ChangesetDisplayModel? changeset)
    {
        if (_installService == null || _dialogService == null || changeset == null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Confirm Uninstall",
            $"Are you sure you want to uninstall '{changeset.ModId}'?\n\nChangeset: {changeset.ChangesetId}\nTarget: {changeset.TargetDirectory}");

        if (!confirmed) return;

        IsLoading = true;
        StatusMessage = $"Uninstalling {changeset.ModId}...";

        try
        {
            var result = await _installService.UninstallAsync(changeset.ChangesetId);

            if (result.Success)
            {
                Changesets.Remove(changeset);
                ForgetChangesets([changeset.ChangesetId]);
                TotalInstalled = Changesets.Count;
                StatusMessage = $"Successfully uninstalled {changeset.ModId}";
            }
            else
            {
                StatusMessage = $"Uninstall failed: {result.Error}";
                await _dialogService.ShowErrorAsync("Uninstall Failed", result.Error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            await _dialogService.ShowErrorAsync("Uninstall Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public partial class ChangesetDisplayModel : ObservableObject
{
    [ObservableProperty]
    private string _changesetId = string.Empty;

    [ObservableProperty]
    private string _modId = string.Empty;

    [ObservableProperty]
    private string _game = string.Empty;

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private string _archivePath = string.Empty;

    [ObservableProperty]
    private string _createdAt = string.Empty;

    [ObservableProperty]
    private string _state = string.Empty;
}

/// <summary>
/// One entry in the Game tab's selector: a game and how many installed mods it has.
/// </summary>
public partial class GameFilterOption : ObservableObject
{
    /// <summary>Key of mods whose game could not be worked out.</summary>
    public const string UnknownKey = "?";

    public GameFilterOption(string? key, string displayName, int modCount)
    {
        Key = key;
        DisplayName = displayName;
        _modCount = modCount;
    }

    /// <summary>Game key (Steam AppID or game id); null for the all-games entry.</summary>
    public string? Key { get; }

    public string DisplayName { get; }

    public bool IsAllGames => Key == null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _modCount;

    public string Label => $"{DisplayName} ({ModCount})";

    public static GameFilterOption AllGames(int modCount) => new(null, "All games", modCount);
}
