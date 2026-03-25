using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Wrapper ViewModel for the GameBanana panel. Contains Subscriptions and Search tabs.
/// </summary>
public partial class GameBananaPanelViewModel : ViewModelBase
{
    public GameBananaViewModel? GameBananaViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public GameBananaPanelViewModel()
    {
        GameBananaViewModel = new GameBananaViewModel();
    }

    // DI constructor
    public GameBananaPanelViewModel(GameBananaViewModel gameBananaViewModel)
    {
        GameBananaViewModel = gameBananaViewModel;
    }
}
