using Modular.Core.Versioning;

namespace Modular.Core.Dependencies;

/// <summary>
/// Represents a mod node in the dependency graph.
/// Each node represents a specific mod (identified by canonical ID) at a specific version.
/// </summary>
public class ModNode : IEquatable<ModNode>
{
    /// <summary>
    /// Canonical mod identifier (e.g., "nexusmods:skyrim:12345").
    /// </summary>
    public string CanonicalId { get; set; } = string.Empty;

    /// <summary>
    /// Version of this mod. Null means "any version" or version not yet resolved.
    /// </summary>
    public SemanticVersion? Version { get; set; }

    /// <summary>
    /// Human-readable mod name for display.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional metadata for this node (used during resolution).
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    public ModNode()
    {
    }

    public ModNode(string canonicalId, SemanticVersion? version = null, string? displayName = null)
    {
        CanonicalId = canonicalId;
        Version = version;
        DisplayName = displayName;
    }

    /// <summary>
    /// Gets a unique identifier for this node (canonical ID + version).
    /// </summary>
    public string GetNodeId()
    {
        return Version != null
            ? $"{CanonicalId}@{Version}"
            : CanonicalId;
    }

    public bool Equals(ModNode? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return CanonicalId == other.CanonicalId && 
               Equals(Version, other.Version);
    }

    public override bool Equals(object? obj) => obj is ModNode other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(CanonicalId, Version);

    public override string ToString()
    {
        var name = !string.IsNullOrEmpty(DisplayName) ? DisplayName : CanonicalId;
        return Version != null ? $"{name} v{Version}" : name;
    }

    public static bool operator ==(ModNode? left, ModNode? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(ModNode? left, ModNode? right) =>
        !(left == right);
}
