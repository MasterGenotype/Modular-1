namespace Modular.Core.Metadata;

/// <summary>
/// Canonical representation of a mod across all backends.
/// This unified schema supports dependencies, versioning, and multi-source metadata.
/// </summary>
public class CanonicalMod
{
    /// <summary>
    /// Unique canonical identifier for this mod.
    /// Format: "backend:projectId" (e.g., "nexusmods:12345", "gamebanana:67890")
    /// </summary>
    public string CanonicalId { get; set; } = string.Empty;

    /// <summary>
    /// Source information for this mod.
    /// </summary>
    public ModSource Source { get; set; } = new();

    /// <summary>
    /// Human-readable mod name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Brief summary/description of the mod.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Mod authors.
    /// </summary>
    public List<ModAuthor> Authors { get; set; } = new();

    /// <summary>
    /// Tags/keywords associated with the mod.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Game information.
    /// </summary>
    public GameInfo? Game { get; set; }

    /// <summary>
    /// Categories this mod belongs to.
    /// </summary>
    public List<ModCategory> Categories { get; set; } = new();

    /// <summary>
    /// Asset URLs (thumbnails, images, etc.).
    /// </summary>
    public ModAssets Assets { get; set; } = new();

    /// <summary>
    /// Timestamps for creation and updates.
    /// </summary>
    public ModTimestamps Timestamps { get; set; } = new();

    /// <summary>
    /// Available versions of this mod.
    /// </summary>
    public List<CanonicalVersion> Versions { get; set; } = new();
}

/// <summary>
/// Source information for a mod.
/// </summary>
public class ModSource
{
    /// <summary>
    /// Backend ID (e.g., "nexusmods", "gamebanana", "modrinth").
    /// </summary>
    public string BackendId { get; set; } = string.Empty;

    /// <summary>
    /// Backend-specific project ID.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Optional slug/friendly URL identifier.
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>
    /// Direct URL to the mod page.
    /// </summary>
    public string? Url { get; set; }
}

/// <summary>
/// Mod author information.
/// </summary>
public class ModAuthor
{
    /// <summary>
    /// Author name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Backend-specific author ID.
    /// </summary>
    public string? Id { get; set; }
}

/// <summary>
/// Game information.
/// </summary>
public class GameInfo
{
    /// <summary>
    /// Game ID (backend-specific).
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Game domain/identifier (e.g., "skyrimspecialedition").
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Human-readable game name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Mod category information.
/// </summary>
public class ModCategory
{
    /// <summary>
    /// Category ID (can be string or integer depending on backend).
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Category name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Mod asset URLs.
/// </summary>
public class ModAssets
{
    /// <summary>
    /// Thumbnail/icon URL.
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Gallery/screenshot URLs.
    /// </summary>
    public List<string> GalleryUrls { get; set; } = new();
}

/// <summary>
/// Mod timestamps.
/// </summary>
public class ModTimestamps
{
    /// <summary>
    /// When the mod was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// When the mod was published/created.
    /// </summary>
    public DateTime? PublishedAt { get; set; }
}
