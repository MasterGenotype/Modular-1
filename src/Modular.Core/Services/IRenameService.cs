using Modular.Core.Database;

namespace Modular.Core.Services;

/// <summary>
/// Interface for mod renaming and organization operations.
/// </summary>
public interface IRenameService
{
    /// <summary>
    /// Gets all game domain directories.
    /// </summary>
    IEnumerable<string> GetGameDomainNames();

    /// <summary>
    /// Gets all mod ID directories within a game domain.
    /// </summary>
    IEnumerable<int> GetModIds(string gameDomainPath);

    /// <summary>
    /// Gets mod metadata from cache, or fetches from API if not cached.
    /// </summary>
    Task<ModMetadata?> GetOrFetchModMetadataAsync(string gameDomain, int modId, CancellationToken ct = default);

    /// <summary>
    /// Fetches metadata for multiple mods, using cache where available.
    /// </summary>
    Task<int> FetchModMetadataBatchAsync(string gameDomain, IEnumerable<int> modIds, CancellationToken ct = default);

    /// <summary>
    /// Fetches and caches metadata for all mod directories in a game domain.
    /// </summary>
    Task<int> FetchAndCacheMetadataAsync(string gameDomainPath, string gameDomain, CancellationToken ct = default);

    /// <summary>
    /// Reorganizes and renames mods in a game domain directory.
    /// </summary>
    Task<int> ReorganizeAndRenameModsAsync(string gameDomainPath, bool organizeByCategory = true, CancellationToken ct = default);

    /// <summary>
    /// Gets game categories from cache, or fetches from API if not cached.
    /// </summary>
    Task<Dictionary<int, string>> GetOrFetchGameCategoriesAsync(string gameDomain, CancellationToken ct = default);

    /// <summary>
    /// Renames category folders from Category_N to actual category names.
    /// </summary>
    Task<int> RenameCategoryFoldersAsync(string gameDomainPath, CancellationToken ct = default);
}
