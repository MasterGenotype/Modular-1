using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Wrapper ViewModel for the mod.io panel. Contains Subscriptions and Search tabs.
/// </summary>
public partial class ModIoPanelViewModel : ViewModelBase
{
    public ModIoViewModel? ModIoViewModel { get; }
    public ModIoSearchViewModel? ModIoSearchViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public ModIoPanelViewModel()
    {
        ModIoViewModel = new ModIoViewModel();
        ModIoSearchViewModel = new ModIoSearchViewModel();
    }

    // DI constructor
    public ModIoPanelViewModel(
        ModIoViewModel modIoViewModel,
        ModIoSearchViewModel modIoSearchViewModel)
    {
        ModIoViewModel = modIoViewModel;
        ModIoSearchViewModel = modIoSearchViewModel;
    }
}
