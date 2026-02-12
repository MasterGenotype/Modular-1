using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Configuration;
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
    private string _statusMessage = "Select a game to view downloaded mods";

    [ObservableProperty]
    private string _modsDirectory = string.Empty;

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
        IDialogService dialogService)
    {
        _settings = settings;
        _renameService = renameService;
        _dialogService = dialogService;

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
        }
    }

    private void LoadModFolders(GameDomainItem domain)
    {
        ModFolders.Clear();
        SelectedModFiles.Clear();

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

        if (string.IsNullOrEmpty(mod.Path) || !Directory.Exists(mod.Path)) return;

        try
        {
            foreach (var file in Directory.GetFiles(mod.Path, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(mod.Path, file);
                SelectedModFiles.Add(relativePath);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading files: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshLibrary()
    {
        LoadGameDomains();
        ModFolders.Clear();
        SelectedModFiles.Clear();
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
