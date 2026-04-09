using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Collections;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Core.Utilities;
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
        ModMetadataCache metadataCache)
    {
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

        var gameDomainPath = Path.Combine(modsDir, collection.GameId);
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

        IsInstalling = true;
        InstallProgress = 0;
        InstallResults.Clear();
        TotalArchives = pathsToInstall.Count;
        CurrentArchiveIndex = 0;

        var succeeded = 0;
        var failed = 0;

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
                    AllowOverwrite = AllowOverwrite,
                    CreateBackups = CreateBackups,
                    DryRun = DryRun
                };

                var progress = new Progress<InstallProgress>(p =>
                {
                    // Scale progress across all archives
                    var archiveBase = (double)(i) / TotalArchives * 100;
                    var archiveSlice = p.Percentage / TotalArchives;
                    InstallProgress = archiveBase + archiveSlice;
                });

                var result = await _installService.InstallAsync(
                    archivePath, TargetDirectory, options, progress);

                if (result.Success)
                {
                    if (result.DryRun)
                    {
                        InstallResults.Add($"[Dry run] {fileName}: {result.PlannedOperations.Count} operation(s)");
                    }
                    else
                    {
                        InstallResults.Add($"Installed: {fileName} ({result.InstalledFiles.Count} files, {result.InstallerUsed})");
                    }
                    succeeded++;
                }
                else
                {
                    InstallResults.Add($"Failed: {fileName} — {result.Error}");
                    failed++;
                }
            }

            StatusMessage = DryRun
                ? $"Dry run complete: {succeeded} previewed, {failed} failed"
                : $"Batch complete: {succeeded} installed, {failed} failed";
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
            IsInstalling = false;
            InstallProgress = 100;
        }
    }

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
}
