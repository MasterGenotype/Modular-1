namespace Modular.Sdk;

/// <summary>
/// Metadata describing a plugin's identity and requirements.
/// All plugins must expose an implementation of this interface.
/// </summary>
public interface IPluginMetadata
{
    /// <summary>
    /// Unique identifier for the plugin (e.g., "curseforge-backend", "modrinth-backend").
    /// Must be lowercase with no spaces. Used for plugin directory names and resolution.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Semantic version of the plugin (e.g., "1.0.0", "2.1.3-beta").
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Human-readable display name (e.g., "CurseForge Backend").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Plugin author name(s).
    /// </summary>
    string Author { get; }

    /// <summary>
    /// Brief description of what the plugin provides.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Minimum version of Modular host required to run this plugin.
    /// Format: semantic version (e.g., "1.0.0").
    /// </summary>
    string MinHostVersion { get; }

    /// <summary>
    /// List of plugin IDs this plugin depends on (optional).
    /// Dependencies will be loaded before this plugin.
    /// </summary>
    IReadOnlyList<string> Dependencies { get; }
}
