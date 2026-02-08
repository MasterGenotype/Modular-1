using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Exceptions;
using Modular.Core.Models;
using Modular.Core.RateLimiting;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Services;

/// <summary>
/// Service for interacting with the NexusMods API.
/// </summary>
/// <remarks>
/// This class is deprecated. Use <see cref="Modular.Core.Backends.NexusMods.NexusModsBackend"/> instead,
/// which implements the unified <see cref="Modular.Core.Backends.IModBackend"/> interface.
/// </remarks>
[Obsolete("Use NexusModsBackend instead. This class will be removed in a future version.")]
public class NexusModsService
{
    private const string BaseUrl = "https://api.nexusmods.com";
    private readonly IFluentClient _client;
    private readonly AppSettings _settings;
    private readonly DownloadDatabase _database;
    private readonly ILogger<NexusModsService>? _logger;
    private HashSet<(string domain, int modId)>? _trackedModsCache;

    public NexusModsService(AppSettings settings, Modular.Core.RateLimiting.IRateLimiter rateLimiter, DownloadDatabase database, ILogger<NexusModsService>? logger = null)
    {
        _settings = settings;
        _database = database;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl, new RateLimiterAdapter(rateLimiter), logger);
        _client.SetUserAgent("Modular/1.0");
    }

    /// <summary>
    /// Gets all tracked mods for the user.
    /// </summary>
    public async Task<List<TrackedMod>> GetTrackedModsAsync(CancellationToken ct = default)
    {
        var response = await _client.GetAsync("v1/user/tracked_mods.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<TrackedMod>();

        _trackedModsCache = response.Select(m => (m.DomainName, m.ModId)).ToHashSet();
        return response;
    }

    /// <summary>
    /// Gets tracked mods for a specific game domain.
    /// </summary>
    public async Task<List<int>> GetTrackedModsForDomainAsync(string gameDomain, CancellationToken ct = default)
    {
        var allMods = await GetTrackedModsAsync(ct);
        return allMods.Where(m => m.DomainName == gameDomain).Select(m => m.ModId).ToList();
    }

    /// <summary>
    /// Checks if a mod is in the user's tracked list.
    /// </summary>
    public async Task<bool> IsModTrackedAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        if (_trackedModsCache == null)
            await GetTrackedModsAsync(ct);
        return _trackedModsCache?.Contains((gameDomain, modId)) ?? false;
    }

    /// <summary>
    /// Gets file IDs for multiple mods.
    /// </summary>
    public async Task<Dictionary<int, List<ModFile>>> GetFileIdsAsync(IEnumerable<int> modIds, string gameDomain,
        string? filterCategories = null, CancellationToken ct = default)
    {
        var result = new Dictionary<int, List<ModFile>>();
        var categoryFilter = filterCategories?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim().ToLowerInvariant()).ToHashSet();

        foreach (var modId in modIds)
        {
            try
            {
                var filesResponse = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}/files.json")
                    .WithHeader("apikey", _settings.NexusApiKey)
                    .WithHeader("accept", "application/json")
                    .AsAsync<ModFilesResponse>();

                var files = filesResponse.Files;

                // Filter by category if specified
                if (categoryFilter != null && categoryFilter.Count > 0)
                {
                    files = files.Where(f =>
                        categoryFilter.Contains(f.CategoryName?.ToLowerInvariant() ?? "") ||
                        categoryFilter.Contains(GetCategoryName(f.CategoryId).ToLowerInvariant())
                    ).ToList();
                }

                result[modId] = files;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get files for mod {ModId}", modId);
            }
        }

        return result;
    }

    /// <summary>
    /// Generates download links for mod files.
    /// </summary>
    public async Task<Dictionary<(int modId, int fileId), string>> GenerateDownloadLinksAsync(
        Dictionary<int, List<ModFile>> modFiles, string gameDomain, CancellationToken ct = default)
    {
        var result = new Dictionary<(int modId, int fileId), string>();

        foreach (var (modId, files) in modFiles)
        {
            foreach (var file in files)
            {
                try
                {
                    var links = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}/files/{file.FileId}/download_link.json")
                        .WithHeader("apikey", _settings.NexusApiKey)
                        .WithHeader("accept", "application/json")
                        .AsArrayAsync<DownloadLink>();

                    if (links.Count > 0)
                        result[(modId, file.FileId)] = links[0].Uri;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to get download link for mod {ModId} file {FileId}", modId, file.FileId);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Downloads all tracked mods for a game domain.
    /// </summary>
    public async Task DownloadFilesAsync(string gameDomain,
        IProgress<(string status, int completed, int total)>? progress = null,
        Action<string>? statusCallback = null,
        bool dryRun = false, bool force = false, CancellationToken ct = default)
    {
        // Scanning phase - use status callback instead of progress bar
        statusCallback?.Invoke($"Fetching tracked mods for {gameDomain}...");

        // Get tracked mods for this domain
        var trackedMods = await GetTrackedModsForDomainAsync(gameDomain, ct);
        _logger?.LogInformation("Found {Count} tracked mods for {Domain}", trackedMods.Count, gameDomain);
        statusCallback?.Invoke($"Found {trackedMods.Count} tracked mods. Fetching file info...");

        // Get file IDs
        var categories = string.Join(",", _settings.DefaultCategories);
        var modFiles = await GetFileIdsAsync(trackedMods, gameDomain, categories, ct);
        var totalFiles = modFiles.Values.Sum(f => f.Count);
        statusCallback?.Invoke($"Found {totalFiles} files. Generating download links...");

        // Generate download links
        var downloadLinks = await GenerateDownloadLinksAsync(modFiles, gameDomain, ct);
        statusCallback?.Invoke($"Ready to download {downloadLinks.Count} files.");

        var completed = 0;
        var total = downloadLinks.Count;
        progress?.Report(($"Starting downloads", completed, total));

        foreach (var ((modId, fileId), url) in downloadLinks)
        {
            ct.ThrowIfCancellationRequested();

            // Check if already downloaded
            if (!force && _database.IsDownloaded(gameDomain, modId, fileId))
            {
                completed++;
                progress?.Report(($"Skipping {modId}/{fileId} (already downloaded)", completed, total));
                continue;
            }

            var file = modFiles[modId].First(f => f.FileId == fileId);
            var outputDir = Path.Combine(_settings.ModsDirectory, gameDomain, modId.ToString());
            var outputPath = Path.Combine(outputDir, FileUtils.SanitizeFilename(file.FileName));

            if (dryRun)
            {
                _logger?.LogInformation("[DRY RUN] Would download: {File}", outputPath);
                completed++;
                progress?.Report(($"[DRY RUN] {file.FileName}", completed, total));
                continue;
            }

            try
            {
                FileUtils.EnsureDirectoryExists(outputDir);
                await _client.GetAsync(url).DownloadToAsync(outputPath, null, ct);

                var record = new DownloadRecord
                {
                    GameDomain = gameDomain,
                    ModId = modId,
                    FileId = fileId,
                    Filename = file.FileName,
                    Filepath = outputPath,
                    Url = url,
                    Md5Expected = file.Md5 ?? string.Empty,
                    FileSize = new FileInfo(outputPath).Length,
                    DownloadTime = DateTime.UtcNow,
                    Status = "success"
                };

                // Verify MD5 if enabled
                if (_settings.VerifyDownloads && !string.IsNullOrEmpty(file.Md5))
                {
                    var actualMd5 = await Md5Calculator.CalculateMd5Async(outputPath, ct);
                    record.Md5Actual = actualMd5;
                    record.Status = actualMd5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase) ? "verified" : "hash_mismatch";
                }

                _database.AddRecord(record);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {File}", file.FileName);
                _database.AddRecord(new DownloadRecord
                {
                    GameDomain = gameDomain,
                    ModId = modId,
                    FileId = fileId,
                    Filename = file.FileName,
                    DownloadTime = DateTime.UtcNow,
                    Status = "failed",
                    ErrorMessage = ex.Message
                });
            }

            completed++;
            progress?.Report(($"Downloaded {file.FileName}", completed, total));
        }

        await _database.SaveAsync();
    }

    /// <summary>
    /// Gets game categories.
    /// </summary>
    public async Task<Dictionary<int, string>> GetGameCategoriesAsync(string gameDomain, CancellationToken ct = default)
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

    private static string GetCategoryName(int categoryId) => categoryId switch
    {
        1 => "main",
        2 => "update",
        3 => "optional",
        4 => "old_version",
        5 => "miscellaneous",
        6 => "deleted",
        _ => $"category_{categoryId}"
    };

    // Adapter to bridge Core.RateLimiting.IRateLimiter to FluentHttp.Interfaces.IRateLimiter
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
