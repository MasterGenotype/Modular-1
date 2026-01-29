using System.Text.Json.Serialization;

namespace Modular.Core.Models;

/// <summary>
/// Represents a category for a game on NexusMods.
/// </summary>
public class GameCategory
{
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("parent_category")]
    public int? ParentCategory { get; set; }
}
