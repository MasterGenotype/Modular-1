using System.Text.Json.Serialization;

namespace Modular.Core.Models;

/// <summary>
/// Represents a file belonging to a mod on NexusMods.
/// </summary>
public class ModFile
{
    [JsonPropertyName("file_id")]
    public int FileId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size_kb")]
    public long SizeKb { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("uploaded_timestamp")]
    public long UploadedTimestamp { get; set; }

    [JsonPropertyName("external_virus_scan_url")]
    public string? ExternalVirusScanUrl { get; set; }
}

/// <summary>
/// Response wrapper for mod files endpoint.
/// </summary>
public class ModFilesResponse
{
    [JsonPropertyName("files")]
    public List<ModFile> Files { get; set; } = [];

    [JsonPropertyName("file_updates")]
    public List<FileUpdate>? FileUpdates { get; set; }
}

/// <summary>
/// Represents a file update record.
/// </summary>
public class FileUpdate
{
    [JsonPropertyName("old_file_id")]
    public int OldFileId { get; set; }

    [JsonPropertyName("new_file_id")]
    public int NewFileId { get; set; }

    [JsonPropertyName("old_file_name")]
    public string OldFileName { get; set; } = string.Empty;

    [JsonPropertyName("new_file_name")]
    public string NewFileName { get; set; } = string.Empty;

    [JsonPropertyName("uploaded_timestamp")]
    public long UploadedTimestamp { get; set; }
}
