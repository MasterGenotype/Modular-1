namespace Modular.Core.Plugins;

/// <summary>
/// Manifest file for a plugin.
/// Stored as plugin.json in the plugin directory.
/// </summary>
public class PluginManifest
{
    /// <summary>
    /// Unique identifier for the plugin (e.g., "curseforge-backend").
    /// Must be lowercase with no spaces.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Semantic version of the plugin (e.g., "1.0.0").
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name (e.g., "CurseForge Backend").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Plugin author name(s).
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Brief description of what the plugin provides.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Minimum version of Modular required to run this plugin.
    /// Format: semantic version (e.g., "1.0.0").
    /// </summary>
    public string MinHostVersion { get; set; } = string.Empty;

    /// <summary>
    /// Name of the entry assembly DLL (relative to plugin directory).
    /// E.g., "CurseForgeBackend.dll"
    /// </summary>
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>
    /// List of plugin IDs this plugin depends on (optional).
    /// Dependencies will be loaded before this plugin.
    /// </summary>
    public List<string> Dependencies { get; set; } = new();

    /// <summary>
    /// Optional icon URL for the plugin.
    /// </summary>
    public string? IconUrl { get; set; }

    /// <summary>
    /// Optional project/source URL for the plugin.
    /// </summary>
    public string? ProjectUrl { get; set; }

    /// <summary>
    /// Whether the plugin is enabled by default.
    /// </summary>
    public bool EnabledByDefault { get; set; } = true;
}
