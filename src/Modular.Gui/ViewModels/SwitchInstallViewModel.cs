using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Gui.Services;
using Modular.Switch.Installer;
using Modular.Switch.Models;
using Modular.Switch.Scanner;

namespace Modular.Gui.ViewModels;

public partial class SwitchInstallViewModel : ViewModelBase
{
    private readonly SwitchModScanner? _scanner;
    private readonly SwitchModInstaller? _installer;
    private readonly IDialogService? _dialogService;
    private SwitchInstallState? _state;

    [ObservableProperty]
    private string _yuzuDataRoot = string.Empty;

    [ObservableProperty]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SwitchModDisplayModel> _discoveredMods = new();

    [ObservableProperty]
    private ObservableCollection<SwitchModDisplayModel> _installedMods = new();

    [ObservableProperty]
    private ObservableCollection<string> _installResults = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _statusMessage = "Configure Yuzu path and select a folder to scan";

    // Designer constructor
    public SwitchInstallViewModel()
    {
        YuzuDataRoot = "~/.local/share/yuzu";
        DiscoveredMods.Add(new SwitchModDisplayModel
        {
            Name = "60 FPS Mod",
            TitleId = "0100F2C0115B6000",
            Category = "RomFs",
            Version = "1.0.0",
            SourcePath = "/home/user/switch-mods/60fps.zip"
        });
        InstalledMods.Add(new SwitchModDisplayModel
        {
            Name = "HD Textures",
            TitleId = "0100F2C0115B6000",
            Category = "RomFs",
            Version = "2.1.0",
            SourcePath = "/home/user/switch-mods/hd-tex.zip",
            IsInstalled = true
        });
    }

    // DI constructor
    public SwitchInstallViewModel(
        SwitchModScanner scanner,
        SwitchModInstaller installer,
        IDialogService dialogService)
    {
        _scanner = scanner;
        _installer = installer;
        _dialogService = dialogService;

        YuzuDataRoot = YuzuPaths.DataRoot;
        _ = LoadStateAsync();
    }

    partial void OnYuzuDataRootChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            YuzuPaths.SetCustomDataRoot(value);
    }

    private async Task LoadStateAsync()
    {
        _state = await SwitchInstallState.LoadAsync();
        RefreshInstalledList();
    }

    private void RefreshInstalledList()
    {
        InstalledMods.Clear();
        if (_state == null) return;

        foreach (var mod in _state.Installed.OrderBy(m => m.Name))
        {
            InstalledMods.Add(new SwitchModDisplayModel
            {
                Name = mod.Name,
                TitleId = mod.TitleId,
                Category = mod.Category.ToString(),
                Version = mod.Version,
                SourcePath = mod.SourcePath,
                IsInstalled = true,
                ModKey = mod.ModKey
            });
        }
    }

    [RelayCommand]
    private async Task BrowseYuzuPathAsync()
    {
        if (_dialogService == null) return;

        var folder = await _dialogService.ShowFolderBrowserAsync(
            "Select Yuzu Data Directory",
            YuzuDataRoot);

        if (!string.IsNullOrEmpty(folder))
            YuzuDataRoot = folder;
    }

    [RelayCommand]
    private async Task BrowseSourceFolderAsync()
    {
        if (_dialogService == null) return;

        var folder = await _dialogService.ShowFolderBrowserAsync(
            "Select Folder Containing Switch Mod Archives",
            SourceFolder);

        if (!string.IsNullOrEmpty(folder))
            SourceFolder = folder;
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        if (_scanner == null)
        {
            StatusMessage = "Scanner not available";
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder))
        {
            StatusMessage = "Please select a valid source folder";
            return;
        }

        IsScanning = true;
        DiscoveredMods.Clear();
        StatusMessage = "Scanning for Switch mods...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var mods = await _scanner.ScanAsync(SourceFolder, progress: progress, ct: cts.Token);

            foreach (var mod in mods.OrderBy(m => m.Name))
            {
                var isInstalled = _state?.TryGet(mod.ModKey, out var existing) == true
                                  && existing.IsInstalled;
                DiscoveredMods.Add(new SwitchModDisplayModel
                {
                    Name = mod.Name,
                    TitleId = mod.TitleId,
                    Category = mod.Category.ToString(),
                    Version = mod.Version,
                    SourcePath = mod.SourcePath,
                    IsInstalled = isInstalled,
                    ModKey = mod.ModKey,
                    IsSelected = !isInstalled
                });
            }

            StatusMessage = mods.Count > 0
                ? $"Found {mods.Count} Switch mod(s)"
                : "No Switch mods found in folder";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan timed out";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        if (_installer == null || _scanner == null)
        {
            StatusMessage = "Installer not available";
            return;
        }

        var selected = DiscoveredMods.Where(m => m.IsSelected && !m.IsInstalled).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No mods selected for installation";
            return;
        }

        if (string.IsNullOrWhiteSpace(YuzuDataRoot))
        {
            StatusMessage = "Please configure the Yuzu data directory";
            return;
        }

        IsInstalling = true;
        InstallProgress = 0;
        InstallResults.Clear();
        var succeeded = 0;
        var failed = 0;

        try
        {
            // Re-scan to get full SwitchMod objects for selected items
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var allMods = await _scanner.ScanAsync(SourceFolder, ct: cts.Token);
            var modsByKey = allMods.ToDictionary(m => m.ModKey, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < selected.Count; i++)
            {
                var display = selected[i];
                StatusMessage = $"Installing {i + 1}/{selected.Count}: {display.Name}";
                InstallProgress = (double)i / selected.Count * 100;

                if (!modsByKey.TryGetValue(display.ModKey, out var mod))
                {
                    InstallResults.Add($"Skipped: {display.Name} (not found in scan)");
                    failed++;
                    continue;
                }

                var progress = new Progress<SwitchInstallProgress>(p =>
                {
                    var baseProgress = (double)i / selected.Count * 100;
                    var sliceProgress = p.TotalFiles > 0
                        ? (double)p.FilesProcessed / p.TotalFiles / selected.Count * 100
                        : 0;
                    InstallProgress = baseProgress + sliceProgress;
                });

                var result = await _installer.InstallAsync(mod, progress: progress, ct: cts.Token);

                if (result.Success)
                {
                    mod.IsInstalled = true;
                    mod.InstalledAt = DateTime.UtcNow;
                    mod.InstalledHash = mod.SourceHash;
                    _state?.Upsert(mod);
                    display.IsInstalled = true;
                    display.IsSelected = false;
                    InstallResults.Add($"Installed: {display.Name}");
                    succeeded++;
                }
                else if (result.Skipped)
                {
                    InstallResults.Add($"Skipped: {display.Name} ({result.SkipReason})");
                    display.IsInstalled = true;
                    display.IsSelected = false;
                }
                else
                {
                    InstallResults.Add($"Failed: {display.Name} -- {result.Error}");
                    failed++;
                }
            }

            if (_state != null)
                await _state.SaveAsync();

            RefreshInstalledList();
            StatusMessage = $"Done: {succeeded} installed, {failed} failed";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Installation cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Install error: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            InstallProgress = 100;
        }
    }

    [RelayCommand]
    private async Task RemoveModAsync(SwitchModDisplayModel? display)
    {
        if (_installer == null || _dialogService == null || _state == null || display == null)
            return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Remove Switch Mod",
            $"Remove '{display.Name}' ({display.TitleId})?\n\nThis will delete the mod from Yuzu's load directory.");

        if (!confirmed) return;

        if (!_state.TryGet(display.ModKey, out var mod))
        {
            StatusMessage = $"Mod '{display.Name}' not found in state";
            return;
        }

        StatusMessage = $"Removing {display.Name}...";

        var result = await _installer.RemoveAsync(mod);

        if (result.Success)
        {
            mod.IsInstalled = false;
            mod.InstalledAt = null;
            mod.InstalledHash = string.Empty;
            _state.Upsert(mod);
            await _state.SaveAsync();
            RefreshInstalledList();
            StatusMessage = $"Removed {display.Name}";
        }
        else
        {
            StatusMessage = $"Remove failed: {result.Error}";
        }
    }

    [RelayCommand]
    private async Task RefreshInstalledAsync()
    {
        _state = await SwitchInstallState.LoadAsync();
        RefreshInstalledList();
        StatusMessage = $"{InstalledMods.Count} installed Switch mod(s)";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var mod in DiscoveredMods.Where(m => !m.IsInstalled))
            mod.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var mod in DiscoveredMods)
            mod.IsSelected = false;
    }
}

public partial class SwitchModDisplayModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _titleId = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _modKey = string.Empty;
}
