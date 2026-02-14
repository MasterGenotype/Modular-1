namespace Modular.Sdk.Backends.Common;

/// <summary>
/// Unified representation of a downloadable file across all backends.
/// Backend implementations map their API-specific file models to this common type.
/// </summary>
public class BackendModFile
{
    /// <summary>
    /// Backend-specific file ID.
    /// Uses string to support both integer IDs (NexusMods) and string IDs (other backends).
    /// </summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>
    /// Actual filename for saving (e.g., "MyMod-1.2.3.zip").
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name (may differ from FileName).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// File size in bytes, if known.
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// MD5 hash for verification, if the backend provides it.
    /// </summary>
    public string? Md5 { get; set; }

    /// <summary>
    /// File version string, if available.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// File category (e.g., "main", "optional", "update", "old_version").
    /// Used for filtering which files to download.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Pre-resolved direct download URL, if available.
    /// Some backends (GameBanana) include URLs in file listings.
    /// Others (NexusMods) require a separate API call to resolve.
    /// </summary>
    public string? DirectDownloadUrl { get; set; }

    /// <summary>
    /// When the file was uploaded/last modified.
    /// </summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>
    /// File description or changelog, if available.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Parent mod ID this file belongs to.
    /// </summary>
    public string? ModId { get; set; }
}
