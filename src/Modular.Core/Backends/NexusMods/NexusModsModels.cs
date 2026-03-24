using System.Text.Json.Serialization;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// GraphQL response wrapper for mod search queries.
/// </summary>
internal record NexusGraphQlResponse
{
    [JsonPropertyName("data")]
    public NexusGraphQlData? Data { get; init; }
}

internal record NexusGraphQlData
{
    [JsonPropertyName("mods")]
    public NexusGraphQlModsResult? Mods { get; init; }
}

internal record NexusGraphQlModsResult
{
    [JsonPropertyName("nodes")]
    public List<NexusGraphQlMod> Nodes { get; init; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

internal record NexusGraphQlMod
{
    [JsonPropertyName("modId")]
    public int ModId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("categoryId")]
    public int? CategoryId { get; init; }

    [JsonPropertyName("endorsementCount")]
    public int EndorsementCount { get; init; }

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; init; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; init; }

    [JsonPropertyName("pictureUrl")]
    public string? PictureUrl { get; init; }

    [JsonPropertyName("adultContent")]
    public bool AdultContent { get; init; }

    [JsonPropertyName("game")]
    public NexusGraphQlGame? Game { get; init; }
}

internal record NexusGraphQlGame
{
    [JsonPropertyName("domainName")]
    public string DomainName { get; init; } = string.Empty;
}

/// <summary>
/// v1 API models for discovery endpoints (trending, latest, updated).
/// </summary>
internal record NexusV1ModInfo
{
    [JsonPropertyName("mod_id")]
    public int ModId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("category_id")]
    public int CategoryId { get; init; }

    [JsonPropertyName("endorsement_count")]
    public int EndorsementCount { get; init; }

    [JsonPropertyName("mod_downloads")]
    public long DownloadCount { get; init; }

    [JsonPropertyName("updated_timestamp")]
    public long UpdatedTimestamp { get; init; }

    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; init; }

    [JsonPropertyName("contains_adult_content")]
    public bool ContainsAdultContent { get; init; }
}

/// <summary>
/// v1 API model for the updated mods endpoint.
/// </summary>
internal record NexusV1UpdatedMod
{
    [JsonPropertyName("mod_id")]
    public int ModId { get; init; }

    [JsonPropertyName("latest_file_update")]
    public long LatestFileUpdate { get; init; }

    [JsonPropertyName("latest_mod_activity")]
    public long LatestModActivity { get; init; }
}
