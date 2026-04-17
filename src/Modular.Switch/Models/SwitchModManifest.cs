using System.Text.Json.Serialization;

namespace Modular.Switch.Models;

/// <summary>
/// Optional per-mod manifest file (manifest.json / mod.json) that may be
/// present inside an archive or extracted folder.  All fields are optional;
/// the scanner fills gaps with heuristics when this file is absent.
/// </summary>
public sealed class SwitchModManifest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("title_id")]
    public string? TitleId { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>Load order hint (0 = no preference).</summary>
    [JsonPropertyName("load_order")]
    public int LoadOrder { get; set; }

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    [JsonPropertyName("conflicts")]
    public List<string> Conflicts { get; set; } = [];

    /// <summary>
    /// Discovers and deserialises a manifest from a candidate directory or
    /// archive entry list.  Returns null if none is found.
    /// </summary>
    public static SwitchModManifest? TryLoad(string directoryPath)
    {
        foreach (var candidate in new[] { "manifest.json", "mod.json", "info.json" })
        {
            var path = Path.Combine(directoryPath, candidate);
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<SwitchModManifest>(json);
            }
            catch { /* ignore malformed manifest */ }
        }
        return null;
    }
}
