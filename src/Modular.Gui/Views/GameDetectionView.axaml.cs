using Avalonia.Controls;
using Avalonia.Input;
using Modular.Gui.ViewModels;

namespace Modular.Gui.Views;

public partial class GameDetectionView : UserControl
{
    public GameDetectionView()
    {
        InitializeComponent();
    }

    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is GameDetectionViewModel vm && vm.SelectedGame is { } game)
            vm.OpenGameCommand.Execute(game);
    }
}
