using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Utilities;

namespace Modular.Core.Downloads;

/// <summary>
/// Downloads files over HTTP with support for resuming interrupted transfers.
/// </summary>
/// <remarks>
/// Data is streamed to <c>{destination}.part</c> with a JSON sidecar at <c>{destination}.part.json</c>
/// that records the file identity and the server's validators (ETag / Last-Modified).
/// On restart, the downloader requests only the missing bytes using <c>Range</c> + <c>If-Range</c>.
/// If the server signals the file changed (200 instead of 206) or the range is inconsistent,
/// the download restarts from zero. The completed file is validated (size / MD5) and then
/// atomically moved to its final path, so a file at the destination is always complete.
/// </remarks>
public sealed class ResumableDownloader : IDisposable
{
    /// <summary>Suffix of the in-progress data file.</summary>
    public const string PartSuffix = ".part";

    /// <summary>Suffix of the sidecar metadata file.</summary>
    public const string StateSuffix = ".part.json";

    private const int BufferSize = 81920;
    private const int MaxUrlRefreshes = 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _httpClient;
    private readonly ResumableDownloaderOptions _options;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a downloader using a default <see cref="SocketsHttpHandler"/> that follows redirects.
    /// </summary>
    public ResumableDownloader(ResumableDownloaderOptions? options = null, ILogger<ResumableDownloader>? logger = null)
        : this(CreateDefaultHandler(), options, logger)
    {
    }

    /// <summary>
    /// Creates a downloader using the supplied message handler (owned and disposed by the downloader).
    /// </summary>
    public ResumableDownloader(HttpMessageHandler handler, ResumableDownloaderOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? new ResumableDownloaderOptions();
        _logger = logger;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // Large files can take arbitrarily long; stalls are detected per read instead.
            Timeout = Timeout.InfiniteTimeSpan
        };
        if (!string.IsNullOrEmpty(_options.UserAgent))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.UserAgent);
    }

    /// <summary>Gets the path of the partial data file for a destination.</summary>
    public static string GetPartPath(string destinationPath) => destinationPath + PartSuffix;

    /// <summary>Gets the path of the sidecar metadata file for a destination.</summary>
    public static string GetStatePath(string destinationPath) => destinationPath + StateSuffix;

    /// <summary>Returns true if partial data exists for the destination.</summary>
    public static bool HasPartialDownload(string destinationPath) => File.Exists(GetPartPath(destinationPath));

    /// <summary>Returns the number of bytes already downloaded for the destination, or 0.</summary>
    public static long GetPartialLength(string destinationPath) => GetLength(GetPartPath(destinationPath));

    /// <summary>Deletes any partial data and sidecar for the destination.</summary>
    public static void DiscardPartial(string destinationPath) =>
        DiscardFiles(GetPartPath(destinationPath), GetStatePath(destinationPath));

    /// <summary>
    /// Downloads (or resumes downloading) a file.
    /// </summary>
    /// <param name="request">Download description.</param>
    /// <param name="progress">Receives (bytes on disk, total bytes or -1 if unknown).</param>
    /// <param name="ct">
    /// Cancellation token. Cancelling keeps the partial data on disk so a later call can resume.
    /// </param>
    /// <exception cref="DownloadIntegrityException">The completed file failed size or MD5 validation.</exception>
    /// <exception cref="HttpRequestException">The server rejected the request or retries were exhausted.</exception>
    public async Task<ResumableDownloadResult> DownloadAsync(
        ResumableDownloadRequest request,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var partPath = GetPartPath(request.DestinationPath);
        var statePath = GetStatePath(request.DestinationPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var state = LoadOrResetState(request, partPath, statePath);
        var counters = new TransferCounters();
        var url = request.Url;
        var maxAttempts = Math.Max(1, request.MaxAttempts);
        var attempts = 0;
        var failures = 0;
        var refreshes = 0;
        var freshRetryUsed = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            var offset = GetLength(partPath);

            TransferResult transfer;
            try
            {
                transfer = await TransferAsync(url, request, partPath, statePath, state, offset, progress, counters, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger?.LogInformation("Download of {File} cancelled at {Bytes} bytes; partial data kept for resume",
                    Path.GetFileName(request.DestinationPath), GetLength(partPath));
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                failures++;
                if (failures >= maxAttempts)
                {
                    _logger?.LogWarning(ex, "Download of {File} failed after {Attempts} attempts; partial data kept for resume",
                        Path.GetFileName(request.DestinationPath), attempts);
                    throw;
                }

                var delay = GetRetryDelay(failures);
                _logger?.LogWarning("Download of {File} interrupted at {Bytes} bytes ({Error}); retrying in {Delay}ms",
                    Path.GetFileName(request.DestinationPath), GetLength(partPath), ex.Message, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                continue;
            }

            if (transfer.Outcome == TransferOutcome.Restart)
            {
                failures++;
                if (failures >= maxAttempts)
                    throw new HttpRequestException(
                        $"Server returned inconsistent range responses for {Path.GetFileName(request.DestinationPath)}");
                continue;
            }

            if (transfer.Outcome == TransferOutcome.UrlRejected)
            {
                if (request.RefreshUrlAsync == null || refreshes >= MaxUrlRefreshes)
                    throw new HttpRequestException(
                        $"Download URL rejected with HTTP {transfer.StatusCode}", null, (HttpStatusCode)transfer.StatusCode);

                refreshes++;
                _logger?.LogInformation("Download URL for {File} rejected with HTTP {Status}; requesting a fresh URL",
                    Path.GetFileName(request.DestinationPath), transfer.StatusCode);
                var newUrl = await request.RefreshUrlAsync(ct);
                if (string.IsNullOrEmpty(newUrl))
                    throw new HttpRequestException(
                        $"Download URL rejected with HTTP {transfer.StatusCode} and could not be refreshed",
                        null, (HttpStatusCode)transfer.StatusCode);
                url = newUrl;
                continue;
            }

            // Completed: validate before publishing the file.
            var length = GetLength(partPath);
            string? failure = null;
            string? md5 = null;
            var md5Verified = false;

            if (request.ExpectedSize is long expectedSize && length != expectedSize)
            {
                failure = $"Size mismatch: expected {expectedSize} bytes, got {length}";
            }
            else if (!string.IsNullOrWhiteSpace(request.ExpectedMd5) && (request.VerifyMd5 || counters.Resumed))
            {
                md5 = await Md5Calculator.CalculateMd5Async(partPath, ct);
                md5Verified = string.Equals(md5, request.ExpectedMd5.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!md5Verified)
                    failure = $"MD5 mismatch: expected {request.ExpectedMd5.Trim().ToLowerInvariant()}, got {md5}";
            }

            if (failure != null)
            {
                DiscardFiles(partPath, statePath);
                if (counters.Resumed && !freshRetryUsed)
                {
                    // The splice may be the culprit; try once more from zero.
                    _logger?.LogWarning("{Failure} for resumed download {File}; restarting from zero",
                        failure, Path.GetFileName(request.DestinationPath));
                    freshRetryUsed = true;
                    counters.Resumed = false;
                    state = CreateState(request);
                    continue;
                }

                throw new DownloadIntegrityException($"{failure} for {Path.GetFileName(request.DestinationPath)}");
            }

            File.Move(partPath, request.DestinationPath, overwrite: true);
            TryDelete(statePath);

            _logger?.LogDebug("Downloaded {File} ({Bytes} bytes, {Transferred} transferred, resumed={Resumed}, attempts={Attempts})",
                Path.GetFileName(request.DestinationPath), length, counters.Bytes, counters.Resumed, attempts);

            return new ResumableDownloadResult
            {
                FilePath = request.DestinationPath,
                FileSize = length,
                BytesTransferred = counters.Bytes,
                Resumed = counters.Resumed,
                Attempts = attempts,
                Md5 = md5,
                Md5Verified = md5Verified,
                FinalUrl = url
            };
        }
    }

    private async Task<TransferResult> TransferAsync(
        string url,
        ResumableDownloadRequest request,
        string partPath,
        string statePath,
        PartialDownloadState state,
        long offset,
        IProgress<(long downloaded, long total)>? progress,
        TransferCounters counters,
        CancellationToken ct)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);

        if (offset > 0)
        {
            httpRequest.Headers.Range = new RangeHeaderValue(offset, null);
            var ifRange = BuildIfRange(state);
            if (ifRange != null)
                httpRequest.Headers.IfRange = ifRange;
        }

        HttpResponseMessage response;
        stallCts.CancelAfter(_options.StallTimeout);
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {_options.StallTimeout.TotalSeconds:0}s");
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Range starts at/after EOF. If the server's total equals what we have, we're done.
                var range = response.Content.Headers.ContentRange;
                if (offset > 0 && range?.Length == offset &&
                    (state.TotalSize == null || state.TotalSize == offset))
                {
                    return TransferResult.Of(TransferOutcome.Completed, status);
                }

                _logger?.LogWarning("Server rejected resume range at {Offset} for {File}; restarting from zero",
                    offset, Path.GetFileName(request.DestinationPath));
                ResetPart(partPath, statePath, state, counters);
                return TransferResult.Of(TransferOutcome.Restart, status);
            }

            if (status is 401 or 403 or 410)
                return TransferResult.Of(TransferOutcome.UrlRejected, status);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Download failed with HTTP {status} {response.ReasonPhrase}", null, response.StatusCode);
            }

            long writeFrom;
            long? total;

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                var knownTotal = state.TotalSize ?? request.ExpectedSize;
                if (range == null || !range.HasRange || range.From != offset ||
                    (range.Length is long rangeTotal && knownTotal is long known && rangeTotal != known))
                {
                    _logger?.LogWarning("Unexpected Content-Range '{Range}' when resuming {File} at {Offset}; restarting from zero",
                        range?.ToString(), Path.GetFileName(request.DestinationPath), offset);
                    ResetPart(partPath, statePath, state, counters);
                    return TransferResult.Of(TransferOutcome.Restart, status);
                }

                writeFrom = offset;
                total = range.Length ?? knownTotal;
                if (offset > 0)
                {
                    counters.Resumed = true;
                    _logger?.LogInformation("Resuming {File} from byte {Offset}", Path.GetFileName(request.DestinationPath), offset);
                }
                UpdateValidators(state, response, total, fullResponse: false, url);
            }
            else
            {
                if (offset > 0)
                {
                    _logger?.LogInformation("Server sent the full file for {File} (changed or no range support); restarting from zero",
                        Path.GetFileName(request.DestinationPath));
                }
                writeFrom = 0;
                counters.Resumed = false;
                total = response.Content.Headers.ContentLength;
                UpdateValidators(state, response, total, fullResponse: true, url);
            }

            SaveState(statePath, state);

            await using var fileStream = new FileStream(
                partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            fileStream.SetLength(writeFrom);
            fileStream.Seek(writeFrom, SeekOrigin.Begin);

            Stream content;
            stallCts.CancelAfter(_options.StallTimeout);
            try
            {
                content = await response.Content.ReadAsStreamAsync(stallCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data received for {_options.StallTimeout.TotalSeconds:0}s");
            }

            var position = writeFrom;
            var reportTotal = total ?? -1;
            progress?.Report((position, reportTotal));
            var lastReport = DateTime.UtcNow;

            await using (content)
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    stallCts.CancelAfter(_options.StallTimeout);
                    int read;
                    try
                    {
                        read = await content.ReadAsync(buffer, stallCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No data received for {_options.StallTimeout.TotalSeconds:0}s");
                    }

                    if (read == 0)
                        break;

                    // Writes are not cancelled mid-chunk so the file stays a clean prefix of the remote file.
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    position += read;
                    counters.Bytes += read;

                    var now = DateTime.UtcNow;
                    if (now - lastReport >= _options.ProgressInterval)
                    {
                        progress?.Report((position, reportTotal));
                        lastReport = now;
                    }
                }
            }

            await fileStream.FlushAsync(CancellationToken.None);
            progress?.Report((position, total ?? position));

            if (total is long expectedTotal)
            {
                if (position < expectedTotal)
                    throw new IOException($"Connection closed after {position} of {expectedTotal} bytes");

                if (position > expectedTotal)
                {
                    await fileStream.DisposeAsync();
                    ResetPart(partPath, statePath, state, counters);
                    return TransferResult.Of(TransferOutcome.Restart, status);
                }
            }

            return TransferResult.Of(TransferOutcome.Completed, status);
        }
    }

    private PartialDownloadState LoadOrResetState(ResumableDownloadRequest request, string partPath, string statePath)
    {
        if (request.Resume && File.Exists(partPath))
        {
            var existing = TryLoadState(statePath);
            var length = GetLength(partPath);
            if (existing != null &&
                existing.Identity == request.Identity &&
                existing.ExpectedSize == request.ExpectedSize &&
                string.Equals(existing.ExpectedMd5 ?? string.Empty, request.ExpectedMd5 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                !(existing.TotalSize is long total && length > total))
            {
                _logger?.LogInformation("Found partial download for {File} ({Bytes} bytes)", Path.GetFileName(request.DestinationPath), length);
                return existing;
            }

            _logger?.LogInformation("Discarding stale partial download for {File}", Path.GetFileName(request.DestinationPath));
        }

        DiscardFiles(partPath, statePath);
        return CreateState(request);
    }

    private static PartialDownloadState CreateState(ResumableDownloadRequest request) => new()
    {
        Identity = request.Identity,
        Url = request.Url,
        ExpectedSize = request.ExpectedSize,
        ExpectedMd5 = request.ExpectedMd5
    };

    private static PartialDownloadState? TryLoadState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath))
                return null;
            return JsonSerializer.Deserialize<PartialDownloadState>(File.ReadAllText(statePath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveState(string statePath, PartialDownloadState state)
    {
        state.UpdatedAt = DateTime.UtcNow;
        var tempPath = statePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempPath, statePath, overwrite: true);
    }

    private static void UpdateValidators(
        PartialDownloadState state, HttpResponseMessage response, long? total, bool fullResponse, string url)
    {
        var etag = response.Headers.ETag?.ToString();
        var lastModified = response.Content.Headers.LastModified;

        if (fullResponse)
        {
            // New representation: replace validators wholesale.
            state.ETag = etag;
            state.LastModified = lastModified;
        }
        else
        {
            state.ETag = etag ?? state.ETag;
            state.LastModified = lastModified ?? state.LastModified;
        }

        state.TotalSize = total;
        state.Url = url;
    }

    private static RangeConditionHeaderValue? BuildIfRange(PartialDownloadState state)
    {
        // If-Range requires a strong validator; weak ETags fall back to Last-Modified.
        if (!string.IsNullOrEmpty(state.ETag) &&
            EntityTagHeaderValue.TryParse(state.ETag, out var tag) && !tag.IsWeak)
        {
            return new RangeConditionHeaderValue(tag);
        }

        return state.LastModified is DateTimeOffset lastModified
            ? new RangeConditionHeaderValue(lastModified)
            : null;
    }

    private static void ResetPart(
        string partPath, string statePath, PartialDownloadState state, TransferCounters counters)
    {
        using (new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None)) { }
        state.ETag = null;
        state.LastModified = null;
        state.TotalSize = null;
        // Nothing of the previous transfer survives, so the next attempt is not a resume.
        counters.Resumed = false;
        SaveState(statePath, state);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } =>
            (int)code >= 500 || code == HttpStatusCode.RequestTimeout || code == HttpStatusCode.TooManyRequests,
        IOException => true,
        _ => false
    };

    private TimeSpan GetRetryDelay(int failures)
    {
        var ms = _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, failures - 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, _options.MaxRetryDelay.TotalMilliseconds));
    }

    private static long GetLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static void DiscardFiles(string partPath, string statePath)
    {
        TryDelete(partPath);
        TryDelete(statePath);
        TryDelete(statePath + ".tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = true,
        // Byte ranges refer to the encoded representation; keep content untouched.
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public void Dispose() => _httpClient.Dispose();

    private enum TransferOutcome
    {
        Completed,
        Restart,
        UrlRejected
    }

    private readonly record struct TransferResult(TransferOutcome Outcome, int StatusCode)
    {
        public static TransferResult Of(TransferOutcome outcome, int statusCode) => new(outcome, statusCode);
    }

    private sealed class TransferCounters
    {
        public long Bytes { get; set; }
        public bool Resumed { get; set; }
    }
}
