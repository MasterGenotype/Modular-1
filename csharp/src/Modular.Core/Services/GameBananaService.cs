using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Services;

/// <summary>
/// Service for interacting with the GameBanana API.
/// </summary>
public class GameBananaService
{
    private const string BaseUrl = "https://gamebanana.com/apiv10";
    private readonly IFluentClient _client;
    private readonly AppSettings _settings;
    private readonly ILogger<GameBananaService>? _logger;

    public GameBananaService(AppSettings settings, ILogger<GameBananaService>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl);
        _client.SetUserAgent("Modular/1.0");
    }

    /// <summary>
    /// Fetches subscribed mods for a user.
    /// </summary>
    public async Task<List<(string modId, string modName)>> FetchSubscribedModsAsync(string userId, CancellationToken ct = default)
    {
        var result = new List<(string modId, string modName)>();

        try
        {
            var response = await _client.GetAsync($"Member/{userId}/Submissions")
                .WithArgument("_aFilters[Generic_SubscriptionCount]", ">0")
                .AsJsonAsync();

            if (response.RootElement.TryGetProperty("_aRecords", out var records))
            {
                foreach (var record in records.EnumerateArray())
                {
                    if (record.TryGetProperty("_idRow", out var idProp) &&
                        record.TryGetProperty("_sName", out var nameProp))
                    {
                        result.Add((idProp.ToString(), nameProp.GetString() ?? "Unknown"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch subscribed mods for user {UserId}", userId);
        }

        return result;
    }

    /// <summary>
    /// Fetches file URLs for a mod.
    /// </summary>
    public async Task<List<string>> FetchModFileUrlsAsync(string modId, CancellationToken ct = default)
    {
        var result = new List<string>();

        try
        {
            var response = await _client.GetAsync($"Mod/{modId}/Files")
                .AsJsonAsync();

            if (response.RootElement.TryGetProperty("_aFiles", out var files))
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.TryGetProperty("_sDownloadUrl", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrEmpty(url))
                            result.Add(url);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch files for mod {ModId}", modId);
        }

        return result;
    }

    /// <summary>
    /// Downloads mod files for a specific mod.
    /// </summary>
    public async Task DownloadModFilesAsync(string modId, string modName, string baseDir,
        IProgress<(string status, int completed, int total)>? progress = null, CancellationToken ct = default)
    {
        var fileUrls = await FetchModFileUrlsAsync(modId, ct);
        var outputDir = Path.Combine(baseDir, FileUtils.SanitizeDirectoryName(modName));
        FileUtils.EnsureDirectoryExists(outputDir);

        var completed = 0;
        var total = fileUrls.Count;

        foreach (var url in fileUrls)
        {
            ct.ThrowIfCancellationRequested();

            var filename = Path.GetFileName(new Uri(url).LocalPath);
            var outputPath = Path.Combine(outputDir, FileUtils.SanitizeFilename(filename));

            try
            {
                await _client.GetAsync(url).DownloadToAsync(outputPath, null, ct);
                _logger?.LogInformation("Downloaded: {File}", outputPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {Url}", url);
            }

            completed++;
            progress?.Report(($"Downloaded {filename}", completed, total));
        }
    }

    /// <summary>
    /// Downloads all subscribed mods for a user.
    /// </summary>
    public async Task DownloadAllSubscribedModsAsync(string baseDir,
        IProgress<(string status, int completed, int total)>? progress = null, CancellationToken ct = default)
    {
        var userId = _settings.GameBananaUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger?.LogError("GameBanana user ID not configured");
            return;
        }

        var mods = await FetchSubscribedModsAsync(userId, ct);
        _logger?.LogInformation("Found {Count} subscribed mods", mods.Count);

        var completed = 0;
        var total = mods.Count;

        foreach (var (modId, modName) in mods)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(($"Processing {modName}", completed, total));
            await DownloadModFilesAsync(modId, modName, baseDir, null, ct);
            completed++;
        }

        progress?.Report(("Done", total, total));
    }
}
