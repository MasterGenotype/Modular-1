using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Backups panel. Contains Snapshots.
/// </summary>
public partial class BackupsViewModel : ViewModelBase
{
    public SnapshotViewModel? SnapshotViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public BackupsViewModel()
    {
        SnapshotViewModel = new SnapshotViewModel();
    }

    // DI constructor
    public BackupsViewModel(
        SnapshotViewModel snapshotViewModel)
    {
        SnapshotViewModel = snapshotViewModel;
    }
}
