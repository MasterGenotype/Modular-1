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

    public async Task<DialogResult> ShowRetryErrorAsync(string title, string message)
    {
        var window = GetMainWindow();
        if (window == null) return DialogResult.Cancel;

        var result = DialogResult.Cancel;

        var dialog = new Window
        {
            Title = title,
            Width = 450,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 20,
                Children =
                {
                    new TextBlock 
                    { 
                        Text = message, 
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 400
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Retry", MinWidth = 80 },
                            new Button { Content = "Cancel", MinWidth = 80 }
                        }
                    }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children[1] is StackPanel btnPanel)
        {
            if (btnPanel.Children[0] is Button retryBtn)
            {
                retryBtn.Click += (_, _) => { result = DialogResult.Retry; dialog.Close(); };
            }
            if (btnPanel.Children[1] is Button cancelBtn)
            {
                cancelBtn.Click += (_, _) => { result = DialogResult.Cancel; dialog.Close(); };
            }
        }

        await dialog.ShowDialog(window);
        return result;
    }

    public Task<IProgressDialog> ShowProgressAsync(string title, string message, bool cancellable = true)
    {
        var window = GetMainWindow();
        var progressDialog = new ProgressDialogImpl(window, title, message, cancellable);
        return Task.FromResult<IProgressDialog>(progressDialog);
    }
}

/// <summary>
/// Implementation of IProgressDialog.
/// </summary>
internal class ProgressDialogImpl : IProgressDialog
{
    private readonly Window? _owner;
    private readonly Window _dialog;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _messageBlock;
    private readonly Button? _cancelButton;
    private bool _isCancellationRequested;

    public bool IsCancellationRequested => _isCancellationRequested;

    public ProgressDialogImpl(Window? owner, string title, string message, bool cancellable)
    {
        _owner = owner;
        _progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 20 };
        _messageBlock = new TextBlock 
        { 
            Text = message, 
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 350
        };

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 15,
            Children = { _messageBlock, _progressBar }
        };

        if (cancellable)
        {
            _cancelButton = new Button 
            { 
                Content = "Cancel", 
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                MinWidth = 80
            };
            _cancelButton.Click += (_, _) => _isCancellationRequested = true;
            content.Children.Add(_cancelButton);
        }

        _dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = cancellable ? 180 : 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = content
        };

        // Show non-modal so we can update it
        if (_owner != null)
        {
            _dialog.Show(_owner);
        }
    }

    public void UpdateProgress(double progress)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _progressBar.Value = Math.Clamp(progress, 0, 100);
        });
    }

    public void UpdateMessage(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _messageBlock.Text = message;
        });
    }

    public void Close()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _dialog.Close();
        });
    }

    public void Dispose()
    {
        Close();
    }
}
