using Modular.Core.Database;

namespace Modular.Core.Services;

/// <summary>
/// Interface for mod renaming and organization operations.
/// </summary>
public interface IRenameService
{
    /// <summary>
    /// Gets the NexusMods game domains that have a folder under <c>nexusmods/</c>.
    /// </summary>
    IEnumerable<string> GetGameDomainNames();

    /// <summary>
    /// Gets the folder holding a NexusMods game's downloads.
    /// </summary>
    string GetGameDomainPath(string gameDomain);

    /// <summary>
    /// Gets all mod ID directories within a game domain.
    /// </summary>
    IEnumerable<int> GetModIds(string gameDomainPath);

    /// <summary>
    /// Gets mod metadata from cache, or fetches from API if not cached.
    /// For hidden/removed mods, fallbackName is used when the API returns no name.
    /// </summary>
    Task<ModMetadata?> GetOrFetchModMetadataAsync(string gameDomain, int modId, CancellationToken ct = default, string? fallbackName = null);

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
    /// <param name="gameDomainPath">The game's folder; its name is the game domain.</param>
    /// <param name="organizeByCategory">Whether to sort mods into category folders.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="folderMoved">Called with the old and new path of every moved folder.</param>
    Task<int> ReorganizeAndRenameModsAsync(
        string gameDomainPath,
        bool organizeByCategory = true,
        CancellationToken ct = default,
        Func<string, string, CancellationToken, Task>? folderMoved = null);

    /// <summary>
    /// Gets game categories from cache, or fetches from API if not cached.
    /// </summary>
    Task<Dictionary<int, string>> GetOrFetchGameCategoriesAsync(string gameDomain, CancellationToken ct = default);

    /// <summary>
    /// Renames category folders from Category_N to actual category names.
    /// </summary>
    Task<int> RenameCategoryFoldersAsync(
        string gameDomainPath,
        CancellationToken ct = default,
        Func<string, string, CancellationToken, Task>? folderMoved = null);
}
