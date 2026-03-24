using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Profiles;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly ProfileExporter? _profileExporter;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<ProfileDisplayModel> _profiles = new();

    [ObservableProperty]
    private ProfileDisplayModel? _selectedProfile;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // Designer constructor
    public ProfilesViewModel()
    {
        Profiles.Add(new ProfileDisplayModel
        {
            Name = "Default Profile",
            GameId = "skyrimse",
            ModCount = 42,
            CreatedAt = "2026-03-15"
        });
        Profiles.Add(new ProfileDisplayModel
        {
            Name = "Minimal Setup",
            GameId = "skyrimse",
            ModCount = 5,
            CreatedAt = "2026-03-18"
        });
    }

    // DI constructor
    public ProfilesViewModel(
        ProfileExporter profileExporter,
        IDialogService dialogService)
    {
        _profileExporter = profileExporter;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (_profileExporter == null || _dialogService == null) return;

        var outputPath = await _dialogService.ShowFolderBrowserAsync("Select Export Location");
        if (string.IsNullOrEmpty(outputPath)) return;

        IsLoading = true;
        StatusMessage = "Exporting profile...";

        try
        {
            var profile = new Core.Dependencies.ModProfile
            {
                Name = SelectedProfile?.Name ?? "Exported Profile"
            };
            var lockfile = new Core.Dependencies.ModLockfile();
            var exportFile = Path.Combine(outputPath, $"{profile.Name.Replace(' ', '_')}_profile.json");

            var result = await _profileExporter.ExportProfileAsync(profile, lockfile, exportFile);

            if (result.Success)
            {
                StatusMessage = $"Profile exported to {exportFile}";
            }
            else
            {
                StatusMessage = $"Export failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            await _dialogService.ShowErrorAsync("Export Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        if (_profileExporter == null || _dialogService == null) return;

        var inputPath = await _dialogService.ShowInputAsync(
            "Import Profile",
            "Enter the full path to the profile file:",
            "");

        if (string.IsNullOrEmpty(inputPath)) return;

        IsLoading = true;
        StatusMessage = "Importing profile...";

        try
        {
            var result = await _profileExporter.ImportProfileAsync(inputPath);

            if (result.Success)
            {
                StatusMessage = "Profile imported successfully";
                Profiles.Add(new ProfileDisplayModel
                {
                    Name = result.Profile?.Name ?? "Imported",
                    CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
                });
            }
            else
            {
                StatusMessage = $"Import failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            await _dialogService.ShowErrorAsync("Import Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public partial class ProfileDisplayModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _gameId = string.Empty;

    [ObservableProperty]
    private int _modCount;

    [ObservableProperty]
    private string _createdAt = string.Empty;
}
