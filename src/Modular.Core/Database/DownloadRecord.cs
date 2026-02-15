using System.Text.Json.Serialization;

namespace Modular.Core.Database;

/// <summary>
/// Record of a downloaded mod file.
/// </summary>
public class DownloadRecord
{
    [JsonPropertyName("game_domain")]
    public string GameDomain { get; set; } = string.Empty;

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("file_id")]
    public int FileId { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("filepath")]
    public string Filepath { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("md5_expected")]
    public string Md5Expected { get; set; } = string.Empty;

    [JsonPropertyName("md5_actual")]
    public string Md5Actual { get; set; } = string.Empty;

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("download_time")]
    public DateTime DownloadTime { get; set; }

    [JsonPropertyName("status")]
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}
