using Modular.Sdk.Backends.Common;

namespace Modular.Sdk.Backends;

/// <summary>
/// Optional interface for backends that support full-text search.
/// Backends declare search support via <see cref="BackendCapabilities.Search"/>.
/// </summary>
public interface ISearchableBackend
{
    /// <summary>
    /// Searches for mods matching the given query.
    /// </summary>
    /// <param name="query">Search parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated search results.</returns>
    Task<ModSearchResult> SearchModsAsync(ModSearchQuery query, CancellationToken ct = default);
}

/// <summary>
/// Search query parameters for mod search.
/// </summary>
public record ModSearchQuery
{
    /// <summary>Full-text search terms. Empty or null returns unfiltered results.</summary>
    public string Terms { get; init; } = string.Empty;

    /// <summary>Optional game domain filter (e.g. "skyrimspecialedition").</summary>
    public string? GameDomain { get; init; }

    /// <summary>Optional category ID filter.</summary>
    public int? CategoryId { get; init; }

    /// <summary>Sort order for results.</summary>
    public ModSortOrder SortBy { get; init; } = ModSortOrder.Relevance;

    /// <summary>Page number (1-based).</summary>
    public int Page { get; init; } = 1;

    /// <summary>Results per page (max 20 recommended for rate limits).</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>Whether to include adult content in results.</summary>
    public bool AdultContent { get; init; } = false;
}

/// <summary>
/// Sort order options for mod search results.
/// </summary>
public enum ModSortOrder
{
    /// <summary>Sort by search relevance.</summary>
    Relevance,
    /// <summary>Sort by endorsement count.</summary>
    Endorsements,
    /// <summary>Sort by download count.</summary>
    Downloads,
    /// <summary>Sort by last updated date.</summary>
    Updated,
    /// <summary>Sort by date added.</summary>
    Added
}

/// <summary>
/// Paginated search result container.
/// </summary>
public record ModSearchResult
{
    /// <summary>List of mods matching the search query.</summary>
    public required List<BackendMod> Mods { get; init; }

    /// <summary>Total number of results across all pages.</summary>
    public int TotalCount { get; init; }

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; init; }

    /// <summary>Number of results per page.</summary>
    public int PageSize { get; init; }

    /// <summary>Whether more pages are available.</summary>
    public bool HasNextPage => Page * PageSize < TotalCount;
}
