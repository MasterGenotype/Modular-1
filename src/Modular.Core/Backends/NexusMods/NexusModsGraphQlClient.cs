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

    private const string SearchQuery = """
        query SearchMods($terms: String!, $game: String!, $limit: Int!, $offset: Int!, $sort: [ModsSortInput!]) {
          mods(
            filter: { gameDomainName: { value: $game } }
            query: $terms
            sort: $sort
            limit: $limit
            offset: $offset
          ) {
            nodes {
              modId
              name
              summary
              author
              version
              categoryId
              endorsementCount
              downloadCount
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
        var sortField = MapSortOrder(query.SortBy);

        var requestBody = new
        {
            query = SearchQuery,
            variables = new
            {
                terms = query.Terms,
                game = query.GameDomain,
                limit = query.PageSize,
                offset,
                sort = new[] { new { field = sortField.field, direction = sortField.direction } }
            }
        };

        _logger?.LogDebug("Searching NexusMods GraphQL: terms={Terms}, game={Game}, page={Page}",
            query.Terms, query.GameDomain, query.Page);

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
            CategoryId = node.CategoryId,
            Summary = node.Summary,
            Author = node.Author,
            Version = node.Version,
            EndorsementCount = node.EndorsementCount,
            DownloadCount = node.DownloadCount,
            IsAdult = node.AdultContent,
            Url = $"https://www.nexusmods.com/{domain}/mods/{node.ModId}",
            ThumbnailUrl = node.PictureUrl,
            UpdatedAt = updatedAt
        };
    }

    private static (string field, string direction) MapSortOrder(ModSortOrder sort) => sort switch
    {
        ModSortOrder.Relevance => ("name", "ASC"),
        ModSortOrder.Endorsements => ("endorsement_count", "DESC"),
        ModSortOrder.Downloads => ("download_count", "DESC"),
        ModSortOrder.Updated => ("updated_at", "DESC"),
        ModSortOrder.Added => ("created_at", "DESC"),
        _ => ("name", "ASC")
    };
}
