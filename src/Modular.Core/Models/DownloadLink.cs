using System.Text.Json.Serialization;

namespace Modular.Core.Models;

/// <summary>
/// Represents a download link for a mod file.
/// </summary>
public class DownloadLink
{
    [JsonPropertyName("URI")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("short_name")]
    public string ShortName { get; set; } = string.Empty;
}
