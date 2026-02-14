namespace Modular.Sdk.Backends;

/// <summary>
/// Progress information reported during download operations.
/// Used with IProgress&lt;DownloadProgress&gt; to update UI.
/// </summary>
public class DownloadProgress
{
    /// <summary>
    /// Current status message (e.g., "Downloading MyMod.zip").
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Number of items completed so far.
    /// </summary>
    public int Completed { get; set; }

    /// <summary>
    /// Total number of items to process.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Name of the file currently being processed, if applicable.
    /// </summary>
    public string? CurrentFile { get; set; }

    /// <summary>
    /// Bytes downloaded for the current file, if tracking file-level progress.
    /// </summary>
    public long? CurrentFileBytes { get; set; }

    /// <summary>
    /// Total bytes for the current file, if known.
    /// </summary>
    public long? CurrentFileTotalBytes { get; set; }

    /// <summary>
    /// Current phase of the download operation.
    /// </summary>
    public DownloadPhase Phase { get; set; } = DownloadPhase.Scanning;

    /// <summary>
    /// Percentage complete (0-100), calculated from Completed/Total.
    /// </summary>
    public int PercentComplete => Total > 0 ? (int)(Completed * 100.0 / Total) : 0;

    /// <summary>
    /// Creates a progress update for the scanning phase.
    /// </summary>
    public static DownloadProgress Scanning(string status) => new()
    {
        Status = status,
        Phase = DownloadPhase.Scanning
    };

    /// <summary>
    /// Creates a progress update for active downloading.
    /// </summary>
    public static DownloadProgress Downloading(string status, int completed, int total, string? currentFile = null) => new()
    {
        Status = status,
        Completed = completed,
        Total = total,
        CurrentFile = currentFile,
        Phase = DownloadPhase.Downloading
    };

    /// <summary>
    /// Creates a progress update for completion.
    /// </summary>
    public static DownloadProgress Done(int total) => new()
    {
        Status = "Done",
        Completed = total,
        Total = total,
        Phase = DownloadPhase.Complete
    };
}

/// <summary>
/// Phase of the download operation.
/// </summary>
public enum DownloadPhase
{
    /// <summary>Fetching mod lists and file information.</summary>
    Scanning,

    /// <summary>Actively downloading files.</summary>
    Downloading,

    /// <summary>Verifying downloaded files.</summary>
    Verifying,

    /// <summary>Renaming/organizing files.</summary>
    Organizing,

    /// <summary>Operation complete.</summary>
    Complete
}
