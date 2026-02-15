using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Modular.Core.Downloads;

/// <summary>
/// Durable download queue with persistent state and automatic recovery.
/// Survives application restarts and supports retry logic.
/// </summary>
public class DownloadQueue
{
    private readonly string _queuePath;
    private readonly DownloadEngine _downloadEngine;
    private readonly ILogger<DownloadQueue>? _logger;
    private readonly List<QueuedDownload> _queue = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private CancellationTokenSource? _processingCts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;
    public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;

    public DownloadQueue(
        string queuePath,
        DownloadEngine downloadEngine,
        ILogger<DownloadQueue>? logger = null)
    {
        _queuePath = queuePath;
        _downloadEngine = downloadEngine;
        _logger = logger;
    }

    /// <summary>
    /// Adds a download to the queue.
    /// </summary>
    public async Task<string> EnqueueAsync(QueuedDownload download)
    {
        download.Id = Guid.NewGuid().ToString();
        download.Status = QueueItemStatus.Pending;
        download.QueuedAt = DateTime.UtcNow;

        lock (_lock)
        {
            _queue.Add(download);
        }

        await SaveQueueAsync();
        _logger?.LogInformation("Enqueued download: {Name}", download.FileName);

        return download.Id;
    }

    /// <summary>
    /// Removes a download from the queue.
    /// </summary>
    public async Task<bool> RemoveAsync(string downloadId)
    {
        lock (_lock)
        {
            var download = _queue.FirstOrDefault(d => d.Id == downloadId);
            if (download == null)
                return false;

            // Cancel if in progress
            if (download.Status == QueueItemStatus.InProgress && download.CancellationSource != null)
            {
                download.CancellationSource.Cancel();
            }

            _queue.Remove(download);
        }

        await SaveQueueAsync();
        return true;
    }

    /// <summary>
    /// Pauses a download.
    /// </summary>
    public void Pause(string downloadId)
    {
        lock (_lock)
        {
            var download = _queue.FirstOrDefault(d => d.Id == downloadId);
            if (download != null && download.Status == QueueItemStatus.InProgress)
            {
                download.CancellationSource?.Cancel();
                download.Status = QueueItemStatus.Paused;
            }
        }
    }

    /// <summary>
    /// Resumes a paused download.
    /// </summary>
    public async Task ResumeAsync(string downloadId)
    {
        lock (_lock)
        {
            var download = _queue.FirstOrDefault(d => d.Id == downloadId);
            if (download != null && download.Status == QueueItemStatus.Paused)
            {
                download.Status = QueueItemStatus.Pending;
            }
        }

        await SaveQueueAsync();
    }

    /// <summary>
    /// Gets all downloads in the queue.
    /// </summary>
    public List<QueuedDownload> GetAll()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    /// <summary>
    /// Gets pending downloads count.
    /// </summary>
    public int GetPendingCount()
    {
        lock (_lock)
        {
            return _queue.Count(d => d.Status == QueueItemStatus.Pending || d.Status == QueueItemStatus.Paused);
        }
    }

    /// <summary>
    /// Starts processing the queue.
    /// </summary>
    public async Task StartProcessingAsync(CancellationToken ct = default)
    {
        await _processingLock.WaitAsync(ct);
        try
        {
            if (_processingCts != null && !_processingCts.IsCancellationRequested)
            {
                _logger?.LogWarning("Queue processing already started");
                return;
            }

            _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ProcessQueueAsync(_processingCts.Token);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    /// <summary>
    /// Stops processing the queue.
    /// </summary>
    public void StopProcessing()
    {
        _processingCts?.Cancel();
        _processingCts = null;
    }

    /// <summary>
    /// Processes the download queue.
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        _logger?.LogInformation("Started queue processing");

        while (!ct.IsCancellationRequested)
        {
            QueuedDownload? download = null;

            lock (_lock)
            {
                download = _queue
                    .Where(d => d.Status == QueueItemStatus.Pending)
                    .OrderBy(d => d.Priority)
                    .ThenBy(d => d.QueuedAt)
                    .FirstOrDefault();
            }

            if (download == null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            await ProcessDownloadAsync(download, ct);
        }

        _logger?.LogInformation("Stopped queue processing");
    }

    /// <summary>
    /// Processes a single download with retry logic.
    /// </summary>
    private async Task ProcessDownloadAsync(QueuedDownload download, CancellationToken ct)
    {
        download.Status = QueueItemStatus.InProgress;
        download.StartedAt = DateTime.UtcNow;
        download.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await SaveQueueAsync();

        try
        {
            var progress = new Progress<FileDownloadProgress>(p =>
            {
                download.BytesDownloaded = p.BytesDownloaded;
                download.TotalBytes = p.TotalBytes;
                download.Speed = p.Speed;
                
                DownloadProgress?.Invoke(this, new DownloadProgressEventArgs
                {
                    DownloadId = download.Id,
                    Progress = p
                });
            });

            var result = await _downloadEngine.DownloadAsync(
                download.Url,
                download.OutputPath,
                download.Options,
                progress,
                download.CancellationSource.Token);

            if (result.Success)
            {
                download.Status = QueueItemStatus.Completed;
                download.CompletedAt = DateTime.UtcNow;
                download.RetryCount = 0;

                _logger?.LogInformation("Download completed: {Name}", download.FileName);
            }
            else if (result.Cancelled)
            {
                download.Status = QueueItemStatus.Paused;
                _logger?.LogInformation("Download paused: {Name}", download.FileName);
            }
            else
            {
                await HandleFailedDownloadAsync(download, result.Error);
            }

            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
            {
                DownloadId = download.Id,
                Success = result.Success,
                Error = result.Error
            });
        }
        catch (Exception ex)
        {
            await HandleFailedDownloadAsync(download, ex.Message);
        }
        finally
        {
            download.CancellationSource?.Dispose();
            download.CancellationSource = null;
            await SaveQueueAsync();
        }
    }

    /// <summary>
    /// Handles failed downloads with exponential backoff retry.
    /// </summary>
    private async Task HandleFailedDownloadAsync(QueuedDownload download, string? error)
    {
        download.RetryCount++;
        download.LastError = error;

        if (download.RetryCount >= download.MaxRetries)
        {
            download.Status = QueueItemStatus.Failed;
            _logger?.LogError("Download failed after {Count} retries: {Name}", download.RetryCount, download.FileName);
        }
        else
        {
            download.Status = QueueItemStatus.Pending;
            
            // Exponential backoff: 2^retry * base delay
            var delay = TimeSpan.FromSeconds(Math.Pow(2, download.RetryCount) * 5);
            download.NextRetryAt = DateTime.UtcNow + delay;

            _logger?.LogWarning("Download failed, retry {Count}/{Max} in {Delay}s: {Name}",
                download.RetryCount, download.MaxRetries, delay.TotalSeconds, download.FileName);

            await Task.Delay(delay);
        }
    }

    /// <summary>
    /// Saves queue state to disk.
    /// </summary>
    public async Task SaveQueueAsync()
    {
        List<QueuedDownload> snapshot;
        lock (_lock)
        {
            snapshot = _queue.Select(d => new QueuedDownload
            {
                Id = d.Id,
                Url = d.Url,
                OutputPath = d.OutputPath,
                FileName = d.FileName,
                TotalBytes = d.TotalBytes,
                BytesDownloaded = d.BytesDownloaded,
                Status = d.Status,
                Priority = d.Priority,
                QueuedAt = d.QueuedAt,
                StartedAt = d.StartedAt,
                CompletedAt = d.CompletedAt,
                RetryCount = d.RetryCount,
                MaxRetries = d.MaxRetries,
                LastError = d.LastError,
                Speed = d.Speed,
                Options = d.Options
            }).ToList();
        }

        var directory = Path.GetDirectoryName(_queuePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(_queuePath, json);
    }

    /// <summary>
    /// Loads queue state from disk.
    /// </summary>
    public async Task LoadQueueAsync()
    {
        if (!File.Exists(_queuePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_queuePath);
            var downloads = JsonSerializer.Deserialize<List<QueuedDownload>>(json, JsonOptions);

            if (downloads != null)
            {
                lock (_lock)
                {
                    _queue.Clear();
                    foreach (var download in downloads)
                    {
                        // Reset in-progress downloads to pending
                        if (download.Status == QueueItemStatus.InProgress)
                            download.Status = QueueItemStatus.Pending;

                        _queue.Add(download);
                    }
                }

                _logger?.LogInformation("Loaded {Count} downloads from queue", downloads.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load queue");
        }
    }
}

/// <summary>
/// A queued download item.
/// </summary>
public class QueuedDownload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("bytes_downloaded")]
    public long BytesDownloaded { get; set; }

    [JsonPropertyName("status")]
    public QueueItemStatus Status { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; } // Lower = higher priority

    [JsonPropertyName("queued_at")]
    public DateTime QueuedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; }

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; } = 3;

    [JsonPropertyName("next_retry_at")]
    public DateTime? NextRetryAt { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    [JsonPropertyName("options")]
    public FileDownloadOptions? Options { get; set; }

    [JsonIgnore]
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Status of a queued download item.
/// Distinct from Modular.Core.Database.DownloadStatus which tracks database record state.
/// </summary>
public enum QueueItemStatus
{
    Pending,
    InProgress,
    Paused,
    Completed,
    Failed
}

/// <summary>
/// Event args for download progress.
/// </summary>
public class DownloadProgressEventArgs : EventArgs
{
    public string DownloadId { get; set; } = string.Empty;
    public FileDownloadProgress Progress { get; set; } = new();
}

/// <summary>
/// Event args for download completion.
/// </summary>
public class DownloadCompletedEventArgs : EventArgs
{
    public string DownloadId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}
