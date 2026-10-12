using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Backends.Common;
using Modular.Core.Configuration;
using Modular.Core.Downloads;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;
using Modular.Sdk.Backends;

namespace Modular.Core.Backends.GameBanana;

/// <summary>
/// GameBanana backend implementation.
/// Provides access to GameBanana API for downloading subscribed mods.
/// </summary>
public partial class GameBananaBackend : IModBackend, IRequirementsBackend
{
    private const string BaseUrl = "https://gamebanana.com/apiv11";
    private const int DefaultPageSize = 15;
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly ILogger<GameBananaBackend>? _logger;
    private readonly ResumableDownloader _downloader;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public string Id => BackendIds.GameBanana;
    public string DisplayName => "GameBanana";

    // GameBanana doesn't have game domains, file categories, rate limiting,
    // or MD5 verification
    public BackendCapabilities Capabilities =>
        BackendCapabilities.Requirements;

    public GameBananaBackend(
        AppSettings settings,
        ILogger<GameBananaBackend>? logger = null,
        ResumableDownloader? downloader = null)
    {
        _settings = settings;
        _logger = logger;
        _downloader = downloader ?? new ResumableDownloader(logger: null);
        _client = FluentClientFactory.Create(BaseUrl);
        _client.SetUserAgent("Modular/1.0");
    }

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(_settings.GameBananaUserId))
            errors.Add("GameBanana user ID is not configured. Set 'gamebanana_user_id' in config or GB_USER_ID environment variable.");
        return errors;
    }

    /// <summary>
    /// Applies courtesy throttling to avoid hammering the GameBanana API.
    /// </summary>
    private async Task ThrottleAsync()
    {
        await _throttle.WaitAsync();
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < ThrottleDelay)
                await Task.Delay(ThrottleDelay - elapsed);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        var result = new List<BackendMod>();

        try
        {
            var page = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await ThrottleAsync();

                // Fetch the user's subscriptions (mods they follow) using v11 API
                var response = await _client.GetAsync($"Member/{_settings.GameBananaUserId}/Subscriptions")
                    .WithArgument("_nPage", page.ToString())
                    .WithArgument("_nPerpage", DefaultPageSize.ToString())
                    .AsJsonAsync();

                var v11Response = JsonSerializer.Deserialize<GameBananaV11Response>(
                    response.RootElement.GetRawText());

                if (v11Response?.Records == null || v11Response.Records.Count == 0)
                    break;

                foreach (var record in v11Response.Records)
                {
                    // Subscription records have the actual mod in _aSubscription
                    var mod = record.Subscription ?? record;
                    if (mod.Id == 0 || string.IsNullOrEmpty(mod.Name))
                        continue;

                    // Optional: Filter by game IDs if configured
                    if (_settings.GameBananaGameIds.Count > 0 &&
                        mod.Game != null &&
                        !_settings.GameBananaGameIds.Contains(mod.Game.Id))
                    {
                        continue;
                    }

                    var modId = mod.Id.ToString();
                    var thumbnailUrl = GetThumbnailUrl(mod.PreviewMedia);

                    result.Add(new BackendMod
                    {
                        ModId = modId,
                        Name = mod.Name,
                        BackendId = Id,
                        Url = mod.ProfileUrl ?? $"https://gamebanana.com/mods/{modId}",
                        Author = mod.Submitter?.Name,
                        UpdatedAt = mod.DateModifiedTimestamp.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds(mod.DateModifiedTimestamp.Value).DateTime
                            : null,
                        GameDomain = mod.Game?.Name,
                        ThumbnailUrl = thumbnailUrl
                    });
                }

                // Check if we've fetched all records
                var totalCount = v11Response.Metadata?.RecordCount ?? 0;
                if (v11Response.Metadata?.IsComplete == true ||
                    result.Count >= totalCount ||
                    v11Response.Records.Count < DefaultPageSize)
                    break;

                page++;
            }

            _logger?.LogInformation("Fetched {Count} subscribed mods from GameBanana", result.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch subscribed mods for user {UserId}", _settings.GameBananaUserId);
        }

        return result;
    }

    /// <summary>
    /// Searches for mods on GameBanana using the Util/Search/Results endpoint.
    /// Falls back to Mod/Index listing (with optional game filter) when no query is provided.
    /// </summary>
    public async Task<List<BackendMod>> SearchModsAsync(
        string searchQuery,
        int? gameId = null,
        int maxResults = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return await ListModsAsync(gameId, maxResults, ct);

        var result = new List<BackendMod>();

        try
        {
            var page = 1;
            while (result.Count < maxResults)
            {
                ct.ThrowIfCancellationRequested();
                await ThrottleAsync();

                // Use the search endpoint — Mod/Index does not support text search
                var request = _client.GetAsync("Util/Search/Results")
                    .WithArgument("_nPage", page.ToString())
                    .WithArgument("_nPerpage", DefaultPageSize.ToString())
                    .WithArgument("_sSearchString", searchQuery);

                // _idGameRow=0 means all games
                if (gameId.HasValue)
                    request = request.WithArgument("_idGameRow", gameId.Value.ToString());

                var response = await request.AsJsonAsync();

                var v11Response = JsonSerializer.Deserialize<GameBananaV11Response>(
                    response.RootElement.GetRawText());

                if (v11Response?.Records == null || v11Response.Records.Count == 0)
                    break;

                foreach (var record in v11Response.Records)
                {
                    // Search endpoint returns all types; keep only Mods
                    if (!string.Equals(record.ModelName, "Mod", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (record.Id == 0 || string.IsNullOrEmpty(record.Name))
                        continue;

                    var modId = record.Id.ToString();
                    var thumbnailUrl = GetThumbnailUrl(record.PreviewMedia);

                    result.Add(new BackendMod
                    {
                        ModId = modId,
                        Name = record.Name,
                        BackendId = Id,
                        Url = record.ProfileUrl ?? $"https://gamebanana.com/mods/{modId}",
                        Author = record.Submitter?.Name,
                        UpdatedAt = record.DateModifiedTimestamp.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds(record.DateModifiedTimestamp.Value).DateTime
                            : null,
                        GameDomain = record.Game?.Name,
                        ThumbnailUrl = thumbnailUrl
                    });

                    if (result.Count >= maxResults)
                        break;
                }

                if (v11Response.Metadata?.IsComplete == true ||
                    v11Response.Records.Count < DefaultPageSize)
                    break;

                page++;
            }

            _logger?.LogInformation("Found {Count} mods matching '{Query}'", result.Count, searchQuery);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to search mods for query '{Query}'", searchQuery);
        }

        return result;
    }

    /// <summary>
    /// Lists mods from GameBanana using Mod/Index, optionally filtered by game.
    /// Used when no search query is provided.
    /// </summary>
    private async Task<List<BackendMod>> ListModsAsync(
        int? gameId = null,
        int maxResults = 50,
        CancellationToken ct = default)
    {
        var result = new List<BackendMod>();

        try
        {
            var page = 1;
            while (result.Count < maxResults)
            {
                ct.ThrowIfCancellationRequested();
                await ThrottleAsync();

                var request = _client.GetAsync("Mod/Index")
                    .WithArgument("_nPage", page.ToString())
                    .WithArgument("_nPerpage", DefaultPageSize.ToString());

                if (gameId.HasValue)
                    request = request.WithArgument("_aFilters[Generic_Game]", gameId.Value.ToString());

                var response = await request.AsJsonAsync();

                var v11Response = JsonSerializer.Deserialize<GameBananaV11Response>(
                    response.RootElement.GetRawText());

                if (v11Response?.Records == null || v11Response.Records.Count == 0)
                    break;

                foreach (var mod in v11Response.Records)
                {
                    if (mod.Id == 0 || string.IsNullOrEmpty(mod.Name))
                        continue;

                    var modId = mod.Id.ToString();
                    var thumbnailUrl = GetThumbnailUrl(mod.PreviewMedia);

                    result.Add(new BackendMod
                    {
                        ModId = modId,
                        Name = mod.Name,
                        BackendId = Id,
                        Url = mod.ProfileUrl ?? $"https://gamebanana.com/mods/{modId}",
                        Author = mod.Submitter?.Name,
                        UpdatedAt = mod.DateModifiedTimestamp.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds(mod.DateModifiedTimestamp.Value).DateTime
                            : null,
                        GameDomain = mod.Game?.Name,
                        ThumbnailUrl = thumbnailUrl
                    });

                    if (result.Count >= maxResults)
                        break;
                }

                if (v11Response.Metadata?.IsComplete == true ||
                    v11Response.Records.Count < DefaultPageSize)
                    break;

                page++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to list mods");
        }

        return result;
    }

    private static string? GetThumbnailUrl(GameBananaPreviewMedia? media)
    {
        var image = media?.Images?.FirstOrDefault();
        if (image == null || string.IsNullOrEmpty(image.BaseUrl) || string.IsNullOrEmpty(image.File220))
            return null;
        return $"{image.BaseUrl}/{image.File220}";
    }

    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId,
        string? gameDomain = null,
        FileFilter? filter = null,
        CancellationToken ct = default)
    {
        var files = new List<BackendModFile>();

        try
        {
            await ThrottleAsync();

            var response = await _client.GetAsync($"Mod/{modId}/Files")
                .AsJsonAsync();

            var rawJson = response.RootElement.GetRawText();
            _logger?.LogDebug("Files API response for mod {ModId}: {Response}", modId, rawJson.Length > 500 ? rawJson[..500] + "..." : rawJson);

            // The API returns a direct array, not wrapped in _aFiles
            List<GameBananaFileEntry>? fileEntries = null;
            
            if (response.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                // Direct array response
                fileEntries = JsonSerializer.Deserialize<List<GameBananaFileEntry>>(rawJson);
            }
            else
            {
                // Wrapped response (legacy format)
                var filesResponse = JsonSerializer.Deserialize<GameBananaFilesResponse>(rawJson);
                fileEntries = filesResponse?.Files;
            }

            if (fileEntries == null || fileEntries.Count == 0)
            {
                _logger?.LogWarning("No files found for mod {ModId}", modId);
                return files;
            }

            foreach (var file in fileEntries)
            {
                if (string.IsNullOrEmpty(file.DownloadUrl))
                {
                    _logger?.LogWarning("File {FileId} in mod {ModId} has no download URL", file.Id, modId);
                    continue;
                }

                // Use actual filename from API, fall back to URL-derived name
                var filename = !string.IsNullOrEmpty(file.FileName)
                    ? file.FileName
                    : Path.GetFileName(new Uri(file.DownloadUrl).LocalPath);

                _logger?.LogDebug("Found file: {FileName} with URL: {Url}", filename, file.DownloadUrl);

                files.Add(new BackendModFile
                {
                    FileId = file.Id.ToString(),
                    FileName = filename,
                    DisplayName = file.Description ?? filename,
                    SizeBytes = file.FileSize > 0 ? file.FileSize : null,
                    DirectDownloadUrl = file.DownloadUrl,
                    Description = file.Description,
                    ModId = modId,
                    Md5 = !string.IsNullOrEmpty(file.Md5Checksum) ? file.Md5Checksum : null,
                    UploadedAt = file.DateAddedTimestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(file.DateAddedTimestamp).DateTime
                        : null
                });
            }

            _logger?.LogDebug("Found {Count} files for mod {ModId}", files.Count, modId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch files for mod {ModId}", modId);
        }

        return files;
    }

    public Task<string?> ResolveDownloadUrlAsync(
        string modId,
        string fileId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        // GameBanana provides download URLs directly in GetModFilesAsync,
        // so this method is not needed. Return null to indicate URL should
        // be taken from BackendModFile.DirectDownloadUrl instead.
        return Task.FromResult<string?>(null);
    }

    public async Task DownloadModsAsync(
        string outputDirectory,
        string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= DownloadOptions.Default;

        // Ensure base output directory exists
        FileUtils.EnsureDirectoryExists(outputDirectory);
        _logger?.LogDebug("Output directory: {OutputDirectory}", outputDirectory);

        // Scanning phase
        progress?.Report(DownloadProgress.Scanning("Fetching subscribed mods from GameBanana..."));
        options.StatusCallback?.Invoke("Fetching subscribed mods from GameBanana...");

        var mods = await GetUserModsAsync(null, ct);
        _logger?.LogInformation("Found {Count} subscribed mods on GameBanana", mods.Count);
        options.StatusCallback?.Invoke($"Found {mods.Count} subscribed mods.");

        // Collect all files
        var allFiles = new List<(BackendMod mod, BackendModFile file)>();
        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgress.Scanning($"Fetching files for {mod.Name}..."));

            try
            {
                var files = await GetModFilesAsync(mod.ModId, null, options.Filter, ct);
                foreach (var file in files)
                {
                    allFiles.Add((mod, file));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get files for mod {ModName}", mod.Name);
            }
        }

        options.StatusCallback?.Invoke($"Ready to download {allFiles.Count} files.");

        // Download phase
        var completed = 0;
        var total = allFiles.Count;

        foreach (var (mod, file) in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            // GameBanana provides URLs inline
            var url = file.DirectDownloadUrl;
            if (string.IsNullOrEmpty(url))
            {
                _logger?.LogWarning("No download URL for file in mod {ModName}", mod.Name);
                completed++;
                continue;
            }

            // Use mod name as directory (sanitized)
            var downloadDir = string.IsNullOrEmpty(_settings.GameBananaDownloadDir)
                ? "gamebanana"
                : _settings.GameBananaDownloadDir;
            var modOutputDir = Path.Combine(outputDirectory, downloadDir, FileUtils.SanitizeDirectoryName(mod.Name));
            var outputPath = Path.Combine(modOutputDir, FileUtils.SanitizeFilename(file.FileName));

            // Check if file exists (simple check - no database tracking for GameBanana yet).
            // Partial downloads live in a separate .part file, so an existing file is always complete.
            if (!options.Force && File.Exists(outputPath))
            {
                completed++;
                progress?.Report(DownloadProgress.Downloading(
                    $"Skipping {file.FileName} (already exists)",
                    completed, total, file.FileName));
                continue;
            }

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
                _logger?.LogDebug("Downloading from URL: {Url}", url);
                _logger?.LogDebug("Output path: {OutputPath}", outputPath);
                FileUtils.EnsureDirectoryExists(modOutputDir);

                var resume = options.Resume && !options.Force;
                var partialBytes = resume ? ResumableDownloader.GetPartialLength(outputPath) : 0;
                var completedSoFar = completed;
                progress?.Report(DownloadProgress.Downloading(
                    partialBytes > 0 ? $"Resuming {file.FileName}" : $"Downloading {file.FileName}",
                    completedSoFar, total, file.FileName));

                var result = await _downloader.DownloadAsync(
                    new ResumableDownloadRequest
                    {
                        Url = url,
                        DestinationPath = outputPath,
                        Identity = DownloadIdentity.GameBanana(mod.ModId, file.FileId),
                        // GameBanana's _nFilesize and _sMd5Checksum are exact.
                        ExpectedSize = file.SizeBytes,
                        ExpectedMd5 = file.Md5,
                        VerifyMd5 = options.VerifyDownloads,
                        Resume = resume
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
                _logger?.LogInformation("Downloaded: {File}{Resumed}", outputPath, result.Resumed ? " (resumed)" : string.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Partial data is kept on disk; the next run resumes it.
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError("Failed to download {ModName}/{FileName}: {Error}", mod.Name, file.FileName, ex.Message);
                progress?.Report(DownloadProgress.Downloading(
                    $"Failed: {file.FileName} - {ex.Message}",
                    completed, total, file.FileName));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {Url}", url);
                progress?.Report(DownloadProgress.Downloading(
                    $"Failed: {file.FileName} - {ex.Message}",
                    completed, total, file.FileName));
            }

            completed++;
            progress?.Report(DownloadProgress.Downloading(
                $"Downloaded {file.FileName}",
                completed, total, file.FileName));
        }

        progress?.Report(DownloadProgress.Done(total));
    }

    /// <inheritdoc />
    /// <remarks>
    /// GameBanana lets a submitter list requirements as free-form name and link pairs. A
    /// link to another GameBanana mod is something this backend can download; any other
    /// link, or none, is only a pointer for the user.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>>> GetRequirementsAsync(
        IReadOnlyList<string> modIds,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        var result = new Dictionary<string, IReadOnlyList<ModRequirement>>(StringComparer.Ordinal);

        foreach (var modId in modIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            if (!int.TryParse(modId, out _))
                continue;

            await ThrottleAsync();

            using var response = await _client.GetAsync($"Mod/{modId}/ProfilePage")
                .WithCancellation(ct)
                .AsJsonAsync();

            result[modId] = ParseRequirements(response.RootElement);
        }

        return result;
    }

    /// <summary>
    /// Reads the <c>_aRequirements</c> list of a mod's profile page.
    /// </summary>
    internal static IReadOnlyList<ModRequirement> ParseRequirements(JsonElement profile)
    {
        var requirements = new List<ModRequirement>();

        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("_aRequirements", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return requirements;
        }

        foreach (var pair in list.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() == 0)
                continue;

            var name = pair[0].ValueKind == JsonValueKind.String ? pair[0].GetString() : null;
            var url = pair.GetArrayLength() > 1 && pair[1].ValueKind == JsonValueKind.String
                ? pair[1].GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Links are often protocol-relative: "//gamebanana.com/mods/598553".
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("//", StringComparison.Ordinal))
                url = "https:" + url;

            var match = string.IsNullOrWhiteSpace(url) ? null : RequirementModLink().Match(url);
            var isMod = match is { Success: true };

            requirements.Add(new ModRequirement
            {
                ModId = isMod ? match!.Groups["id"].Value : string.Empty,
                Name = name.Trim(),
                SameGame = isMod,
                IsExternal = !isMod,
                Url = string.IsNullOrWhiteSpace(url) ? null : url
            });
        }

        return requirements;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^https?://(www\.)?gamebanana\.com/mods/(?<id>\d+)/?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RequirementModLink();

    public async Task<BackendMod?> GetModInfoAsync(
        string modId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        try
        {
            await ThrottleAsync();

            var response = await _client.GetAsync($"Mod/{modId}")
                .AsJsonAsync();

            var profile = JsonSerializer.Deserialize<GameBananaModProfile>(
                response.RootElement.GetRawText());

            if (profile == null)
                return null;

            return new BackendMod
            {
                ModId = modId,
                Name = profile.Name,
                BackendId = Id,
                Author = profile.Submitter?.Name,
                Summary = profile.Description,
                Url = $"https://gamebanana.com/mods/{modId}",
                GameDomain = profile.Game?.Name,
                UpdatedAt = profile.DateModifiedTimestamp.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(profile.DateModifiedTimestamp.Value).DateTime
                    : null
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get mod info for {ModId}", modId);
            return null;
        }
    }
}
