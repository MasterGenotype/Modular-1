using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Backends.GameBanana;

/// <summary>
/// Enriches GameBanana mod metadata into canonical format.
/// </summary>
public class GameBananaMetadataEnricher : IMetadataEnricher
{
    private const string BaseUrl = "https://gamebanana.com/apiv11";
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly ILogger<GameBananaMetadataEnricher>? _logger;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public string BackendId => "gamebanana";

    public GameBananaMetadataEnricher(
        AppSettings settings,
        ILogger<GameBananaMetadataEnricher>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl);
        _client.SetUserAgent("Modular/1.0");
    }

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

    public async Task<CanonicalMod?> EnrichModAsync(
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

            var canonicalId = $"{BackendId}:{modId}";

            var mod = new CanonicalMod
            {
                CanonicalId = canonicalId,
                Name = profile.Name,
                Source = new ModSource
                {
                    BackendId = BackendId,
                    ProjectId = modId,
                    Url = $"https://gamebanana.com/mods/{modId}"
                },
                Summary = profile.Description,
                Game = profile.Game != null
                    ? new GameInfo
                    {
                        Id = profile.Game.Id.ToString(),
                        Name = profile.Game.Name
                    }
                    : null,
                Timestamps = new ModTimestamps
                {
                    PublishedAt = profile.DateAddedTimestamp.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(profile.DateAddedTimestamp.Value).DateTime
                        : null,
                    UpdatedAt = profile.DateModifiedTimestamp.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(profile.DateModifiedTimestamp.Value).DateTime
                        : null
                }
            };

            // Authors
            if (profile.Submitter != null)
            {
                mod.Authors.Add(new ModAuthor
                {
                    Name = profile.Submitter.Name,
                    Id = profile.Submitter.Id.ToString()
                });
            }

            // Fetch files/versions
            await EnrichVersionsAsync(mod, modId, ct);

            // Fetch preview images
            await EnrichAssetsAsync(mod, modId, ct);

            return mod;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich GameBanana mod {ModId}", modId);
            return null;
        }
    }

    private async Task EnrichVersionsAsync(CanonicalMod mod, string modId, CancellationToken ct)
    {
        try
        {
            await ThrottleAsync();

            var response = await _client.GetAsync($"Mod/{modId}/Files")
                .AsJsonAsync();

            var filesResponse = JsonSerializer.Deserialize<GameBananaFilesResponse>(
                response.RootElement.GetRawText());

            if (filesResponse?.Files == null)
                return;

            foreach (var gbFile in filesResponse.Files)
            {
                if (string.IsNullOrEmpty(gbFile.DownloadUrl))
                    continue;

                // GameBanana doesn't have explicit version strings in files
                // We'll use the file description or file ID as version identifier
                var versionNumber = !string.IsNullOrEmpty(gbFile.Description)
                    ? gbFile.Description
                    : gbFile.Id.ToString();

                var version = new CanonicalVersion
                {
                    VersionId = gbFile.Id.ToString(),
                    VersionNumber = versionNumber,
                    ReleaseChannel = ReleaseChannel.Stable,
                    PublishedAt = gbFile.DateAddedTimestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(gbFile.DateAddedTimestamp).DateTime
                        : DateTime.UtcNow
                };

                var fileName = !string.IsNullOrEmpty(gbFile.FileName)
                    ? gbFile.FileName
                    : Path.GetFileName(new Uri(gbFile.DownloadUrl).LocalPath);

                var file = new CanonicalFile
                {
                    FileId = gbFile.Id.ToString(),
                    FileName = fileName,
                    DisplayName = gbFile.Description ?? fileName,
                    SizeBytes = gbFile.FileSize > 0 ? gbFile.FileSize : null,
                    Download = new DownloadInfo
                    {
                        DirectUrl = gbFile.DownloadUrl,
                        RequiresResolution = false
                    }
                };

                // Add hash if available
                if (!string.IsNullOrEmpty(gbFile.Md5Checksum))
                {
                    file.Hashes.Md5 = gbFile.Md5Checksum;
                }

                version.Files.Add(file);
                mod.Versions.Add(version);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich versions for GameBanana mod {ModId}", modId);
        }
    }

    private async Task EnrichAssetsAsync(CanonicalMod mod, string modId, CancellationToken ct)
    {
        try
        {
            await ThrottleAsync();

            // Fetch preview media via the Mod endpoint with media field
            var response = await _client.GetAsync($"Mod/{modId}")
                .AsJsonAsync();

            var profile = JsonSerializer.Deserialize<GameBananaV11Record>(
                response.RootElement.GetRawText());

            if (profile?.PreviewMedia?.Images == null)
                return;

            var isPrimary = true;
            foreach (var image in profile.PreviewMedia.Images)
            {
                if (string.IsNullOrEmpty(image.BaseUrl) || string.IsNullOrEmpty(image.File))
                    continue;

                var imageUrl = $"{image.BaseUrl}/{image.File}";
                
                if (isPrimary && !string.IsNullOrEmpty(image.File220))
                {
                    // Set first image as thumbnail
                    mod.Assets.ThumbnailUrl = $"{image.BaseUrl}/{image.File220}";
                }
                
                // Add to gallery
                mod.Assets.GalleryUrls.Add(imageUrl);
                isPrimary = false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich assets for GameBanana mod {ModId}", modId);
        }
    }

    public async Task<List<CanonicalMod>> EnrichModsBatchAsync(
        IEnumerable<string> modIds,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        var results = new List<CanonicalMod>();

        foreach (var modId in modIds)
        {
            ct.ThrowIfCancellationRequested();

            var mod = await EnrichModAsync(modId, gameDomain, ct);
            if (mod != null)
            {
                results.Add(mod);
            }

            // Already throttled in EnrichModAsync
        }

        return results;
    }
}
