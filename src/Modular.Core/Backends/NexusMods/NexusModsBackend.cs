using Microsoft.Extensions.Logging;
using Modular.Sdk.Backends.Common;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Exceptions;
using Modular.Core.Models;
using Modular.Core.RateLimiting;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// NexusMods backend implementation.
/// Provides access to NexusMods API for downloading tracked mods.
/// </summary>
public class NexusModsBackend : IModBackend
{
    private const string BaseUrl = "https://api.nexusmods.com";

    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly DownloadDatabase _database;
    private readonly ModMetadataCache _metadataCache;
    private readonly ILogger<NexusModsBackend>? _logger;

    // Cache of tracked mods to avoid repeated API calls
    private HashSet<(string domain, int modId)>? _trackedModsCache;

    public string Id => "nexusmods";
    public string DisplayName => "NexusMods";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.GameDomains |
        BackendCapabilities.FileCategories |
        BackendCapabilities.Md5Verification |
        BackendCapabilities.RateLimited |
        BackendCapabilities.Authentication |
        BackendCapabilities.ModCategories;

    public NexusModsBackend(
        AppSettings settings,
        Modular.Core.RateLimiting.IRateLimiter rateLimiter,
        DownloadDatabase database,
        ModMetadataCache metadataCache,
        ILogger<NexusModsBackend>? logger = null)
    {
        _settings = settings;
        _database = database;
        _metadataCache = metadataCache;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl, new RateLimiterAdapter(rateLimiter), logger);
        _client.SetUserAgent("Modular/1.0");
    }

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(_settings.NexusApiKey))
            errors.Add("NexusMods API key is not configured. Set 'nexus_api_key' in config or API_KEY environment variable.");
        return errors;
    }

    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        var response = await _client.GetAsync("v1/user/tracked_mods.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<TrackedMod>();

        _trackedModsCache = response.Select(m => (m.DomainName, m.ModId)).ToHashSet();

        // Filter by game domain first to minimize API calls for mod info
        var filteredMods = !string.IsNullOrEmpty(gameDomain)
            ? response.Where(m => m.DomainName == gameDomain).ToList()
            : response;

        var mods = new List<BackendMod>();
        foreach (var m in filteredMods)
        {
            ct.ThrowIfCancellationRequested();

            // Check cache first to avoid rate limiting
            var cached = _metadataCache.GetModMetadata(m.DomainName, m.ModId);
            if (cached != null)
            {
                mods.Add(new BackendMod
                {
                    ModId = m.ModId.ToString(),
                    Name = cached.Name,
                    GameDomain = m.DomainName,
                    BackendId = Id,
                    Url = $"https://www.nexusmods.com/{m.DomainName}/mods/{m.ModId}",
                    CategoryId = cached.CategoryId
                });
            }
            else
            {
                // Fetch mod info from API (only if not in cache)
                var modInfo = await GetModInfoAsync(m.ModId.ToString(), m.DomainName, ct);

                mods.Add(new BackendMod
                {
                    ModId = m.ModId.ToString(),
                    Name = modInfo?.Name ?? $"Mod {m.ModId}",
                    GameDomain = m.DomainName,
                    BackendId = Id,
                    Url = $"https://www.nexusmods.com/{m.DomainName}/mods/{m.ModId}",
                    Author = modInfo?.Author,
                    Summary = modInfo?.Summary,
                    UpdatedAt = modInfo?.UpdatedAt,
                    ThumbnailUrl = modInfo?.ThumbnailUrl,
                    CategoryId = modInfo?.CategoryId
                });

                // Save to cache for future use
                if (modInfo != null)
                {
                    _metadataCache.SetModMetadata(m.DomainName, new ModMetadata
                    {
                        ModId = m.ModId,
                        Name = modInfo.Name,
                        CategoryId = modInfo.CategoryId ?? 0,
                        FetchedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // Save cache after loading mods
        _ = _metadataCache.SaveAsync();

        return mods;
    }

    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId,
        string? gameDomain = null,
        FileFilter? filter = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameDomain))
            throw new ArgumentException("Game domain is required for NexusMods", nameof(gameDomain));

        var filesResponse = await _client
            .GetAsync($"v1/games/{gameDomain}/mods/{modId}/files.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsAsync<ModFilesResponse>();

        var files = filesResponse.Files.Select(f => new BackendModFile
        {
            FileId = f.FileId.ToString(),
            FileName = f.FileName,
            DisplayName = f.Name,
            SizeBytes = f.SizeKb * 1024,
            Md5 = f.Md5,
            Version = f.Version,
            Category = GetCategoryName(f.CategoryId),
            UploadedAt = DateTimeOffset.FromUnixTimeSeconds(f.UploadedTimestamp).DateTime,
            ModId = modId
        }).ToList();

        // Apply category filter
        if (filter?.Categories is { Count: > 0 } cats)
        {
            var catSet = cats.Select(c => c.ToLowerInvariant()).ToHashSet();
            files = files.Where(f =>
                catSet.Contains(f.Category?.ToLowerInvariant() ?? "")).ToList();
        }

        // Apply date filter
        if (filter?.UploadedAfter.HasValue == true)
        {
            files = files.Where(f =>
                f.UploadedAt.HasValue && f.UploadedAt.Value > filter.UploadedAfter.Value).ToList();
        }

        // Exclude archived unless requested
        if (filter?.IncludeArchived != true)
        {
            files = files.Where(f =>
                f.Category?.ToLowerInvariant() != "old_version").ToList();
        }

        return files;
    }

    public async Task<string?> ResolveDownloadUrlAsync(
        string modId,
        string fileId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameDomain))
            throw new ArgumentException("Game domain is required for NexusMods", nameof(gameDomain));

        try
        {
            var response = await _client
                .GetAsync($"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsResponseAsync();

            // Check for successful response before deserializing
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == 403)
                {
                    _logger?.LogWarning("Cannot get download link for mod {ModId} file {FileId}: NexusMods Premium membership required", modId, fileId);
                }
                else
                {
                    _logger?.LogWarning("Failed to resolve download URL for mod {ModId} file {FileId}: HTTP {StatusCode}", 
                        modId, fileId, response.StatusCode);
                }
                return null;
            }

            var links = response.AsArray<DownloadLink>();
            return links.Count > 0 ? links[0].Uri : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve download URL for mod {ModId} file {FileId}", modId, fileId);
            return null;
        }
    }

    public async Task DownloadModsAsync(
        string outputDirectory,
        string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameDomain))
            throw new ArgumentException("Game domain is required for NexusMods", nameof(gameDomain));

        options ??= DownloadOptions.Default;

        // Ensure base output directory exists
        FileUtils.EnsureDirectoryExists(outputDirectory);

        // Scanning phase
        progress?.Report(DownloadProgress.Scanning($"Fetching tracked mods for {gameDomain}..."));
        options.StatusCallback?.Invoke($"Fetching tracked mods for {gameDomain}...");

        var trackedMods = await GetUserModsAsync(gameDomain, ct);
        _logger?.LogInformation("Found {Count} tracked mods for {Domain}", trackedMods.Count, gameDomain);
        options.StatusCallback?.Invoke($"Found {trackedMods.Count} tracked mods. Fetching file info...");

        // Get files for each mod
        var allFiles = new List<(BackendMod mod, BackendModFile file)>();
        foreach (var mod in trackedMods)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var files = await GetModFilesAsync(mod.ModId, gameDomain, options.Filter, ct);
                foreach (var file in files)
                {
                    allFiles.Add((mod, file));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get files for mod {ModId}", mod.ModId);
            }
        }

        options.StatusCallback?.Invoke($"Found {allFiles.Count} files. Generating download links...");

        // Generate download links
        var downloadQueue = new List<(BackendMod mod, BackendModFile file, string url)>();
        foreach (var (mod, file) in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            var url = await ResolveDownloadUrlAsync(mod.ModId, file.FileId, gameDomain, ct);
            if (!string.IsNullOrEmpty(url))
            {
                downloadQueue.Add((mod, file, url));
            }
        }

        options.StatusCallback?.Invoke($"Ready to download {downloadQueue.Count} files.");

        // Check if no download links were obtained
        if (downloadQueue.Count == 0 && allFiles.Count > 0)
        {
            _logger?.LogError("Could not obtain any download links. NexusMods Premium membership is required to download mods via the API.");
            throw new ApiException(
                "Cannot download mods: NexusMods requires a Premium membership to access download links via the API. " +
                "Visit https://www.nexusmods.com/register/premium to upgrade your account.",
                403);
        }

        // Download phase
        var completed = 0;
        var total = downloadQueue.Count;

        foreach (var (mod, file, url) in downloadQueue)
        {
            ct.ThrowIfCancellationRequested();

            var modIdInt = int.Parse(mod.ModId);
            var fileIdInt = int.Parse(file.FileId);

            // Check if already downloaded
            if (!options.Force && _database.IsDownloaded(gameDomain, modIdInt, fileIdInt))
            {
                completed++;
                progress?.Report(DownloadProgress.Downloading(
                    $"Skipping {file.FileName} (already downloaded)",
                    completed, total, file.FileName));
                continue;
            }

            var modOutputDir = Path.Combine(outputDirectory, gameDomain, mod.ModId);
            var outputPath = Path.Combine(modOutputDir, FileUtils.SanitizeFilename(file.FileName));

            if (options.DryRun)
            {
                _logger?.LogInformation("[DRY RUN] Would download: {File}", outputPath);
                completed++;
                progress?.Report(DownloadProgress.Downloading(
                    $"[DRY RUN] {file.FileName}",
                    completed, total, file.FileName));
                continue;
            }

            try
            {
                FileUtils.EnsureDirectoryExists(modOutputDir);
                await _client.GetAsync(url).DownloadToAsync(outputPath, null, ct);

                var record = new DownloadRecord
                {
                    GameDomain = gameDomain,
                    ModId = modIdInt,
                    FileId = fileIdInt,
                    Filename = file.FileName,
                    Filepath = outputPath,
                    Url = url,
                    Md5Expected = file.Md5 ?? string.Empty,
                    FileSize = new FileInfo(outputPath).Length,
                    DownloadTime = DateTime.UtcNow,
                    Status = "success"
                };

                // Verify MD5 if enabled
                if (options.VerifyDownloads && !string.IsNullOrEmpty(file.Md5))
                {
                    var actualMd5 = await Md5Calculator.CalculateMd5Async(outputPath, ct);
                    record.Md5Actual = actualMd5;
                    record.Status = actualMd5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase)
                        ? "verified"
                        : "hash_mismatch";
                }

                _database.AddRecord(record);
                _logger?.LogInformation("Downloaded: {File}", file.FileName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {File}", file.FileName);
                _database.AddRecord(new DownloadRecord
                {
                    GameDomain = gameDomain,
                    ModId = modIdInt,
                    FileId = fileIdInt,
                    Filename = file.FileName,
                    DownloadTime = DateTime.UtcNow,
                    Status = "failed",
                    ErrorMessage = ex.Message
                });
            }

            completed++;
            progress?.Report(DownloadProgress.Downloading(
                $"Downloaded {file.FileName}",
                completed, total, file.FileName));
        }

        await _database.SaveAsync();
        progress?.Report(DownloadProgress.Done(total));
    }

    public async Task<BackendMod?> GetModInfoAsync(
        string modId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameDomain))
            throw new ArgumentException("Game domain is required for NexusMods", nameof(gameDomain));

        try
        {
            var response = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsJsonAsync();

            var root = response.RootElement;

            return new BackendMod
            {
                ModId = modId,
                Name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? $"Mod {modId}" : $"Mod {modId}",
                GameDomain = gameDomain,
                BackendId = Id,
                CategoryId = root.TryGetProperty("category_id", out var catProp) ? catProp.GetInt32() : null,
                Summary = root.TryGetProperty("summary", out var sumProp) ? sumProp.GetString() : null,
                Author = root.TryGetProperty("author", out var authProp) ? authProp.GetString() : null,
                Url = $"https://www.nexusmods.com/{gameDomain}/mods/{modId}",
                UpdatedAt = root.TryGetProperty("updated_timestamp", out var updProp)
                    ? DateTimeOffset.FromUnixTimeSeconds(updProp.GetInt64()).DateTime
                    : null,
                ThumbnailUrl = root.TryGetProperty("picture_url", out var picProp) ? picProp.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get mod info for {ModId}", modId);
            return null;
        }
    }

    /// <summary>
    /// Checks if a mod is in the user's tracked list.
    /// </summary>
    public async Task<bool> IsModTrackedAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        if (_trackedModsCache == null)
            await GetUserModsAsync(null, ct);
        return _trackedModsCache?.Contains((gameDomain, modId)) ?? false;
    }

    /// <summary>
    /// Gets game categories for a domain.
    /// </summary>
    public async Task<Dictionary<int, string>> GetGameCategoriesAsync(string gameDomain, CancellationToken ct = default)
    {
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
