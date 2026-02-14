namespace Modular.Core.Metadata;

/// <summary>
/// Represents a specific version of a mod with dependencies and files.
/// </summary>
public class CanonicalVersion
{
    /// <summary>
    /// Backend-specific version ID.
    /// </summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>
    /// Semantic version number (e.g., "1.2.3").
    /// </summary>
    public string? VersionNumber { get; set; }

    /// <summary>
    /// Release channel (stable, beta, alpha, etc.).
    /// </summary>
    public ReleaseChannel ReleaseChannel { get; set; } = ReleaseChannel.Stable;

    /// <summary>
    /// Changelog/release notes for this version.
    /// </summary>
    public string? Changelog { get; set; }

    /// <summary>
    /// Dependencies for this version.
    /// </summary>
    public List<ModDependency> Dependencies { get; set; } = new();

    /// <summary>
    /// Downloadable files for this version.
    /// </summary>
    public List<CanonicalFile> Files { get; set; } = new();

    /// <summary>
    /// Installation metadata.
    /// </summary>
    public InstallMetadata? Install { get; set; }

    /// <summary>
    /// When this version was published.
    /// </summary>
    public DateTime? PublishedAt { get; set; }
}

/// <summary>
/// Release channel for a version.
/// </summary>
public enum ReleaseChannel
{
    /// <summary>Stable release.</summary>
    Stable,

    /// <summary>Beta/preview release.</summary>
    Beta,

    /// <summary>Alpha/experimental release.</summary>
    Alpha,

    /// <summary>Unknown/not specified.</summary>
    Unknown
}

/// <summary>
/// Installation metadata for a mod version.
/// </summary>
public class InstallMetadata
{
    /// <summary>
    /// Installation format (fomod, loose, plugin, etc.).
    /// </summary>
    public InstallFormat Format { get; set; } = InstallFormat.Unknown;

    /// <summary>
    /// Optional installation instructions (format-specific).
    /// </summary>
    public Dictionary<string, object>? Instructions { get; set; }
}

/// <summary>
/// Installation format types.
/// </summary>
public enum InstallFormat
{
    /// <summary>Unknown format.</summary>
    Unknown,

    /// <summary>FOMOD installer.</summary>
    Fomod,

    /// <summary>Loose files (simple extraction).</summary>
    Loose,

    /// <summary>Plugin/mod manager specific format.</summary>
    Plugin,

    /// <summary>BepInEx plugin.</summary>
    BepInEx
}
