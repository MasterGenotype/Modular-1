using System.Reflection;
using System.Runtime.Loader;

namespace Modular.Core.Plugins;

/// <summary>
/// Custom AssemblyLoadContext for loading plugins in isolation.
/// Each plugin gets its own load context to avoid version conflicts.
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginPath;

    /// <summary>
    /// Creates a new plugin load context.
    /// </summary>
    /// <param name="pluginPath">Path to the plugin directory.</param>
    /// <param name="isCollectible">
    /// Whether this context can be unloaded (true for hot reload support).
    /// </param>
    public PluginLoadContext(string pluginPath, bool isCollectible = true)
        : base(name: Path.GetFileName(pluginPath), isCollectible: isCollectible)
    {
        _pluginPath = pluginPath;
        
        // Find the main assembly in the plugin directory
        var assemblyPath = Directory.GetFiles(pluginPath, "*.dll")
            .FirstOrDefault() ?? throw new FileNotFoundException(
                $"No assembly found in plugin directory: {pluginPath}");

        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    /// <summary>
    /// Load assembly from the plugin directory or resolve from dependencies.
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try to resolve from plugin's dependencies first
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        // Check if assembly exists in the plugin directory
        var localPath = Path.Combine(_pluginPath, $"{assemblyName.Name}.dll");
        if (File.Exists(localPath))
        {
            return LoadFromAssemblyPath(localPath);
        }

        // Fall back to default context for shared assemblies (SDK, etc.)
        return null;
    }

    /// <summary>
    /// Resolve unmanaged (native) DLL dependencies.
    /// </summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }
}
