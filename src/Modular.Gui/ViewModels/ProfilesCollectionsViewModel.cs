using CommunityToolkit.Mvvm.ComponentModel;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel wrapping Profiles panel.
/// </summary>
public partial class ProfilesCollectionsViewModel : ViewModelBase
{
    public ProfilesViewModel? ProfilesViewModel { get; }

    // Designer constructor
    public ProfilesCollectionsViewModel()
    {
        ProfilesViewModel = new ProfilesViewModel();
    }

    // DI constructor
    public ProfilesCollectionsViewModel(
        ProfilesViewModel profilesViewModel)
    {
        ProfilesViewModel = profilesViewModel;
    }
}
