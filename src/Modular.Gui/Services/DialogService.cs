using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Modular.Gui.Services;

/// <summary>
/// Implementation of IDialogService using Avalonia dialogs.
/// </summary>
public class DialogService : IDialogService
{
    private Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public Task ShowErrorAsync(string title, string message) => ShowMessageAsync(title, message);

    public Task ShowInfoAsync(string title, string message) => ShowMessageAsync(title, message);

    // Grows with the message up to a cap and scrolls past it, so a list of errors is
    // never cut off at a fixed height.
    private async Task ShowMessageAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window == null) return;

        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

        var body = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(ok, Dock.Bottom);
        ok.Margin = new Thickness(0, 20, 0, 0);
        body.Children.Add(ok);
        body.Children.Add(new ScrollViewer
        {
            Content = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        });

        var dialog = new Window
        {
            Title = title,
            Width = 450,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = body
        };

        ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(window);
    }

    public async Task ShowWarningAsync(string title, string message)
    {
        await ShowErrorAsync(title, message);
    }

    public async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window == null) return false;

        var result = false;

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Yes" },
                            new Button { Content = "No" }
                        }
                    }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children[1] is StackPanel btnPanel)
        {
            if (btnPanel.Children[0] is Button yesBtn)
            {
                yesBtn.Click += (_, _) => { result = true; dialog.Close(); };
            }
            if (btnPanel.Children[1] is Button noBtn)
            {
                noBtn.Click += (_, _) => { result = false; dialog.Close(); };
            }
        }

        await dialog.ShowDialog(window);
        return result;
    }

    public async Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
    {
        var window = GetMainWindow();
        if (window == null) return defaultValue;

        string? result = null;
        var inputBox = new TextBox 
        { 
            Text = defaultValue ?? string.Empty,
            MinWidth = 300,
            Watermark = "Enter value..."
        };

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock 
                    { 
                        Text = prompt, 
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap 
                    },
                    inputBox,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "OK", MinWidth = 80 },
                            new Button { Content = "Cancel", MinWidth = 80 }
                        }
                    }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children[2] is StackPanel btnPanel)
        {
            if (btnPanel.Children[0] is Button okBtn)
            {
                okBtn.Click += (_, _) => { result = inputBox.Text; dialog.Close(); };
            }
            if (btnPanel.Children[1] is Button cancelBtn)
            {
                cancelBtn.Click += (_, _) => { result = null; dialog.Close(); };
            }
        }

        // Focus the input box when the dialog opens
        dialog.Opened += (_, _) => inputBox.Focus();
        
        // Allow Enter key to submit
        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                result = inputBox.Text;
                dialog.Close();
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                result = null;
                dialog.Close();
            }
        };

        await dialog.ShowDialog(window);
        return result;
    }

    public async Task<List<string>> ShowFileBrowserAsync(string? title = null, bool allowMultiple = false, string? initialDirectory = null)
    {
        var window = GetMainWindow();
        if (window == null) return [];

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title ?? "Select File(s)",
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = initialDirectory != null
                ? await window.StorageProvider.TryGetFolderFromPathAsync(initialDirectory)
                : null,
            FileTypeFilter =
            [
                new FilePickerFileType("Mod Archives") { Patterns = ["*.zip", "*.7z", "*.rar", "*.tar.gz", "*.tgz", "*.tar", "*.pak"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        return files.Select(f => f.Path.LocalPath).ToList();
    }

    public async Task<string?> ShowFolderBrowserAsync(string? title = null, string? initialDirectory = null)
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title ?? "Select Folder",
            AllowMultiple = false,
            SuggestedStartLocation = initialDirectory != null
                ? await window.StorageProvider.TryGetFolderFromPathAsync(initialDirectory)
                : null
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    public async Task<int> ShowListPickerAsync(string title, string message, List<string> items)
    {
        var window = GetMainWindow();
        if (window == null) return -1;

        var result = -1;
        var listBox = new ListBox
        {
            ItemsSource = items,
            SelectedIndex = items.Count > 0 ? 0 : -1
        };

        var selectBtn = new Button { Content = "Select", MinWidth = 80 };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };

        var dialog = new Window
        {
            Title = title,
            Width = 550,
            Height = 480,
            MinWidth = 400,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildPickerLayout(message, null, listBox, selectBtn, cancelBtn)
        };

        selectBtn.Click += (_, _) => { result = listBox.SelectedIndex; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = -1; dialog.Close(); };

        listBox.DoubleTapped += (_, _) =>
        {
            if (listBox.SelectedIndex >= 0)
            {
                result = listBox.SelectedIndex;
                dialog.Close();
            }
        };

        await dialog.ShowDialog(window);
        return result;
    }

    public async Task<List<int>> ShowMultiSelectAsync(string title, string message, List<string> items, List<int>? preSelected = null)
    {
        var window = GetMainWindow();
        if (window == null) return [];

        var result = new List<int>();
        var checkBoxes = new List<CheckBox>();

        var listPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 0) };
        for (var i = 0; i < items.Count; i++)
        {
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = items[i], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                IsChecked = preSelected == null || preSelected.Contains(i),
                FontSize = 12
            };
            checkBoxes.Add(cb);
            listPanel.Children.Add(cb);
        }

        var selectAllBtn = new Button { Content = "Select All", FontSize = 11, Padding = new Thickness(8, 3) };
        var selectNoneBtn = new Button { Content = "Select None", FontSize = 11, Padding = new Thickness(8, 3) };
        selectAllBtn.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        selectNoneBtn.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };

        var okBtn = new Button { Content = "OK", MinWidth = 80 };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };

        var dialog = new Window
        {
            Title = title,
            Width = 600,
            Height = 600,
            MinWidth = 400,
            MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildPickerLayout(
                message,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children = { selectAllBtn, selectNoneBtn }
                },
                new ScrollViewer
                {
                    Content = listPanel,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                },
                okBtn,
                cancelBtn)
        };

        var cancelled = true;
        okBtn.Click += (_, _) => { cancelled = false; dialog.Close(); };
        cancelBtn.Click += (_, _) => { cancelled = true; dialog.Close(); };

        await dialog.ShowDialog(window);

        if (cancelled) return [];

        for (var i = 0; i < checkBoxes.Count; i++)
        {
            if (checkBoxes[i].IsChecked == true)
                result.Add(i);
        }

        return result;
    }

    // Message on top, the list filling whatever height is left, buttons pinned to the
    // bottom. The list scrolls on its own, and a long message scrolls in its own capped
    // area, so neither can push the other or the buttons out of the window.
    private static Grid BuildPickerLayout(
        string message, Control? toolbar, Control list, Button confirm, Button cancel)
    {
        var grid = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var messageView = new ScrollViewer
        {
            MaxHeight = 200,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        };
        grid.Children.Add(messageView);

        if (toolbar != null)
        {
            toolbar.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(toolbar, 1);
            grid.Children.Add(toolbar);
        }

        var listBorder = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Avalonia.Media.Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Child = list
        };
        Grid.SetRow(listBorder, 2);
        grid.Children.Add(listBorder);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { confirm, cancel }
        };
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        return grid;
    }
}
