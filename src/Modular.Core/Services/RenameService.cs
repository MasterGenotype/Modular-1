using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Models;
using Modular.Core.RateLimiting;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;
using System.Text.Json;

namespace Modular.Core.Services;

/// <summary>
/// Service for renaming mod folders and organizing by category.
/// </summary>
public class RenameService
{
    private const string BaseUrl = "https://api.nexusmods.com";
    private readonly IFluentClient _client;
    private readonly AppSettings _settings;
    private readonly ILogger<RenameService>? _logger;

    public RenameService(AppSettings settings, Modular.Core.RateLimiting.IRateLimiter rateLimiter, ILogger<RenameService>? logger = null)
    {
        _settings = settings;
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
    /// Fetches the mod name from the NexusMods API.
    /// </summary>
    public async Task<string?> FetchModNameAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsJsonAsync();

            if (response.RootElement.TryGetProperty("name", out var nameProp))
                return nameProp.GetString();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch mod name for {ModId}", modId);
        }
        return null;
    }

    /// <summary>
    /// Fetches the mod's category ID from the NexusMods API.
    /// </summary>
    public async Task<int?> FetchModCategoryAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsJsonAsync();

            if (response.RootElement.TryGetProperty("category_id", out var catProp))
                return catProp.GetInt32();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch category for mod {ModId}", modId);
        }
        return null;
    }

    /// <summary>
    /// Reorganizes and renames mods in a game domain directory.
    /// </summary>
    public async Task<int> ReorganizeAndRenameModsAsync(string gameDomainPath, bool organizeByCategory = true, CancellationToken ct = default)
    {
        var gameDomain = Path.GetFileName(gameDomainPath);
        var modIds = GetModIds(gameDomainPath).ToList();
        var renamedCount = 0;

        // Fetch categories if organizing
        Dictionary<int, string>? categories = null;
        if (organizeByCategory)
        {
            categories = await FetchGameCategoriesAsync(gameDomain, ct);
        }

        foreach (var modId in modIds)
        {
            ct.ThrowIfCancellationRequested();

            var oldPath = Path.Combine(gameDomainPath, modId.ToString());
            var modName = await FetchModNameAsync(gameDomain, modId, ct);

            if (string.IsNullOrEmpty(modName))
            {
                _logger?.LogWarning("Could not fetch name for mod {ModId}", modId);
                continue;
            }

            var sanitizedName = FileUtils.SanitizeDirectoryName(modName);
            string newPath;

            if (organizeByCategory && categories != null)
            {
                var categoryId = await FetchModCategoryAsync(gameDomain, modId, ct);
                var categoryName = categoryId.HasValue && categories.TryGetValue(categoryId.Value, out var name)
                    ? FileUtils.SanitizeDirectoryName(name)
                    : $"Category_{categoryId ?? 0}";

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
                    FileUtils.MoveDirectory(oldPath, newPath);
                    _logger?.LogInformation("Renamed: {ModId} -> {ModName}", modId, modName);
                    renamedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to rename {OldPath} to {NewPath}", oldPath, newPath);
                }
            }
        }

        return renamedCount;
    }

    /// <summary>
    /// Fetches game categories from the API.
    /// </summary>
    public async Task<Dictionary<int, string>> FetchGameCategoriesAsync(string gameDomain, CancellationToken ct = default)
    {
        try
        {
            // Categories are part of the game info response, not a separate endpoint
            var response = await _client.GetAsync($"v1/games/{gameDomain}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
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
        var categories = await FetchGameCategoriesAsync(gameDomain, ct);
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
                            FileUtils.MoveDirectory(dir, newPath);
                            _logger?.LogInformation("Merging {OldName} into {NewName}", dirName, catName);
                            renamedCount++;
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
    }
}
