using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Collections;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Services;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Library view (downloaded mods browser).
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly AppSettings? _settings;
    private readonly IRenameService? _renameService;
    private readonly IDialogService? _dialogService;
    private readonly DownloadDatabase? _downloadDatabase;
    private readonly ModMetadataCache? _metadataCache;
    private readonly ModCollectionRepository? _collectionRepository;

    [ObservableProperty]
    private ObservableCollection<GameDomainItem> _gameDomains = new();

    [ObservableProperty]
    private GameDomainItem? _selectedDomain;

    [ObservableProperty]
    private ObservableCollection<ModFolderItem> _modFolders = new();

    [ObservableProperty]
    private ModFolderItem? _selectedMod;

    [ObservableProperty]
    private ObservableCollection<string> _selectedModFiles = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isReorganizing;

    [ObservableProperty]
    private string _statusMessage = "Select a game to view downloaded mods";

    [ObservableProperty]
    private string _modsDirectory = string.Empty;

    [ObservableProperty]
    private string _modLocation = string.Empty;

    [ObservableProperty]
    private string _modDownloadDate = string.Empty;

    [ObservableProperty]
    private string _modArchiveTypes = string.Empty;

    [ObservableProperty]
    private string _modDescription = string.Empty;

    [ObservableProperty]
    private string? _modCollectionName;

    [ObservableProperty]
    private ObservableCollection<FileTreeItem> _fileTree = new();

    // Designer constructor
    public LibraryViewModel()
    {
        ModsDirectory = "~/Games/Mods-Lists";
        GameDomains.Add(new GameDomainItem { Name = "skyrimspecialedition", ModCount = 42 });
        GameDomains.Add(new GameDomainItem { Name = "fallout4", ModCount = 15 });
        ModFolders.Add(new ModFolderItem { Name = "SkyUI", Path = "/mods/skyui", FileCount = 3 });
    }

    // DI constructor
    public LibraryViewModel(
        AppSettings settings,
        IRenameService renameService,
        IDialogService dialogService,
        DownloadDatabase downloadDatabase,
        ModMetadataCache metadataCache,
        ModCollectionRepository collectionRepository)
    {
        _settings = settings;
        _renameService = renameService;
        _dialogService = dialogService;
        _downloadDatabase = downloadDatabase;
        _metadataCache = metadataCache;
        _collectionRepository = collectionRepository;

        ModsDirectory = settings.ModsDirectory ?? string.Empty;
        LoadGameDomains();
    }

    private void LoadGameDomains()
    {
        if (_renameService == null || string.IsNullOrEmpty(ModsDirectory)) return;

        GameDomains.Clear();
        try
        {
            var domains = _renameService.GetGameDomainNames();
            foreach (var domain in domains)
            {
                var domainPath = Path.Combine(ModsDirectory, domain);
                var modCount = Directory.Exists(domainPath)
                    ? Directory.GetDirectories(domainPath).Length
                    : 0;

                GameDomains.Add(new GameDomainItem
                {
                    Name = domain,
                    Path = domainPath,
                    ModCount = modCount
                });
            }

            StatusMessage = $"Found {GameDomains.Count} game domains";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading domains: {ex.Message}";
        }
    }

    partial void OnSelectedDomainChanged(GameDomainItem? value)
    {
        if (value != null)
        {
            LoadModFolders(value);
        }
    }

    partial void OnSelectedModChanged(ModFolderItem? value)
    {
        if (value != null)
        {
            LoadModFiles(value);
            PopulateModMetadata(value);
        }
        else
        {
            ClearModMetadata();
        }
    }

    private void LoadModFolders(GameDomainItem domain)
    {
        ModFolders.Clear();
        SelectedModFiles.Clear();
        FileTree.Clear();
        ClearModMetadata();

        if (string.IsNullOrEmpty(domain.Path) || !Directory.Exists(domain.Path))
        {
            StatusMessage = "Directory not found";
            return;
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(domain.Path))
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                ModFolders.Add(new ModFolderItem
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    FileCount = files.Length,
                    TotalSize = files.Sum(f => new FileInfo(f).Length)
                });
            }

            StatusMessage = $"{ModFolders.Count} mods in {domain.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private void LoadModFiles(ModFolderItem mod)
    {
        SelectedModFiles.Clear();
        FileTree.Clear();

        if (string.IsNullOrEmpty(mod.Path) || !Directory.Exists(mod.Path)) return;

        try
        {
            var rootItems = new ObservableCollection<FileTreeItem>();
            BuildFileTree(mod.Path, rootItems, 0);
            FileTree = rootItems;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading files: {ex.Message}";
        }
    }

    private static void BuildFileTree(string directoryPath, ObservableCollection<FileTreeItem> items, int depth)
    {
        // Add subdirectories
        foreach (var dir in Directory.GetDirectories(directoryPath).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var dirItem = new FileTreeItem
            {
                Name = Path.GetFileName(dir),
                FullPath = dir,
                IsDirectory = true,
                Depth = depth
            };

            BuildFileTree(dir, dirItem.Children, depth + 1);
            items.Add(dirItem);
        }

        // Add files
        foreach (var file in Directory.GetFiles(directoryPath).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new FileTreeItem
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                IsDirectory = false,
                Depth = depth
            });
        }
    }

    private void PopulateModMetadata(ModFolderItem mod)
    {
        // Location
        ModLocation = mod.Path;

        // Try to extract mod ID from folder name
        var folderName = mod.Name;
        int? modId = null;
        string? gameDomain = SelectedDomain?.Name;

        if (int.TryParse(folderName, out var numericId))
        {
            modId = numericId;
        }
        else if (_metadataCache != null && gameDomain != null)
        {
            var metadata = _metadataCache.FindModByDirectoryName(gameDomain, folderName);
            if (metadata != null)
            {
                modId = metadata.ModId;
            }
        }

        // Download date from DownloadDatabase
        ModDownloadDate = string.Empty;
        if (_downloadDatabase != null && gameDomain != null && modId.HasValue)
        {
            var records = _downloadDatabase.GetRecordsByMod(gameDomain, modId.Value);
            var latestRecord = records.OrderByDescending(r => r.DownloadTime).FirstOrDefault();
            if (latestRecord != null)
            {
                ModDownloadDate = latestRecord.DownloadTime.ToString("yyyy-MM-dd HH:mm");
            }
        }

        // Mod description from metadata cache
        ModDescription = string.Empty;
        if (_metadataCache != null && gameDomain != null && modId.HasValue)
        {
            var metadata = _metadataCache.GetModMetadata(gameDomain, modId.Value);
            if (metadata != null)
            {
                ModDescription = metadata.Name;
            }
        }

        // Archive types - scan for archive file extensions
        ModArchiveTypes = string.Empty;
        if (Directory.Exists(mod.Path))
        {
            var archiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".fomod"
            };
            var foundExtensions = Directory.GetFiles(mod.Path, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetExtension(f).ToLowerInvariant())
                .Where(ext => archiveExtensions.Contains(ext))
                .Distinct()
                .OrderBy(ext => ext)
                .ToList();

            if (foundExtensions.Count > 0)
            {
                ModArchiveTypes = string.Join(", ", foundExtensions);
            }
        }

        // Collection name
        ModCollectionName = null;
        if (_collectionRepository != null && modId.HasValue)
        {
            _ = PopulateCollectionNameAsync(modId.Value);
        }
    }

    private async Task PopulateCollectionNameAsync(int modId)
    {
        try
        {
            if (_collectionRepository == null) return;

            var collections = await _collectionRepository.ListAsync();
            var modIdStr = modId.ToString();
            foreach (var collection in collections)
            {
                if (collection.Entries.Any(e => e.ModId == modIdStr))
                {
                    ModCollectionName = collection.Name;
                    return;
                }
            }
        }
        catch
        {
            // Ignore collection lookup failures
        }
    }

    private void ClearModMetadata()
    {
        ModLocation = string.Empty;
        ModDownloadDate = string.Empty;
        ModArchiveTypes = string.Empty;
        ModDescription = string.Empty;
        ModCollectionName = null;
        FileTree.Clear();
    }

    [RelayCommand]
    private void RefreshLibrary()
    {
        LoadGameDomains();
        ModFolders.Clear();
        SelectedModFiles.Clear();
        FileTree.Clear();
        ClearModMetadata();
        SelectedDomain = null;
        SelectedMod = null;
    }

    [RelayCommand]
    private void OpenModFolder()
    {
        if (SelectedMod == null || string.IsNullOrEmpty(SelectedMod.Path)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedMod.Path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogService?.ShowErrorAsync("Error", $"Could not open folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReorganizeModsAsync()
    {
        if (SelectedDomain == null || _renameService == null || _dialogService == null || _settings == null) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Re-organize Mods",
            $"This will reorganize mods in '{SelectedDomain.Name}' into category folders and rename them. Continue?");

        if (!confirmed) return;

        var gameDomain = SelectedDomain.Name;
        var gameDomainPath = SelectedDomain.Path;

        try
        {
            IsReorganizing = true;

            // Fetch and cache metadata for all mods first (same as download flow)
            StatusMessage = $"Fetching mod metadata for {gameDomain}...";
            await _renameService.FetchAndCacheMetadataAsync(gameDomainPath, gameDomain);

            // Reorganize and rename mods (matches download flow)
            StatusMessage = $"Reorganizing mods in {gameDomain}...";
            var renamed = await _renameService.ReorganizeAndRenameModsAsync(
                gameDomainPath,
                _settings.OrganizeByCategory);

            // Rename category folders from Category_N to actual names
            if (_settings.OrganizeByCategory)
            {
                await _renameService.RenameCategoryFoldersAsync(gameDomainPath);
            }

            StatusMessage = renamed > 0
                ? $"Organized {renamed} mod(s) in {gameDomain}"
                : $"Mods in {gameDomain} already organized";

            // Refresh the mod list to reflect the new structure
            LoadModFolders(SelectedDomain);

            // Update the mod count on the domain item
            if (Directory.Exists(gameDomainPath))
            {
                SelectedDomain.ModCount = Directory.GetDirectories(gameDomainPath).Length;
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Error", $"Failed to reorganize mods: {ex.Message}");
            StatusMessage = $"Reorganization failed: {ex.Message}";
        }
        finally
        {
            IsReorganizing = false;
        }
    }

    [RelayCommand]
    private void OpenModsDirectory()
    {
        if (string.IsNullOrEmpty(ModsDirectory)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ModsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogService?.ShowErrorAsync("Error", $"Could not open folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedModAsync()
    {
        if (SelectedMod == null || _dialogService == null) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Delete Mod",
            $"Are you sure you want to delete '{SelectedMod.Name}'? This cannot be undone.");

        if (!confirmed) return;

        try
        {
            if (Directory.Exists(SelectedMod.Path))
            {
                Directory.Delete(SelectedMod.Path, true);
                ModFolders.Remove(SelectedMod);
                SelectedMod = null;
                SelectedModFiles.Clear();
                FileTree.Clear();
                ClearModMetadata();
                StatusMessage = "Mod deleted successfully";
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Error", $"Failed to delete mod: {ex.Message}");
        }
    }
}

/// <summary>
/// Represents a game domain directory in the library.
/// </summary>
public class GameDomainItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int ModCount { get; set; }

    public override string ToString() => $"{Name} ({ModCount} mods)";
}

/// <summary>
/// Represents a mod folder in the library.
/// </summary>
public class ModFolderItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSize { get; set; }

    public string SizeText => FormatSize(TotalSize);

    private static string FormatSize(long bytes)
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
