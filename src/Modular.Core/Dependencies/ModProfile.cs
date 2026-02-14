using System.Text.Json;
using System.Text.Json.Serialization;
using Modular.Core.Versioning;

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

/// <summary>
/// Manages mod profiles and lockfiles.
/// </summary>
public class ProfileManager
{
    private readonly string _profilesDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ProfileManager(string profilesDirectory)
    {
        _profilesDirectory = profilesDirectory;
        Directory.CreateDirectory(_profilesDirectory);
    }

    /// <summary>
    /// Saves a profile to disk.
    /// </summary>
    public async Task SaveProfileAsync(ModProfile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        var path = GetProfilePath(profile.Id);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Loads a profile from disk.
    /// </summary>
    public async Task<ModProfile?> LoadProfileAsync(string profileId)
    {
        var path = GetProfilePath(profileId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<ModProfile>(json, JsonOptions);
    }

    /// <summary>
    /// Lists all available profiles.
    /// </summary>
    public async Task<List<ModProfile>> ListProfilesAsync()
    {
        var profiles = new List<ModProfile>();
        var files = Directory.GetFiles(_profilesDirectory, "*.json");

        foreach (var file in files)
        {
            if (file.EndsWith(".lock.json"))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(file);
                var profile = JsonSerializer.Deserialize<ModProfile>(json, JsonOptions);
                if (profile != null)
                    profiles.Add(profile);
            }
            catch
            {
                // Skip invalid profiles
            }
        }

        return profiles.OrderBy(p => p.Name).ToList();
    }

    /// <summary>
    /// Deletes a profile.
    /// </summary>
    public Task DeleteProfileAsync(string profileId)
    {
        var path = GetProfilePath(profileId);
        if (File.Exists(path))
            File.Delete(path);

        var lockPath = GetLockfilePath(profileId);
        if (File.Exists(lockPath))
            File.Delete(lockPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Exports a profile to a file.
    /// </summary>
    public async Task ExportProfileAsync(string profileId, string exportPath)
    {
        var profile = await LoadProfileAsync(profileId);
        if (profile == null)
            throw new FileNotFoundException($"Profile {profileId} not found");

        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(exportPath, json);
    }

    /// <summary>
    /// Imports a profile from a file.
    /// </summary>
    public async Task<ModProfile> ImportProfileAsync(string importPath)
    {
        var json = await File.ReadAllTextAsync(importPath);
        var profile = JsonSerializer.Deserialize<ModProfile>(json, JsonOptions);
        if (profile == null)
            throw new InvalidDataException("Invalid profile format");

        // Generate new ID for imported profile
        profile.Id = Guid.NewGuid().ToString();
        await SaveProfileAsync(profile);

        return profile;
    }

    /// <summary>
    /// Saves a lockfile.
    /// </summary>
    public async Task SaveLockfileAsync(string profileId, ModLockfile lockfile)
    {
        lockfile.ProfileId = profileId;
        lockfile.GeneratedAt = DateTime.UtcNow;

        var path = GetLockfilePath(profileId);
        var json = JsonSerializer.Serialize(lockfile, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Loads a lockfile.
    /// </summary>
    public async Task<ModLockfile?> LoadLockfileAsync(string profileId)
    {
        var path = GetLockfilePath(profileId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<ModLockfile>(json, JsonOptions);
    }

    /// <summary>
    /// Generates a lockfile from a resolution result.
    /// </summary>
    public ModLockfile GenerateLockfile(ResolutionResult result, string? profileId = null)
    {
        var lockfile = new ModLockfile
        {
            ProfileId = profileId,
            InstallOrder = result.InstallOrder.Select(n => n.CanonicalId).ToList()
        };

        foreach (var (modId, version) in result.ResolvedVersions)
        {
            lockfile.Mods[modId] = new LockfileMod
            {
                Version = version.ToString(),
                Backend = modId.Split(':').FirstOrDefault()
            };
        }

        // Add dependencies from graph
        if (result.Graph != null)
        {
            foreach (var node in result.Graph.GetAllNodes())
            {
                var dependencies = result.Graph.GetDependencies(node);
                if (dependencies.Count > 0 && lockfile.Mods.ContainsKey(node.CanonicalId))
                {
                    lockfile.Mods[node.CanonicalId].Dependencies = dependencies
                        .Where(e => e.Type == Modular.Core.Metadata.DependencyType.Required)
                        .ToDictionary(
                            e => e.To.CanonicalId,
                            e => e.To.Version?.ToString() ?? "*"
                        );
                }
            }
        }

        return lockfile;
    }

    private string GetProfilePath(string profileId) =>
        Path.Combine(_profilesDirectory, $"{profileId}.json");

    private string GetLockfilePath(string profileId) =>
        Path.Combine(_profilesDirectory, $"{profileId}.lock.json");
}
