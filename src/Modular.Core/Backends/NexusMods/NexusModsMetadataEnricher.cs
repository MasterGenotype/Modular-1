using Microsoft.Extensions.Logging;
using Modular.Core.Configuration;
using Modular.Core.Metadata;
using Modular.Core.Versioning;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// Enriches NexusMods mod metadata into canonical format.
/// </summary>
public class NexusModsMetadataEnricher : IMetadataEnricher
{
    private const string BaseUrl = "https://api.nexusmods.com";

    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly ILogger<NexusModsMetadataEnricher>? _logger;

    public string BackendId => "nexusmods";

    public NexusModsMetadataEnricher(
        AppSettings settings,
        ILogger<NexusModsMetadataEnricher>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl);
        _client.SetUserAgent("Modular/1.0");
    }

    public async Task<CanonicalMod?> EnrichModAsync(
        string modId,
        string? gameDomain = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(gameDomain))
        {
            _logger?.LogWarning("Game domain is required for NexusMods enrichment");
            return null;
        }

        try
        {
            // Fetch mod info
            var response = await _client.GetAsync($"v1/games/{gameDomain}/mods/{modId}.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsJsonAsync();

            var root = response.RootElement;

            var canonicalId = $"{BackendId}:{gameDomain}:{modId}";
            var name = root.TryGetProperty("name", out var nameProp) 
                ? nameProp.GetString() ?? $"Mod {modId}" 
                : $"Mod {modId}";

            var mod = new CanonicalMod
            {
                CanonicalId = canonicalId,
                Name = name,
                Source = new ModSource
                {
                    BackendId = BackendId,
                    ProjectId = modId,
                    Url = $"https://www.nexusmods.com/{gameDomain}/mods/{modId}"
                },
                Summary = root.TryGetProperty("summary", out var sumProp) ? sumProp.GetString() : null,
                Game = new GameInfo
                {
                    Domain = gameDomain,
                    Name = gameDomain // Could be enriched further with game name lookup
                },
                Timestamps = new ModTimestamps
                {
                    PublishedAt = root.TryGetProperty("created_timestamp", out var createdProp)
                        ? DateTimeOffset.FromUnixTimeSeconds(createdProp.GetInt64()).DateTime
                        : null,
                    UpdatedAt = root.TryGetProperty("updated_timestamp", out var updProp)
                        ? DateTimeOffset.FromUnixTimeSeconds(updProp.GetInt64()).DateTime
                        : null
                }
            };

            // Authors
            if (root.TryGetProperty("author", out var authorProp) && authorProp.GetString() is string authorName)
            {
                mod.Authors.Add(new ModAuthor
                {
                    Name = authorName
                });
            }

            // Uploader (if different from author)
            if (root.TryGetProperty("uploaded_by", out var uploaderProp) && uploaderProp.GetString() is string uploaderName &&
                uploaderName != mod.Authors.FirstOrDefault()?.Name)
            {
                mod.Authors.Add(new ModAuthor
                {
                    Name = uploaderName
                });
            }

            // Categories
            if (root.TryGetProperty("category_id", out var catProp))
            {
                mod.Categories.Add(new ModCategory
                {
                    Id = catProp.GetInt32().ToString()
                });
            }

            // Assets
            if (root.TryGetProperty("picture_url", out var picProp) && picProp.GetString() is string picUrl)
            {
                mod.Assets.ThumbnailUrl = picUrl;
            }

            // Fetch files/versions
            await EnrichVersionsAsync(mod, gameDomain, modId, ct);

            return mod;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich NexusMods mod {ModId}", modId);
            return null;
        }
    }

    private async Task EnrichVersionsAsync(CanonicalMod mod, string gameDomain, string modId, CancellationToken ct)
    {
        try
        {
            var filesResponse = await _client
                .GetAsync($"v1/games/{gameDomain}/mods/{modId}/files.json")
                .WithHeader("apikey", _settings.NexusApiKey)
                .WithHeader("accept", "application/json")
                .AsJsonAsync();

            if (!filesResponse.RootElement.TryGetProperty("files", out var filesArray))
                return;

            foreach (var fileElement in filesArray.EnumerateArray())
            {
                var versionString = fileElement.TryGetProperty("version", out var verProp) 
                    ? verProp.GetString() 
                    : null;

                var categoryId = fileElement.TryGetProperty("category_id", out var catProp) 
                    ? catProp.GetInt32() 
                    : 0;

                var releaseChannel = categoryId switch
                {
                    1 => ReleaseChannel.Stable,  // main
                    2 => ReleaseChannel.Stable,  // update
                    3 => ReleaseChannel.Beta,    // optional (treat as beta)
                    _ => ReleaseChannel.Stable
                };

                var fileId = fileElement.TryGetProperty("file_id", out var fidProp) ? fidProp.GetInt32() : 0;

                var version = new CanonicalVersion
                {
                    VersionId = fileId.ToString(),
                    VersionNumber = versionString ?? "unknown",
                    ReleaseChannel = releaseChannel,
                    Changelog = fileElement.TryGetProperty("changelog_html", out var changeProp) 
                        ? changeProp.GetString() 
                        : null,
                    PublishedAt = fileElement.TryGetProperty("uploaded_timestamp", out var uploadProp)
                        ? DateTimeOffset.FromUnixTimeSeconds(uploadProp.GetInt64()).DateTime
                        : DateTime.UtcNow
                };

                // Add file info
                var fileName = fileElement.TryGetProperty("file_name", out var fnameProp) 
                    ? fnameProp.GetString() ?? "unknown" 
                    : "unknown";

                var file = new CanonicalFile
                {
                    FileId = fileId.ToString(),
                    FileName = fileName,
                    DisplayName = fileElement.TryGetProperty("name", out var nameProp) 
                        ? nameProp.GetString() ?? fileName 
                        : fileName,
                    SizeBytes = fileElement.TryGetProperty("size_kb", out var sizeProp) 
                        ? sizeProp.GetInt64() * 1024 
                        : null,
                    Download = new DownloadInfo
                    {
                        RequiresResolution = true // NexusMods requires URL resolution via API
                    }
                };

                // Add hashes
                if (fileElement.TryGetProperty("md5", out var md5Prop) && md5Prop.GetString() is string md5)
                {
                    file.Hashes.Md5 = md5;
                }

                version.Files.Add(file);
                mod.Versions.Add(version);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich versions for NexusMods mod {ModId}", modId);
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

            // Rate limiting: wait a bit between requests
            await Task.Delay(250, ct);
        }

        return results;
    }
}
