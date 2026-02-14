namespace Modular.Core.Backends;

/// <summary>
/// Registry of available mod backends.
/// Backends register themselves at application startup, and consumers can
/// iterate or look up backends by ID.
/// </summary>
public class BackendRegistry
{
    private readonly Dictionary<string, IModBackend> _backends = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a backend. Call during application startup.
    /// If a backend with the same ID is already registered, it will be replaced.
    /// </summary>
    /// <param name="backend">The backend to register.</param>
    public void Register(IModBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backends[backend.Id] = backend;
    }

    /// <summary>
    /// Unregister a backend by ID.
    /// </summary>
    /// <param name="id">The backend ID to remove.</param>
    /// <returns>True if the backend was removed, false if not found.</returns>
    public bool Unregister(string id)
    {
        return _backends.Remove(id);
    }

    /// <summary>
    /// Get a backend by ID.
    /// </summary>
    /// <param name="id">The backend ID (case-insensitive).</param>
    /// <returns>The backend, or null if not registered.</returns>
    public IModBackend? Get(string id)
    {
        return _backends.TryGetValue(id, out var backend) ? backend : null;
    }

    /// <summary>
    /// Get a backend by ID, throwing if not found.
    /// </summary>
    /// <param name="id">The backend ID (case-insensitive).</param>
    /// <returns>The backend.</returns>
    /// <exception cref="KeyNotFoundException">If no backend with the given ID is registered.</exception>
    public IModBackend GetRequired(string id)
    {
        return Get(id) ?? throw new KeyNotFoundException($"Backend '{id}' is not registered.");
    }

    /// <summary>
    /// Check if a backend is registered.
    /// </summary>
    /// <param name="id">The backend ID (case-insensitive).</param>
    /// <returns>True if registered.</returns>
    public bool IsRegistered(string id)
    {
        return _backends.ContainsKey(id);
    }

    /// <summary>
    /// Get all registered backends.
    /// </summary>
    /// <returns>Read-only list of all backends.</returns>
    public IReadOnlyList<IModBackend> GetAll()
    {
        return _backends.Values.ToList();
    }

    /// <summary>
    /// Get all backend IDs.
    /// </summary>
    /// <returns>Read-only list of backend IDs.</returns>
    public IReadOnlyList<string> GetIds()
    {
        return _backends.Keys.ToList();
    }

    /// <summary>
    /// Get all backends that are properly configured (validation passes).
    /// </summary>
    /// <returns>Read-only list of configured backends.</returns>
    public IReadOnlyList<IModBackend> GetConfigured()
    {
        return _backends.Values
            .Where(b => b.ValidateConfiguration().Count == 0)
            .ToList();
    }

    /// <summary>
    /// Get backends that support a specific capability.
    /// </summary>
    /// <param name="capability">The capability to filter by.</param>
    /// <returns>Read-only list of backends with the capability.</returns>
    public IReadOnlyList<IModBackend> GetWithCapability(BackendCapabilities capability)
    {
        return _backends.Values
            .Where(b => b.Capabilities.HasFlag(capability))
            .ToList();
    }

    /// <summary>
    /// Get backends that support all of the specified capabilities.
    /// </summary>
    /// <param name="capabilities">The capabilities to filter by (all must be present).</param>
    /// <returns>Read-only list of backends with all capabilities.</returns>
    public IReadOnlyList<IModBackend> GetWithAllCapabilities(BackendCapabilities capabilities)
    {
        return _backends.Values
            .Where(b => (b.Capabilities & capabilities) == capabilities)
            .ToList();
    }

    /// <summary>
    /// Get backends that support any of the specified capabilities.
    /// </summary>
    /// <param name="capabilities">The capabilities to filter by (any must be present).</param>
    /// <returns>Read-only list of backends with at least one capability.</returns>
    public IReadOnlyList<IModBackend> GetWithAnyCapability(BackendCapabilities capabilities)
    {
        return _backends.Values
            .Where(b => (b.Capabilities & capabilities) != BackendCapabilities.None)
            .ToList();
    }

    /// <summary>
    /// Get configuration errors for all backends.
    /// </summary>
    /// <returns>
    /// Dictionary mapping backend ID to list of validation errors.
    /// Backends with no errors are not included.
    /// </returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetAllConfigurationErrors()
    {
        return _backends
            .Select(kvp => (kvp.Key, Errors: kvp.Value.ValidateConfiguration()))
            .Where(x => x.Errors.Count > 0)
            .ToDictionary(x => x.Key, x => x.Errors);
    }

    /// <summary>
    /// Number of registered backends.
    /// </summary>
    public int Count => _backends.Count;

    /// <summary>
    /// Registers all backends from a plugin composer.
    /// This integrates with the plugin system to dynamically load backend implementations.
    /// </summary>
    /// <param name="composer">Plugin composer with loaded plugins.</param>
    /// <returns>Number of backends registered from plugins.</returns>
    public int RegisterFromPlugins(Plugins.PluginComposer composer)
    {
        var backends = composer.GetBackendExports().ToList();
        foreach (var backend in backends)
        {
            Register(backend);
        }
        return backends.Count;
    }
}
