using Modular.Sdk.Backends.Common;

namespace Modular.Sdk.Backends;

/// <summary>
/// Options for controlling download behavior in DownloadModsAsync.
/// </summary>
public class DownloadOptions
{
    /// <summary>
    /// If true, show what would be downloaded without actually downloading.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// If true, re-download files even if they already exist locally.
    /// Also discards any partially downloaded data.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// If true (default), interrupted downloads resume from their partial data
    /// instead of starting over. Ignored when <see cref="Force"/> is true.
    /// </summary>
    public bool Resume { get; set; } = true;

    /// <summary>
    /// Filter to apply when selecting which files to download.
    /// </summary>
    public FileFilter? Filter { get; set; }

    /// <summary>
    /// Callback for status messages during the scanning/preparation phase.
    /// Used for messages like "Fetching tracked mods..." before download progress starts.
    /// </summary>
    public Action<string>? StatusCallback { get; set; }

    /// <summary>
    /// Maximum number of concurrent downloads.
    /// Default is 1 (sequential downloads).
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Whether to verify downloads using MD5 checksums (if available).
    /// </summary>
    public bool VerifyDownloads { get; set; }

    /// <summary>
    /// Whether to automatically rename/organize downloaded mods.
    /// </summary>
    public bool AutoRename { get; set; }

    /// <summary>
    /// Whether to organize mods into category subdirectories.
    /// </summary>
    public bool OrganizeByCategory { get; set; }

    /// <summary>
    /// Whether to include optional mods when downloading a collection.
    /// Defaults to true.
    /// </summary>
    public bool IncludeOptional { get; set; } = true;

    /// <summary>
    /// Creates default options for normal download behavior.
    /// </summary>
    public static DownloadOptions Default => new()
    {
        DryRun = false,
        Force = false,
        Resume = true,
        Filter = FileFilter.MainAndOptional,
        MaxConcurrency = 1,
        VerifyDownloads = true,
        AutoRename = true,
        OrganizeByCategory = true
    };
}
