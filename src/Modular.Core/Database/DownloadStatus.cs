using System.Text.Json.Serialization;

namespace Modular.Core.Database;

/// <summary>
/// Status of a download operation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadStatus
{
    /// <summary>
    /// Download is pending/not started.
    /// </summary>
    Pending,

    /// <summary>
    /// Download is in progress.
    /// </summary>
    Downloading,

    /// <summary>
    /// Download completed successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Download completed and hash verified.
    /// </summary>
    Verified,

    /// <summary>
    /// Download completed but hash mismatch detected.
    /// </summary>
    HashMismatch,

    /// <summary>
    /// Download failed.
    /// </summary>
    Failed
}
