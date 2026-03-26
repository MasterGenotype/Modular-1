using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Wrapper ViewModel for the NexusMods panel. Contains Search, Tracked Mods, and Collections tabs.
/// </summary>
public partial class NexusModsViewModel : ViewModelBase
{
    public NexusSearchViewModel? NexusSearchViewModel { get; }
    public ModListViewModel? ModListViewModel { get; }
    public CollectionViewModel? CollectionViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public NexusModsViewModel()
    {
        NexusSearchViewModel = new NexusSearchViewModel();
        ModListViewModel = new ModListViewModel();
        CollectionViewModel = new CollectionViewModel();
    }

    // DI constructor
    public NexusModsViewModel(
        NexusSearchViewModel nexusSearchViewModel,
        ModListViewModel modListViewModel,
        CollectionViewModel collectionViewModel)
    {
        NexusSearchViewModel = nexusSearchViewModel;
        ModListViewModel = modListViewModel;
        CollectionViewModel = collectionViewModel;
    }
}
