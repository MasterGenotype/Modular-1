using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Archives;
using Modular.Core.Collections;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Core.Snapshots;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Modular.Gui.Services;
using Modular.Sdk.Collections;
using Modular.Sdk.Installers;

namespace Modular.Gui.ViewModels;

public partial class InstallViewModel : ViewModelBase
{
    private readonly ModInstallationService? _installService;
    private readonly SteamGameScanner? _scanner;
    private readonly IDialogService? _dialogService;
    private readonly AppSettings? _settings;
    private readonly ModMetadataCache? _metadataCache;
    private readonly Modular.Core.Backends.BackendRegistry? _backends;

    [ObservableProperty]
    private ObservableCollection<string> _archivePaths = new();

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private ObservableCollection<GameDisplayModel> _detectedGames = new();

    [ObservableProperty]
    private GameDisplayModel? _selectedGame;

    [ObservableProperty]
    private bool _allowOverwrite;

    [ObservableProperty]
    private bool _createBackups = true;

    [ObservableProperty]
    private bool _dryRun;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbortInstallCommand))]
    private bool _isAborting;

    private CancellationTokenSource? _installCts;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _statusMessage = "Select archives and a target directory to install";

    [ObservableProperty]
    private ObservableCollection<string> _installResults = new();

    [ObservableProperty]
    private int _currentArchiveIndex;

    [ObservableProperty]
    private int _totalArchives;

    // Designer constructor
    public InstallViewModel()
    {
        ArchivePaths.Add("/home/user/Mods/skyrim/some-mod.zip");
        ArchivePaths.Add("/home/user/Mods/skyrim/another-mod.zip");
        TargetDirectory = "/home/user/.steam/steamapps/common/Skyrim";
        DetectedGames.Add(new GameDisplayModel
        {
            AppId = 489830,
            DisplayName = "The Elder Scrolls V: Skyrim Special Edition",
            InstallPath = "/home/user/.steam/steamapps/common/Skyrim Special Edition"
        });
    }

    // DI constructor
    public InstallViewModel(
        ModInstallationService installService,
        SteamGameScanner scanner,
        IDialogService dialogService,
        AppSettings settings,
        ModMetadataCache metadataCache,
        Modular.Core.Backends.BackendRegistry backends)
    {
        _backends = backends;
        _installService = installService;
        _scanner = scanner;
        _dialogService = dialogService;
        _settings = settings;
        _metadataCache = metadataCache;
    }

    partial void OnSelectedGameChanged(GameDisplayModel? value)
    {
        if (value != null)
        {
            TargetDirectory = value.InstallPath;
        }
    }

    /// <summary>
    /// Archive extensions to detect when scanning directories.
    /// </summary>
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2",
        ".xz", ".txz", ".lz", ".lzma", ".pak", ".zst", ".cab", ".dmg",
        ".iso", ".jar", ".war", ".apk", ".deb", ".rpm"
    };

    /// <summary>
    /// Extensions that are clearly not installable mod archives.
    /// Files with these extensions are excluded when scanning folders.
    /// </summary>
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".html", ".htm", ".pdf", ".doc", ".docx",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".log", ".csv", ".db", ".sqlite"
    };

    [RelayCommand]
    private async Task AddArchivesAsync()
    {
        if (_dialogService == null) return;

        var files = await _dialogService.ShowFileBrowserAsync(
            "Select Mod Archives",
            allowMultiple: true);

        foreach (var file in files)
        {
            if (!ArchivePaths.Contains(file))
            {
                ArchivePaths.Add(file);
            }
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_dialogService == null) return;

        var folder = await _dialogService.ShowFolderBrowserAsync("Select Folder Containing Mod Archives");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        var found = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            // Include files with known archive extensions, extensionless files
            // (collection downloads may use {modId}_{fileId} fallback names),
            // and any other file that isn't a known non-mod format
            var isArchive = ArchiveExtensions.Contains(ext);
            var isExcluded = ext.Length > 0 && ExcludedExtensions.Contains(ext);
            if (!isExcluded && (isArchive || ext.Length == 0) && !ArchivePaths.Contains(file))
            {
                ArchivePaths.Add(file);
                found++;
            }
        }

        StatusMessage = found > 0
            ? $"Added {found} archive(s) from {Path.GetFileName(folder)}"
            : $"No archives found in {Path.GetFileName(folder)}";
    }

    [RelayCommand]
    private void RemoveArchive(string path)
    {
        ArchivePaths.Remove(path);
    }

    [RelayCommand]
    private void ClearArchives()
    {
        ArchivePaths.Clear();
    }

    [RelayCommand]
    private async Task InstallCollectionAsync()
    {
        if (_dialogService == null) return;

        var repository = new ModCollectionRepository();
        var collections = await repository.ListAsync();

        if (collections.Count == 0)
        {
            StatusMessage = "No collections found. Create or download collections in the Collections tab first.";
            return;
        }

        var modsDir = _settings?.ModsDirectory;

        // Build display items and find archives for each collection
        var displayItems = new List<string>();
        var collectionArchives = new List<List<string>>();

        foreach (var collection in collections)
        {
            var archives = FindCollectionArchives(collection, modsDir);
            collectionArchives.Add(archives);
            var archiveInfo = archives.Count > 0
                ? $"{archives.Count} file(s) on disk"
                : "no files downloaded";
            displayItems.Add(
                $"{collection.Name}  [{collection.GameId}]  \u2014  {collection.Entries.Count} mod(s), {archiveInfo}");
        }

        var selectedIndex = await _dialogService.ShowListPickerAsync(
            "Install Collection",
            "Select a collection to load its downloaded archives:",
            displayItems);

        if (selectedIndex < 0 || selectedIndex >= collections.Count) return;

        var selected = collections[selectedIndex];
        var archivesToAdd = collectionArchives[selectedIndex];

        if (archivesToAdd.Count == 0)
        {
            StatusMessage = $"No downloaded files found for '{selected.Name}'. Download the collection first.";
            return;
        }

        var added = 0;
        foreach (var archive in archivesToAdd)
        {
            if (!ArchivePaths.Contains(archive))
            {
                ArchivePaths.Add(archive);
                added++;
            }
        }

        StatusMessage = $"Loaded {added} archive(s) from collection '{selected.Name}'";
    }

    private List<string> FindCollectionArchives(ModCollection collection, string? modsDir)
    {
        var archives = new List<string>();
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return archives;

        // Collections are NexusMods collections, downloaded under nexusmods/<game>.
        var gameDomainPath = ModLibraryLayout.GameFolder(modsDir, BackendIds.NexusMods, collection.GameId);
        if (!Directory.Exists(gameDomainPath)) return archives;

        // Build a modId → directory path map that includes both numeric
        // and renamed/categorized directories via the metadata cache
        var modDirMap = new Dictionary<string, string>();
        ModMetadata? MetadataLookup(string dirName) =>
            _metadataCache?.FindModByDirectoryName(collection.GameId, dirName);

        foreach (var (modId, dirPath, _) in FileUtils.GetAllModDirectoriesWithMetadata(
            gameDomainPath, MetadataLookup))
        {
            modDirMap[modId.ToString()] = dirPath;
        }

        // Also build a name → directory map for direct name matching fallback.
        // After renaming, directory names are SanitizeDirectoryName(modName),
        // so we can match collection entry names directly.
        var nameDirMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(gameDomainPath))
        {
            var dirName = Path.GetFileName(dir);
            if (!int.TryParse(dirName, out _))
            {
                // Top-level renamed mod directory
                nameDirMap[dirName] = dir;

                // Also index subdirectories (mods inside category folders)
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var subName = Path.GetFileName(subDir);
                    if (!int.TryParse(subName, out _))
                        nameDirMap[subName] = subDir;
                }
            }
        }

        foreach (var entry in collection.Entries)
        {
            string? modDir = null;

            // Try the metadata-aware map first (covers renamed & categorized paths)
            if (modDirMap.TryGetValue(entry.ModId, out var mapped))
                modDir = mapped;

            // Fallback: original numeric path
            if (modDir == null || !Directory.Exists(modDir))
            {
                var numericDir = Path.Combine(gameDomainPath, entry.ModId);
                if (Directory.Exists(numericDir))
                    modDir = numericDir;
            }

            // Fallback: match by sanitized entry name against directory names
            if (modDir == null || !Directory.Exists(modDir))
            {
                var sanitizedName = FileUtils.SanitizeDirectoryName(entry.Name);
                if (nameDirMap.TryGetValue(sanitizedName, out var nameMatched))
                    modDir = nameMatched;
            }

            if (modDir == null || !Directory.Exists(modDir)) continue;

            foreach (var file in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
            {
                archives.Add(file);
            }
        }

        return archives;
    }

    [RelayCommand]
    private async Task BrowseTargetAsync()
    {
        if (_dialogService == null) return;

        var folder = await _dialogService.ShowFolderBrowserAsync(
            "Select Target Directory",
            TargetDirectory);

        if (!string.IsNullOrEmpty(folder))
        {
            TargetDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task ScanGamesAsync()
    {
        if (_scanner == null)
        {
            StatusMessage = "Game scanner not available";
            return;
        }

        StatusMessage = "Scanning for games...";
        DetectedGames.Clear();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await _scanner.ScanAllAsync(cts.Token);

            foreach (var game in results.OrderBy(g => g.DisplayName))
            {
                DetectedGames.Add(new GameDisplayModel
                {
                    AppId = game.AppId,
                    DisplayName = game.DisplayName,
                    InstallPath = game.InstallPath,
                    IsFullyInstalled = game.IsFullyInstalled
                });
            }

            StatusMessage = $"Found {DetectedGames.Count} game(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallModsAsync()
    {
        if (_installService == null)
        {
            StatusMessage = "Installation service not available";
            return;
        }

        if (ArchivePaths.Count == 0)
        {
            StatusMessage = "Please add at least one archive";
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            StatusMessage = "Please specify a target directory";
            return;
        }

        // If multiple archives are queued, offer a selection dialog
        var pathsToInstall = ArchivePaths.ToList();
        if (pathsToInstall.Count > 1 && _dialogService != null)
        {
            var displayItems = pathsToInstall.Select(p =>
            {
                var name = Path.GetFileName(p);
                var dir = Path.GetFileName(Path.GetDirectoryName(p) ?? "");
                var size = File.Exists(p) ? new FileInfo(p).Length : 0;
                var sizeText = FormatFileSize(size);
                return $"{name}  ({sizeText})  [{dir}]";
            }).ToList();

            var selectedIndices = await _dialogService.ShowMultiSelectAsync(
                "Select Files to Install",
                $"{pathsToInstall.Count} file(s) queued. Choose which files to install:",
                displayItems);

            if (selectedIndices.Count == 0)
            {
                StatusMessage = "Installation cancelled";
                return;
            }

            pathsToInstall = selectedIndices.Select(idx => pathsToInstall[idx]).ToList();
        }

        // Several archives of one mod are usually versions of it, not parts: ask which.
        (pathsToInstall, var notChosen) = await ChooseArchivesPerModAsync(pathsToInstall);
        if (pathsToInstall.Count == 0)
        {
            StatusMessage = "Installation cancelled";
            return;
        }

        IsInstalling = true;
        IsAborting = false;
        InstallProgress = 0;
        InstallResults.Clear();
        if (notChosen.Count > 0)
            InstallResults.Add($"Not installed (no archive chosen): {string.Join(", ", notChosen)}");
        TotalArchives = pathsToInstall.Count;
        CurrentArchiveIndex = 0;

        var succeeded = 0;
        var failed = 0;
        var aborted = false;

        // One snapshot for the whole batch, taken at the end, instead of one per archive.
        var installed = new List<SnapshotBatchEntry>();

        using var cts = new CancellationTokenSource();
        _installCts = cts;

        try
        {
            for (var i = 0; i < pathsToInstall.Count; i++)
            {
                var archivePath = pathsToInstall[i];
                var fileName = Path.GetFileName(archivePath);
                CurrentArchiveIndex = i + 1;

                if (!File.Exists(archivePath))
                {
                    InstallResults.Add($"Skipped (not found): {fileName}");
                    failed++;
                    continue;
                }

                StatusMessage = DryRun
                    ? $"Dry-run {CurrentArchiveIndex}/{TotalArchives}: {fileName}"
                    : $"Installing {CurrentArchiveIndex}/{TotalArchives}: {fileName}";

                var options = new ModInstallationOptions
                {
                    ModId = Path.GetFileNameWithoutExtension(archivePath),
                    // The selected game's AppID scopes installer selection, so a
                    // game-specific installer is only considered for its own game.
                    GameId = SelectedGame?.AppId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    AllowOverwrite = AllowOverwrite,
                    CreateBackups = CreateBackups,
                    DryRun = DryRun,
                    AutoSnapshot = false,
                    ChoiceResolver = AskInstallChoicesAsync
                };

                var progress = new Progress<InstallProgress>(p =>
                {
                    // Scale progress across all archives
                    var archiveBase = (double)(i) / TotalArchives * 100;
                    var archiveSlice = p.Percentage / TotalArchives;
                    InstallProgress = archiveBase + archiveSlice;
                });

                var result = await _installService.InstallAsync(
                    archivePath, TargetDirectory, options, progress, cts.Token);

                // Mods installed before the abort stay installed; only the one in flight
                // and those still queued are dropped.
                if (cts.IsCancellationRequested && !result.Success)
                {
                    InstallResults.Add($"Aborted: {fileName}");
                    aborted = true;
                    break;
                }

                if (result.Success)
                {
                    if (result.DryRun)
                    {
                        InstallResults.Add($"[Dry run] {fileName}: {result.PlannedOperations.Count} operation(s)");
                    }
                    else if (result.Deployed)
                    {
                        InstallResults.Add(
                            $"Installed: {fileName} ({result.InstalledFiles.Count} files, {result.InstallerUsed})");
                    }
                    else
                    {
                        InstallResults.Add(
                            $"Staged: {fileName} ({result.StagedFiles.Count} files, {result.InstallerUsed})");
                    }

                    // Layout findings and launch requirements go into the same log the
                    // user is already watching; a mod that installs but cannot load is
                    // the failure mode worth surfacing here.
                    foreach (var diagnostic in result.Diagnostics)
                        InstallResults.Add($"! {diagnostic.Code}: {diagnostic.Message}");

                    if (!result.DryRun && result.ArchivePath != archivePath)
                    {
                        InstallResults.Add($"  Archive kept at: {result.ArchivePath}");

                        // The install moved the archive into the store, so point the list at
                        // its new home or the next run reports it as not found.
                        var listIndex = ArchivePaths.IndexOf(archivePath);
                        if (listIndex >= 0)
                        {
                            if (ArchivePaths.Contains(result.ArchivePath))
                                ArchivePaths.RemoveAt(listIndex);
                            else
                                ArchivePaths[listIndex] = result.ArchivePath;
                        }
                    }

                    foreach (var warning in result.Warnings)
                        InstallResults.Add($"! {warning}");

                    if (result.Deployment?.WineDllOverridesLaunchOption is { } overrideOption)
                    {
                        InstallResults.Add(
                            "! Steam launch options needed for Proton: " + overrideOption);
                    }

                    foreach (var argument in result.Deployment?.LaunchRequirements.LaunchArguments
                                             ?? Array.Empty<string>())
                    {
                        InstallResults.Add($"! Launch argument needed: {argument}");
                    }

                    if (!result.DryRun && result.Deployed)
                    {
                        installed.Add(new SnapshotBatchEntry(
                            TargetDirectory, result.GameKey, options.ModId ?? fileName));
                    }

                    succeeded++;
                }
                else
                {
                    InstallResults.Add($"Failed: {fileName} — {result.Error}");
                    failed++;
                }
            }

            aborted |= cts.IsCancellationRequested && CurrentArchiveIndex < TotalArchives;

            if (aborted)
            {
                var skipped = TotalArchives - succeeded - failed;
                StatusMessage =
                    $"Installation aborted: {succeeded} installed, {failed} failed, {skipped} not installed";
            }
            else
            {
                StatusMessage = DryRun
                    ? $"Dry run complete: {succeeded} previewed, {failed} failed"
                    : $"Batch complete: {succeeded} installed, {failed} failed";
            }

            // Only a batch that finished cleanly is a state worth restoring to.
            if (!aborted && failed == 0 && installed.Count > 0)
            {
                await _installService.TakeBatchSnapshotsAsync(
                    installed, SnapshotTrigger.AutoInstall);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Install Error", ex.Message);
            }
        }
        finally
        {
            _installCts = null;
            IsInstalling = false;
            IsAborting = false;
            InstallProgress = 100;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAbortInstall))]
    private void AbortInstall()
    {
        if (_installCts == null) return;

        IsAborting = true;
        StatusMessage = "Aborting installation...";
        _installCts.Cancel();
    }

    private bool CanAbortInstall() => !IsAborting;

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:F1} {suffixes[i]}";
    }
    /// <summary>
    /// Asks, for each mod with more than one archive in the list, which of them to install.
    /// </summary>
    /// <param name="paths">The archives to install.</param>
    /// <returns>
    /// The archives to install, in their original order, and the mods left out because
    /// nothing was chosen for them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A mod page often offers its versions as separate files, "Mod A - Light" beside
    /// "Mod A - Dark". Installed together, both versions load, or one silently replaces the
    /// other, so only one should go in. Add-ons offered as optional files can go with it.
    /// </para>
    /// <para>
    /// Which archives go together is read from the archives themselves: two that write the
    /// same files are alternatives, two that do not are parts of one mod (a body and a
    /// head). The newest of each set of alternatives is ticked to begin with, with every
    /// part that clashes with none of them, and ticking two that clash is marked in the
    /// dialog. When the mod's page says what each file is, main files and old versions are
    /// listed apart from add-ons. Cancelling leaves that mod out of the install.
    /// </para>
    /// </remarks>
    private async Task<(List<string> Paths, List<string> NotChosen)> ChooseArchivesPerModAsync(List<string> paths)
    {
        if (_dialogService == null || paths.Count < 2)
            return (paths, []);

        // Only mods with more than one archive have their page read; the rest pass straight through.
        if (ArchiveGrouper.Identify(paths, _settings?.ModsDirectory).All(g => g.Archives.Count < 2))
            return (paths, []);

        StatusMessage = "Checking which archives belong to the same mod...";

        IReadOnlyList<ArchiveGroup> groups;
        try
        {
            groups = await new ArchiveGrouper(_backends?.GetAll() ?? [], _settings?.ModsDirectory)
                .GroupAsync(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (paths, []);
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();

        foreach (var group in groups)
        {
            if (group.Archives.Count == 1)
            {
                keep.Add(group.Archives[0].Path);
                continue;
            }

            var picked = group.KnowsFileTypes
                ? await ChooseByFileTypeAsync(group)
                : await ChooseAnyAsync(group);

            if (picked.Count == 0)
                skipped.Add(group.Name);

            keep.UnionWith(picked);
        }

        return (paths.Where(keep.Contains).ToList(), skipped);
    }

    // The page said what each file is: main files (and old versions of them), plus any
    // add-ons.
    private async Task<List<string>> ChooseByFileTypeAsync(ArchiveGroup group)
    {
        var versions = group.Archives
            .Where(a => a.IsVersion)
            .OrderBy(a => IsMainFile(a) ? 0 : 1)
            .ThenByDescending(a => File.GetLastWriteTimeUtc(a.Path))
            .ToList();
        var addOns = group.Archives.Where(a => !a.IsVersion).ToList();

        // Main files are usually alternatives, but a mod can split itself across several
        // (a body and a head) that are all needed, and may offer some of those parts in
        // versions too. The archives tell which: alternatives write the same files, parts
        // of one mod write different ones. So the newest of each set of alternatives is
        // ticked, with every part that clashes with none of them, and the dialog marks any
        // two ticked files that would overwrite each other. Old versions are checked too:
        // each clashes with the main file it is a version of.
        var mains = Enumerable.Range(0, versions.Count).Where(i => IsMainFile(versions[i])).ToList();
        var clashes = versions.Count > 1
            ? ArchiveGrouper.FindClashes(versions.Select(a => a.Path).ToList(), new ArchiveReaderFactory())
            : [];
        var ticked = ArchiveGrouper.PickCompatible(mains, clashes);
        var mainClashes = clashes.Where(c => mains.Contains(c.First) && mains.Contains(c.Second)).ToList();

        var message = $"'{group.Name}' has {group.Archives.Count} archives queued. ";
        if (mains.Count > 1 && mainClashes.Count == 0)
            message += "Its main files install different files, so they are parts of one mod and are all ticked. ";
        else if (mains.Count > 1)
            message += $"Some of its main files write the same files, so they are alternatives ({DescribeClashes(versions, mainClashes)}). " +
                       (ticked.Count > 1
                           ? "The newest of each is ticked, with the parts that go alongside. "
                           : "The newest is ticked. ");
        message += "Old versions replace the main file they are a version of; add-ons go with it.";

        var choice = await _dialogService!.ShowFileChoiceAsync(
            "Choose Files",
            message,
            versions.Select(Describe).ToList(),
            addOns.Select(Describe).ToList(),
            ticked,
            clashes);

        if (choice == null)
            return [];

        var picked = choice.Value.Main
            .Where(i => i >= 0 && i < versions.Count)
            .Select(i => versions[i].Path)
            .ToList();
        picked.AddRange(choice.Value.Optional
            .Where(i => i >= 0 && i < addOns.Count)
            .Select(i => addOns[i].Path));

        return picked;
    }

    private static bool IsMainFile(QueuedArchive archive) =>
        string.Equals(archive.Category, "main", StringComparison.OrdinalIgnoreCase);

    // "A and B; C and D": the archives that write the same files, by the page's names.
    private static string DescribeClashes(
        List<QueuedArchive> archives, IEnumerable<(int First, int Second)> clashes) =>
        string.Join("; ", clashes.Select(c => $"{Name(archives[c.First])} and {Name(archives[c.Second])}"));

    private static string Name(QueuedArchive archive) =>
        string.IsNullOrWhiteSpace(archive.Label) ? Path.GetFileName(archive.Path) : archive.Label;

    // Nothing says what the files are: any may be ticked, the newest to begin with.
    private async Task<List<string>> ChooseAnyAsync(ArchiveGroup group)
    {
        var archives = group.Archives
            .OrderByDescending(a => File.GetLastWriteTimeUtc(a.Path))
            .ToList();

        // Archives that write different files are parts of the mod and go together; ones
        // that write the same files are versions, of which one is wanted. The newest of
        // each set of versions is ticked, with every part, and the dialog marks any two
        // ticked archives that would overwrite each other.
        var clashes = ArchiveGrouper.FindClashes(archives.Select(a => a.Path).ToList(), new ArchiveReaderFactory());

        var choice = await _dialogService!.ShowFileChoiceAsync(
            "Choose Archives",
            clashes.Count == 0
                ? $"'{group.Name}' has {archives.Count} archives queued. None of them write the same " +
                  "files, so they look like parts of one mod and are all ticked. Untick any you do not want."
                : $"'{group.Name}' has {archives.Count} archives queued, and some write the same files, " +
                  $"so they are versions of each other ({DescribeClashes(archives, clashes)}): installing " +
                  "both lets one replace the other. The newest of each is ticked, with any archives that " +
                  "go alongside it.",
            archives.Select(Describe).ToList(),
            [],
            ArchiveGrouper.PickCompatible(Enumerable.Range(0, archives.Count), clashes),
            clashes);

        var picked = choice?.Main ?? [];

        return picked
            .Where(i => i >= 0 && i < archives.Count)
            .Select(i => archives[i].Path)
            .ToList();
    }

    private static string Describe(QueuedArchive archive)
    {
        var name = Path.GetFileName(archive.Path);
        var size = File.Exists(archive.Path) ? FormatFileSize(new FileInfo(archive.Path).Length) : "missing";
        var label = !string.IsNullOrWhiteSpace(archive.Label) &&
                    !string.Equals(archive.Label, name, StringComparison.OrdinalIgnoreCase)
            ? $"{archive.Label} — {name}"
            : name;

        return archive.Category switch
        {
            null => $"{label}  ({size})",
            var category => $"{label}  ({size}, {category.Replace('_', ' ')})"
        };
    }

    // Asks which alternative to install when an archive offers several, such as the same
    // pak in an "Option A" and an "Option B" folder, or a FOMOD wizard's options. Each
    // choice's title is its whole question. Cancelling any question cancels the install,
    // since there is no telling which one was wanted.
    private async Task<IReadOnlyDictionary<string, string>?> AskInstallChoicesAsync(
        IReadOnlyList<InstallChoice> choices, CancellationToken ct)
    {
        if (_dialogService == null)
            return new Dictionary<string, string>();

        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var choice in choices.Where(c => !c.IsResolved))
        {
            ct.ThrowIfCancellationRequested();

            // The install runs off the UI thread once it starts doing I/O.
            var index = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                _dialogService.ShowListPickerAsync(
                    "Choose an Option",
                    choice.Title,
                    choice.Options.Select(o => $"{o.Label} ({o.FileCount} file(s))").ToList()));

            if (index < 0 || index >= choice.Options.Count)
                return null;

            answers[choice.Id] = choice.Options[index].Id;
        }

        return answers;
    }

}
