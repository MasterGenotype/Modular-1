using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Core.Backends.Common;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Gui.Models;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the download queue view.
/// </summary>
public partial class DownloadQueueViewModel : ViewModelBase
{
    private readonly NexusModsBackend? _backend;
    private readonly AppSettings? _settings;
    private readonly DownloadHistoryService? _historyService;
    private readonly ConcurrentQueue<DownloadItemModel> _pendingQueue = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1); // Single concurrent download
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private ObservableCollection<DownloadItemModel> _activeDownloads = new();

    [ObservableProperty]
    private ObservableCollection<DownloadItemModel> _completedDownloads = new();

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _queuedCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // History statistics
    [ObservableProperty]
    private int _totalHistoryDownloads;

    [ObservableProperty]
    private int _successfulHistoryDownloads;

    [ObservableProperty]
    private int _failedHistoryDownloads;

    [ObservableProperty]
    private string _totalBytesDownloadedFormatted = "0 B";

    // Designer constructor
    public DownloadQueueViewModel()
    {
        // Sample data for designer
        ActiveDownloads.Add(new DownloadItemModel(new BackendMod
        {
            ModId = "1",
            Name = "Sample Mod",
            BackendId = "nexusmods"
        })
        {
            State = DownloadItemState.Downloading,
            Progress = 45.5,
            BytesDownloaded = 50000000,
            TotalBytes = 110000000,
            SpeedBytesPerSecond = 5500000
        });
    }

    // DI constructor
    public DownloadQueueViewModel(NexusModsBackend backend, AppSettings settings, DownloadHistoryService historyService)
    {
        _backend = backend;
        _settings = settings;
        _historyService = historyService;
        UpdateHistoryStats();
    }

    private void UpdateHistoryStats()
    {
        if (_historyService == null) return;

        TotalHistoryDownloads = _historyService.TotalDownloads;
        SuccessfulHistoryDownloads = _historyService.SuccessfulDownloads;
        FailedHistoryDownloads = _historyService.FailedDownloads;
        TotalBytesDownloadedFormatted = FormatBytes(_historyService.TotalBytesDownloaded);
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Adds a mod to the download queue.
    /// </summary>
    public async Task EnqueueAsync(BackendMod mod, BackendModFile file)
    {
        var item = new DownloadItemModel(mod, file);
        _pendingQueue.Enqueue(item);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ActiveDownloads.Add(item);
            QueuedCount = _pendingQueue.Count + ActiveDownloads.Count(d => d.State == DownloadItemState.Queued);
        });

        // Start processing if not already running
        if (!IsDownloading)
        {
            _ = ProcessQueueAsync();
        }
    }

    /// <summary>
    /// Adds multiple items to the download queue.
    /// </summary>
    public async Task EnqueueManyAsync(IEnumerable<(BackendMod mod, BackendModFile file)> items)
    {
        foreach (var (mod, file) in items)
        {
            await EnqueueAsync(mod, file);
        }
    }

    [RelayCommand]
    private async Task ProcessQueueAsync()
    {
        if (_backend == null || _settings == null)
        {
            StatusMessage = "Backend not initialized";
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;

        try
        {
            while (_pendingQueue.TryDequeue(out var item))
            {
                _downloadCts.Token.ThrowIfCancellationRequested();

                await _downloadSemaphore.WaitAsync(_downloadCts.Token);
                try
                {
                    await DownloadItemAsync(item, _downloadCts.Token);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Downloads cancelled";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
        }
    }

    private async Task DownloadItemAsync(DownloadItemModel item, CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.State = DownloadItemState.Downloading;
            item.StartTime = DateTime.UtcNow;
        });

        StatusMessage = $"Downloading {item.DisplayName}...";

        try
        {
            // Get download URL
            var url = item.File?.DirectDownloadUrl;
            if (string.IsNullOrEmpty(url) && item.File != null)
            {
                url = await _backend!.ResolveDownloadUrlAsync(
                    item.Mod.ModId,
                    item.File.FileId,
                    item.GameDomain,
                    ct);
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new InvalidOperationException("Could not resolve download URL");
            }

            // Simulate download progress for now
            // In a real implementation, this would use the HTTP client with progress reporting
            for (int i = 0; i <= 100; i += 5)
            {
                ct.ThrowIfCancellationRequested();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Progress = i;
                    item.BytesDownloaded = (long)(item.TotalBytes * i / 100.0);
                    item.SpeedBytesPerSecond = 5_000_000; // 5 MB/s placeholder
                });

                await Task.Delay(100, ct); // Simulated delay
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Completed;
                item.Progress = 100;
                item.EndTime = DateTime.UtcNow;
                ActiveDownloads.Remove(item);
                CompletedDownloads.Insert(0, item);
                CompletedCount = CompletedDownloads.Count;
                QueuedCount = _pendingQueue.Count + ActiveDownloads.Count(d => d.State == DownloadItemState.Queued);
            });

            StatusMessage = $"Downloaded {item.DisplayName}";

            // Record success in history
            var duration = item.EndTime.HasValue
                ? item.EndTime.Value - item.StartTime
                : TimeSpan.Zero;
            _historyService?.RecordSuccess(
                item.Mod.Name,
                item.File?.FileName ?? "unknown",
                item.GameDomain ?? "",
                item.TotalBytes,
                duration);
            _ = _historyService?.SaveAsync();
            UpdateHistoryStats();
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Cancelled;
                item.EndTime = DateTime.UtcNow;
            });
            throw;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Failed;
                item.ErrorMessage = ex.Message;
                item.EndTime = DateTime.UtcNow;
            });
            StatusMessage = $"Failed: {ex.Message}";

            // Record failure in history
            _historyService?.RecordFailure(
                item.Mod.Name,
                item.File?.FileName ?? "unknown",
                item.GameDomain ?? "",
                ex.Message);
            _ = _historyService?.SaveAsync();
            UpdateHistoryStats();
        }
    }

    [RelayCommand]
    private void CancelAll()
    {
        _downloadCts?.Cancel();

        // Clear pending queue
        while (_pendingQueue.TryDequeue(out var item))
        {
            item.State = DownloadItemState.Cancelled;
        }

        // Mark active downloads as cancelled
        foreach (var item in ActiveDownloads.Where(d => d.State == DownloadItemState.Queued))
        {
            item.State = DownloadItemState.Cancelled;
        }

        QueuedCount = 0;
        StatusMessage = "All downloads cancelled";
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        CompletedDownloads.Clear();
        CompletedCount = 0;
    }

    [RelayCommand]
    private void RemoveItem(DownloadItemModel? item)
    {
        if (item == null) return;

        if (item.State == DownloadItemState.Completed || item.State == DownloadItemState.Failed)
        {
            CompletedDownloads.Remove(item);
            CompletedCount = CompletedDownloads.Count;
        }
        else if (item.State == DownloadItemState.Queued)
        {
            ActiveDownloads.Remove(item);
            QueuedCount--;
        }
    }

    /// <summary>
    /// Moves an item in the active downloads list (for drag-drop reordering).
    /// </summary>
    public void MoveItem(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= ActiveDownloads.Count)
            return;
        if (newIndex < 0 || newIndex >= ActiveDownloads.Count)
            return;
        if (oldIndex == newIndex)
            return;

        var item = ActiveDownloads[oldIndex];
        
        // Only allow reordering of queued items (not the currently downloading one)
        if (item.State != DownloadItemState.Queued)
        {
            StatusMessage = "Cannot reorder - item is currently downloading";
            return;
        }

        ActiveDownloads.RemoveAt(oldIndex);
        ActiveDownloads.Insert(newIndex, item);
        StatusMessage = $"Moved {item.DisplayName} in queue";
    }

    /// <summary>
    /// Gets the index of an item in the active downloads list.
    /// </summary>
    public int GetItemIndex(DownloadItemModel item)
    {
        return ActiveDownloads.IndexOf(item);
    }
}
