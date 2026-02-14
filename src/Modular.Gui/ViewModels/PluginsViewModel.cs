using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Plugins;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the Plugins management view.
/// </summary>
public partial class PluginsViewModel : ViewModelBase
{
    private readonly PluginLoader? _pluginLoader;
    private readonly PluginComposer? _pluginComposer;
    private readonly IDialogService? _dialogService;

    [ObservableProperty]
    private ObservableCollection<PluginDisplayModel> _plugins = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _totalPlugins;

    [ObservableProperty]
    private int _enabledPlugins;

    // Designer constructor
    public PluginsViewModel()
    {
        // Add sample data for designer
        Plugins.Add(new PluginDisplayModel
        {
            Id = "sample-plugin",
            DisplayName = "Sample Plugin",
            Version = "1.0.0",
            Author = "Sample Author",
            Description = "A sample plugin for demonstration",
            IsEnabled = true,
            IsLoaded = true
        });
        TotalPlugins = 1;
        EnabledPlugins = 1;
    }

    // DI constructor
    public PluginsViewModel(
        PluginLoader pluginLoader,
        PluginComposer pluginComposer,
        IDialogService dialogService)
    {
        _pluginLoader = pluginLoader;
        _pluginComposer = pluginComposer;
        _dialogService = dialogService;

        // Load plugins on construction
        _ = RefreshPluginsAsync();
    }

    [RelayCommand]
    private async Task RefreshPluginsAsync()
    {
        if (_pluginLoader == null)
        {
            StatusMessage = "Plugin loader not initialized";
            return;
        }

        IsLoading = true;
        StatusMessage = "Discovering plugins...";

        try
        {
            var manifests = _pluginLoader.DiscoverPlugins();
            var loadedPlugins = _pluginLoader.GetLoadedPlugins();

            Plugins.Clear();
            foreach (var manifest in manifests)
            {
                var isLoaded = loadedPlugins.Any(p => p.Manifest.Id == manifest.Id);
                
                Plugins.Add(new PluginDisplayModel
                {
                    Id = manifest.Id,
                    DisplayName = manifest.DisplayName,
                    Version = manifest.Version,
                    Author = manifest.Author,
                    Description = manifest.Description,
                    MinHostVersion = manifest.MinHostVersion,
                    IconUrl = manifest.IconUrl,
                    ProjectUrl = manifest.ProjectUrl,
                    IsEnabled = manifest.EnabledByDefault,
                    IsLoaded = isLoaded,
                    Dependencies = manifest.Dependencies
                });
            }

            TotalPlugins = Plugins.Count;
            EnabledPlugins = Plugins.Count(p => p.IsEnabled);
            StatusMessage = $"Found {TotalPlugins} plugin(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Error", $"Failed to discover plugins: {ex.Message}");
            }
        }
        finally
        {
            IsLoading = false;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoadPluginAsync(PluginDisplayModel plugin)
    {
        if (_pluginLoader == null || plugin.IsLoaded)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = $"Loading {plugin.DisplayName}...";

        try
        {
            // Find the manifest
            var manifests = _pluginLoader.DiscoverPlugins();
            var manifest = manifests.FirstOrDefault(m => m.Id == plugin.Id);
            
            if (manifest == null)
            {
                throw new InvalidOperationException($"Manifest not found for plugin: {plugin.Id}");
            }

            _pluginLoader.LoadPlugin(manifest);
            plugin.IsLoaded = true;
            plugin.IsEnabled = true;
            EnabledPlugins = Plugins.Count(p => p.IsEnabled);

            StatusMessage = $"Successfully loaded {plugin.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load {plugin.DisplayName}: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Load Failed", 
                    $"Failed to load plugin '{plugin.DisplayName}':\n{ex.Message}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UnloadPluginAsync(PluginDisplayModel plugin)
    {
        if (_pluginLoader == null || !plugin.IsLoaded)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = $"Unloading {plugin.DisplayName}...";

        try
        {
            _pluginLoader.UnloadPlugin(plugin.Id);
            plugin.IsLoaded = false;
            plugin.IsEnabled = false;
            EnabledPlugins = Plugins.Count(p => p.IsEnabled);

            StatusMessage = $"Successfully unloaded {plugin.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to unload {plugin.DisplayName}: {ex.Message}";
            if (_dialogService != null)
            {
                await _dialogService.ShowErrorAsync("Unload Failed",
                    $"Failed to unload plugin '{plugin.DisplayName}':\n{ex.Message}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task TogglePluginAsync(PluginDisplayModel plugin)
    {
        if (plugin.IsLoaded)
        {
            await UnloadPluginAsync(plugin);
        }
        else
        {
            await LoadPluginAsync(plugin);
        }
    }
}

/// <summary>
/// Display model for a plugin in the UI.
/// </summary>
public partial class PluginDisplayModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _minHostVersion = string.Empty;

    [ObservableProperty]
    private string? _iconUrl;

    [ObservableProperty]
    private string? _projectUrl;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private List<string> _dependencies = new();

    public string StatusText => IsLoaded ? "Loaded" : "Not Loaded";
}
