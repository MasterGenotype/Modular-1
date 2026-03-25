using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Backups panel. Contains Collections and Snapshots tabs.
/// </summary>
public partial class BackupsViewModel : ViewModelBase
{
    public CollectionViewModel? CollectionViewModel { get; }
    public SnapshotViewModel? SnapshotViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public BackupsViewModel()
    {
        CollectionViewModel = new CollectionViewModel();
        SnapshotViewModel = new SnapshotViewModel();
    }

    // DI constructor
    public BackupsViewModel(
        CollectionViewModel collectionViewModel,
        SnapshotViewModel snapshotViewModel)
    {
        CollectionViewModel = collectionViewModel;
        SnapshotViewModel = snapshotViewModel;
    }
}
