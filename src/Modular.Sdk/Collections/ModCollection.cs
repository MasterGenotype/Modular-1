namespace Modular.Sdk.Collections;

/// <summary>
/// A curated list of mods that can be shared, exported, and bulk-downloaded.
/// Inspired by Wabbajack manifests and Vortex Collections.
/// </summary>
public class ModCollection
{
    /// <summary>Unique identifier for the collection.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Human-readable collection name.</summary>
    public required string Name { get; set; }

    /// <summary>Optional description of the collection.</summary>
    public string? Description { get; set; }

    /// <summary>Game domain or ID this collection targets (e.g. "skyrimspecialedition").</summary>
    public required string GameId { get; set; }

    /// <summary>Backend ID the mods belong to (default: "nexusmods").</summary>
    public string BackendId { get; init; } = "nexusmods";

    /// <summary>Schema version for forward compatibility.</summary>
    public string SchemaVersion { get; init; } = "1.0";

    /// <summary>When the collection was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the collection was last modified.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ordered list of mod entries in this collection.</summary>
    public List<ModCollectionEntry> Entries { get; set; } = [];
}

/// <summary>
/// A single mod entry within a collection, optionally pinned to a specific file version.
/// </summary>
public class ModCollectionEntry
{
    /// <summary>Backend-specific mod ID.</summary>
    public required string ModId { get; set; }

    /// <summary>Human-readable mod name.</summary>
    public required string Name { get; set; }

    /// <summary>Mod author name.</summary>
    public string? Author { get; set; }

    /// <summary>Pinned version string, if any.</summary>
    public string? Version { get; set; }

    /// <summary>Pinned file ID for a specific download.</summary>
    public string? FileId { get; set; }

    /// <summary>File name of the pinned download.</summary>
    public string? FileName { get; set; }

    /// <summary>Expected file size in bytes.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>MD5 hash for integrity verification (Wabbajack-style).</summary>
    public string? Md5 { get; set; }

    /// <summary>URL to the mod's page on the backend website.</summary>
    public string? Url { get; set; }

    /// <summary>User notes for this entry.</summary>
    public string? Notes { get; set; }

    /// <summary>Whether this mod is optional in the collection.</summary>
    public bool IsOptional { get; set; }

    /// <summary>When this entry was added to the collection.</summary>
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}
