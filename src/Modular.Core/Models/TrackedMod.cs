using System.Text.Json.Serialization;

namespace Modular.Core.Models;

/// <summary>
/// Represents a mod tracked by the user on NexusMods.
/// </summary>
public class TrackedMod
{
    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("domain_name")]
    public string DomainName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
