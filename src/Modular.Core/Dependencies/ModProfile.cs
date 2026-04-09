using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Core.Dependencies;

/// <summary>
/// Represents a mod profile - a saved configuration of mods with pinned versions.
/// Provides reproducible mod setups across installations.
/// </summary>
public class ModProfile
{
    /// <summary>
    /// Unique profile identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Human-readable profile name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default Profile";

    /// <summary>
    /// Target game identifier.
    /// </summary>
    [JsonPropertyName("game")]
    public string? Game { get; set; }

    /// <summary>
    /// Profile description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Enabled mods with their pinned versions.
    /// </summary>
    [JsonPropertyName("mods")]
    public List<ProfileMod> Mods { get; set; } = new();

    /// <summary>
    /// Load order for mods (canonical IDs in order).
    /// </summary>
    [JsonPropertyName("load_order")]
    public List<string> LoadOrder { get; set; } = new();

    /// <summary>
    /// Manual resolution overrides.
    /// </summary>
    [JsonPropertyName("resolution_overrides")]
    public Dictionary<string, string> ResolutionOverrides { get; set; } = new();

    /// <summary>
    /// When this profile was created.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this profile was last modified.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Profile metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// A mod entry in a profile.
/// </summary>
public class ProfileMod
{
    /// <summary>
    /// Canonical mod ID.
    /// </summary>
    [JsonPropertyName("canonical_id")]
    public string CanonicalId { get; set; } = string.Empty;

    /// <summary>
    /// Pinned version (null means latest).
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Whether this mod is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional notes about this mod in the profile.
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Lockfile representing a resolved dependency graph.
/// Ensures reproducible installations.
/// </summary>
public class ModLockfile
{
    /// <summary>
    /// Lockfile format version.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Profile ID this lockfile was generated from.
    /// </summary>
    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; set; }

    /// <summary>
    /// When this lockfile was generated.
    /// </summary>
    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Resolved mods with their versions.
    /// </summary>
    [JsonPropertyName("mods")]
    public Dictionary<string, LockfileMod> Mods { get; set; } = new();

    /// <summary>
    /// Resolved install order.
    /// </summary>
    [JsonPropertyName("install_order")]
    public List<string> InstallOrder { get; set; } = new();
}

/// <summary>
/// A mod entry in a lockfile.
/// </summary>
public class LockfileMod
{
    /// <summary>
    /// Resolved version.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Direct dependencies.
    /// </summary>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new();

    /// <summary>
    /// Backend source.
    /// </summary>
    [JsonPropertyName("backend")]
    public string? Backend { get; set; }

    /// <summary>
    /// Checksum for verification.
    /// </summary>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }
}
