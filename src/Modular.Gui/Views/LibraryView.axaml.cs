using Avalonia.Controls;
using Modular.Gui.ViewModels;

namespace Modular.Gui.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    // The tree's selection of several nodes has no two-way binding; hand it over as it changes.
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TreeView tree && DataContext is LibraryViewModel vm)
            vm.SelectedNodes = tree.SelectedItems.OfType<LibraryNode>().ToList();
    }
}
