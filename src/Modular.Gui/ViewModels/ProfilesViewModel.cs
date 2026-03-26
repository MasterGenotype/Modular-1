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

    // Create profile fields
    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _newProfileGameId = string.Empty;

    [ObservableProperty]
    private string _newProfileType = "Single Player";

    public string[] ProfileTypes { get; } = ["Single Player", "Multiplayer", "Custom"];

    // Designer constructor
    public ProfilesViewModel()
    {
        Profiles.Add(new ProfileDisplayModel
        {
            Name = "Default Profile",
            GameId = "skyrimse",
            ProfileType = "Single Player",
            ModCount = 42,
            CreatedAt = "2026-03-15"
        });
        Profiles.Add(new ProfileDisplayModel
        {
            Name = "Minimal Setup",
            GameId = "skyrimse",
            ProfileType = "Multiplayer",
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
    private void CreateProfile()
    {
        if (string.IsNullOrWhiteSpace(NewProfileName))
        {
            StatusMessage = "Enter a profile name";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewProfileGameId))
        {
            StatusMessage = "Enter a game ID for this profile";
            return;
        }

        var profile = new ProfileDisplayModel
        {
            Name = NewProfileName.Trim(),
            GameId = NewProfileGameId.Trim(),
            ProfileType = NewProfileType,
            ModCount = 0,
            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };

        Profiles.Add(profile);
        NewProfileName = string.Empty;
        NewProfileGameId = string.Empty;
        NewProfileType = "Single Player";
        StatusMessage = $"Created profile '{profile.Name}' for {profile.GameId} ({profile.ProfileType})";
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile == null)
        {
            StatusMessage = "Select a profile to delete";
            return;
        }

        if (_dialogService != null)
        {
            var confirmed = await _dialogService.ShowConfirmationAsync(
                "Delete Profile",
                $"Are you sure you want to delete the profile '{SelectedProfile.Name}'?");
            if (!confirmed) return;
        }

        var name = SelectedProfile.Name;
        Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        StatusMessage = $"Deleted profile '{name}'";
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

        var files = await _dialogService.ShowFileBrowserAsync(
            "Select Profile to Import",
            allowMultiple: false);

        if (files.Count == 0) return;

        var inputPath = files[0];

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
    private string _profileType = "Single Player";

    [ObservableProperty]
    private int _modCount;

    [ObservableProperty]
    private string _createdAt = string.Empty;
}
