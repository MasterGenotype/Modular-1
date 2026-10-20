using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Collections;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Backends;
using Modular.Core.Installers;
using Modular.Core.Services;
using Modular.Core.Utilities;
using Modular.Gui.Services;
using Modular.Sdk.Backends;

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
    private readonly BackendRegistry? _backends;
    private readonly ChangesetManager? _changesets;

    [ObservableProperty]
    private ObservableCollection<GameDomainItem> _gameDomains = new();

    /// <summary>
    /// The mods directory as a tree: backend folders, the game folders in each, and the
    /// mod folders in each game.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LibraryNode> _libraryTree = new();

    /// <summary>The node selected in the tree.</summary>
    [ObservableProperty]
    private LibraryNode? _selectedNode;

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

    /// <summary>Whether some downloads are still in a layout from before backend folders.</summary>
    [ObservableProperty]
    private bool _hasOldLayout;

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
        GameDomains.Add(new GameDomainItem { Name = "skyrimspecialedition", BackendId = BackendIds.NexusMods, ModCount = 42 });
        GameDomains.Add(new GameDomainItem { Name = "baldursgate3", BackendId = BackendIds.ModIo, ModCount = 15 });
        ModFolders.Add(new ModFolderItem { Name = "SkyUI", Path = "/mods/skyui", FileCount = 3 });
        BuildTree(_ => [new ModFolderItem { Name = "SkyUI", Path = "/mods/skyui", FileCount = 3 }]);
    }

    // DI constructor
    public LibraryViewModel(
        AppSettings settings,
        IRenameService renameService,
        IDialogService dialogService,
        DownloadDatabase downloadDatabase,
        ModMetadataCache metadataCache,
        ModCollectionRepository collectionRepository,
        BackendRegistry backends,
        ChangesetManager changesets)
    {
        _backends = backends;
        _changesets = changesets;
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
        if (string.IsNullOrEmpty(ModsDirectory)) return;

        GameDomains.Clear();
        try
        {
            // <mods>/<backend>/<game>
            foreach (var game in ModLibraryLayout.ListGames(ModsDirectory))
            {
                GameDomains.Add(new GameDomainItem
                {
                    Name = game.Game,
                    BackendId = game.BackendId,
                    Path = game.Path,
                    ModCount = Directory.GetDirectories(game.Path).Length
                });
            }

            // Downloads still in an old layout are listed too, so they can be browsed
            // until Re-organize moves them.
            var legacyFlat = ModLibraryMigrator.LegacyFlatFolders(_settings);

            foreach (var folder in ModLibraryMigrator.LegacyNexusFolders(ModsDirectory, legacyFlat))
            {
                GameDomains.Add(new GameDomainItem
                {
                    Name = Path.GetFileName(folder),
                    BackendId = BackendIds.NexusMods,
                    Path = folder,
                    ModCount = Directory.GetDirectories(folder).Length,
                    IsOldLayout = true
                });
            }

            foreach (var (folderName, backendId) in legacyFlat)
            {
                var mods = ModLibraryMigrator.LegacyModFolders(ModsDirectory, folderName).ToList();
                if (mods.Count == 0)
                    continue;

                GameDomains.Add(new GameDomainItem
                {
                    Name = folderName,
                    BackendId = backendId,
                    Path = Path.Combine(ModsDirectory, folderName),
                    ModCount = mods.Count,
                    IsOldLayout = true,
                    OnlyModFolders = mods
                });
            }

            HasOldLayout = GameDomains.Any(g => g.IsOldLayout);
            BuildTree(ListModFolders);
            StatusMessage = HasOldLayout
                ? $"Found {GameDomains.Count} game folders. Some are in the old layout: Re-organize moves them into <backend>/<game> folders."
                : $"Found {GameDomains.Count} game folders";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading domains: {ex.Message}";
        }
    }

    /// <summary>
    /// Builds the tree from <see cref="GameDomains"/>: one node per backend folder, holding
    /// its game folders, each holding its mod folders.
    /// </summary>
    /// <param name="modsOf">Lists a game's mod folders.</param>
    private void BuildTree(Func<GameDomainItem, List<ModFolderItem>> modsOf)
    {
        var tree = new ObservableCollection<LibraryNode>();

        foreach (var backend in GameDomains.GroupBy(g => g.BackendId).OrderBy(g => BackendOrder(g.Key)))
        {
            var games = backend.ToList();
            var backendNode = new LibraryNode
            {
                Name = backend.Key,
                Caption = $"{games[0].BackendName} · {games.Count} game(s)",
                Kind = LibraryNodeKind.Backend,
                IsExpanded = true
            };

            foreach (var game in games)
            {
                List<ModFolderItem> mods;
                try
                {
                    mods = modsOf(game);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    mods = [];
                }

                game.ModCount = mods.Count;

                var gameNode = new LibraryNode
                {
                    Name = game.Name,
                    Caption = game.IsOldLayout
                        ? $"{mods.Count} mod(s) · old layout, Re-organize to move"
                        : $"{mods.Count} mod(s)",
                    Kind = LibraryNodeKind.Game,
                    Game = game,
                    Parent = backendNode
                };

                foreach (var mod in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    gameNode.Children.Add(new LibraryNode
                    {
                        Name = mod.Name,
                        Caption = $"{mod.FileCount} file(s) · {mod.SizeText}",
                        Kind = LibraryNodeKind.Mod,
                        Game = game,
                        Mod = mod,
                        Parent = gameNode
                    });
                }

                backendNode.Children.Add(gameNode);
            }

            tree.Add(backendNode);
        }

        LibraryTree = tree;
    }

    // Backends in the order the mods directory lists them; anything else after.
    private static int BackendOrder(string backendId)
    {
        var index = ModLibraryLayout.Backends.ToList().IndexOf(backendId);
        return index < 0 ? int.MaxValue : index;
    }

    partial void OnSelectedNodeChanged(LibraryNode? value)
    {
        switch (value)
        {
            case { IsGame: true }:
                SelectedMod = null;
                SelectedDomain = value.Game;
                break;

            case { IsMod: true }:
                if (!ReferenceEquals(SelectedDomain, value.Game))
                    SelectedDomain = value.Game;
                SelectedMod = value.Mod;
                break;

            default:
                SelectedMod = null;
                SelectedDomain = null;
                break;
        }
    }

    // Selects a game's node, opening its backend, after the tree has been rebuilt.
    private void SelectGameNode(string backendId, string game)
    {
        var node = LibraryTree
            .Where(b => b.Name == backendId)
            .SelectMany(b => b.Children)
            .FirstOrDefault(g => string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase));

        if (node == null)
            return;

        if (node.Parent != null)
            node.Parent.IsExpanded = true;
        SelectedNode = node;
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
            foreach (var mod in ListModFolders(domain))
                ModFolders.Add(mod);

            StatusMessage = $"{ModFolders.Count} mods in {domain.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists the mod folders of a game folder.
    /// </summary>
    /// <param name="domain">The game folder.</param>
    /// <returns>The mods; empty when the folder is gone.</returns>
    private static List<ModFolderItem> ListModFolders(GameDomainItem domain)
    {
        var mods = new List<ModFolderItem>();

        if (string.IsNullOrEmpty(domain.Path) || !Directory.Exists(domain.Path))
            return mods;

        // An old flat folder also holds new game folders; only its mods belong here.
        if (domain.OnlyModFolders != null)
        {
            mods.AddRange(domain.OnlyModFolders.Where(Directory.Exists).Select(ModFolder));
            return mods;
        }

        foreach (var dir in Directory.GetDirectories(domain.Path))
        {
            var dirName = Path.GetFileName(dir);

            // A folder of mod folders with no archives of its own is a category folder from
            // before Re-organize took mods out of them; list the mods inside it.
            var subDirs = Directory.GetDirectories(dir);
            var hasTopLevelArchives = Directory.GetFiles(dir)
                .Any(f => IsModFile(Path.GetExtension(f)));

            if (subDirs.Length > 0 && !hasTopLevelArchives && !int.TryParse(dirName, out _))
                mods.AddRange(subDirs.Select(ModFolder));
            else
                mods.Add(ModFolder(dir));
        }

        return mods;
    }

    private static ModFolderItem ModFolder(string dir)
    {
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        return new ModFolderItem
        {
            Name = Path.GetFileName(dir),
            Path = dir,
            FileCount = files.Length,
            TotalSize = files.Sum(f => new FileInfo(f).Length)
        };
    }

    private static bool IsModFile(string extension)
    {
        return extension.Length > 0 && extension.ToLowerInvariant() switch
        {
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".pak" or ".xz" or ".bz2" => true,
            _ => false
        };
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
        // Mod metadata and download history are kept by NexusMods game domain.
        string? gameDomain = SelectedDomain?.BackendId == BackendIds.NexusMods ? SelectedDomain.Name : null;

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
        SelectedNode = null;
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
        if (_renameService == null || _dialogService == null || _settings == null || string.IsNullOrEmpty(ModsDirectory))
            return;

        var legacyFlat = ModLibraryMigrator.LegacyFlatFolders(_settings);
        var migrate = ModLibraryMigrator.NeedsMigration(ModsDirectory, legacyFlat);

        // Every NexusMods game's mods are renamed from their ids; the other hubs' folders
        // are named after the mod already. Mods sit straight in the game folder:
        // <backend>/<game>/<mod>/, with no category level.
        var selected = SelectedDomain;
        var nexusGames = GameDomains
            .Where(g => g.BackendId == BackendIds.NexusMods)
            .Select(g => g.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!migrate && nexusGames.Count == 0)
        {
            StatusMessage = "Every download is already in a <backend>/<game>/<mod> folder";
            return;
        }

        var steps = new List<string>();
        if (migrate)
            steps.Add("move downloads into <backend>/<game>/<mod> folders");
        if (nexusGames.Count > 0)
            steps.Add($"rename the NexusMods mods in {string.Join(", ", nexusGames)} after the mod and take them out of category folders");

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Re-organize Mods",
            $"This will {string.Join(", then ", steps)}. Installed mods are updated to follow their archives. Continue?");

        if (!confirmed) return;

        // Installed mods keep the path of the archive they came from; every move updates it.
        Func<string, string, CancellationToken, Task>? folderMoved = _changesets == null
            ? null
            : _changesets.RelocateArchiveFolderAsync;

        var summary = new List<string>();

        try
        {
            IsReorganizing = true;

            if (migrate)
            {
                StatusMessage = "Moving downloads into backend and game folders...";

                var lookup = _backends == null
                    ? null
                    : new BackendLibraryGameLookup(_backends.GetAll(), _settings);
                var result = await new ModLibraryMigrator(lookup, folderMoved)
                    .MigrateAsync(ModsDirectory, legacyFlat);

                summary.Add($"moved {result.Moved} folder(s)");
                if (result.Unresolved.Count > 0)
                    summary.Add($"{result.Unresolved.Count} mod(s) left in place because their game is unknown");
                if (result.Errors.Count > 0)
                {
                    summary.Add($"{result.Errors.Count} move(s) failed");
                    await _dialogService.ShowErrorAsync(
                        "Some folders could not be moved", string.Join("\n", result.Errors.Take(20)));
                }
            }

            var renamedTotal = 0;
            foreach (var nexusGame in _renameService.GetGameDomainNames().ToList())
            {
                // Read after the move, which may have just put the game under nexusmods/.
                var gameDomainPath = _renameService.GetGameDomainPath(nexusGame);
                if (!Directory.Exists(gameDomainPath))
                    continue;

                // Fetch and cache metadata for all mods first (same as download flow)
                StatusMessage = $"Fetching mod metadata for {nexusGame}...";
                await _renameService.FetchAndCacheMetadataAsync(gameDomainPath, nexusGame);

                StatusMessage = $"Renaming mods in {nexusGame}...";
                renamedTotal += await _renameService.ReorganizeAndRenameModsAsync(
                    gameDomainPath,
                    organizeByCategory: false,
                    folderMoved: folderMoved);
            }

            summary.Add(renamedTotal > 0
                ? $"renamed or moved {renamedTotal} NexusMods mod folder(s)"
                : "NexusMods mods already organized");

            StatusMessage = summary.Count > 0
                ? char.ToUpperInvariant(summary[0][0]) + string.Join("; ", summary)[1..]
                : "Nothing to re-organize";
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

        // Folders have moved: list them again and keep the same game selected.
        var message = StatusMessage;
        // An old flat folder is no game, so there is nothing to select again.
        var reselect = selected is { IsOldLayout: true } && selected.BackendId != BackendIds.NexusMods
            ? null
            : selected;

        RefreshLibrary();
        StatusMessage = message;

        if (reselect != null)
            SelectGameNode(reselect.BackendId, reselect.Name);

        // Selecting a game reports its mod count; the outcome matters more.
        StatusMessage = message;
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

                // Take it out of the tree too, and leave its game selected.
                var path = SelectedMod.Path;
                var node = LibraryTree
                    .SelectMany(b => b.Children)
                    .SelectMany(g => g.Children)
                    .FirstOrDefault(m => m.Mod?.Path == path);
                if (node?.Parent != null)
                {
                    node.Parent.Children.Remove(node);
                    SelectedNode = node.Parent;
                }

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
    /// <summary>The game folder's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The backend the mods were downloaded from.</summary>
    public string BackendId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
    public int ModCount { get; set; }

    /// <summary>Whether the folder is in a layout from before backend folders existed.</summary>
    public bool IsOldLayout { get; set; }

    /// <summary>
    /// The mod folders to list, when the folder holds other things too: an old flat
    /// GameBanana or mod.io folder also holds the new game folders.
    /// </summary>
    public IReadOnlyList<string>? OnlyModFolders { get; set; }

    /// <summary>The backend's name as its site writes it.</summary>
    public string BackendName => BackendId switch
    {
        BackendIds.NexusMods => "NexusMods",
        BackendIds.GameBanana => "GameBanana",
        BackendIds.ModIo => "mod.io",
        _ => BackendId
    };

    /// <summary>The line under the name: backend, and whether it still needs moving.</summary>
    public string Caption => IsOldLayout
        ? $"{BackendName} · old layout · {ModCount} mods"
        : $"{BackendName} · {ModCount} mods";

    /// <summary>The game and the backend it came from.</summary>
    public string DisplayName => $"{Name} ({BackendName})";

    public override string ToString() => $"{DisplayName} ({ModCount} mods)";
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
