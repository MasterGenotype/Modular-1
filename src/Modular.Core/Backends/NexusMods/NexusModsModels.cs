using System.Text.Json.Serialization;

namespace Modular.Core.Backends.NexusMods;

/// <summary>
/// GraphQL response wrapper for mod search queries.
/// </summary>
internal record NexusGraphQlResponse
{
    [JsonPropertyName("data")]
    public NexusGraphQlData? Data { get; init; }

    [JsonPropertyName("errors")]
    public List<NexusGraphQlError>? Errors { get; init; }
}

internal record NexusGraphQlError
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public List<string>? Path { get; init; }
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

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("modCategory")]
    public NexusGraphQlModCategory? ModCategory { get; init; }

    [JsonPropertyName("endorsements")]
    public int Endorsements { get; init; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; init; }

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

internal record NexusGraphQlModCategory
{
    [JsonPropertyName("categoryId")]
    public int CategoryId { get; init; }
}

internal record NexusGraphQlGame
{
    [JsonPropertyName("domainName")]
    public string DomainName { get; init; } = string.Empty;
}

// --- GraphQL collection models ---

/// <summary>
/// GraphQL response wrapper for collection search queries.
/// </summary>
internal record NexusGraphQlCollectionResponse
{
    [JsonPropertyName("data")]
    public NexusGraphQlCollectionData? Data { get; init; }

    [JsonPropertyName("errors")]
    public List<NexusGraphQlError>? Errors { get; init; }
}

internal record NexusGraphQlCollectionData
{
    [JsonPropertyName("collectionsV2")]
    public NexusGraphQlCollectionsResult? Collections { get; init; }
}

internal record NexusGraphQlCollectionsResult
{
    [JsonPropertyName("nodes")]
    public List<NexusGraphQlCollection> Nodes { get; init; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

internal record NexusGraphQlCollection
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("endorsements")]
    public int Endorsements { get; init; }

    [JsonPropertyName("totalDownloads")]
    public int TotalDownloads { get; init; }

    [JsonPropertyName("user")]
    public NexusGraphQlCollectionUser? User { get; init; }

    [JsonPropertyName("game")]
    public NexusGraphQlGame? Game { get; init; }

    [JsonPropertyName("latestPublishedRevision")]
    public NexusGraphQlCollectionRevision? LatestRevision { get; init; }
}

internal record NexusGraphQlCollectionUser
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

internal record NexusGraphQlCollectionRevision
{
    [JsonPropertyName("revisionNumber")]
    public int RevisionNumber { get; init; }

    [JsonPropertyName("modCount")]
    public int ModCount { get; init; }

    [JsonPropertyName("modFiles")]
    public List<NexusGraphQlCollectionModFile>? ModFiles { get; init; }
}

internal record NexusGraphQlCollectionModFile
{
    [JsonPropertyName("file")]
    public NexusGraphQlModFileRef? File { get; init; }

    [JsonPropertyName("optional")]
    public bool Optional { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

internal record NexusGraphQlModFileRef
{
    [JsonPropertyName("modId")]
    public int ModId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("fileId")]
    public int FileId { get; init; }

    [JsonPropertyName("mod")]
    public NexusGraphQlModRef? Mod { get; init; }
}

internal record NexusGraphQlModRef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("modId")]
    public int ModId { get; init; }
}

// --- GraphQL collection detail response ---

internal record NexusGraphQlCollectionDetailResponse
{
    [JsonPropertyName("data")]
    public NexusGraphQlCollectionDetailData? Data { get; init; }
}

internal record NexusGraphQlCollectionDetailData
{
    [JsonPropertyName("collection")]
    public NexusGraphQlCollection? Collection { get; init; }
}

// --- Public collection detail models ---

/// <summary>
/// Detailed collection info including the mod entry list.
/// </summary>
public record NexusCollectionDetail
{
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string GameDomain { get; init; } = string.Empty;
    public int RevisionNumber { get; init; }
    public int ModCount { get; init; }
    public List<NexusCollectionModEntry> Entries { get; init; } = [];
}

public record NexusCollectionModEntry
{
    public string ModId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? FileId { get; init; }
    public string? FileName { get; init; }
    public bool IsOptional { get; init; }
}

/// <summary>
/// Public model representing a NexusMods collection search result.
/// </summary>
public record NexusCollectionInfo
{
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string GameDomain { get; init; } = string.Empty;
    public int Endorsements { get; init; }
    public int TotalDownloads { get; init; }
    public int ModCount { get; init; }
    public int RevisionNumber { get; init; }
    public string Url => $"https://next.nexusmods.com/{GameDomain}/collections/{Slug}";
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
