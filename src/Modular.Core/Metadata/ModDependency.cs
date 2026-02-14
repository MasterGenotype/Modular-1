namespace Modular.Core.Metadata;

/// <summary>
/// Represents a dependency relationship between mods.
/// </summary>
public class ModDependency
{
    /// <summary>
    /// Type of dependency relationship.
    /// </summary>
    public DependencyType Type { get; set; } = DependencyType.Required;

    /// <summary>
    /// Target mod information.
    /// </summary>
    public DependencyTarget Target { get; set; } = new();

    /// <summary>
    /// Version constraint (e.g., ">=1.0.0", "^2.0", "~1.2.3").
    /// </summary>
    public string? Constraint { get; set; }
}

/// <summary>
/// Dependency type based on Modrinth's dependency model.
/// </summary>
public enum DependencyType
{
    /// <summary>Required dependency - must be present.</summary>
    Required,

    /// <summary>Optional dependency - recommended but not required.</summary>
    Optional,

    /// <summary>Incompatible - this mod conflicts with the target.</summary>
    Incompatible,

    /// <summary>Embedded - dependency is bundled within this mod.</summary>
    Embedded
}

/// <summary>
/// Target of a dependency relationship.
/// </summary>
public class DependencyTarget
{
    /// <summary>
    /// Target project ID (backend-specific or canonical ID).
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Optional specific version ID.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>
    /// Optional backend ID if cross-backend dependency.
    /// </summary>
    public string? BackendId { get; set; }
}
