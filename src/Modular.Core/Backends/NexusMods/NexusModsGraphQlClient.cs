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
            // Wrap search terms in wildcards so partial matches work with the WILDCARD operator
            var wildcardTerms = query.Terms!.Trim();
            if (!wildcardTerms.StartsWith('*')) wildcardTerms = "*" + wildcardTerms;
            if (!wildcardTerms.EndsWith('*')) wildcardTerms = wildcardTerms + "*";
            filter["name"] = new[] { new { value = wildcardTerms, op = "WILDCARD" } };
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
