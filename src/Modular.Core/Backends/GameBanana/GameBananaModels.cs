using System.Text.Json.Serialization;

namespace Modular.Core.Backends.GameBanana;

/// <summary>
/// Response wrapper for paginated record listings from GameBanana API.
/// Used for endpoints like Member/{id}/Submissions.
/// </summary>
public class GameBananaRecordResponse
{
    [JsonPropertyName("_aRecords")]
    public List<GameBananaRecord> Records { get; set; } = [];

    [JsonPropertyName("_nRecordCount")]
    public int RecordCount { get; set; }
}

/// <summary>
/// Response wrapper for v11 API paginated listings.
/// Used for endpoints like Member/{id}/Subscriptions and Mod/Index.
/// </summary>
public class GameBananaV11Response
{
    [JsonPropertyName("_aMetadata")]
    public GameBananaMetadata? Metadata { get; set; }

    [JsonPropertyName("_aRecords")]
    public List<GameBananaV11Record> Records { get; set; } = [];
}

/// <summary>
/// Metadata for paginated responses.
/// </summary>
public class GameBananaMetadata
{
    [JsonPropertyName("_nRecordCount")]
    public int RecordCount { get; set; }

    [JsonPropertyName("_nPerpage")]
    public int PerPage { get; set; }

    [JsonPropertyName("_bIsComplete")]
    public bool IsComplete { get; set; }
}

/// <summary>
/// A record from v11 API (can be a subscription wrapper or direct mod).
/// </summary>
public class GameBananaV11Record
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sModelName")]
    public string? ModelName { get; set; }

    [JsonPropertyName("_sName")]
    public string? Name { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long? DateAddedTimestamp { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public long? DateModifiedTimestamp { get; set; }

    [JsonPropertyName("_bHasFiles")]
    public bool HasFiles { get; set; }

    [JsonPropertyName("_aSubmitter")]
    public GameBananaSubmitter? Submitter { get; set; }

    [JsonPropertyName("_aGame")]
    public GameBananaGameRef? Game { get; set; }

    [JsonPropertyName("_aPreviewMedia")]
    public GameBananaPreviewMedia? PreviewMedia { get; set; }

    /// <summary>
    /// For subscription records, contains the actual mod data.
    /// </summary>
    [JsonPropertyName("_aSubscription")]
    public GameBananaV11Record? Subscription { get; set; }
}

/// <summary>
/// Preview media (images) for a mod.
/// </summary>
public class GameBananaPreviewMedia
{
    [JsonPropertyName("_aImages")]
    public List<GameBananaImage>? Images { get; set; }
}

/// <summary>
/// An image from GameBanana.
/// </summary>
public class GameBananaImage
{
    [JsonPropertyName("_sType")]
    public string? Type { get; set; }

    [JsonPropertyName("_sBaseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("_sFile")]
    public string? File { get; set; }

    [JsonPropertyName("_sFile220")]
    public string? File220 { get; set; }

    [JsonPropertyName("_sFile100")]
    public string? File100 { get; set; }
}

/// <summary>
/// A single submission record (mod, skin, tool, etc.) from GameBanana.
/// </summary>
public class GameBananaRecord
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("_sModelName")]
    public string? ModelName { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long? DateAddedTimestamp { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public long? DateModifiedTimestamp { get; set; }

    [JsonPropertyName("_nSubscriptionCount")]
    public int SubscriptionCount { get; set; }

    [JsonPropertyName("_aSubmitter")]
    public GameBananaSubmitter? Submitter { get; set; }

    [JsonPropertyName("_aGame")]
    public GameBananaGameRef? Game { get; set; }

    [JsonPropertyName("_sDescription")]
    public string? Description { get; set; }
}

/// <summary>
/// Submitter (author) information nested within records.
/// </summary>
public class GameBananaSubmitter
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; set; }
}

/// <summary>
/// Game reference nested within records.
/// </summary>
public class GameBananaGameRef
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Response wrapper for mod file listings from GameBanana API.
/// Used for endpoint Mod/{id}/Files.
/// </summary>
public class GameBananaFilesResponse
{
    [JsonPropertyName("_aFiles")]
    public List<GameBananaFileEntry> Files { get; set; } = [];
}

/// <summary>
/// A single downloadable file entry from GameBanana.
/// </summary>
public class GameBananaFileEntry
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sFile")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("_nFilesize")]
    public long FileSize { get; set; }

    [JsonPropertyName("_sDownloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("_sMd5Checksum")]
    public string? Md5Checksum { get; set; }

    [JsonPropertyName("_sDescription")]
    public string? Description { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long DateAddedTimestamp { get; set; }

    [JsonPropertyName("_nDownloadCount")]
    public int DownloadCount { get; set; }
}

/// <summary>
/// Response for single mod profile data from GameBanana API.
/// Used for endpoint Mod/{id} or Mod/{id}/ProfilePage.
/// </summary>
public class GameBananaModProfile
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("_sDescription")]
    public string? Description { get; set; }

    [JsonPropertyName("_aSubmitter")]
    public GameBananaSubmitter? Submitter { get; set; }

    [JsonPropertyName("_aGame")]
    public GameBananaGameRef? Game { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long? DateAddedTimestamp { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public long? DateModifiedTimestamp { get; set; }
}
