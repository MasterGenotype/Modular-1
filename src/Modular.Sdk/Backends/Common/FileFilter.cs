namespace Modular.Sdk.Backends.Common;

/// <summary>
/// Filter options for retrieving mod files.
/// Used to narrow down which files are returned from GetModFilesAsync.
/// </summary>
public class FileFilter
{
    /// <summary>
    /// Filter by file categories (e.g., "main", "optional").
    /// Only files matching one of these categories will be returned.
    /// If null or empty, all categories are included.
    /// </summary>
    public List<string>? Categories { get; set; }

    /// <summary>
    /// Filter by file version pattern.
    /// If set, only files matching this version (or newer) are returned.
    /// </summary>
    public string? MinVersion { get; set; }

    /// <summary>
    /// Filter by upload date.
    /// If set, only files uploaded after this date are returned.
    /// </summary>
    public DateTime? UploadedAfter { get; set; }

    /// <summary>
    /// Whether to include archived/old versions of files.
    /// Default is false (only current files).
    /// </summary>
    public bool IncludeArchived { get; set; } = false;

    /// <summary>
    /// Creates an empty filter that includes all files.
    /// </summary>
    public static FileFilter All => new();

    /// <summary>
    /// Creates a filter for main files only.
    /// </summary>
    public static FileFilter MainOnly => new() { Categories = ["main"] };

    /// <summary>
    /// Creates a filter for main and optional files.
    /// </summary>
    public static FileFilter MainAndOptional => new() { Categories = ["main", "optional"] };
}
