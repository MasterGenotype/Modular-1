using Modular.Core.Metadata;
using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Result of dependency resolution.
/// </summary>
public class ResolutionResult
{
    /// <summary>
    /// Whether resolution was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Resolved mod versions (canonical ID -> version).
    /// </summary>
    public Dictionary<string, SemanticVersion> ResolvedVersions { get; set; } = new();

    /// <summary>
    /// Install order (topologically sorted).
    /// </summary>
    public List<ModNode> InstallOrder { get; set; } = new();

    /// <summary>
    /// Conflicts that prevented resolution (if Success = false).
    /// </summary>
    public List<ResolutionConflict> Conflicts { get; set; } = new();

    /// <summary>
    /// Human-readable explanation of resolution failure.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Resolution graph showing the full dependency tree.
    /// </summary>
    public DependencyGraph? Graph { get; set; }
}

/// <summary>
/// Represents a conflict detected during resolution.
/// </summary>
public class ResolutionConflict
{
    /// <summary>
    /// The mod that has conflicting requirements.
    /// </summary>
    public string CanonicalId { get; set; } = string.Empty;

    /// <summary>
    /// Conflicting version constraints.
    /// </summary>
    public List<VersionConstraintSource> Constraints { get; set; } = new();

    /// <summary>
    /// Type of conflict.
    /// </summary>
    public ConflictType Type { get; set; }

    /// <summary>
    /// Human-readable explanation.
    /// </summary>
    public string Explanation { get; set; } = string.Empty;

    public override string ToString() => Explanation;
}

/// <summary>
/// Source of a version constraint.
/// </summary>
public class VersionConstraintSource
{
    /// <summary>
    /// The mod that imposed this constraint.
    /// </summary>
    public string SourceMod { get; set; } = string.Empty;

    /// <summary>
    /// The version constraint.
    /// </summary>
    public VersionRange? Constraint { get; set; }

    /// <summary>
    /// Dependency type that imposed this constraint.
    /// </summary>
    public DependencyType DependencyType { get; set; }

    public override string ToString()
    {
        var constraint = Constraint?.ToString() ?? "any version";
        return $"{SourceMod} requires {constraint}";
    }
}

/// <summary>
/// Type of resolution conflict.
/// </summary>
public enum ConflictType
{
    /// <summary>No satisfying version found for constraints.</summary>
    NoSatisfyingVersion,

    /// <summary>Circular dependency detected.</summary>
    CircularDependency,

    /// <summary>Incompatible mods both required.</summary>
    IncompatibleMods,

    /// <summary>Mod not found in any source.</summary>
    ModNotFound,

    /// <summary>No versions available for mod.</summary>
    NoVersionsAvailable
}
