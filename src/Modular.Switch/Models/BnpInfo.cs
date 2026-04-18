using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Switch.Models;

/// <summary>
/// A single selectable option within a BNP option group.
/// Maps to an entry in info.json options.single[].options[] or options.multi[].options[].
/// </summary>
public sealed class BnpOption
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;
}

/// <summary>
/// A group of related options (e.g. "Standing idle animation").
/// For "single" groups the user picks exactly one; for "multi" groups, any number.
/// </summary>
public sealed class BnpOptionGroup
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public string Required { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<BnpOption> Options { get; set; } = [];
}

/// <summary>
/// Top-level options container from BNP info.json.
/// </summary>
public sealed class BnpOptionsContainer
{
    [JsonPropertyName("single")]
    public List<BnpOptionGroup> Single { get; set; } = [];

    [JsonPropertyName("multi")]
    public List<BnpOptionGroup> Multi { get; set; } = [];

    /// <summary>True when at least one group with at least one option exists.</summary>
    [JsonIgnore]
    public bool HasOptions => Single.Any(g => g.Options.Count > 0)
                           || Multi.Any(g => g.Options.Count > 0);
}

/// <summary>
/// Full BNP info.json model. Only the fields Modular cares about are mapped;
/// BCML-specific fields (depends, id, priority, url, image) are ignored.
/// </summary>
public sealed class BnpInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("desc")]
    public string? Description { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("options")]
    public BnpOptionsContainer? Options { get; set; }

    /// <summary>
    /// Attempts to deserialize a BnpInfo from JSON text.
    /// Returns null if deserialization fails.
    /// </summary>
    public static BnpInfo? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BnpInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to load and deserialize from a file path.
    /// Returns null if the file doesn't exist or deserialization fails.
    /// </summary>
    public static BnpInfo? TryLoad(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            return TryParse(json);
        }
        catch
        {
            return null;
        }
    }
}
