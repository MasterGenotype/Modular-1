using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Models;
using Modular.Core.RateLimiting;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;
using System.Text.Json;

namespace Modular.Core.Services;

/// <summary>
/// Service for renaming mod folders and organizing by category.
/// Uses a metadata cache to minimize API calls.
/// </summary>
public class RenameService : IRenameService
{
    private const string BaseUrl = "https://api.nexusmods.com";
    private readonly IFluentClient _client;
    private readonly AppSettings _settings;
    private readonly ModMetadataCache _cache;
    private readonly ILogger<RenameService>? _logger;

    public RenameService(AppSettings settings, Modular.Core.RateLimiting.IRateLimiter rateLimiter, ModMetadataCache cache, ILogger<RenameService>? logger = null)
    {
        _settings = settings;
        _cache = cache;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl, new RateLimiterAdapter(rateLimiter), logger);
        _client.SetUserAgent("Modular/1.0");
    }

    /// <summary>
    /// Gets all game domain directories.
    /// </summary>
    public IEnumerable<string> GetGameDomainNames()
    {
        return FileUtils.GetGameDomains(_settings.ModsDirectory);
    }

    /// <summary>
    /// Gets all mod ID directories within a game domain.
    /// </summary>
    public IEnumerable<int> GetModIds(string gameDomainPath)
    {
        return FileUtils.GetModIdDirectories(gameDomainPath);
    }

    /// <summary>
    /// Gets mod metadata from cache, or fetches from API if not cached.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Mod metadata or null if unavailable</returns>
    public async Task<ModMetadata?> GetOrFetchModMetadataAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        // Check cache first
        var cached = _cache.GetModMetadata(gameDomain, modId);
        if (cached != null)
        {
            _logger?.LogDebug("Using cached metadata for mod {ModId}", modId);
            return cached;
        }

        // Fetch from API
        try
        {
            _logger?.LogDebug("Fetching metadata for mod {ModId} from API", modId);
            var response = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .WithCancellation(ct)
                .AsJsonAsync();

            string? name = null;
            int categoryId = 0;

            if (response.RootElement.TryGetProperty("name", out var nameProp))
                name = nameProp.GetString();

            if (response.RootElement.TryGetProperty("category_id", out var catProp))
                categoryId = catProp.GetInt32();

            if (!string.IsNullOrEmpty(name))
            {
                var metadata = new ModMetadata
                {
                    ModId = modId,
                    Name = name,
                    CategoryId = categoryId,
                    FetchedAt = DateTime.UtcNow
                };
                _cache.SetModMetadata(gameDomain, metadata);
                return metadata;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch metadata for mod {ModId}", modId);
        }
        return null;
    }

    /// <summary>
    /// Fetches metadata for multiple mods, using cache where available.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modIds">List of mod IDs to fetch</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of mods successfully fetched/cached</returns>
    public async Task<int> FetchModMetadataBatchAsync(string gameDomain, IEnumerable<int> modIds, CancellationToken ct = default)
    {
        var fetchedCount = 0;
        var modIdList = modIds.ToList();
        var uncachedIds = modIdList.Where(id => _cache.GetModMetadata(gameDomain, id) == null).ToList();

        _logger?.LogInformation("Fetching metadata: {Cached} cached, {ToFetch} to fetch",
            modIdList.Count - uncachedIds.Count, uncachedIds.Count);

        foreach (var modId in uncachedIds)
        {
            ct.ThrowIfCancellationRequested();

            var metadata = await GetOrFetchModMetadataAsync(gameDomain, modId, ct);
            if (metadata != null)
                fetchedCount++;
        }

        return fetchedCount;
    }

    /// <summary>
    /// Fetches and caches metadata for all mod directories in a game domain.
    /// This is useful for pre-populating the cache without renaming.
    /// </summary>
    /// <param name="gameDomainPath">Path to the game domain directory</param>
    /// <param name="gameDomain">Game domain name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of mods with metadata fetched/cached</returns>
    public async Task<int> FetchAndCacheMetadataAsync(string gameDomainPath, string gameDomain, CancellationToken ct = default)
    {
        // Fetch game categories first
        await GetOrFetchGameCategoriesAsync(gameDomain, ct);

        // Find all mod ID directories
        var modIds = GetModIds(gameDomainPath).ToList();
        _logger?.LogInformation("Found {Count} mod directories in {Domain}", modIds.Count, gameDomain);

        return await FetchModMetadataBatchAsync(gameDomain, modIds, ct);
    }

    /// <summary>
    /// Reorganizes and renames mods in a game domain directory.
    /// Uses cached metadata - call FetchModMetadataBatchAsync first to populate cache.
    /// Searches both top-level directories and inside category subdirectories.
    /// Supports both numeric (unrenamed) and named (already renamed) directories.
    /// </summary>
    public async Task<int> ReorganizeAndRenameModsAsync(string gameDomainPath, bool organizeByCategory = true, CancellationToken ct = default)
    {
        var gameDomain = Path.GetFileName(gameDomainPath);
        
        // Create a lookup function for metadata by directory name
        Func<string, ModMetadata?> metadataLookup = (dirName) => _cache.FindModByDirectoryName(gameDomain, dirName);
        
        // Find all mod directories (both numeric and already-renamed)
        var modEntries = FileUtils.GetAllModDirectoriesWithMetadata(gameDomainPath, metadataLookup).ToList();
        var renamedCount = 0;

        _logger?.LogInformation("Found {Count} mod directories to process in {Domain}", modEntries.Count, gameDomain);

        // Get categories from cache or fetch if needed
        Dictionary<int, string>? categories = null;
        if (organizeByCategory)
        {
            categories = await GetOrFetchGameCategoriesAsync(gameDomain, ct);
        }

        foreach (var (modId, oldPath, isRenamed) in modEntries)
        {
            ct.ThrowIfCancellationRequested();

            // Get metadata from cache (or fetch if not cached)
            var metadata = await GetOrFetchModMetadataAsync(gameDomain, modId, ct);

            if (metadata == null || string.IsNullOrEmpty(metadata.Name))
            {
                _logger?.LogWarning("No metadata available for mod {ModId}", modId);
                continue;
            }

            var sanitizedName = FileUtils.SanitizeDirectoryName(metadata.Name);
            string newPath;

            if (organizeByCategory && categories != null)
            {
                var categoryName = categories.TryGetValue(metadata.CategoryId, out var name)
                    ? FileUtils.SanitizeDirectoryName(name)
                    : $"Category_{metadata.CategoryId}";

                var categoryPath = Path.Combine(gameDomainPath, categoryName);
                FileUtils.EnsureDirectoryExists(categoryPath);
                newPath = Path.Combine(categoryPath, sanitizedName);
            }
            else
            {
                newPath = Path.Combine(gameDomainPath, sanitizedName);
            }

            if (oldPath != newPath)
            {
                try
                {
                    if (FileUtils.MoveDirectory(oldPath, newPath))
                    {
                        _logger?.LogInformation("{Action}: {OldName} -> {Category}/{ModName}",
                            isRenamed ? "Reorganized" : "Renamed",
                            Path.GetFileName(oldPath),
                            organizeByCategory && categories != null ? Path.GetFileName(Path.GetDirectoryName(newPath)) : "",
                            metadata.Name);
                        renamedCount++;
                    }
                    else
                    {
                        _logger?.LogWarning("Could not fully move {OldPath} to {NewPath}", oldPath, newPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to move {OldPath} to {NewPath}", oldPath, newPath);
                }
            }
        }

        return renamedCount;
    }

    /// <summary>
    /// Gets game categories from cache, or fetches from API if not cached.
    /// </summary>
    public async Task<Dictionary<int, string>> GetOrFetchGameCategoriesAsync(string gameDomain, CancellationToken ct = default)
    {
        // Check cache first
        var cached = _cache.GetGameCategories(gameDomain);
        if (cached != null)
        {
            _logger?.LogDebug("Using cached categories for {Domain}", gameDomain);
            return cached;
        }

        // Fetch from API
        try
        {
            _logger?.LogDebug("Fetching categories for {Domain} from API", gameDomain);
            var response = await _client.GetAsync($"v1/games/{gameDomain}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .WithCancellation(ct)
                .AsJsonAsync();

            var result = new Dictionary<int, string>();
            if (response.RootElement.TryGetProperty("categories", out var categoriesElement))
            {
                foreach (var cat in categoriesElement.EnumerateArray())
                {
                    if (cat.TryGetProperty("category_id", out var idProp) &&
                        cat.TryGetProperty("name", out var nameProp))
                    {
                        result[idProp.GetInt32()] = nameProp.GetString() ?? string.Empty;
                    }
                }
            }

            // Cache the result
            _cache.SetGameCategories(gameDomain, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch categories for {Domain}", gameDomain);
            return new Dictionary<int, string>();
        }
    }

    /// <summary>
    /// Renames category folders from Category_N to actual category names.
    /// </summary>
    public async Task<int> RenameCategoryFoldersAsync(string gameDomainPath, CancellationToken ct = default)
    {
        var gameDomain = Path.GetFileName(gameDomainPath);
        var categories = await GetOrFetchGameCategoriesAsync(gameDomain, ct);
        var renamedCount = 0;

        foreach (var dir in Directory.GetDirectories(gameDomainPath))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.StartsWith("Category_") && int.TryParse(dirName[9..], out var catId))
            {
                if (categories.TryGetValue(catId, out var catName))
                {
                    var newPath = Path.Combine(gameDomainPath, FileUtils.SanitizeDirectoryName(catName));
                    if (dir != newPath)
                    {
                        try
                        {
                            if (FileUtils.MoveDirectory(dir, newPath))
                            {
                                _logger?.LogInformation("Renamed category: {OldName} -> {NewName}", dirName, catName);
                                renamedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to rename category {OldPath}", dir);
                        }
                    }
                }
            }
        }

        return renamedCount;
    }

    private class RateLimiterAdapter : Modular.FluentHttp.Interfaces.IRateLimiter
    {
        private readonly Modular.Core.RateLimiting.IRateLimiter _inner;
        public RateLimiterAdapter(Modular.Core.RateLimiting.IRateLimiter inner) => _inner = inner;
        public void UpdateFromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) => _inner.UpdateFromHeaders(headers);
        public bool CanMakeRequest() => _inner.CanMakeRequest();
        public Task WaitIfNeededAsync(CancellationToken ct = default) => _inner.WaitIfNeededAsync(ct);
        public void ReserveRequest() => _inner.ReserveRequest();
    }
}
