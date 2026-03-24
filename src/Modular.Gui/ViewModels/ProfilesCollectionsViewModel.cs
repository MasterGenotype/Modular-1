using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// Combined ViewModel wrapping Profiles and Collections as sub-tabs.
/// </summary>
public partial class ProfilesCollectionsViewModel : ViewModelBase
{
    public ProfilesViewModel? ProfilesViewModel { get; }
    public CollectionViewModel? CollectionViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    // Designer constructor
    public ProfilesCollectionsViewModel()
    {
        ProfilesViewModel = new ProfilesViewModel();
        CollectionViewModel = new CollectionViewModel();
    }

    // DI constructor
    public ProfilesCollectionsViewModel(
        ProfilesViewModel profilesViewModel,
        CollectionViewModel collectionViewModel)
    {
        ProfilesViewModel = profilesViewModel;
        CollectionViewModel = collectionViewModel;
    }
}
