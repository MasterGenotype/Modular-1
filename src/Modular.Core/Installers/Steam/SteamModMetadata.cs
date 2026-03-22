using Modular.Core.Versioning;

namespace Modular.Core.Installers.Steam;

/// <summary>
/// Metadata describing a Steam game mod, including its identity, version,
/// dependencies, and archive location.
/// </summary>
public class SteamModMetadata
{
    /// <summary>
    /// Unique mod name (used as canonical identifier).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Target Steam game identifier (e.g., "GameX", "HalfLife2").
    /// </summary>
    public string TargetGame { get; set; } = string.Empty;

    /// <summary>
    /// List of dependency specifications. Each entry can be a plain mod name
    /// (e.g., "ModB") or include a version constraint (e.g., "ModB>=1.0.0").
    /// </summary>
    public List<SteamModDependency> Dependencies { get; set; } = new();

    /// <summary>
    /// Semantic version string for this mod (e.g., "1.0.0", "2.3.1-beta").
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Expected checksum for archive integrity verification.
    /// Placeholder: extendable to real SHA256 verification.
    /// </summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>
    /// Path to the mod archive file (.zip, .tar.gz, or .tar).
    /// </summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>
    /// Parses the version string into a <see cref="SemanticVersion"/>.
    /// </summary>
    public SemanticVersion GetSemanticVersion()
    {
        return SemanticVersion.Parse(Version);
    }
}

/// <summary>
/// Represents a dependency of a Steam mod, with an optional version constraint
/// and optional/conditional flags.
/// </summary>
public class SteamModDependency
{
    /// <summary>
    /// Name of the required mod.
    /// </summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>
    /// Version constraint string (e.g., ">=1.0.0", "^2.0.0"). Null means any version.
    /// </summary>
    public string? VersionConstraint { get; set; }

    /// <summary>
    /// Whether this dependency is optional (recommended but not required).
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>
    /// Condition under which this dependency is needed (e.g., "linux", "with-extras").
    /// Null means unconditional.
    /// </summary>
    public string? Condition { get; set; }

    /// <summary>
    /// Creates a required dependency with no version constraint.
    /// </summary>
    public static SteamModDependency Required(string modName, string? versionConstraint = null)
    {
        return new SteamModDependency { ModName = modName, VersionConstraint = versionConstraint };
    }

    /// <summary>
    /// Creates an optional dependency.
    /// </summary>
    public static SteamModDependency Optional(string modName, string? versionConstraint = null)
    {
        return new SteamModDependency { ModName = modName, VersionConstraint = versionConstraint, IsOptional = true };
    }

    /// <summary>
    /// Creates a conditional dependency.
    /// </summary>
    public static SteamModDependency Conditional(string modName, string condition, string? versionConstraint = null)
    {
        return new SteamModDependency { ModName = modName, VersionConstraint = versionConstraint, Condition = condition };
    }
}
