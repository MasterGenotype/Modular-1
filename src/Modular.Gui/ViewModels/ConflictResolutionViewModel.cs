using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Dependencies;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Conflict Resolution view — detects and displays file conflicts
/// between mods, letting users choose resolution strategies.
/// </summary>
public partial class ConflictResolutionViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<ConflictItem> _conflicts = new();

    [ObservableProperty]
    private ConflictItem? _selectedConflict;

    [ObservableProperty]
    private ObservableCollection<string> _conflictingProviders = new();

    [ObservableProperty]
    private string _statusMessage = "Scan for file conflicts between installed mods";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _overwriteCount;

    [ObservableProperty]
    private int _identicalCount;

    [ObservableProperty]
    private int _mergeCandidateCount;

    private FileConflictIndex? _conflictIndex;

    // Designer constructor
    public ConflictResolutionViewModel()
    {
        Conflicts.Add(new ConflictItem
        {
            GamePath = "Data/Scripts/MyMod.dll",
            Type = FileConflictType.Overwrite,
            ConflictingMods = "ModA, ModB"
        });
        OverwriteCount = 1;
    }

    // DI constructor
    public ConflictResolutionViewModel(FileConflictIndex conflictIndex)
    {
        _conflictIndex = conflictIndex;
    }

    [RelayCommand]
    private void ScanConflicts()
    {
        if (_conflictIndex == null) return;

        IsScanning = true;
        Conflicts.Clear();
        ConflictingProviders.Clear();

        try
        {
            var report = _conflictIndex.GenerateReport();

            foreach (var conflict in report.Conflicts)
            {
                Conflicts.Add(new ConflictItem
                {
                    GamePath = conflict.GamePath,
                    Type = conflict.Type,
                    ConflictingMods = string.Join(", ", conflict.ConflictingMods),
                    ModsList = conflict.ConflictingMods
                });
            }

            report.ConflictsByType.TryGetValue(FileConflictType.Overwrite, out var ow);
            report.ConflictsByType.TryGetValue(FileConflictType.IdenticalFiles, out var id);
            report.ConflictsByType.TryGetValue(FileConflictType.MergeCandidate, out var mc);

            OverwriteCount = ow;
            IdenticalCount = id;
            MergeCandidateCount = mc;

            StatusMessage = $"Found {report.TotalConflicts} conflict(s): {ow} overwrite, {id} identical, {mc} merge candidates";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    partial void OnSelectedConflictChanged(ConflictItem? value)
    {
        ConflictingProviders.Clear();
        if (value?.ModsList != null)
        {
            foreach (var mod in value.ModsList)
                ConflictingProviders.Add(mod);
        }
    }

    [RelayCommand]
    private void SetResolution(string resolution)
    {
        if (SelectedConflict == null) return;
        SelectedConflict.Resolution = resolution;
        StatusMessage = $"Set resolution for {SelectedConflict.GamePath}: {resolution}";
    }
}

public class ConflictItem
{
    public string GamePath { get; set; } = string.Empty;
    public FileConflictType Type { get; set; }
    public string ConflictingMods { get; set; } = string.Empty;
    public List<string>? ModsList { get; set; }
    public string? Resolution { get; set; }

    public string TypeLabel => Type switch
    {
        FileConflictType.Overwrite => "Overwrite",
        FileConflictType.IdenticalFiles => "Identical",
        FileConflictType.MergeCandidate => "Mergeable",
        _ => "Unknown"
    };
}
