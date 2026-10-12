using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Modular.Core.Dependencies;
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

    // The records behind the rows, which carry what a dependency check needs.
    private readonly Dictionary<string, ChangesetRecord> _records = new(StringComparer.Ordinal);

    [ObservableProperty]
    private ObservableCollection<ChangesetDisplayModel> _changesets = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _totalInstalled;

    // Designer constructor
    public InstalledModsViewModel()
    {
        Changesets.Add(new ChangesetDisplayModel
        {
            ChangesetId = "abc123",
            ModId = "SkyUI",
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
        MissingDependencyService dependencyService)
    {
        _installService = installService;
        _dialogService = dialogService;
        _dependencyService = dependencyService;

        _ = RefreshInstalledAsync();
    }

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
            Changesets.Clear();
            _records.Clear();

            foreach (var record in installed)
            {
                _records[record.ChangesetId] = record;
                Changesets.Add(new ChangesetDisplayModel
                {
                    ChangesetId = record.ChangesetId,
                    ModId = record.ModId ?? "Unknown",
                    TargetDirectory = record.TargetDirectory ?? "Unknown",
                    ArchivePath = record.ArchivePath ?? "",
                    CreatedAt = record.CreatedAtUtc,
                    State = record.State.ToString()
                });
            }

            TotalInstalled = Changesets.Count;
            StatusMessage = $"{TotalInstalled} installed mod(s)";
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

    [RelayCommand]
    private async Task RemoveAllAsync()
    {
        if (_installService == null || _dialogService == null || Changesets.Count == 0)
            return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Remove All Mods",
            $"Are you sure you want to uninstall all {Changesets.Count} installed mod(s)?\n\nThis action cannot be undone.");

        if (!confirmed) return;

        IsLoading = true;
        var total = Changesets.Count;
        var removed = 0;
        var failed = 0;

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
        await FetchDependenciesAsync(_records.Values.ToList(), "all installed mods", includeUnmanaged: true);
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
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private string _archivePath = string.Empty;

    [ObservableProperty]
    private string _createdAt = string.Empty;

    [ObservableProperty]
    private string _state = string.Empty;
}
