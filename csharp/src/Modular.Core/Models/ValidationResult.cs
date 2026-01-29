namespace Modular.Core.Models;

/// <summary>
/// Result of tracking validation between API and web tracking center.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Game domain being validated.
    /// </summary>
    public string GameDomain { get; set; } = string.Empty;

    /// <summary>
    /// Mods found via API.
    /// </summary>
    public HashSet<int> ApiMods { get; set; } = [];

    /// <summary>
    /// Mods found via web scraping.
    /// </summary>
    public HashSet<int> WebMods { get; set; } = [];

    /// <summary>
    /// Mods present in both sources (intersection).
    /// </summary>
    public HashSet<int> MatchedMods => [.. ApiMods.Intersect(WebMods)];

    /// <summary>
    /// Mods only found via API (not in web).
    /// </summary>
    public HashSet<int> ApiOnlyMods => [.. ApiMods.Except(WebMods)];

    /// <summary>
    /// Mods only found via web (not in API).
    /// </summary>
    public HashSet<int> WebOnlyMods => [.. WebMods.Except(ApiMods)];

    /// <summary>
    /// Whether there is a mismatch between sources.
    /// </summary>
    public bool HasMismatch => ApiOnlyMods.Count > 0 || WebOnlyMods.Count > 0;
}

/// <summary>
/// Represents a mod entry for validation purposes.
/// </summary>
public class ValidationModEntry
{
    public int ModId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "API" or "Web"
}
