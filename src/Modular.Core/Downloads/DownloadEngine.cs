using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Downloads;

/// <summary>
/// Production-grade download engine with streaming, resumable downloads, and progress tracking.
/// </summary>
public class DownloadEngine
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DownloadEngine>? _logger;
    private readonly SemaphoreSlim _concurrencyLimit;

    public DownloadEngine(
        HttpClient? httpClient = null,
        int maxConcurrentDownloads = 3,
        ILogger<DownloadEngine>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        _logger = logger;
        _concurrencyLimit = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);

        // Set default timeout
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Downloads a file with streaming and progress tracking.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string url,
        string outputPath,
        FileDownloadOptions? options = null,
        IProgress<FileDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new FileDownloadOptions();
        var result = new DownloadResult { Url = url, OutputPath = outputPath };

        try
        {
            await _concurrencyLimit.WaitAsync(ct);

            try
            {
                // Check if file exists and resume if supported
                long resumePosition = 0;
                if (File.Exists(outputPath) && options.AllowResume)
                {
                    var fileInfo = new FileInfo(outputPath);
                    resumePosition = fileInfo.Length;
                    _logger?.LogDebug("Resuming download from byte {Position}", resumePosition);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                
                // Add authentication headers if provided
                if (options.AuthenticationHeaders != null)
                {
                    foreach (var (key, value) in options.AuthenticationHeaders)
                        request.Headers.Add(key, value);
                }

                // Add Range header for resume
                if (resumePosition > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumePosition, null);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                // Check for expired URL (403, 404, or redirect)
                if (!response.IsSuccessStatusCode && options.UrlResolver != null)
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden || 
                        response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger?.LogWarning("Download URL expired or invalid, attempting re-resolution");
                        var newUrl = await options.UrlResolver();
                        if (!string.IsNullOrEmpty(newUrl) && newUrl != url)
                        {
                            return await DownloadAsync(newUrl, outputPath, options, progress, ct);
                        }
                    }
                }

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var supportsResume = response.StatusCode == HttpStatusCode.PartialContent;

                result.TotalBytes = totalBytes + resumePosition;
                result.SupportsResume = supportsResume;

                // Get ETag for caching
                if (response.Headers.TryGetValues("ETag", out var etagValues))
                    result.ETag = etagValues.FirstOrDefault();

                // Stream to file
                var downloadedBytes = await StreamToFileAsync(
                    response,
                    outputPath,
                    resumePosition,
                    totalBytes + resumePosition,
                    options,
                    progress,
                    ct);

                result.DownloadedBytes = downloadedBytes;

                // Verify hash if provided
                if (!string.IsNullOrEmpty(options.ExpectedHash))
                {
                    var actualHash = await ComputeHashAsync(outputPath, options.HashAlgorithm, ct);
                    result.ActualHash = actualHash;
                    result.HashVerified = actualHash.Equals(options.ExpectedHash, StringComparison.OrdinalIgnoreCase);

                    if (!result.HashVerified)
                    {
                        result.Success = false;
                        result.Error = $"Hash mismatch: expected {options.ExpectedHash}, got {actualHash}";
                        _logger?.LogWarning("Hash verification failed for {Path}", outputPath);
                        return result;
                    }
                }

                result.Success = true;
                _logger?.LogInformation("Downloaded {Bytes} bytes to {Path}", downloadedBytes, outputPath);
            }
            finally
            {
                _concurrencyLimit.Release();
            }
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Download cancelled";
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Download failed for {Url}", url);
        }

        return result;
    }

    /// <summary>
    /// Streams HTTP response content to a file with progress tracking.
    /// </summary>
    private async Task<long> StreamToFileAsync(
        HttpResponseMessage response,
        string outputPath,
        long resumePosition,
        long totalBytes,
        FileDownloadOptions options,
        IProgress<FileDownloadProgress>? progress,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var fileMode = resumePosition > 0 ? FileMode.Append : FileMode.Create;
        
        await using var fileStream = new FileStream(outputPath, fileMode, FileAccess.Write, FileShare.None, 8192, true);
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[8192];
        long totalRead = resumePosition;
        var startTime = DateTime.UtcNow;
        var lastProgressUpdate = DateTime.UtcNow;
        int bytesRead;

        while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;

            // Update progress (throttle to avoid excessive updates)
            var now = DateTime.UtcNow;
            if ((now - lastProgressUpdate).TotalMilliseconds >= options.ProgressUpdateInterval)
            {
                var elapsed = (now - startTime).TotalSeconds;
                var speed = elapsed > 0 ? totalRead / elapsed : 0;
                var eta = speed > 0 && totalBytes > 0 ? (TimeSpan?)TimeSpan.FromSeconds((totalBytes - totalRead) / speed) : null;

                progress?.Report(new FileDownloadProgress
                {
                    BytesDownloaded = totalRead,
                    TotalBytes = totalBytes,
                    Percentage = totalBytes > 0 ? (double)totalRead / totalBytes * 100 : 0,
                    Speed = speed,
                    EstimatedTimeRemaining = eta
                });

                lastProgressUpdate = now;
            }
        }

        // Final progress update
        progress?.Report(new FileDownloadProgress
        {
            BytesDownloaded = totalRead,
            TotalBytes = totalBytes,
            Percentage = 100,
            Speed = 0,
            IsComplete = true
        });

        return totalRead;
    }

    /// <summary>
    /// Computes hash of a file.
    /// </summary>
    private async Task<string> ComputeHashAsync(
        string filePath,
        HashAlgorithmType algorithm,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        
        using HashAlgorithm hashAlg = algorithm switch
        {
            HashAlgorithmType.MD5 => MD5.Create(),
            HashAlgorithmType.SHA1 => SHA1.Create(),
            HashAlgorithmType.SHA256 => SHA256.Create(),
            _ => MD5.Create()
        };

        var hashBytes = await hashAlg.ComputeHashAsync(stream, ct);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}

/// <summary>
/// Options for file download operations.
/// Distinct from Modular.Sdk.Backends.DownloadOptions which is for backend-level download configuration.
/// </summary>
public class FileDownloadOptions
{
    /// <summary>
    /// Allow resuming interrupted downloads.
    /// </summary>
    public bool AllowResume { get; set; } = true;

    /// <summary>
    /// Expected hash for verification.
    /// </summary>
    public string? ExpectedHash { get; set; }

    /// <summary>
    /// Hash algorithm to use for verification.
    /// </summary>
    public HashAlgorithmType HashAlgorithm { get; set; } = HashAlgorithmType.MD5;

    /// <summary>
    /// Authentication headers (e.g., API keys).
    /// </summary>
    public Dictionary<string, string>? AuthenticationHeaders { get; set; }

    /// <summary>
    /// Function to re-resolve expired download URLs.
    /// </summary>
    public Func<Task<string?>>? UrlResolver { get; set; }

    /// <summary>
    /// Progress update interval in milliseconds.
    /// </summary>
    public int ProgressUpdateInterval { get; set; } = 100;
}

/// <summary>
/// Result of a download operation.
/// </summary>
public class DownloadResult
{
    public bool Success { get; set; }
    public string Url { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public bool SupportsResume { get; set; }
    public string? ETag { get; set; }
    public string? ActualHash { get; set; }
    public bool HashVerified { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Progress information for an active file download.
/// Distinct from Modular.Sdk.Backends.DownloadProgress which is for backend-level operation progress.
/// </summary>
public class FileDownloadProgress
{
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage { get; set; }
    public double Speed { get; set; } // bytes per second
    public TimeSpan? EstimatedTimeRemaining { get; set; }
    public bool IsComplete { get; set; }

    public string GetSpeedString()
    {
        if (Speed < 1024) return $"{Speed:F0} B/s";
        if (Speed < 1024 * 1024) return $"{Speed / 1024:F1} KB/s";
        return $"{Speed / (1024 * 1024):F1} MB/s";
    }

    public string GetSizeString(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>
/// Hash algorithm types.
/// </summary>
public enum HashAlgorithmType
{
    MD5,
    SHA1,
    SHA256
}
