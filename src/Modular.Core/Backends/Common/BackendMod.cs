namespace Modular.Core.Backends.Common;

/// <summary>
/// Unified representation of a mod across all backends.
/// Backend implementations map their API-specific models to this common type.
/// </summary>
public class BackendMod
{
    /// <summary>
    /// Backend-specific mod ID.
    /// Uses string to support both integer IDs (NexusMods) and string IDs (GameBanana).
    /// </summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable mod name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Game domain or game identifier (backend-specific).
    /// For NexusMods: "skyrimspecialedition", "stardewvalley", etc.
    /// May be null for backends that don't use game domains.
    /// </summary>
    public string? GameDomain { get; set; }

    /// <summary>
    /// Mod category ID, if the backend supports categories.
    /// Used for organizing mods into category subdirectories.
    /// </summary>
    public int? CategoryId { get; set; }

    /// <summary>
    /// Category name, if available from the backend.
    /// </summary>
    public string? CategoryName { get; set; }

    /// <summary>
    /// Which backend this mod came from (e.g., "nexusmods", "gamebanana").
    /// </summary>
    public string BackendId { get; set; } = string.Empty;

    /// <summary>
    /// URL to the mod's page on the backend website.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Brief description of the mod, if available.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Mod author/uploader name.
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// When the mod was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
