using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.ErrorHandling;
using Modular.Sdk;
using Modular.Sdk.Installers;
using Modular.Sdk.Metadata;
using Modular.Sdk.UI;

namespace Modular.Core.Plugins;

/// <summary>
/// Service for discovering and loading plugins from the plugin directory.
/// </summary>
public class PluginLoader
{
    private readonly string _pluginDirectory;
    private readonly ILogger<PluginLoader>? _logger;
    private readonly Dictionary<string, LoadedPlugin> _loadedPlugins = new();
    private readonly ErrorBoundary _errorBoundary;

    /// <summary>
    /// Default plugin directory: ~/.config/Modular/plugins
    /// </summary>
    public static string DefaultPluginDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "Modular",
            "plugins");

    public PluginLoader(string? pluginDirectory = null, ILogger<PluginLoader>? logger = null)
    {
        _pluginDirectory = pluginDirectory ?? DefaultPluginDirectory;
        _logger = logger;
        _errorBoundary = new ErrorBoundary(ErrorBoundaryPolicy.Permissive, logger);

        // Create plugin directory if it doesn't exist
        if (!Directory.Exists(_pluginDirectory))
        {
            Directory.CreateDirectory(_pluginDirectory);
            _logger?.LogInformation("Created plugin directory: {PluginDirectory}", _pluginDirectory);
        }
    }

    /// <summary>
    /// Discovers all plugins in the plugin directory.
    /// </summary>
    /// <returns>List of plugin manifests found.</returns>
    public List<PluginManifest> DiscoverPlugins()
    {
        var manifests = new List<PluginManifest>();

        if (!Directory.Exists(_pluginDirectory))
        {
            _logger?.LogWarning("Plugin directory does not exist: {PluginDirectory}", _pluginDirectory);
            return manifests;
        }

        // Each plugin is in its own subdirectory with a plugin.json manifest
        foreach (var pluginDir in Directory.GetDirectories(_pluginDirectory))
        {
            var manifestPath = Path.Combine(pluginDir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                _logger?.LogWarning("No manifest found in plugin directory: {PluginDir}", pluginDir);
                continue;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (manifest == null)
                {
                    _logger?.LogError("Failed to parse manifest: {ManifestPath}", manifestPath);
                    continue;
                }

                // Validate manifest
                if (string.IsNullOrEmpty(manifest.Id) ||
                    string.IsNullOrEmpty(manifest.Version) ||
                    string.IsNullOrEmpty(manifest.EntryAssembly))
                {
                    _logger?.LogError("Invalid manifest (missing required fields): {ManifestPath}", manifestPath);
                    continue;
                }

                manifests.Add(manifest);
                _logger?.LogDebug("Discovered plugin: {PluginId} v{Version}", manifest.Id, manifest.Version);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error reading plugin manifest: {ManifestPath}", manifestPath);
            }
        }

        return manifests;
    }

    /// <summary>
    /// Loads a plugin from its manifest.
    /// </summary>
    /// <param name="manifest">Plugin manifest.</param>
    /// <returns>Loaded plugin information.</returns>
    public LoadedPlugin LoadPlugin(PluginManifest manifest)
    {
        if (_loadedPlugins.ContainsKey(manifest.Id))
        {
            _logger?.LogWarning("Plugin already loaded: {PluginId}", manifest.Id);
            return _loadedPlugins[manifest.Id];
        }

        var pluginDir = Path.Combine(_pluginDirectory, manifest.Id);
        var assemblyPath = Path.Combine(pluginDir, manifest.EntryAssembly);

        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {assemblyPath}");
        }

        _logger?.LogInformation("Loading plugin: {PluginId} from {AssemblyPath}", manifest.Id, assemblyPath);

        // Create isolated load context
        var loadContext = new PluginLoadContext(pluginDir, isCollectible: true);

        // Load the entry assembly
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        // Find types implementing IPluginMetadata
        var metadataTypes = assembly.GetTypes()
            .Where(t => typeof(IPluginMetadata).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        if (metadataTypes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Plugin {manifest.Id} does not contain any types implementing IPluginMetadata");
        }

        // Create instance of the metadata type
        var metadataInstance = Activator.CreateInstance(metadataTypes[0]) as IPluginMetadata;
        if (metadataInstance == null)
        {
            throw new InvalidOperationException(
                $"Failed to create instance of IPluginMetadata for plugin {manifest.Id}");
        }

        var loadedPlugin = new LoadedPlugin
        {
            Manifest = manifest,
            LoadContext = loadContext,
            Assembly = assembly,
            Metadata = metadataInstance,
            Installers = DiscoverInstallers(assembly),
            Enrichers = DiscoverEnrichers(assembly),
            UiExtensions = DiscoverUiExtensions(assembly)
        };

        _loadedPlugins[manifest.Id] = loadedPlugin;
        _logger?.LogInformation(
            "Successfully loaded plugin: {PluginId} v{Version} (Installers: {InstallerCount}, Enrichers: {EnricherCount}, UI: {UiCount})",
            manifest.Id, manifest.Version,
            loadedPlugin.Installers.Count, loadedPlugin.Enrichers.Count, loadedPlugin.UiExtensions.Count);

        return loadedPlugin;
    }

    /// <summary>
    /// Loads all discovered plugins, respecting dependency order.
    /// </summary>
    /// <returns>List of loaded plugins.</returns>
    public List<LoadedPlugin> LoadAllPlugins()
    {
        var manifests = DiscoverPlugins();
        var loaded = new List<LoadedPlugin>();

        // Sort plugins by dependency order
        var sorted = TopologicalSort(manifests);

        foreach (var manifest in sorted)
        {
            try
            {
                var plugin = LoadPlugin(manifest);
                loaded.Add(plugin);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load plugin: {PluginId}", manifest.Id);
            }
        }

        _logger?.LogInformation("Loaded {Count} plugins", loaded.Count);
        return loaded;
    }

    /// <summary>
    /// Unloads a plugin and its assembly load context.
    /// </summary>
    /// <param name="pluginId">Plugin ID to unload.</param>
    public void UnloadPlugin(string pluginId)
    {
        if (!_loadedPlugins.TryGetValue(pluginId, out var plugin))
        {
            _logger?.LogWarning("Plugin not loaded: {PluginId}", pluginId);
            return;
        }

        _logger?.LogInformation("Unloading plugin: {PluginId}", pluginId);

        // Unload the assembly load context
        plugin.LoadContext.Unload();

        _loadedPlugins.Remove(pluginId);
    }

    /// <summary>
    /// Gets all loaded plugins.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> GetLoadedPlugins() => _loadedPlugins.Values.ToList();

    /// <summary>
    /// Gets a loaded plugin by ID.
    /// </summary>
    public LoadedPlugin? GetPlugin(string pluginId) =>
        _loadedPlugins.TryGetValue(pluginId, out var plugin) ? plugin : null;

    /// <summary>
    /// Topologically sorts plugins by their dependencies.
    /// Ensures dependencies are loaded before dependents.
    /// </summary>
    private List<PluginManifest> TopologicalSort(List<PluginManifest> manifests)
    {
        var sorted = new List<PluginManifest>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        var manifestDict = manifests.ToDictionary(m => m.Id);

        void Visit(PluginManifest manifest)
        {
            if (visited.Contains(manifest.Id))
                return;

            if (visiting.Contains(manifest.Id))
            {
                throw new InvalidOperationException(
                    $"Circular dependency detected involving plugin: {manifest.Id}");
            }

            visiting.Add(manifest.Id);

            // Visit dependencies first
            foreach (var depId in manifest.Dependencies)
            {
                if (manifestDict.TryGetValue(depId, out var dep))
                {
                    Visit(dep);
                }
                else
                {
                    _logger?.LogWarning("Plugin {PluginId} depends on {DepId} which is not available",
                        manifest.Id, depId);
                }
            }

            visiting.Remove(manifest.Id);
            visited.Add(manifest.Id);
            sorted.Add(manifest);
        }

        foreach (var manifest in manifests)
        {
            Visit(manifest);
        }

        return sorted;
    }

    /// <summary>
    /// Discovers installer implementations in an assembly.
    /// </summary>
    private List<IModInstaller> DiscoverInstallers(Assembly assembly)
    {
        return _errorBoundary.Execute(
            $"Discover installers in {assembly.GetName().Name}",
            () =>
            {
                var installers = new List<IModInstaller>();
                var installerTypes = assembly.GetTypes()
                    .Where(t => typeof(IModInstaller).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();

                foreach (var type in installerTypes)
                {
                    var result = _errorBoundary.Execute(
                        $"Create installer {type.Name}",
                        () =>
                        {
                            var instance = Activator.CreateInstance(type) as IModInstaller;
                            if (instance != null)
                            {
                                installers.Add(instance);
                                _logger?.LogDebug("Discovered installer: {InstallerId} ({Type})", instance.InstallerId, type.Name);
                            }
                            return instance;
                        },
                        null!);
                }
                return installers;
            },
            new List<IModInstaller>()).Value ?? new List<IModInstaller>();
    }

    /// <summary>
    /// Discovers metadata enricher implementations in an assembly.
    /// </summary>
    private List<IMetadataEnricher> DiscoverEnrichers(Assembly assembly)
    {
        return _errorBoundary.Execute(
            $"Discover enrichers in {assembly.GetName().Name}",
            () =>
            {
                var enrichers = new List<IMetadataEnricher>();
                var enricherTypes = assembly.GetTypes()
                    .Where(t => typeof(IMetadataEnricher).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();

                foreach (var type in enricherTypes)
                {
                    var result = _errorBoundary.Execute(
                        $"Create enricher {type.Name}",
                        () =>
                        {
                            var instance = Activator.CreateInstance(type) as IMetadataEnricher;
                            if (instance != null)
                            {
                                enrichers.Add(instance);
                                _logger?.LogDebug("Discovered enricher: {BackendId} ({Type})", instance.BackendId, type.Name);
                            }
                            return instance;
                        },
                        null!);
                }
                return enrichers;
            },
            new List<IMetadataEnricher>()).Value ?? new List<IMetadataEnricher>();
    }

    /// <summary>
    /// Discovers UI extension implementations in an assembly.
    /// </summary>
    private List<IUiExtension> DiscoverUiExtensions(Assembly assembly)
    {
        return _errorBoundary.Execute(
            $"Discover UI extensions in {assembly.GetName().Name}",
            () =>
            {
                var extensions = new List<IUiExtension>();
                var extensionTypes = assembly.GetTypes()
                    .Where(t => typeof(IUiExtension).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();

                foreach (var type in extensionTypes)
                {
                    var result = _errorBoundary.Execute(
                        $"Create UI extension {type.Name}",
                        () =>
                        {
                            var instance = Activator.CreateInstance(type) as IUiExtension;
                            if (instance != null)
                            {
                                extensions.Add(instance);
                                _logger?.LogDebug("Discovered UI extension: {ExtensionId} ({Type})", instance.ExtensionId, type.Name);
                            }
                            return instance;
                        },
                        null!);
                }
                return extensions;
            },
            new List<IUiExtension>()).Value ?? new List<IUiExtension>();
    }

    /// <summary>
    /// Gets all installers from loaded plugins.
    /// </summary>
    public List<IModInstaller> GetAllInstallers()
    {
        return _loadedPlugins.Values
            .SelectMany(p => p.Installers)
            .OrderByDescending(i => i.Priority)
            .ToList();
    }

    /// <summary>
    /// Gets all enrichers from loaded plugins.
    /// </summary>
    public Dictionary<string, IMetadataEnricher> GetAllEnrichers()
    {
        return _loadedPlugins.Values
            .SelectMany(p => p.Enrichers)
            .ToDictionary(e => e.BackendId, e => e);
    }

    /// <summary>
    /// Gets all UI extensions from loaded plugins, grouped by location.
    /// </summary>
    public Dictionary<UiExtensionLocation, List<IUiExtension>> GetAllUiExtensions()
    {
        return _loadedPlugins.Values
            .SelectMany(p => p.UiExtensions)
            .GroupBy(e => e.Location)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Priority).ToList());
    }
}

/// <summary>
/// Represents a loaded plugin with its context and metadata.
/// </summary>
public class LoadedPlugin
{
    /// <summary>
    /// Plugin manifest.
    /// </summary>
    public required PluginManifest Manifest { get; init; }

    /// <summary>
    /// Assembly load context for the plugin.
    /// </summary>
    public required PluginLoadContext LoadContext { get; init; }

    /// <summary>
    /// Loaded assembly.
    /// </summary>
    public required Assembly Assembly { get; init; }

    /// <summary>
    /// Plugin metadata instance.
    /// </summary>
    public required IPluginMetadata Metadata { get; init; }

    /// <summary>
    /// Mod installers provided by this plugin.
    /// </summary>
    public List<IModInstaller> Installers { get; init; } = new();

    /// <summary>
    /// Metadata enrichers provided by this plugin.
    /// </summary>
    public List<IMetadataEnricher> Enrichers { get; init; } = new();

    /// <summary>
    /// UI extensions provided by this plugin.
    /// </summary>
    public List<IUiExtension> UiExtensions { get; init; } = new();
}
