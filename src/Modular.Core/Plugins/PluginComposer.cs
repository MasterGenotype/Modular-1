using System.Composition.Hosting;
using Microsoft.Extensions.Logging;

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
    /// Disposes the composition host.
    /// </summary>
    public void Dispose()
    {
        _compositionHost?.Dispose();
        _compositionHost = null;
    }
}
