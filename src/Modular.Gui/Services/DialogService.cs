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

    public async Task ShowErrorAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window == null) return;

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
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children[1] is Button btn)
        {
            btn.Click += (_, _) => dialog.Close();
        }

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
            MinHeight = 150,
            MaxHeight = 350,
            SelectedIndex = items.Count > 0 ? 0 : -1
        };

        var dialog = new Window
        {
            Title = title,
            Width = 500,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    listBox,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Select", MinWidth = 80 },
                            new Button { Content = "Cancel", MinWidth = 80 }
                        }
                    }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children[2] is StackPanel btnPanel)
        {
            if (btnPanel.Children[0] is Button selectBtn)
            {
                selectBtn.Click += (_, _) => { result = listBox.SelectedIndex; dialog.Close(); };
            }
            if (btnPanel.Children[1] is Button cancelBtn)
            {
                cancelBtn.Click += (_, _) => { result = -1; dialog.Close(); };
            }
        }

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

        var listPanel = new StackPanel { Spacing = 4 };
        for (var i = 0; i < items.Count; i++)
        {
            var cb = new CheckBox
            {
                Content = items[i],
                IsChecked = preSelected == null || preSelected.Contains(i),
                FontSize = 12
            };
            checkBoxes.Add(cb);
            listPanel.Children.Add(cb);
        }

        var scrollViewer = new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = 350,
            MinHeight = 100
        };

        var selectAllBtn = new Button { Content = "Select All", FontSize = 11, Padding = new Thickness(8, 3) };
        var selectNoneBtn = new Button { Content = "Select None", FontSize = 11, Padding = new Thickness(8, 3) };
        selectAllBtn.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        selectNoneBtn.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };

        var dialog = new Window
        {
            Title = title,
            Width = 550,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        Children = { selectAllBtn, selectNoneBtn }
                    },
                    scrollViewer,
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

        var cancelled = true;
        if (dialog.Content is StackPanel panel && panel.Children[3] is StackPanel btnPanel)
        {
            if (btnPanel.Children[0] is Button okBtn)
            {
                okBtn.Click += (_, _) => { cancelled = false; dialog.Close(); };
            }
            if (btnPanel.Children[1] is Button cancelBtn)
            {
                cancelBtn.Click += (_, _) => { cancelled = true; dialog.Close(); };
            }
        }

        await dialog.ShowDialog(window);

        if (cancelled) return [];

        for (var i = 0; i < checkBoxes.Count; i++)
        {
            if (checkBoxes[i].IsChecked == true)
                result.Add(i);
        }

        return result;
    }

}
