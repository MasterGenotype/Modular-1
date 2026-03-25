using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Wrapper ViewModel for the NexusMods panel. Contains Search and Tracked Mods tabs.
/// </summary>
public partial class NexusModsViewModel : ViewModelBase
{
    public NexusSearchViewModel? NexusSearchViewModel { get; }
    public ModListViewModel? ModListViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public NexusModsViewModel()
    {
        NexusSearchViewModel = new NexusSearchViewModel();
        ModListViewModel = new ModListViewModel();
    }

    // DI constructor
    public NexusModsViewModel(
        NexusSearchViewModel nexusSearchViewModel,
        ModListViewModel modListViewModel)
    {
        NexusSearchViewModel = nexusSearchViewModel;
        ModListViewModel = modListViewModel;
    }
}
