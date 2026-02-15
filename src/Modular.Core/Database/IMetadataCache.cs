using Modular.Core.Metadata;

namespace Modular.Core.Database;

/// <summary>
/// Interface for mod metadata caching.
/// Abstracts the storage implementation from consumers.
/// </summary>
public interface IMetadataCache
{
    /// <summary>
    /// Gets cached mod metadata if available (legacy format).
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <returns>Cached metadata or null if not cached</returns>
    ModMetadata? GetModMetadata(string gameDomain, int modId);

    /// <summary>
    /// Gets cached canonical mod if available.
    /// </summary>
    /// <param name="canonicalId">Canonical ID (e.g., "nexusmods:skyrim:12345")</param>
    /// <returns>Cached canonical mod or null if not cached</returns>
    CanonicalMod? GetCanonicalMod(string canonicalId);

    /// <summary>
    /// Stores mod metadata in the cache (legacy format).
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="metadata">Mod metadata to cache</param>
    void SetModMetadata(string gameDomain, ModMetadata metadata);

    /// <summary>
    /// Stores canonical mod in the cache.
    /// </summary>
    /// <param name="mod">Canonical mod to cache</param>
    void SetCanonicalMod(CanonicalMod mod);

    /// <summary>
    /// Gets cached game categories if available.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>Dictionary of category ID to name, or null if not cached</returns>
    Dictionary<int, string>? GetGameCategories(string gameDomain);

    /// <summary>
    /// Stores game categories in the cache.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="categories">Dictionary of category ID to name</param>
    void SetGameCategories(string gameDomain, Dictionary<int, string> categories);

    /// <summary>
    /// Gets the number of cached mods for a game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>Number of cached mods</returns>
    int GetCachedModCount(string gameDomain);

    /// <summary>
    /// Gets all cached mod IDs for a game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>List of cached mod IDs</returns>
    IEnumerable<int> GetCachedModIds(string gameDomain);

    /// <summary>
    /// Finds a mod by matching its sanitized name against cached metadata.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="directoryName">Directory name to match</param>
    /// <returns>Mod metadata if found, null otherwise</returns>
    ModMetadata? FindModByDirectoryName(string gameDomain, string directoryName);

    /// <summary>
    /// Clears all cached data for a specific game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain to clear</param>
    void ClearDomain(string gameDomain);

    /// <summary>
    /// Clears all cached data.
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Persists the cache to storage.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Loads the cache from storage.
    /// </summary>
    Task LoadAsync();
}
