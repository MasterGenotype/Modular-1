using Microsoft.Extensions.Logging;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Downloads;
using Modular.Core.Exceptions;
using Modular.Core.Models;
using Modular.Core.RateLimiting;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// NexusMods backend implementation.
/// Provides access to NexusMods API for downloading tracked mods,
/// full-text search via the v2 GraphQL API, and discovery feeds.
/// </summary>
public class NexusModsBackend : IModBackend, ISearchableBackend, IRequirementsBackend
{
    private const string BaseUrl = "https://api.nexusmods.com";

    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly DownloadDatabase _database;
    private readonly ModMetadataCache _metadataCache;
    private readonly ILogger<NexusModsBackend>? _logger;
    private readonly NexusModsGraphQlClient _graphQlClient;
    private readonly ResumableDownloader _downloader;

    // Cache of tracked mods to avoid repeated API calls
    private HashSet<(string domain, int modId)>? _trackedModsCache;

    public string Id => BackendIds.NexusMods;
    public string DisplayName => "NexusMods";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.GameDomains |
        BackendCapabilities.FileCategories |
        BackendCapabilities.Md5Verification |
        BackendCapabilities.RateLimited |
        BackendCapabilities.Authentication |
        BackendCapabilities.ModCategories |
        BackendCapabilities.Search |
        BackendCapabilities.Requirements;

    public NexusModsBackend(
        AppSettings settings,
        Modular.Core.RateLimiting.IRateLimiter rateLimiter,
        DownloadDatabase database,
        ModMetadataCache metadataCache,
        ILogger<NexusModsBackend>? logger = null,
        ResumableDownloader? downloader = null)
    {
        _settings = settings;
        _database = database;
        _metadataCache = metadataCache;
        _logger = logger;
        _downloader = downloader ?? new ResumableDownloader(logger: null);
        var rateLimiterAdapter = new RateLimiterAdapter(rateLimiter);
        _client = FluentClientFactory.Create(BaseUrl, rateLimiterAdapter, logger);
        _client.SetUserAgent("Modular/1.0");
        _graphQlClient = new NexusModsGraphQlClient(settings.NexusApiKey ?? string.Empty, rateLimiterAdapter, logger);
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

        options.StatusCallback?.Invoke($"Ready to download {allFiles.Count} files.");

        // Download phase. Download links are resolved just before each file is downloaded,
        // because NexusMods CDN links are signed and expire.
        var completed = 0;
        var total = allFiles.Count;
        var attemptedResolutions = 0;
        var resolvedUrls = 0;

        foreach (var (mod, file) in allFiles)
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

            attemptedResolutions++;
            var url = await ResolveDownloadUrlAsync(mod.ModId, file.FileId, gameDomain, ct);
            if (string.IsNullOrEmpty(url))
            {
                completed++;
                progress?.Report(DownloadProgress.Failure(
                    file.FileName, "NexusMods gave no download link", completed, total));
                continue;
            }
            resolvedUrls++;

            try
            {
                FileUtils.EnsureDirectoryExists(modOutputDir);

                var resume = options.Resume && !options.Force;
                var partialBytes = resume ? ResumableDownloader.GetPartialLength(outputPath) : 0;
                var completedSoFar = completed;
                progress?.Report(DownloadProgress.Downloading(
                    partialBytes > 0 ? $"Resuming {file.FileName}" : $"Downloading {file.FileName}",
                    completedSoFar, total, file.FileName));

                var modId = mod.ModId;
                var fileId = file.FileId;
                var result = await _downloader.DownloadAsync(
                    new ResumableDownloadRequest
                    {
                        Url = url,
                        DestinationPath = outputPath,
                        Identity = DownloadIdentity.NexusMods(gameDomain, modId, fileId),
                        // size_kb is approximate, so size is not validated for NexusMods.
                        ExpectedMd5 = file.Md5,
                        VerifyMd5 = options.VerifyDownloads,
                        Resume = resume,
                        RefreshUrlAsync = token => ResolveDownloadUrlAsync(modId, fileId, gameDomain, token)
                    },
                    progress == null ? null : new SyncProgress<(long downloaded, long total)>(p =>
                        progress.Report(new DownloadProgress
                        {
                            Status = $"Downloading {file.FileName}",
                            Completed = completedSoFar,
                            Total = total,
                            CurrentFile = file.FileName,
                            CurrentFileBytes = p.downloaded,
                            CurrentFileTotalBytes = p.total >= 0 ? p.total : file.SizeBytes,
                            Phase = DownloadPhase.Downloading
                        })),
                    ct);

                var record = new DownloadRecord
                {
                    GameDomain = gameDomain,
                    ModId = modIdInt,
                    FileId = fileIdInt,
                    Filename = file.FileName,
                    Filepath = outputPath,
                    Url = result.FinalUrl,
                    Md5Expected = file.Md5 ?? string.Empty,
                    FileSize = result.FileSize,
                    DownloadTime = DateTime.UtcNow,
                    Status = DownloadStatus.Success
                };

                // The downloader verifies MD5 when requested (and always after a resume);
                // a mismatch throws DownloadIntegrityException, so a computed hash here is a match.
                if (result.Md5 != null)
                {
                    record.Md5Actual = result.Md5;
                    record.Status = result.Md5Verified ? DownloadStatus.Verified : DownloadStatus.HashMismatch;
                }

                _database.AddRecord(record);
                _logger?.LogInformation("Downloaded: {File}{Resumed}", file.FileName, result.Resumed ? " (resumed)" : string.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Partial data is kept on disk; the next run resumes it.
                await _database.SaveAsync();
                throw;
            }
            catch (DownloadIntegrityException ex)
            {
                _logger?.LogError("Integrity check failed for {File}: {Error}", file.FileName, ex.Message);
                _database.AddRecord(new DownloadRecord
                {
                    GameDomain = gameDomain,
                    ModId = modIdInt,
                    FileId = fileIdInt,
                    Filename = file.FileName,
                    Md5Expected = file.Md5 ?? string.Empty,
                    DownloadTime = DateTime.UtcNow,
                    Status = DownloadStatus.HashMismatch,
                    ErrorMessage = ex.Message
                });
                completed++;
                progress?.Report(DownloadProgress.Failure(file.FileName, ex.Message, completed, total));
                continue;
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
                    Status = DownloadStatus.Failed,
                    ErrorMessage = ex.Message
                });
                completed++;
                progress?.Report(DownloadProgress.Failure(file.FileName, ex.Message, completed, total));
                continue;
            }

            completed++;
            progress?.Report(DownloadProgress.Downloading(
                $"Downloaded {file.FileName}",
                completed, total, file.FileName));
        }

        await _database.SaveAsync();

        // No download links at all means the account cannot download via the API.
        if (attemptedResolutions > 0 && resolvedUrls == 0)
        {
            _logger?.LogError("Could not obtain any download links. NexusMods Premium membership is required to download mods via the API.");
            throw new ApiException(
                "Cannot download mods: NexusMods requires a Premium membership to access download links via the API. " +
                "Visit https://www.nexusmods.com/register/premium to upgrade your account.",
                403);
        }

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

    /// <summary>
    /// Gets the list of all games on NexusMods (domain name + display name).
    /// Results are cached after first call.
    /// </summary>
    public async Task<List<(string Domain, string Name)>> GetGamesAsync(CancellationToken ct = default)
    {
        if (_gamesCache != null)
            return _gamesCache;

        var response = await _client.GetAsync("v1/games.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsJsonAsync();

        var games = new List<(string Domain, string Name)>();
        foreach (var game in response.RootElement.EnumerateArray())
        {
            var domain = game.TryGetProperty("domain_name", out var d) ? d.GetString() : null;
            var name = game.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(name))
                games.Add((domain, name));
        }

        _gamesCache = games;
        return games;
    }
    private List<(string Domain, string Name)>? _gamesCache;

    /// <summary>
    /// Resolves a game domain name (e.g. "baldursgate3") to its numeric NexusMods game ID.
    /// Results are cached per domain.
    /// </summary>
    public Task<int> ResolveGameIdAsync(string gameDomain, CancellationToken ct = default) =>
        _graphQlClient.ResolveGameIdAsync(gameDomain, ct);

    // --- Requirements ---

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>>> GetRequirementsAsync(
        IReadOnlyList<string> modIds,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        if (string.IsNullOrEmpty(gameDomain))
            throw new ArgumentException("Game domain is required for NexusMods", nameof(gameDomain));

        var numericIds = modIds
            .Select(id => int.TryParse(id, out var parsed) ? parsed : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var result = new Dictionary<string, IReadOnlyList<ModRequirement>>(StringComparer.Ordinal);
        if (numericIds.Count == 0)
            return result;

        var gameId = await ResolveGameIdAsync(gameDomain, ct);
        var fetched = await _graphQlClient.FetchRequirementsAsync(gameId, numericIds, ct);

        foreach (var (modId, requirements) in fetched)
        {
            result[modId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                requirements.Select(r => MapRequirement(r, gameId, gameDomain)).ToList();
        }

        return result;
    }

    /// <summary>
    /// Maps a requirement as the API sends it onto the backend-neutral shape.
    /// </summary>
    internal static ModRequirement MapRequirement(
        NexusGraphQlRequirement requirement, int gameId, string gameDomain)
    {
        // An off-site requirement comes back with mod id "0" as well as the flag.
        _ = int.TryParse(requirement.ModId, out var modId);
        _ = int.TryParse(requirement.GameId, out var requirementGameId);

        var isExternal = requirement.ExternalRequirement || modId <= 0;
        var sameGame = !isExternal && requirementGameId == gameId;

        var url = !string.IsNullOrWhiteSpace(requirement.Url)
            ? requirement.Url
            : sameGame ? $"https://www.nexusmods.com/{gameDomain}/mods/{modId}" : null;

        return new ModRequirement
        {
            ModId = isExternal ? string.Empty : modId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name = string.IsNullOrWhiteSpace(requirement.ModName) ? $"Mod {modId}" : requirement.ModName,
            SameGame = sameGame,
            IsExternal = isExternal,
            Url = url,
            Notes = string.IsNullOrWhiteSpace(requirement.Notes) ? null : requirement.Notes
        };
    }

    // --- Collections ---

    /// <summary>
    /// Searches for NexusMods collections for a given game domain.
    /// Resolves the domain name to a numeric game ID before querying the GraphQL API.
    /// </summary>
    public async Task<(List<NexusCollectionInfo> Collections, int TotalCount)> SearchCollectionsAsync(
        string gameDomain, string? searchTerm = null, int count = 20, int offset = 0, CancellationToken ct = default)
    {
        var gameId = await ResolveGameIdAsync(gameDomain, ct);
        return await _graphQlClient.SearchCollectionsAsync(gameId, gameDomain, searchTerm, count, offset, ct);
    }

    /// <summary>
    /// Fetches the full mod list for a NexusMods collection by slug and game domain.
    /// </summary>
    public Task<NexusCollectionDetail?> GetCollectionDetailsAsync(
        string slug, string gameDomain, CancellationToken ct = default)
    {
        return _graphQlClient.GetCollectionDetailsAsync(slug, gameDomain, ct);
    }

    // --- ISearchableBackend ---

    public Task<ModSearchResult> SearchModsAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        return _graphQlClient.SearchAsync(query, ct);
    }

    /// <summary>
    /// Fetches gallery image URLs for a specific mod via the v1 API.
    /// Returns the main picture URL plus any additional images from the mod detail.
    /// </summary>
    public async Task<List<string>> GetModImagesAsync(string modId, string gameDomain, CancellationToken ct = default)
    {
        var images = new List<string>();
        try
        {
            var response = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsJsonAsync();

            var root = response.RootElement;

            // Main picture (full-size version)
            if (root.TryGetProperty("picture_url", out var picProp))
            {
                var picUrl = picProp.GetString();
                if (!string.IsNullOrEmpty(picUrl))
                    images.Add(picUrl);
            }

            // The v1 mod detail description often contains embedded image URLs.
            // Extract them as additional gallery images.
            if (root.TryGetProperty("description", out var descProp))
            {
                var description = descProp.GetString() ?? string.Empty;
                foreach (var url in ExtractImageUrls(description))
                {
                    if (!images.Contains(url))
                        images.Add(url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch images for mod {ModId}", modId);
        }
        return images;
    }

    /// <summary>
    /// Extracts image URLs from NexusMods BBCode/HTML mod descriptions.
    /// Matches [img]...[/img] tags and common image URL patterns.
    /// </summary>
    private static IEnumerable<string> ExtractImageUrls(string description)
    {
        if (string.IsNullOrEmpty(description)) yield break;

        // Match [img]URL[/img] BBCode tags (common in NexusMods descriptions)
        var bbcodeRegex = new System.Text.RegularExpressions.Regex(
            @"\[img\](https?://[^\[\]\s]+?)\[/img\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in bbcodeRegex.Matches(description))
        {
            var url = match.Groups[1].Value.Trim();
            if (IsImageUrl(url))
                yield return url;
        }

        // Match <img src="..."> HTML tags
        var htmlRegex = new System.Text.RegularExpressions.Regex(
            @"<img[^>]+src=""(https?://[^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in htmlRegex.Matches(description))
        {
            var url = match.Groups[1].Value.Trim();
            if (IsImageUrl(url))
                yield return url;
        }
    }

    private static bool IsImageUrl(string url)
    {
        return url.Contains("staticdelivery.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
               url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    // --- Discovery endpoints (v1 API) ---

    /// <summary>
    /// Gets currently trending mods for a game domain.
    /// </summary>
    public async Task<List<BackendMod>> GetTrendingModsAsync(string gameDomain, int limit = 20, CancellationToken ct = default)
    {
        var mods = await _client.GetAsync($"v1/games/{gameDomain}/mods/trending.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<NexusV1ModInfo>();

        return mods.Take(limit).Select(m => MapV1ModToBackendMod(m, gameDomain)).ToList();
    }

    /// <summary>
    /// Gets the most recently added mods for a game domain.
    /// </summary>
    public async Task<List<BackendMod>> GetLatestAddedModsAsync(string gameDomain, int limit = 20, CancellationToken ct = default)
    {
        var mods = await _client.GetAsync($"v1/games/{gameDomain}/mods/latest_added.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<NexusV1ModInfo>();

        return mods.Take(limit).Select(m => MapV1ModToBackendMod(m, gameDomain)).ToList();
    }

    /// <summary>
    /// Gets recently updated mods for a game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain name.</param>
    /// <param name="period">Time period: "1d", "1w", or "1m".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<BackendMod>> GetRecentlyUpdatedModsAsync(string gameDomain, string period = "1w", CancellationToken ct = default)
    {
        var updatedMods = await _client.GetAsync($"v1/games/{gameDomain}/mods/updated.json?period={period}")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<NexusV1UpdatedMod>();

        // The updated endpoint only returns mod IDs and timestamps,
        // so we fetch full info for each (cached where possible)
        var result = new List<BackendMod>();
        foreach (var updated in updatedMods)
        {
            ct.ThrowIfCancellationRequested();
            var modInfo = await GetModInfoAsync(updated.ModId.ToString(), gameDomain, ct);
            if (modInfo != null)
                result.Add(modInfo);
        }
        return result;
    }

    private static BackendMod MapV1ModToBackendMod(NexusV1ModInfo mod, string gameDomain)
    {
        return new BackendMod
        {
            ModId = mod.ModId.ToString(),
            Name = mod.Name,
            GameDomain = gameDomain,
            BackendId = BackendIds.NexusMods,
            CategoryId = mod.CategoryId,
            Summary = mod.Summary,
            Author = mod.Author,
            Version = mod.Version,
            EndorsementCount = mod.EndorsementCount,
            DownloadCount = mod.DownloadCount,
            IsAdult = mod.ContainsAdultContent,
            Url = $"https://www.nexusmods.com/{gameDomain}/mods/{mod.ModId}",
            ThumbnailUrl = mod.PictureUrl,
            UpdatedAt = mod.UpdatedTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(mod.UpdatedTimestamp).DateTime
                : null
        };
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
