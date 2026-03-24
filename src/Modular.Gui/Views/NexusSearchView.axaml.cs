using Avalonia.Controls;
using Modular.Gui.ViewModels;
using Modular.Sdk.Backends;

namespace Modular.Gui.Views;

public partial class NexusSearchView : UserControl
{
    public NexusSearchView()
    {
        InitializeComponent();
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item &&
            DataContext is NexusSearchViewModel vm)
        {
            var tag = item.Tag?.ToString();
            vm.SortOrder = tag switch
            {
                "Downloads" => ModSortOrder.Downloads,
                "Endorsements" => ModSortOrder.Endorsements,
                "Updated" => ModSortOrder.Updated,
                "Added" => ModSortOrder.Added,
                _ => ModSortOrder.Relevance
            };
        }
    }
}
