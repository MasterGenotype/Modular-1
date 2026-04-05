using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// Thin client for the NexusMods v2 GraphQL API.
/// Supports full-text mod search, filtering, sorting, and pagination.
/// </summary>
internal class NexusModsGraphQlClient
{
    private const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";

    private const string CollectionsQuery = """
        query SearchCollections($filter: CollectionsSearchFilter, $count: Int, $offset: Int) {
          collectionsV2(filter: $filter, count: $count, offset: $offset) {
            nodes {
              slug
              name
              summary
              description
              endorsements
              totalDownloads
              tileImage { url }
              user { name }
              game { domainName }
              latestPublishedRevision {
                revisionNumber
                modCount
              }
            }
            totalCount
          }
        }
        """;

    private const string CollectionDetailQuery = """
        query GetCollection($slug: String, $domainName: String) {
          collection(slug: $slug, domainName: $domainName) {
            name
            slug
            latestPublishedRevision {
              revisionNumber
              modCount
              modFiles {
                file {
                  modId
                  name
                  version
                  fileId
                  mod { name author modId }
                }
                optional
                version
              }
            }
          }
        }
        """;

    private const string ModsQuery = """
        query SearchMods($filter: ModsFilter, $sort: [ModsSort!], $count: Int, $offset: Int) {
          mods(filter: $filter, sort: $sort, count: $count, offset: $offset) {
            nodes {
              modId
              name
              summary
              author
              version
              category
              modCategory { categoryId }
              endorsements
              downloads
              createdAt
              updatedAt
              pictureUrl
              adultContent
              game { domainName }
            }
            totalCount
          }
        }
        """;

    private const int MaxModsPerQuery = 20;

    private readonly IFluentClient _client;
    private readonly string _apiKey;
    private readonly ILogger? _logger;

    public NexusModsGraphQlClient(string apiKey, Modular.FluentHttp.Interfaces.IRateLimiter rateLimiter, ILogger? logger = null)
    {
        _apiKey = apiKey;
        _logger = logger;
        _client = FluentClientFactory.Create(GraphQlUrl, rateLimiter, logger);
        _client.SetUserAgent("Modular/1.0");
    }

    public async Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.GameDomain))
            throw new ArgumentException("GameDomain is required for NexusMods search", nameof(query));

        var offset = (query.Page - 1) * query.PageSize;
        var hasTerms = !string.IsNullOrWhiteSpace(query.Terms);

        // Build the filter: always filter by game domain, optionally add name wildcard
        var filter = new Dictionary<string, object>
        {
            ["gameDomainName"] = new[] { new { value = query.GameDomain } }
        };
        if (hasTerms)
        {
            // Use nameStemmed with MATCHES for full-text search (handles stemming,
            // partial words, and fuzzy matching server-side)
            filter["nameStemmed"] = new[] { new { value = query.Terms!.Trim(), op = "MATCHES" } };
        }

        // Include adult content when requested
        if (query.AdultContent)
        {
            filter["adultContent"] = new[] { new { value = true, op = "EQUALS" } };
        }

        var requestBody = new
        {
            query = ModsQuery,
            variables = new
            {
                filter,
                sort = BuildSort(query.SortBy),
                count = query.PageSize,
                offset
            }
        };

        _logger?.LogDebug("Searching NexusMods GraphQL: terms={Terms}, game={Game}, page={Page}, adult={Adult}",
            query.Terms, query.GameDomain, query.Page, query.AdultContent);

        var json = await _client.PostAsync(GraphQlUrl)
            .WithHeader("apikey", _apiKey)
            .WithHeader("accept", "application/json")
            .WithJsonBody(requestBody)
            .AsStringAsync();

        _logger?.LogDebug("GraphQL response: {Response}", json.Length > 500 ? json[..500] : json);

        var response = JsonSerializer.Deserialize<NexusGraphQlResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (response?.Errors is { Count: > 0 })
        {
            var errorMessages = string.Join("; ", response.Errors.Select(e => e.Message));
            _logger?.LogWarning("GraphQL returned errors: {Errors}", errorMessages);
            throw new InvalidOperationException($"NexusMods API error: {errorMessages}");
        }

        var modsResult = response?.Data?.Mods;
        if (modsResult == null)
        {
            _logger?.LogWarning("GraphQL search returned no data. Response: {Response}", json);
            return new ModSearchResult
            {
                Mods = [],
                TotalCount = 0,
                Page = query.Page,
                PageSize = query.PageSize
            };
        }

        var mods = modsResult.Nodes.Select(n => MapToBackendMod(n, query.GameDomain)).ToList();

        return new ModSearchResult
        {
            Mods = mods,
            TotalCount = modsResult.TotalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    private static BackendMod MapToBackendMod(NexusGraphQlMod node, string fallbackDomain)
    {
        var domain = node.Game?.DomainName ?? fallbackDomain;

        DateTime? updatedAt = null;
        if (DateTime.TryParse(node.UpdatedAt, out var parsed))
            updatedAt = parsed;

        return new BackendMod
        {
            ModId = node.ModId.ToString(),
            Name = node.Name,
            GameDomain = domain,
            BackendId = "nexusmods",
            CategoryId = node.ModCategory?.CategoryId,
            Summary = node.Summary,
            Author = node.Author,
            Version = node.Version,
            EndorsementCount = node.Endorsements,
            DownloadCount = node.Downloads,
            IsAdult = node.AdultContent,
            Url = $"https://www.nexusmods.com/{domain}/mods/{node.ModId}",
            ThumbnailUrl = node.PictureUrl,
            UpdatedAt = updatedAt
        };
    }

    /// <summary>
    /// Fetches mod metadata for multiple mod IDs in a single GraphQL request.
    /// Returns up to <see cref="MaxModsPerQuery"/> mods per call.
    /// </summary>
    public async Task<List<NexusGraphQlMod>> FetchModsByIdsAsync(
        string gameDomain, IReadOnlyList<int> modIds, CancellationToken ct = default)
    {
        if (modIds.Count == 0) return [];

        var filter = new Dictionary<string, object>
        {
            ["gameDomainName"] = new[] { new { value = gameDomain } },
            ["modId"] = modIds.Select(id => new { value = id }).ToArray()
        };

        var requestBody = new
        {
            query = ModsQuery,
            variables = new
            {
                filter,
                count = Math.Min(modIds.Count, MaxModsPerQuery),
                offset = 0
            }
        };

        _logger?.LogDebug("GraphQL batch fetch: {Count} mod(s) for {Game}", modIds.Count, gameDomain);

        var json = await _client.PostAsync(GraphQlUrl)
            .WithHeader("apikey", _apiKey)
            .WithHeader("accept", "application/json")
            .WithJsonBody(requestBody)
            .AsStringAsync();

        var response = JsonSerializer.Deserialize<NexusGraphQlResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (response?.Errors is { Count: > 0 })
        {
            var errorMessages = string.Join("; ", response.Errors.Select(e => e.Message));
            _logger?.LogWarning("GraphQL batch fetch errors: {Errors}", errorMessages);
            throw new InvalidOperationException($"NexusMods API error: {errorMessages}");
        }

        return response?.Data?.Mods?.Nodes ?? [];
    }

    /// <summary>
    /// Fetches mod metadata for a large number of mod IDs, automatically batching
    /// into groups of <see cref="MaxModsPerQuery"/> and pacing requests.
    /// </summary>
    public async Task<List<NexusGraphQlMod>> FetchModsByIdsBatchedAsync(
        string gameDomain, IReadOnlyList<int> modIds, TimeSpan pacingDelay, CancellationToken ct = default)
    {
        var results = new List<NexusGraphQlMod>();

        // Chunk into groups of MaxModsPerQuery
        for (var i = 0; i < modIds.Count; i += MaxModsPerQuery)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = modIds.Skip(i).Take(MaxModsPerQuery).ToList();
            var batch = await FetchModsByIdsAsync(gameDomain, chunk, ct);
            results.AddRange(batch);

            // Pace between batches (not after the last one)
            if (i + MaxModsPerQuery < modIds.Count && pacingDelay > TimeSpan.Zero)
            {
                await Task.Delay(pacingDelay, ct);
            }
        }

        return results;
    }

    /// <summary>
    /// Searches for collections on NexusMods filtered by game domain.
    /// </summary>
    public async Task<(List<NexusCollectionInfo> Collections, int TotalCount)> SearchCollectionsAsync(
        int gameId, string gameDomain, string? searchTerm = null, int count = 20, int offset = 0, CancellationToken ct = default)
    {
        var filter = new Dictionary<string, object>
        {
            ["gameId"] = new[] { new { op = "EQUALS", value = gameId.ToString() } },
            ["collectionStatus"] = new[]
            {
                new { op = "EQUALS", value = "listed" },
                new { op = "EQUALS", value = "published" }
            }
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var wildcardTerm = searchTerm.Trim();
            if (!wildcardTerm.StartsWith('*')) wildcardTerm = "*" + wildcardTerm;
            if (!wildcardTerm.EndsWith('*')) wildcardTerm = wildcardTerm + "*";
            filter["generalSearch"] = new[] { new { value = wildcardTerm, op = "WILDCARD" } };
        }

        var requestBody = new
        {
            query = CollectionsQuery,
            variables = new { filter, count, offset }
        };

        _logger?.LogDebug("Searching NexusMods collections: gameId={GameId}, game={Game}, count={Count}, offset={Offset}",
            gameId, gameDomain, count, offset);

        var json = await _client.PostAsync(GraphQlUrl)
            .WithHeader("apikey", _apiKey)
            .WithHeader("accept", "application/json")
            .WithJsonBody(requestBody)
            .AsStringAsync();

        _logger?.LogDebug("GraphQL collection response: {Response}", json.Length > 1000 ? json[..1000] : json);

        var response = JsonSerializer.Deserialize<NexusGraphQlCollectionResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (response?.Errors is { Count: > 0 })
        {
            var errorMessages = string.Join("; ", response.Errors.Select(e => e.Message));
            _logger?.LogWarning("GraphQL collection search errors: {Errors}", errorMessages);
            throw new InvalidOperationException($"NexusMods API error: {errorMessages}");
        }

        var result = response?.Data?.Collections;
        if (result == null)
        {
            _logger?.LogWarning("GraphQL collection search returned no data. Response: {Response}", json);
            return ([], 0);
        }

        var collections = result.Nodes.Select(n => new NexusCollectionInfo
        {
            Slug = n.Slug,
            Name = n.Name,
            Summary = n.Summary,
            Description = n.Description,
            Author = n.User?.Name,
            GameDomain = n.Game?.DomainName ?? gameDomain,
            Endorsements = n.Endorsements,
            TotalDownloads = n.TotalDownloads,
            ModCount = n.LatestRevision?.ModCount ?? 0,
            RevisionNumber = n.LatestRevision?.RevisionNumber ?? 0,
            TileImageUrl = n.TileImage?.ThumbnailUrl ?? n.TileImage?.Url
        }).ToList();

        return (collections, result.TotalCount);
    }

    /// <summary>
    /// Fetches the mod list for a specific NexusMods collection by slug and game domain.
    /// </summary>
    public async Task<NexusCollectionDetail?> GetCollectionDetailsAsync(
        string slug, string gameDomain, CancellationToken ct = default)
    {
        var requestBody = new
        {
            query = CollectionDetailQuery,
            variables = new { slug, domainName = gameDomain }
        };

        _logger?.LogDebug("Fetching NexusMods collection details: slug={Slug}, game={Game}", slug, gameDomain);

        var json = await _client.PostAsync(GraphQlUrl)
            .WithHeader("apikey", _apiKey)
            .WithHeader("accept", "application/json")
            .WithJsonBody(requestBody)
            .AsStringAsync();

        var response = JsonSerializer.Deserialize<NexusGraphQlCollectionDetailResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var collection = response?.Data?.Collection;
        if (collection == null)
        {
            _logger?.LogWarning("GraphQL collection detail returned no data. Response: {Response}", json);
            return null;
        }

        var revision = collection.LatestRevision;
        var entries = new List<NexusCollectionModEntry>();
        if (revision?.ModFiles != null)
        {
            foreach (var mf in revision.ModFiles)
            {
                var file = mf.File;
                var mod = file?.Mod;
                entries.Add(new NexusCollectionModEntry
                {
                    ModId = (mod?.ModId ?? file?.ModId ?? 0).ToString(),
                    Name = mod?.Name ?? file?.Name ?? "Unknown",
                    Author = mod?.Author,
                    Version = mf.Version ?? file?.Version,
                    FileId = file?.FileId.ToString(),
                    FileName = file?.Name,
                    IsOptional = mf.Optional
                });
            }
        }

        return new NexusCollectionDetail
        {
            Name = collection.Name,
            Slug = collection.Slug,
            GameDomain = gameDomain,
            RevisionNumber = revision?.RevisionNumber ?? 0,
            ModCount = revision?.ModCount ?? 0,
            Entries = entries
        };
    }

    /// <summary>
    /// Builds the sort array for the NexusMods GraphQL API.
    /// Sort format: [{ "fieldName": { "direction": "ASC"|"DESC" } }]
    /// </summary>
    private static object[] BuildSort(ModSortOrder sort)
    {
        var (field, direction) = sort switch
        {
            ModSortOrder.Endorsements => ("endorsements", "DESC"),
            ModSortOrder.Downloads => ("downloads", "DESC"),
            ModSortOrder.Updated => ("updatedAt", "DESC"),
            ModSortOrder.Added => ("createdAt", "DESC"),
            _ => ("name", "ASC")
        };
        return [new Dictionary<string, object> { [field] = new { direction } }];
    }
}
