using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Wrapper ViewModel for the GameBanana panel. Contains Subscriptions and Search tabs.
/// </summary>
public partial class GameBananaPanelViewModel : ViewModelBase
{
    public GameBananaViewModel? GameBananaViewModel { get; }
    public GameBananaSearchViewModel? GameBananaSearchViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public GameBananaPanelViewModel()
    {
        GameBananaViewModel = new GameBananaViewModel();
        GameBananaSearchViewModel = new GameBananaSearchViewModel();
    }

    // DI constructor
    public GameBananaPanelViewModel(
        GameBananaViewModel gameBananaViewModel,
        GameBananaSearchViewModel gameBananaSearchViewModel)
    {
        GameBananaViewModel = gameBananaViewModel;
        GameBananaSearchViewModel = gameBananaSearchViewModel;
    }
}
