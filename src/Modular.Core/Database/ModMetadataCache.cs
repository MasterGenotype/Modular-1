using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Core.Database;

/// <summary>
/// Cached metadata for a mod.
/// </summary>
public class ModMetadata
{
    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("fetched_at")]
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cached category information for a game.
/// </summary>
public class GameCategoryCache
{
    [JsonPropertyName("game_domain")]
    public string GameDomain { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    public Dictionary<int, string> Categories { get; set; } = [];

    [JsonPropertyName("fetched_at")]
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Root structure for the metadata cache file.
/// </summary>
public class MetadataCacheData
{
    [JsonPropertyName("mods")]
    public Dictionary<string, Dictionary<int, ModMetadata>> Mods { get; set; } = [];

    [JsonPropertyName("game_categories")]
    public Dictionary<string, GameCategoryCache> GameCategories { get; set; } = [];
}

/// <summary>
/// JSON-based cache for mod metadata and game categories.
/// Reduces API calls by caching previously fetched data.
/// Thread-safe for concurrent access.
/// </summary>
public class ModMetadataCache
{
    private readonly string _cachePath;
    private MetadataCacheData _data = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Creates/opens a metadata cache at the specified path.
    /// </summary>
    /// <param name="cachePath">Path to cache file (will be created if doesn't exist)</param>
    public ModMetadataCache(string cachePath)
    {
        _cachePath = cachePath;
    }

    /// <summary>
    /// Gets cached mod metadata if available.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <returns>Cached metadata or null if not cached</returns>
    public ModMetadata? GetModMetadata(string gameDomain, int modId)
    {
        lock (_lock)
        {
            if (_data.Mods.TryGetValue(gameDomain, out var mods) &&
                mods.TryGetValue(modId, out var metadata))
            {
                return metadata;
            }
            return null;
        }
    }

    /// <summary>
    /// Stores mod metadata in the cache.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="metadata">Mod metadata to cache</param>
    public void SetModMetadata(string gameDomain, ModMetadata metadata)
    {
        lock (_lock)
        {
            if (!_data.Mods.ContainsKey(gameDomain))
                _data.Mods[gameDomain] = [];

            _data.Mods[gameDomain][metadata.ModId] = metadata;
        }
    }

    /// <summary>
    /// Gets cached game categories if available.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>Dictionary of category ID to name, or null if not cached</returns>
    public Dictionary<int, string>? GetGameCategories(string gameDomain)
    {
        lock (_lock)
        {
            if (_data.GameCategories.TryGetValue(gameDomain, out var cache))
            {
                return cache.Categories;
            }
            return null;
        }
    }

    /// <summary>
    /// Stores game categories in the cache.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="categories">Dictionary of category ID to name</param>
    public void SetGameCategories(string gameDomain, Dictionary<int, string> categories)
    {
        lock (_lock)
        {
            _data.GameCategories[gameDomain] = new GameCategoryCache
            {
                GameDomain = gameDomain,
                Categories = categories,
                FetchedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Gets the number of cached mods for a game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>Number of cached mods</returns>
    public int GetCachedModCount(string gameDomain)
    {
        lock (_lock)
        {
            if (_data.Mods.TryGetValue(gameDomain, out var mods))
                return mods.Count;
            return 0;
        }
    }

    /// <summary>
    /// Gets all cached mod IDs for a game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>List of cached mod IDs</returns>
    public IEnumerable<int> GetCachedModIds(string gameDomain)
    {
        lock (_lock)
        {
            if (_data.Mods.TryGetValue(gameDomain, out var mods))
                return mods.Keys.ToList();
            return [];
        }
    }

    /// <summary>
    /// Saves the cache to disk.
    /// </summary>
    public async Task SaveAsync()
    {
        MetadataCacheData snapshot;
        lock (_lock)
        {
            // Deep copy to avoid holding lock during IO
            snapshot = new MetadataCacheData
            {
                Mods = _data.Mods.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToDictionary(m => m.Key, m => m.Value)),
                GameCategories = _data.GameCategories.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value)
            };
        }

        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(_cachePath, json);
    }

    /// <summary>
    /// Loads the cache from disk.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(_cachePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath);
            var data = JsonSerializer.Deserialize<MetadataCacheData>(json, JsonOptions);

            lock (_lock)
            {
                _data = data ?? new MetadataCacheData();
            }
        }
        catch (JsonException)
        {
            // Invalid cache, start fresh
            lock (_lock)
            {
                _data = new MetadataCacheData();
            }
        }
    }

    /// <summary>
    /// Clears all cached data for a specific game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain to clear</param>
    public void ClearDomain(string gameDomain)
    {
        lock (_lock)
        {
            _data.Mods.Remove(gameDomain);
            _data.GameCategories.Remove(gameDomain);
        }
    }

    /// <summary>
    /// Finds a mod by matching its sanitized name against cached metadata.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="directoryName">Directory name to match</param>
    /// <returns>Mod metadata if found, null otherwise</returns>
    public ModMetadata? FindModByDirectoryName(string gameDomain, string directoryName)
    {
        lock (_lock)
        {
            if (!_data.Mods.TryGetValue(gameDomain, out var mods))
                return null;

            var sanitizedDirName = Utilities.FileUtils.SanitizeDirectoryName(directoryName);
            
            // Try exact match first
            foreach (var mod in mods.Values)
            {
                var sanitizedModName = Utilities.FileUtils.SanitizeDirectoryName(mod.Name);
                if (sanitizedModName.Equals(sanitizedDirName, StringComparison.OrdinalIgnoreCase))
                    return mod;
            }

            // Try partial match (directory name equals sanitized mod name)
            foreach (var mod in mods.Values)
            {
                var sanitizedModName = Utilities.FileUtils.SanitizeDirectoryName(mod.Name);
                if (directoryName.Equals(sanitizedModName, StringComparison.OrdinalIgnoreCase))
                    return mod;
            }

            return null;
        }
    }

    /// <summary>
    /// Clears all cached data.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _data = new MetadataCacheData();
        }
    }
}
