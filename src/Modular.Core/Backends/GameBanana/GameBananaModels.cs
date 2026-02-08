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
