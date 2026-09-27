using System.Text.Json.Serialization;

namespace Modular.Core.Downloads;

/// <summary>
/// Describes a single file download that can be resumed after interruption.
/// </summary>
public sealed class ResumableDownloadRequest
{
    /// <summary>Download URL. May be replaced at runtime via <see cref="RefreshUrlAsync"/>.</summary>
    public required string Url { get; init; }

    /// <summary>Final path of the completed file. Partial data is kept at <c>{DestinationPath}.part</c>.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>
    /// Stable identity of the remote file (e.g. <c>nexusmods:skyrim:123:456</c>).
    /// A leftover partial file is only resumed if its recorded identity matches.
    /// </summary>
    public required string Identity { get; init; }

    /// <summary>Exact expected size in bytes, if known. Used for validation.</summary>
    public long? ExpectedSize { get; init; }

    /// <summary>Expected MD5 (hex), if known.</summary>
    public string? ExpectedMd5 { get; init; }

    /// <summary>
    /// If true, verify <see cref="ExpectedMd5"/> on every download.
    /// Resumed downloads are always verified when an MD5 is available, regardless of this flag.
    /// </summary>
    public bool VerifyMd5 { get; init; }

    /// <summary>
    /// Optional callback used to obtain a fresh URL when the current one is rejected
    /// (401/403/410), e.g. an expired signed CDN link.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? RefreshUrlAsync { get; init; }

    /// <summary>If false, any existing partial data is discarded and the download starts from zero.</summary>
    public bool Resume { get; init; } = true;

    /// <summary>Maximum number of transfer attempts within a single call.</summary>
    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// Outcome of a successful resumable download.
/// </summary>
public sealed class ResumableDownloadResult
{
    /// <summary>Path of the completed file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Final size of the file in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Bytes actually transferred over the network during this call.</summary>
    public long BytesTransferred { get; init; }

    /// <summary>True if any previously downloaded bytes were reused.</summary>
    public bool Resumed { get; init; }

    /// <summary>Number of transfer attempts used.</summary>
    public int Attempts { get; init; }

    /// <summary>Actual MD5 of the file, if it was computed.</summary>
    public string? Md5 { get; init; }

    /// <summary>True if the MD5 was computed and matched <see cref="ResumableDownloadRequest.ExpectedMd5"/>.</summary>
    public bool Md5Verified { get; init; }

    /// <summary>The URL the final transfer used (may differ from the request after a refresh).</summary>
    public required string FinalUrl { get; init; }
}

/// <summary>
/// Tunables for <see cref="ResumableDownloader"/>.
/// </summary>
public sealed class ResumableDownloaderOptions
{
    /// <summary>Abort and retry a transfer if no bytes arrive for this long.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Initial delay before retrying a failed transfer.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for the exponential retry delay.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(16);

    /// <summary>Minimum interval between progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>User-Agent header sent with every request.</summary>
    public string UserAgent { get; init; } = "Modular/1.0";
}

/// <summary>
/// Thrown when a completed download fails size or checksum validation.
/// The partial data is discarded before this is thrown.
/// </summary>
public sealed class DownloadIntegrityException : Exception
{
    public DownloadIntegrityException(string message) : base(message) { }
}

/// <summary>
/// Sidecar metadata persisted next to a <c>.part</c> file.
/// </summary>
internal sealed class PartialDownloadState
{
    [JsonPropertyName("identity")]
    public string Identity { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("expected_size")]
    public long? ExpectedSize { get; set; }

    [JsonPropertyName("expected_md5")]
    public string? ExpectedMd5 { get; set; }

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("total_size")]
    public long? TotalSize { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
