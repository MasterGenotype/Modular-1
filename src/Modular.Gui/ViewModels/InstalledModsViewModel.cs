using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Installed Mods view — shows all tracked installations
/// with verify, uninstall, and rollback actions.
/// </summary>
public partial class InstalledModsViewModel : ViewModelBase
{
    private readonly InstallationTracker? _tracker;
    private readonly UninstallService? _uninstaller;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<InstalledModItem> _installedMods = new();

    [ObservableProperty]
    private InstalledModItem? _selectedMod;

    [ObservableProperty]
    private ObservableCollection<string> _selectedModFiles = new();

    [ObservableProperty]
    private string _statusMessage = "Loading installed mods...";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _filterDomain;

    // Designer constructor
    public InstalledModsViewModel()
    {
        InstalledMods.Add(new InstalledModItem
        {
            ModId = "skyui",
            ModName = "SkyUI",
            Version = "5.2",
            GameDomain = "skyrimspecialedition",
            FileCount = 12,
            InstalledAt = "2026-03-20"
        });
    }

    // DI constructor
    public InstalledModsViewModel(
        InstallationTracker tracker,
        UninstallService uninstaller,
        IDialogService dialogService)
    {
        _tracker = tracker;
        _uninstaller = uninstaller;
        _dialogService = dialogService;
        LoadInstalledMods();
    }

    private async void LoadInstalledMods()
    {
        if (_tracker == null) return;

        IsLoading = true;
        try
        {
            InstalledMods.Clear();
            var mods = await _tracker.GetInstalledModsAsync(FilterDomain);

            foreach (var mod in mods)
            {
                InstalledMods.Add(new InstalledModItem
                {
                    ModId = mod.ModId,
                    ModName = mod.ModName ?? mod.ModId,
                    Version = mod.Version ?? "-",
                    GameDomain = mod.GameDomain ?? "-",
                    FileCount = mod.InstalledFiles.Count,
                    BackupCount = mod.BackupFiles.Count,
                    InstallerId = mod.InstallerId ?? "-",
                    InstalledAt = mod.InstalledAtUtc.Length >= 10 ? mod.InstalledAtUtc[..10] : mod.InstalledAtUtc,
                    Record = mod
                });
            }

            StatusMessage = $"{InstalledMods.Count} mod(s) installed";
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

    partial void OnSelectedModChanged(InstalledModItem? value)
    {
        SelectedModFiles.Clear();
        if (value?.Record != null)
        {
            foreach (var file in value.Record.InstalledFiles)
                SelectedModFiles.Add(file);
        }
    }

    [RelayCommand]
    private void RefreshList()
    {
        LoadInstalledMods();
    }

    [RelayCommand]
    private async Task VerifySelectedAsync()
    {
        if (SelectedMod?.Record == null || _tracker == null || _dialogService == null) return;

        var result = await _tracker.VerifyInstallationAsync(SelectedMod.ModId);
        if (result.IsValid)
        {
            await _dialogService.ShowErrorAsync("Verification",
                $"All {result.ValidFiles.Count} files verified successfully.");
        }
        else
        {
            await _dialogService.ShowErrorAsync("Verification Failed",
                $"{result.MissingFiles.Count} file(s) missing:\n" +
                string.Join("\n", result.MissingFiles.Take(10)));
        }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (SelectedMod == null || _uninstaller == null || _dialogService == null) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Uninstall Mod",
            $"Are you sure you want to uninstall '{SelectedMod.ModName}'?\n" +
            $"This will remove {SelectedMod.FileCount} files and restore {SelectedMod.BackupCount} backups.");

        if (!confirmed) return;

        IsLoading = true;
        StatusMessage = $"Uninstalling {SelectedMod.ModName}...";

        try
        {
            var result = await _uninstaller.UninstallAsync(SelectedMod.ModId, restoreBackups: true);
            if (result.Success)
            {
                StatusMessage = $"Uninstalled '{SelectedMod.ModName}': {result.RemovedFiles.Count} removed, {result.RestoredFiles.Count} restored.";
                LoadInstalledMods();
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
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>
/// Display item for an installed mod.
/// </summary>
public class InstalledModItem
{
    public string ModId { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string Version { get; set; } = "-";
    public string GameDomain { get; set; } = "-";
    public int FileCount { get; set; }
    public int BackupCount { get; set; }
    public string InstallerId { get; set; } = "-";
    public string InstalledAt { get; set; } = string.Empty;
    public InstalledModRecord? Record { get; set; }
}
