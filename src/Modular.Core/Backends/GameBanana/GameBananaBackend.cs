using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends.Common;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Backends.GameBanana;

/// <summary>
/// GameBanana backend implementation.
/// Provides access to GameBanana API for downloading subscribed mods.
/// </summary>
public class GameBananaBackend : IModBackend
{
    private const string BaseUrl = "https://gamebanana.com/apiv11";
    private const int DefaultPageSize = 15;
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly ILogger<GameBananaBackend>? _logger;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public string Id => "gamebanana";
    public string DisplayName => "GameBanana";

    // GameBanana doesn't have game domains, file categories, rate limiting,
    // or MD5 verification - just basic download functionality
    public BackendCapabilities Capabilities => BackendCapabilities.None;

    public GameBananaBackend(
        AppSettings settings,
        ILogger<GameBananaBackend>? logger = null)
    {
        _settings = settings;
        _logger = logger;
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
    /// Searches for mods on GameBanana.
    /// </summary>
    public async Task<List<BackendMod>> SearchModsAsync(
        string searchQuery,
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

                if (!string.IsNullOrWhiteSpace(searchQuery))
                    request = request.WithArgument("_sSearchText", searchQuery);

                if (gameId.HasValue)
                    request = request.WithArgument("_aFilters[Game][_idRow]", gameId.Value.ToString());

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

            var filesResponse = JsonSerializer.Deserialize<GameBananaFilesResponse>(
                response.RootElement.GetRawText());

            if (filesResponse?.Files != null)
            {
                foreach (var file in filesResponse.Files)
                {
                    if (string.IsNullOrEmpty(file.DownloadUrl))
                        continue;

                    // Use actual filename from API, fall back to URL-derived name
                    var filename = !string.IsNullOrEmpty(file.FileName)
                        ? file.FileName
                        : Path.GetFileName(new Uri(file.DownloadUrl).LocalPath);

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
            }
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

            // Check if file exists (simple check - no database tracking for GameBanana yet)
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
                FileUtils.EnsureDirectoryExists(modOutputDir);
                await _client.GetAsync(url).DownloadToAsync(outputPath, null, ct);
                _logger?.LogInformation("Downloaded: {File}", outputPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {Url}", url);
            }

            completed++;
            progress?.Report(DownloadProgress.Downloading(
                $"Downloaded {file.FileName}",
                completed, total, file.FileName));
        }

        progress?.Report(DownloadProgress.Done(total));
    }

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
