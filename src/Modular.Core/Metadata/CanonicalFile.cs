namespace Modular.Core.Metadata;

/// <summary>
/// Represents a downloadable file with hashes and metadata.
/// </summary>
public class CanonicalFile
{
    /// <summary>
    /// Backend-specific file ID.
    /// </summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>
    /// Filename for saving.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// File hashes for verification.
    /// </summary>
    public FileHashes Hashes { get; set; } = new();

    /// <summary>
    /// When the file was uploaded.
    /// </summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>
    /// Download information.
    /// </summary>
    public DownloadInfo Download { get; set; } = new();

    /// <summary>
    /// File category/type (main, optional, patch, etc.).
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// File description or changelog.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// File hash information for verification.
/// </summary>
public class FileHashes
{
    /// <summary>
    /// MD5 hash.
    /// </summary>
    public string? Md5 { get; set; }

    /// <summary>
    /// SHA-256 hash.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// SHA-1 hash.
    /// </summary>
    public string? Sha1 { get; set; }
}

/// <summary>
/// Download information for a file.
/// </summary>
public class DownloadInfo
{
    /// <summary>
    /// Direct download URL if available.
    /// </summary>
    public string? DirectUrl { get; set; }

    /// <summary>
    /// Whether URL requires backend-specific resolution.
    /// </summary>
    public bool RequiresResolution { get; set; }
}
