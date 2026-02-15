using Microsoft.Extensions.Logging;
using Modular.Core.Exceptions;
using Modular.Core.RateLimiting;

namespace Modular.Core.Http;

/// <summary>
/// HTTP client wrapper with retry logic, rate limiting, and progress reporting.
/// </summary>
public class ModularHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IRateLimiter? _rateLimiter;
    private readonly ILogger<ModularHttpClient>? _logger;
    private RetryConfig _retryConfig = new();
    private bool _disposed;

    public ModularHttpClient(
        HttpClient httpClient,
        IRateLimiter? rateLimiter = null,
        ILogger<ModularHttpClient>? logger = null)
    {
        _httpClient = httpClient;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    /// <summary>
    /// Sets the retry configuration for requests.
    /// </summary>
    public void SetRetryConfig(RetryConfig config)
    {
        _retryConfig = config;
    }

    /// <summary>
    /// Performs an HTTP GET request with retry logic.
    /// </summary>
    /// <param name="url">URL to request</param>
    /// <param name="headers">Optional headers to include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>HTTP response message</returns>
    public async Task<HttpResponseMessage> GetAsync(
        string url,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        return await SendWithRetryAsync(HttpMethod.Get, url, null, headers, cancellationToken);
    }

    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    /// <param name="url">URL to download from</param>
    /// <param name="outputPath">Path to save the file</param>
    /// <param name="headers">Optional headers to include</param>
    /// <param name="progress">Progress reporter (downloaded bytes, total bytes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if download succeeded</returns>
    public async Task<bool> DownloadFileAsync(
        string url,
        string outputPath,
        Dictionary<string, string>? headers = null,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Wait for rate limit if needed
        if (_rateLimiter != null)
            await _rateLimiter.WaitIfNeededAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // Update rate limiter from response headers
        if (_rateLimiter != null)
            _rateLimiter.UpdateFromHeaders(response.Headers);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogError("Download failed with status {StatusCode}: {Url}", (int)response.StatusCode, url);
            throw new ApiException($"Download failed: {response.ReasonPhrase}", (int)response.StatusCode)
            {
                Url = url
            };
        }

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var downloadedBytes = 0L;
        var lastProgressUpdate = DateTime.UtcNow;

        // Ensure directory exists
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloadedBytes += bytesRead;

            // Throttle progress updates to ~10 per second
            var now = DateTime.UtcNow;
            if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
            {
                progress?.Report((downloadedBytes, totalBytes));
                lastProgressUpdate = now;
            }
        }

        // Final progress update
        progress?.Report((downloadedBytes, totalBytes));

        _logger?.LogDebug("Downloaded {Bytes} bytes to {Path}", downloadedBytes, outputPath);
        return true;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= _retryConfig.MaxRetries; attempt++)
        {
            try
            {
                // Wait for rate limit if needed
                if (_rateLimiter != null)
                    await _rateLimiter.WaitIfNeededAsync(cancellationToken);

                using var request = new HttpRequestMessage(method, url);
                if (content != null)
                    request.Content = content;

                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);

                // Update rate limiter from response headers
                if (_rateLimiter != null)
                    _rateLimiter.UpdateFromHeaders(response.Headers);

                // Check if we should retry
                if (!response.IsSuccessStatusCode && RetryConfig.ShouldRetry((int)response.StatusCode))
                {
                    if (attempt < _retryConfig.MaxRetries)
                    {
                        var delay = _retryConfig.GetDelay(attempt);
                        _logger?.LogWarning(
                            "Request to {Url} failed with {StatusCode}, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                            url, (int)response.StatusCode, delay, attempt + 1, _retryConfig.MaxRetries);

                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                }

                return response;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;

                if (attempt < _retryConfig.MaxRetries)
                {
                    var delay = _retryConfig.GetDelay(attempt);
                    _logger?.LogWarning(ex,
                        "Request to {Url} failed, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                        url, delay, attempt + 1, _retryConfig.MaxRetries);

                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout
                lastException = new NetworkException($"Request timed out: {url}") { Url = url };

                if (attempt < _retryConfig.MaxRetries)
                {
                    var delay = _retryConfig.GetDelay(attempt);
                    _logger?.LogWarning(
                        "Request to {Url} timed out, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                        url, delay, attempt + 1, _retryConfig.MaxRetries);

                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        throw new NetworkException($"Request failed after {_retryConfig.MaxRetries} retries", lastException!)
        {
            Url = url
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // Note: We don't dispose _httpClient as it's injected via constructor.
            // The caller that creates the HttpClient is responsible for its lifecycle.
            // This class is a thin wrapper providing retry and rate limiting logic.
            _disposed = true;
        }
    }
}
