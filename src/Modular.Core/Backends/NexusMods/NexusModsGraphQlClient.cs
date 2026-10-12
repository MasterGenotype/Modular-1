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

    // Looks mods up by (game id, mod id). The "mods" search query cannot do this: it
    // rejects a modId filter without a game id, and treats several ids as "all of them".
    private const string ModsByIdsQuery = """
        query ModsByIds($ids: [CompositeIdInput!]!) {
          legacyMods(ids: $ids) {
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
          }
        }
        """;

    private const string RequirementsQuery = """
        query ModRequirements($ids: [CompositeIdInput!]!, $count: Int) {
          legacyMods(ids: $ids) {
            nodes {
              modId
              modRequirements {
                nexusRequirements(count: $count) {
                  nodes { modId modName gameId url notes externalRequirement }
                }
              }
            }
          }
        }
        """;

    private const string GameIdQuery = """
        query GameId($domainName: String) {
          game(domainName: $domainName) { id }
        }
        """;

    private const int MaxModsPerQuery = 20;

    // The API prices a query at roughly mods x requirements x fields and refuses anything
    // over 10,000, so a full batch of mods leaves room for about this many each. That is
    // still more than a mod page realistically lists.
    private const int MaxRequirementsPerMod = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, int> _gameIds = new(StringComparer.OrdinalIgnoreCase);

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

        // The site returns adult and non-adult mods together unless told which to keep.
        if (query.AdultContent is { } adultOnly)
        {
            filter["adultContent"] = new[] { new { value = adultOnly, op = "EQUALS" } };
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

        var modsResult = (await PostAsync<NexusGraphQlData>(requestBody, ct))?.Mods;
        if (modsResult == null)
        {
            _logger?.LogWarning("GraphQL search returned no data");
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
            BackendId = BackendIds.NexusMods,
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
    /// Fetches mod metadata for the given mod IDs, <see cref="MaxModsPerQuery"/> per request.
    /// </summary>
    public async Task<List<NexusGraphQlMod>> FetchModsByIdsAsync(
        string gameDomain, IReadOnlyList<int> modIds, CancellationToken ct = default)
    {
        if (modIds.Count == 0) return [];

        var gameId = await ResolveGameIdAsync(gameDomain, ct);
        var results = new List<NexusGraphQlMod>();

        for (var i = 0; i < modIds.Count; i += MaxModsPerQuery)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = modIds.Skip(i).Take(MaxModsPerQuery).ToList();

            _logger?.LogDebug("GraphQL batch fetch: {Count} mod(s) for {Game}", chunk.Count, gameDomain);

            var data = await PostAsync<NexusGraphQlData>(
                new
                {
                    query = ModsByIdsQuery,
                    variables = new { ids = BuildIds(gameId, chunk) }
                },
                ct);

            results.AddRange(data?.LegacyMods?.Nodes ?? []);
        }

        return results;
    }

    /// <summary>
    /// Fetches the requirements the authors of the given mods have listed.
    /// </summary>
    /// <param name="gameId">Numeric NexusMods game id the mods belong to.</param>
    /// <param name="modIds">Mod ids to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Requirements keyed by the requiring mod's id. Mods the site does not know are absent.
    /// </returns>
    public async Task<Dictionary<int, List<NexusGraphQlRequirement>>> FetchRequirementsAsync(
        int gameId, IReadOnlyList<int> modIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, List<NexusGraphQlRequirement>>();

        for (var i = 0; i < modIds.Count; i += MaxModsPerQuery)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = modIds.Skip(i).Take(MaxModsPerQuery).ToList();

            _logger?.LogDebug("GraphQL requirements fetch: {Count} mod(s) for game {Game}", chunk.Count, gameId);

            var data = await PostAsync<NexusGraphQlData>(
                new
                {
                    query = RequirementsQuery,
                    variables = new { ids = BuildIds(gameId, chunk), count = MaxRequirementsPerMod }
                },
                ct);

            foreach (var mod in data?.LegacyMods?.Nodes ?? [])
                result[mod.ModId] = mod.ModRequirements?.NexusRequirements?.Nodes ?? [];
        }

        return result;
    }

    /// <summary>
    /// Resolves a game domain name (e.g. "baldursgate3") to its numeric NexusMods game ID.
    /// Results are cached per domain.
    /// </summary>
    public async Task<int> ResolveGameIdAsync(string gameDomain, CancellationToken ct = default)
    {
        if (_gameIds.TryGetValue(gameDomain, out var cached))
            return cached;

        var data = await PostAsync<NexusGraphQlData>(
            new
            {
                query = GameIdQuery,
                variables = new { domainName = gameDomain }
            },
            ct);

        var id = data?.Game?.Id
            ?? throw new InvalidOperationException($"Could not resolve game ID for domain '{gameDomain}'");

        _gameIds[gameDomain] = id;
        return id;
    }

    private static object[] BuildIds(int gameId, IEnumerable<int> modIds) =>
        modIds.Select(modId => (object)new { gameId, modId }).ToArray();

    // Every query goes through here, so each one is cancellable and reports API errors
    // the same way.
    private async Task<TData?> PostAsync<TData>(object requestBody, CancellationToken ct)
        where TData : class
    {
        var json = await _client.PostAsync(GraphQlUrl)
            .WithHeader("apikey", _apiKey)
            .WithHeader("accept", "application/json")
            .WithJsonBody(requestBody)
            .WithCancellation(ct)
            .AsStringAsync();

        _logger?.LogDebug("GraphQL response: {Response}", json.Length > 500 ? json[..500] : json);

        return ParseResponse<TData>(json, _logger);
    }

    /// <summary>
    /// Parses a GraphQL response body, turning reported errors into an exception.
    /// </summary>
    internal static TData? ParseResponse<TData>(string json, ILogger? logger = null)
        where TData : class
    {
        var response = JsonSerializer.Deserialize<NexusGraphQlResponse<TData>>(json, JsonOptions);

        if (response?.Errors is { Count: > 0 })
        {
            var errorMessages = string.Join("; ", response.Errors.Select(e => e.Message));
            logger?.LogWarning("GraphQL returned errors: {Errors}", errorMessages);
            throw new InvalidOperationException($"NexusMods API error: {errorMessages}");
        }

        return response?.Data;
    }

    /// <summary>
    /// Fetches mod metadata for a large number of mod IDs, pausing between requests of
    /// <see cref="MaxModsPerQuery"/> so a long list does not exhaust the rate limit.
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

        var result = (await PostAsync<NexusGraphQlCollectionData>(requestBody, ct))?.Collections;
        if (result == null)
        {
            _logger?.LogWarning("GraphQL collection search returned no data");
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

        var collection = (await PostAsync<NexusGraphQlCollectionDetailData>(requestBody, ct))?.Collection;
        if (collection == null)
        {
            _logger?.LogWarning("GraphQL collection detail returned no data");
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
