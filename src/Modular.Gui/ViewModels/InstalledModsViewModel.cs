using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Installers;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

public partial class InstalledModsViewModel : ViewModelBase
{
    private readonly ModInstallationService? _installService;
    private readonly IDialogService? _dialogService;

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
        IDialogService dialogService)
    {
        _installService = installService;
        _dialogService = dialogService;

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

            foreach (var record in installed)
            {
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
