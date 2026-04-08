using Microsoft.Extensions.Logging;
using Modular.Core.Backends.NexusMods;
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
/// Uses GraphQL batch queries to minimize API calls and adaptive pacing
/// to spread requests evenly across the hourly rate limit window.
/// </summary>
public class RenameService : IRenameService
{
    private const string BaseUrl = "https://api.nexusmods.com";
    private readonly IFluentClient _client;
    private readonly NexusModsGraphQlClient _graphQlClient;
    private readonly Modular.Core.RateLimiting.IRateLimiter _rateLimiter;
    private readonly AppSettings _settings;
    private readonly ModMetadataCache _cache;
    private readonly ILogger<RenameService>? _logger;

    public RenameService(AppSettings settings, Modular.Core.RateLimiting.IRateLimiter rateLimiter, ModMetadataCache cache, ILogger<RenameService>? logger = null)
    {
        _settings = settings;
        _rateLimiter = rateLimiter;
        _cache = cache;
        _logger = logger;
        var adapter = new RateLimiterAdapter(rateLimiter);
        _client = FluentClientFactory.Create(BaseUrl, adapter, logger);
        _client.SetUserAgent("Modular/1.0");
        _graphQlClient = new NexusModsGraphQlClient(settings.NexusApiKey, adapter, logger);
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
    /// For hidden/removed mods where the API returns name=null, the category_id
    /// is still available. The caller can supply a fallback name (e.g. from directory contents).
    /// </summary>
    public async Task<ModMetadata?> GetOrFetchModMetadataAsync(string gameDomain, int modId, CancellationToken ct = default, string? fallbackName = null)
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

            // Hidden/removed mods return name=null but still have category_id.
            // Use the fallback name (derived from directory contents) if API name is missing.
            if (string.IsNullOrEmpty(name))
                name = fallbackName;

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

            _logger?.LogDebug("Mod {ModId} has no name from API and no fallback", modId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch metadata for mod {ModId}", modId);
        }
        return null;
    }

    /// <summary>
    /// Fetches metadata for multiple mods using GraphQL batch queries.
    /// Fetches up to 20 mods per API call (vs 1 per call with REST).
    /// Falls back to individual REST calls if GraphQL fails.
    /// </summary>
    public async Task<int> FetchModMetadataBatchAsync(string gameDomain, IEnumerable<int> modIds, CancellationToken ct = default)
    {
        var modIdList = modIds.ToList();
        var uncachedIds = modIdList.Where(id => _cache.GetModMetadata(gameDomain, id) == null).ToList();
        var cachedCount = modIdList.Count - uncachedIds.Count;

        _logger?.LogInformation("Fetching metadata: {Cached} cached, {ToFetch} to fetch",
            cachedCount, uncachedIds.Count);

        if (uncachedIds.Count == 0)
            return cachedCount;

        // Pre-fetch game categories so we can resolve GraphQL category strings to IDs
        var gameCategories = await GetOrFetchGameCategoriesAsync(gameDomain, ct);
        var reverseCategoryLookup = gameCategories.ToDictionary(
            kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        // Try GraphQL batch first (20 mods per request vs 1)
        var fetchedCount = 0;
        try
        {
            var pacingDelay = _rateLimiter.GetRecommendedDelay();
            _logger?.LogDebug("Using GraphQL batch with {Delay}ms pacing between batches", pacingDelay.TotalMilliseconds);

            var requestedIds = uncachedIds.ToHashSet();
            var results = await _graphQlClient.FetchModsByIdsBatchedAsync(gameDomain, uncachedIds, pacingDelay, ct);

            foreach (var mod in results)
            {
                // Only cache mods we actually requested
                if (!requestedIds.Contains(mod.ModId)) continue;

                // Resolve category ID: prefer modCategory.categoryId, fall back to
                // reverse-looking up the category string name in the game categories
                var categoryId = mod.ModCategory?.CategoryId ?? 0;
                if (categoryId == 0 && !string.IsNullOrEmpty(mod.Category) && reverseCategoryLookup.TryGetValue(mod.Category, out var resolvedId))
                {
                    categoryId = resolvedId;
                }

                var metadata = new ModMetadata
                {
                    ModId = mod.ModId,
                    Name = mod.Name,
                    CategoryId = categoryId,
                    FetchedAt = DateTime.UtcNow
                };
                _cache.SetModMetadata(gameDomain, metadata);
                fetchedCount++;
            }

            // Fetch any IDs that GraphQL didn't return (removed/hidden mods) via REST
            var returnedIds = results.Where(m => requestedIds.Contains(m.ModId))
                                     .Select(m => m.ModId).ToHashSet();
            var missingIds = uncachedIds.Where(id => !returnedIds.Contains(id)).ToList();
            if (missingIds.Count > 0)
            {
                _logger?.LogDebug("{Count} mod(s) not returned by GraphQL, falling back to REST", missingIds.Count);
                fetchedCount += await FetchModMetadataIndividuallyAsync(gameDomain, missingIds, ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GraphQL batch fetch failed, falling back to individual REST calls");
            fetchedCount = await FetchModMetadataIndividuallyAsync(gameDomain, uncachedIds, ct);
        }

        return fetchedCount;
    }

    /// <summary>
    /// Fallback: fetches mod metadata one at a time via REST API.
    /// FluentClient already handles rate limiting — no additional pacing needed.
    /// </summary>
    private async Task<int> FetchModMetadataIndividuallyAsync(string gameDomain, List<int> modIds, CancellationToken ct)
    {
        var fetchedCount = 0;

        foreach (var modId in modIds)
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

        // Find numeric mod ID directories
        var modIds = GetModIds(gameDomainPath).ToList();

        // Also discover already-renamed directories via the persistent cache.
        // Without this, mods renamed in a previous session are invisible.
        ModMetadata? MetadataLookup(string dirName) => _cache.FindModByDirectoryName(gameDomain, dirName);
        var allEntries = FileUtils.GetAllModDirectoriesWithMetadata(gameDomainPath, MetadataLookup);
        foreach (var (modId, _, _) in allEntries)
        {
            if (!modIds.Contains(modId))
                modIds.Add(modId);
        }

        _logger?.LogInformation("Found {Count} mod directories in {Domain}", modIds.Count, gameDomain);

        var fetched = await FetchModMetadataBatchAsync(gameDomain, modIds, ct);
        await _cache.SaveAsync();
        return fetched;
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

            // Derive a fallback name from directory contents for hidden/removed mods
            // (NexusMods API returns name=null for these, but category_id is still available).
            string? fallbackName = null;
            if (!isRenamed && Directory.Exists(oldPath))
            {
                var subdirs = Directory.GetDirectories(oldPath);
                if (subdirs.Length >= 1)
                {
                    // Use the first subdirectory's name as the mod name
                    fallbackName = Path.GetFileName(subdirs[0]);
                }
                else
                {
                    // No subdirs — use first file's name without extension
                    var files = Directory.GetFiles(oldPath);
                    if (files.Length > 0)
                        fallbackName = Path.GetFileNameWithoutExtension(files[0]);
                }
            }
            else if (isRenamed)
            {
                // Already-renamed directory — its name IS the mod name
                fallbackName = Path.GetFileName(oldPath);
            }
            // Last resort: use mod ID
            fallbackName ??= $"Mod_{modId}";

            // Get metadata from cache (or fetch if not cached)
            var metadata = await GetOrFetchModMetadataAsync(gameDomain, modId, ct, fallbackName);

            // If cached metadata has no category and we need categories, re-fetch from REST
            // to get the reliable category_id (GraphQL sometimes returns null modCategory)
            if (metadata != null && organizeByCategory && metadata.CategoryId == 0)
            {
                _logger?.LogDebug("Re-fetching mod {ModId} via REST for category data", modId);
                _cache.RemoveModMetadata(gameDomain, modId);
                metadata = await GetOrFetchModMetadataAsync(gameDomain, modId, ct, fallbackName);
            }

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

        // Persist cache so renamed mods are discoverable in future sessions
        await _cache.SaveAsync();
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
