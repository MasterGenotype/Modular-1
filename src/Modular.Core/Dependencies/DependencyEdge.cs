using Modular.Core.Metadata;
using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Represents a directed edge in the dependency graph.
/// </summary>
public class DependencyEdge
{
    /// <summary>
    /// Source node (the mod that has the dependency).
    /// </summary>
    public ModNode From { get; set; } = new();

    /// <summary>
    /// Target node (the mod being depended upon).
    /// </summary>
    public ModNode To { get; set; } = new();

    /// <summary>
    /// Type of dependency relationship.
    /// </summary>
    public DependencyType Type { get; set; }

    /// <summary>
    /// Version constraint for this dependency (e.g., ">=1.0.0").
    /// Null means no constraint (any version).
    /// </summary>
    public VersionRange? VersionConstraint { get; set; }

    /// <summary>
    /// Optional reason/description for this dependency.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Optional metadata for this edge.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    public DependencyEdge()
    {
    }

    public DependencyEdge(
        ModNode from,
        ModNode to,
        DependencyType type,
        VersionRange? versionConstraint = null,
        string? reason = null)
    {
        From = from;
        To = to;
        Type = type;
        VersionConstraint = versionConstraint;
        Reason = reason;
    }

    /// <summary>
    /// Checks if this edge constraint is satisfied by a given version.
    /// </summary>
    public bool IsSatisfiedBy(SemanticVersion version)
    {
        return VersionConstraint?.IsSatisfiedBy(version) ?? true;
    }

    public override string ToString()
    {
        var constraint = VersionConstraint != null ? $" ({VersionConstraint})" : "";
        return $"{From} -> {To}{constraint} [{Type}]";
    }
}

/// <summary>
/// Type of dependency edge in the graph.
/// </summary>
public enum EdgeType
{
    /// <summary>Dependency is required for mod to function.</summary>
    Requires,

    /// <summary>Dependency is optional (enhances functionality).</summary>
    Optional,

    /// <summary>Mods are incompatible and cannot coexist.</summary>
    Incompatible,

    /// <summary>Dependency is embedded/bundled with the mod.</summary>
    Embedded,

    /// <summary>Mod recommends another mod but doesn't require it.</summary>
    Recommends,

    /// <summary>Mod breaks compatibility with another mod's version.</summary>
    Breaks
}
