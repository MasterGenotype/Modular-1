using Avalonia.Controls;
using Avalonia.Input;
using Modular.Gui.ViewModels;

namespace Modular.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // Handle keyboard shortcuts
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.R:
                    // Refresh current view
                    vm.RefreshCurrentViewCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.D:
                    // Download selected
                    vm.DownloadSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Q:
                    // Quit application
                    Close();
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.Escape)
        {
            // Cancel current operation
            vm.CancelOperationCommand.Execute(null);
            e.Handled = true;
        }
    }
}
