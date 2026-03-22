using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Installers;
using Modular.Gui.Services;
using Modular.Sdk.Installers;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Staging Preview view — shows the two-phase install pipeline:
/// staged files ready for commit, with preview of what will change.
/// </summary>
public partial class StagingPreviewViewModel : ViewModelBase
{
    private readonly InstallerManager? _installerManager;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<StagedFileItem> _stagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<FileOperationItem> _plannedOperations = new();

    [ObservableProperty]
    private string _archivePath = string.Empty;

    [ObservableProperty]
    private string _gameDirectory = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Select an archive and game directory to preview installation";

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private string _selectedInstaller = string.Empty;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private bool _requiresUserInput;

    private InstallPlan? _currentPlan;

    // Designer constructor
    public StagingPreviewViewModel()
    {
        PlannedOperations.Add(new FileOperationItem
        {
            Type = "Extract",
            Source = "Data/Scripts/MyMod.dll",
            Destination = "/game/Data/Scripts/MyMod.dll",
            Size = "2.3 MB"
        });
        HasPlan = true;
        FileCount = 1;
        TotalBytes = 2_400_000;
    }

    // DI constructor
    public StagingPreviewViewModel(
        InstallerManager installerManager,
        IDialogService dialogService)
    {
        _installerManager = installerManager;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task AnalyzeArchiveAsync()
    {
        if (_installerManager == null) return;

        if (string.IsNullOrWhiteSpace(ArchivePath) || !File.Exists(ArchivePath))
        {
            StatusMessage = "Please enter a valid archive path";
            return;
        }

        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
        {
            StatusMessage = "Please enter a valid game directory";
            return;
        }

        IsAnalyzing = true;
        PlannedOperations.Clear();
        StagedFiles.Clear();
        HasPlan = false;

        try
        {
            var context = new InstallContext
            {
                GameDirectory = GameDirectory,
                CreateBackups = true,
                ConflictPolicy = ConflictPolicy.FailOnConflict
            };

            // Select installer.
            var selection = await _installerManager.SelectInstallerAsync(ArchivePath);
            if (selection == null)
            {
                StatusMessage = "No suitable installer found for this archive";
                return;
            }

            SelectedInstaller = selection.Installer.DisplayName;

            // Create plan.
            _currentPlan = await selection.Installer.AnalyzeAsync(ArchivePath, context, CancellationToken.None);
            TotalBytes = _currentPlan.TotalBytes;
            FileCount = _currentPlan.Operations.Count;
            RequiresUserInput = _currentPlan.RequiresUserInput;

            foreach (var op in _currentPlan.Operations)
            {
                PlannedOperations.Add(new FileOperationItem
                {
                    Type = op.Type.ToString(),
                    Source = op.SourcePath ?? "-",
                    Destination = op.DestinationPath ?? "-",
                    Size = FormatSize(op.Size),
                    IsCritical = op.IsCritical
                });
            }

            HasPlan = true;
            StatusMessage = $"Analysis complete: {FileCount} operations, {FormatSize(TotalBytes)} total ({SelectedInstaller})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task CommitInstallAsync()
    {
        if (_currentPlan == null || _installerManager == null || _dialogService == null)
            return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Commit Installation",
            $"Install {FileCount} files ({FormatSize(TotalBytes)}) to {GameDirectory}?");

        if (!confirmed) return;

        IsAnalyzing = true;
        StatusMessage = "Installing...";

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                StatusMessage = $"Installing: [{p.FilesProcessed}/{p.TotalFiles}] {p.CurrentFile ?? ""}";
            });

            var result = await _installerManager.ExecuteInstallAsync(_currentPlan, progress);

            if (result.Success)
            {
                StatusMessage = $"Installation complete: {result.InstalledFiles.Count} files installed.";
                if (result.BackedUpFiles.Count > 0)
                    StatusMessage += $" ({result.BackedUpFiles.Count} backups created)";
            }
            else
            {
                StatusMessage = $"Installation failed: {result.Error}";
                await _dialogService.ShowErrorAsync("Installation Failed", result.Error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {suffixes[i]}";
    }
}

public class StagedFileItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
}

public class FileOperationItem
{
    public string Type { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public bool IsCritical { get; set; }
}
