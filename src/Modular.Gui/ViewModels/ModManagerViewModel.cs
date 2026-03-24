using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Configuration;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Combined ViewModel wrapping Install and Installed Mods as sub-tabs.
/// Also scans the mods directory for installable archives.
/// </summary>
public partial class ModManagerViewModel : ViewModelBase
{
    public InstallViewModel? InstallViewModel { get; }
    public InstalledModsViewModel? InstalledModsViewModel { get; }

    private readonly AppSettings? _settings;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private ObservableCollection<string> _detectedArchives = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // Designer constructor
    public ModManagerViewModel()
    {
        InstallViewModel = new InstallViewModel();
        InstalledModsViewModel = new InstalledModsViewModel();
        DetectedArchives.Add("/home/user/Mods/skyrim/sample-mod.zip");
    }

    // DI constructor
    public ModManagerViewModel(
        InstallViewModel installViewModel,
        InstalledModsViewModel installedModsViewModel,
        AppSettings settings)
    {
        InstallViewModel = installViewModel;
        InstalledModsViewModel = installedModsViewModel;
        _settings = settings;
        ScanForArchives();
    }

    [RelayCommand]
    private void ScanForArchives()
    {
        DetectedArchives.Clear();
        var modsDir = _settings?.ModsDirectory;
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
        {
            StatusMessage = "Mods directory not configured or doesn't exist";
            return;
        }

        var archiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".zip", ".7z", ".rar", ".tar.gz", ".tar" };

        try
        {
            var files = Directory.GetFiles(modsDir, "*", SearchOption.AllDirectories)
                .Where(f => archiveExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files)
                DetectedArchives.Add(file);

            StatusMessage = files.Count > 0
                ? $"Found {files.Count} archive(s) in {modsDir}"
                : "No archives found in mods directory";

            // Auto-populate the Install VM's archive list if empty
            if (InstallViewModel != null && InstallViewModel.ArchivePaths.Count == 0)
            {
                foreach (var archive in files.Take(50)) // Limit to avoid overwhelming
                    InstallViewModel.ArchivePaths.Add(archive);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning: {ex.Message}";
        }
    }
}
