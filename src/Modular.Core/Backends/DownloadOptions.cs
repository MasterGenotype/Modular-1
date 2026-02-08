using Modular.Core.Backends.Common;

namespace Modular.Core.Backends;

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
    /// </summary>
    public bool Force { get; set; }

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
    /// Creates default options for normal download behavior.
    /// </summary>
    public static DownloadOptions Default => new()
    {
        DryRun = false,
        Force = false,
        Filter = FileFilter.MainAndOptional,
        MaxConcurrency = 1,
        VerifyDownloads = false,
        AutoRename = true,
        OrganizeByCategory = true
    };
}
