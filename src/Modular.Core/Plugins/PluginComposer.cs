using System.Composition;
using System.Composition.Hosting;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends;

namespace Modular.Core.Plugins;

/// <summary>
/// Uses MEF (Managed Extensibility Framework) to discover and compose plugin exports.
/// Plugins can export IModBackend implementations and other extension points.
/// </summary>
public class PluginComposer
{
    private readonly PluginLoader _pluginLoader;
    private readonly ILogger<PluginComposer>? _logger;
    private CompositionHost? _compositionHost;

    public PluginComposer(PluginLoader pluginLoader, ILogger<PluginComposer>? logger = null)
    {
        _pluginLoader = pluginLoader;
        _logger = logger;
    }

    /// <summary>
    /// Composes all loaded plugins using MEF.
    /// This discovers all exports from plugin assemblies.
    /// </summary>
    public void ComposePlugins()
    {
        var loadedPlugins = _pluginLoader.GetLoadedPlugins();
        if (loadedPlugins.Count == 0)
        {
            _logger?.LogInformation("No plugins loaded, skipping composition");
            return;
        }

        _logger?.LogInformation("Composing {Count} plugins using MEF", loadedPlugins.Count);

        // Create MEF composition configuration
        var configuration = new ContainerConfiguration();

        // Add plugin assemblies to the composition
        foreach (var plugin in loadedPlugins)
        {
            try
            {
                configuration.WithAssembly(plugin.Assembly);
                _logger?.LogDebug("Added plugin assembly to composition: {PluginId}", plugin.Manifest.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to add plugin assembly to composition: {PluginId}", 
                    plugin.Manifest.Id);
            }
        }

        // Also include the host assembly to allow plugins to import host services
        configuration.WithAssembly(Assembly.GetExecutingAssembly());

        // Create the composition host
        _compositionHost = configuration.CreateContainer();
        _logger?.LogInformation("MEF composition complete");
    }

    /// <summary>
    /// Gets all exports of a specific type from composed plugins.
    /// </summary>
    /// <typeparam name="T">Type to export.</typeparam>
    /// <returns>List of exported instances.</returns>
    public IEnumerable<T> GetExports<T>()
    {
        if (_compositionHost == null)
        {
            _logger?.LogWarning("Composition host not initialized, call ComposePlugins() first");
            return Enumerable.Empty<T>();
        }

        try
        {
            return _compositionHost.GetExports<T>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get exports of type {Type}", typeof(T).Name);
            return Enumerable.Empty<T>();
        }
    }

    /// <summary>
    /// Gets all backend exports from plugins.
    /// </summary>
    public IEnumerable<IModBackend> GetBackendExports()
    {
        return GetExports<IModBackend>();
    }

    /// <summary>
    /// Tries to get a specific export by contract name.
    /// </summary>
    public T? TryGetExport<T>(string? contractName = null)
    {
        if (_compositionHost == null)
        {
            return default;
        }

        try
        {
            return contractName == null 
                ? _compositionHost.GetExport<T>()
                : _compositionHost.GetExport<T>(contractName);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Disposes the composition host.
    /// </summary>
    public void Dispose()
    {
        _compositionHost?.Dispose();
        _compositionHost = null;
    }
}
