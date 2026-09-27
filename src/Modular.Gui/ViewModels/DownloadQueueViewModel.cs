using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Modular.Core.Backends;
using Modular.Core.Database;
using Modular.Sdk.Backends.Common;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Modular.Core.Downloads;
using Modular.Core.Services;
using Modular.Core.Utilities;
using Modular.Gui.Messages;
using Modular.Gui.Models;
using Modular.Gui.Services;

namespace Modular.Gui.ViewModels;

/// <summary>
/// ViewModel for the download queue view.
/// </summary>
public partial class DownloadQueueViewModel : ViewModelBase
{
    private readonly NexusModsBackend? _nexusBackend;
    private readonly GameBananaBackend? _gameBananaBackend;
    private readonly IRenameService? _renameService;
    private readonly AppSettings? _settings;
    private readonly DownloadHistoryService? _historyService;
    private readonly DownloadDatabase? _downloadDatabase;
    private readonly ResumableDownloader _downloader;
    private readonly ConcurrentQueue<DownloadItemModel> _pendingQueue = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1); // Single concurrent download
    private CancellationTokenSource? _downloadCts;
    
    // Track domains that need reorganization after download batch completes
    private readonly HashSet<string> _domainsToReorganize = new();

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
        _downloader = new ResumableDownloader();
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
    public DownloadQueueViewModel(
        NexusModsBackend nexusBackend,
        GameBananaBackend gameBananaBackend,
        IRenameService renameService,
        AppSettings settings,
        DownloadHistoryService historyService,
        DownloadDatabase downloadDatabase,
        ResumableDownloader downloader)
    {
        _nexusBackend = nexusBackend;
        _gameBananaBackend = gameBananaBackend;
        _renameService = renameService;
        _settings = settings;
        _historyService = historyService;
        _downloadDatabase = downloadDatabase;
        _downloader = downloader;
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
        if (_settings == null)
        {
            StatusMessage = "Settings not initialized";
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;

        try
        {
            while (_pendingQueue.TryDequeue(out var item))
            {
                _downloadCts.Token.ThrowIfCancellationRequested();

                // Items paused or removed while waiting in the queue are skipped.
                if (item.State != DownloadItemState.Queued)
                    continue;

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
            
            // Auto-reorganize downloaded mods for NexusMods
            await ReorganizeDownloadedModsAsync();

            // Notify listeners that the download batch is complete
            WeakReferenceMessenger.Default.Send(new DownloadBatchCompletedMessage());
        }
    }
    
    /// <summary>
    /// Reorganizes downloaded mods into proper folder structure with human-readable names.
    /// </summary>
    private async Task ReorganizeDownloadedModsAsync()
    {
        if (_renameService == null || _settings == null || _domainsToReorganize.Count == 0)
            return;
            
        var domains = _domainsToReorganize.ToList();
        _domainsToReorganize.Clear();
        
        foreach (var domain in domains)
        {
            try
            {
                StatusMessage = $"Organizing mods in {domain}...";
                var gameDomainPath = Path.Combine(_settings.ModsDirectory, domain);
                
                if (!Directory.Exists(gameDomainPath))
                    continue;
                    
                // Reorganize and rename mods
                var renamed = await _renameService.ReorganizeAndRenameModsAsync(
                    gameDomainPath, 
                    _settings.OrganizeByCategory);
                    
                // Also rename category folders if organizing by category
                if (_settings.OrganizeByCategory)
                {
                    await _renameService.RenameCategoryFoldersAsync(gameDomainPath);
                }
                
                StatusMessage = renamed > 0 
                    ? $"Organized {renamed} mod(s) in {domain}" 
                    : $"Mods in {domain} already organized";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to organize {domain}: {ex.Message}";
            }
        }
        
        StatusMessage = "Ready";
    }

    private async Task DownloadItemAsync(DownloadItemModel item, CancellationToken ct)
    {
        // Per-item token so a single item can be paused without stopping the queue.
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        item.ItemCts = itemCts;
        item.PauseRequested = false;
        item.RemoveRequested = false;
        var itemToken = itemCts.Token;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.State = DownloadItemState.Downloading;
            item.StartTime = DateTime.UtcNow;
            item.EndTime = null;
            item.ErrorMessage = null;
        });

        StatusMessage = $"Downloading {item.DisplayName}...";

        try
        {
            // Get download URL based on backend
            var url = item.File?.DirectDownloadUrl;
            if (string.IsNullOrEmpty(url) && item.File != null)
            {
                if (item.Mod.BackendId == "nexusmods" && _nexusBackend != null)
                {
                    url = await _nexusBackend.ResolveDownloadUrlAsync(
                        item.Mod.ModId,
                        item.File.FileId,
                        item.GameDomain,
                        itemToken);
                }
                // GameBanana URLs are provided directly, no resolution needed
            }

            if (string.IsNullOrEmpty(url))
            {
                throw new InvalidOperationException("Could not resolve download URL");
            }

            // Build output path
            var outputDir = _settings?.ModsDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Games", "Mods-Lists");

            string modOutputDir;
            if (item.Mod.BackendId == "gamebanana")
            {
                var gbDir = _settings?.GameBananaDownloadDir ?? "gamebanana";
                modOutputDir = Path.Combine(outputDir, gbDir, FileUtils.SanitizeDirectoryName(item.Mod.Name));
            }
            else
            {
                // NexusMods: domain/modId structure
                modOutputDir = Path.Combine(outputDir, item.GameDomain ?? "unknown", item.Mod.ModId);
            }

            var fileName = item.File?.FileName ?? $"{item.Mod.ModId}.zip";
            var outputPath = Path.Combine(modOutputDir, FileUtils.SanitizeFilename(fileName));

            // Ensure directory exists
            FileUtils.EnsureDirectoryExists(modOutputDir);

            // Identity must match the backends' so partial files from the CLI resume here too.
            var fileId = item.File?.FileId ?? "0";
            var isGameBanana = item.Mod.BackendId == "gamebanana";
            var identity = isGameBanana
                ? DownloadIdentity.GameBanana(item.Mod.ModId, fileId)
                : DownloadIdentity.NexusMods(item.GameDomain ?? "unknown", item.Mod.ModId, fileId);

            if (ResumableDownloader.GetPartialLength(outputPath) > 0)
                StatusMessage = $"Resuming {item.DisplayName}...";

            // NexusMods CDN links expire; allow the downloader to fetch a fresh one.
            Func<CancellationToken, Task<string?>>? refreshUrl = null;
            if (!isGameBanana && _nexusBackend != null && item.File != null)
            {
                var nexus = _nexusBackend;
                var modId = item.Mod.ModId;
                var domain = item.GameDomain;
                refreshUrl = token => nexus.ResolveDownloadUrlAsync(modId, fileId, domain, token);
            }

            // Progress callback runs on the download thread; UI updates are posted.
            var speedWindowStart = DateTime.UtcNow;
            long? speedWindowBytes = null;
            var progress = new SyncProgress<(long downloaded, long total)>(p =>
            {
                var now = DateTime.UtcNow;
                double? speed = null;
                if (speedWindowBytes is not long windowBytes)
                {
                    speedWindowBytes = p.downloaded;
                    speedWindowStart = now;
                }
                else if ((now - speedWindowStart).TotalSeconds >= 0.5)
                {
                    speed = Math.Max(0, (p.downloaded - windowBytes) / (now - speedWindowStart).TotalSeconds);
                    speedWindowBytes = p.downloaded;
                    speedWindowStart = now;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    var total = p.total > 0 ? p.total : item.TotalBytes;
                    item.BytesDownloaded = p.downloaded;
                    if (total > 0)
                    {
                        item.TotalBytes = total;
                        item.Progress = p.downloaded * 100.0 / total;
                    }
                    if (speed is double s)
                        item.SpeedBytesPerSecond = s;
                });
            });

            var result = await _downloader.DownloadAsync(
                new ResumableDownloadRequest
                {
                    Url = url,
                    DestinationPath = outputPath,
                    Identity = identity,
                    // GameBanana sizes are exact; NexusMods only provides size_kb.
                    ExpectedSize = isGameBanana ? item.File?.SizeBytes : null,
                    ExpectedMd5 = item.File?.Md5,
                    VerifyMd5 = _settings?.VerifyDownloads ?? false,
                    RefreshUrlAsync = refreshUrl
                },
                progress,
                itemToken);

            // Final update
            var finalSize = result.FileSize;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Completed;
                item.Progress = 100;
                item.BytesDownloaded = finalSize;
                item.TotalBytes = finalSize;
                item.SpeedBytesPerSecond = 0;
                item.EndTime = DateTime.UtcNow;
                ActiveDownloads.Remove(item);
                CompletedDownloads.Insert(0, item);
                CompletedCount = CompletedDownloads.Count;
                QueuedCount = _pendingQueue.Count + ActiveDownloads.Count(d => d.State == DownloadItemState.Queued);
            });

            StatusMessage = result.Resumed
                ? $"Downloaded {item.DisplayName} to {outputPath} (resumed)"
                : $"Downloaded {item.DisplayName} to {outputPath}";

            // Record success in history
            var duration = item.EndTime.HasValue
                ? item.EndTime.Value - item.StartTime
                : TimeSpan.Zero;
            _historyService?.RecordSuccess(
                item.Mod.Name,
                item.File?.FileName ?? "unknown",
                item.GameDomain ?? "",
                finalSize,
                duration);
            _ = _historyService?.SaveAsync();
            UpdateHistoryStats();

            // Write download record to database (used by Library panel and Check Updates)
            if (_downloadDatabase != null && item.File != null &&
                int.TryParse(item.Mod.ModId, out var recModId) &&
                int.TryParse(item.File.FileId, out var recFileId))
            {
                var record = new DownloadRecord
                {
                    GameDomain = item.GameDomain ?? "",
                    ModId = recModId,
                    FileId = recFileId,
                    Filename = item.File.FileName,
                    Filepath = outputPath,
                    Url = result.FinalUrl,
                    Md5Expected = item.File.Md5 ?? "",
                    Md5Actual = result.Md5 ?? "",
                    FileSize = finalSize,
                    DownloadTime = DateTime.UtcNow,
                    Status = result.Md5Verified ? DownloadStatus.Verified : DownloadStatus.Success
                };
                _downloadDatabase.AddRecord(record);
                _ = _downloadDatabase.SaveAsync();
            }
            
            // Track domain for reorganization (NexusMods only - GameBanana already uses mod names)
            if (item.Mod.BackendId == "nexusmods" && !string.IsNullOrEmpty(item.GameDomain))
            {
                _domainsToReorganize.Add(item.GameDomain);
            }
        }
        catch (OperationCanceledException) when (item.RemoveRequested && !ct.IsCancellationRequested)
        {
            // Removed by the user mid-transfer: drop the item, but keep the partial data so
            // queueing the same file again resumes instead of starting over.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Cancelled;
                item.SpeedBytesPerSecond = 0;
                item.EndTime = DateTime.UtcNow;
                ActiveDownloads.Remove(item);
                QueuedCount = _pendingQueue.Count(i => i.State == DownloadItemState.Queued);
            });
            StatusMessage = $"Removed {item.DisplayName}";
        }
        catch (OperationCanceledException) when (item.PauseRequested && !ct.IsCancellationRequested)
        {
            // Paused by the user: partial data stays on disk; the queue moves on.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Paused;
                item.SpeedBytesPerSecond = 0;
                QueuedCount = _pendingQueue.Count + ActiveDownloads.Count(d => d.State == DownloadItemState.Queued);
            });
            StatusMessage = $"Paused {item.DisplayName}";
        }
        catch (OperationCanceledException)
        {
            // Partial data is kept, so a cancelled item can be resumed later.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Cancelled;
                item.SpeedBytesPerSecond = 0;
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
                item.SpeedBytesPerSecond = 0;
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
        finally
        {
            item.ItemCts = null;
        }
    }

    /// <summary>
    /// Pauses a queued or downloading item. Downloaded bytes are kept for resuming.
    /// </summary>
    [RelayCommand]
    private void PauseItem(DownloadItemModel? item)
    {
        if (item == null)
            return;

        if (item.State == DownloadItemState.Queued)
        {
            item.State = DownloadItemState.Paused;
            QueuedCount = _pendingQueue.Count(i => i.State == DownloadItemState.Queued);
            StatusMessage = $"Paused {item.DisplayName}";
        }
        else if (item.State == DownloadItemState.Downloading)
        {
            item.PauseRequested = true;
            try
            {
                item.ItemCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Transfer finished at the same moment.
            }
        }
    }

    /// <summary>
    /// Resumes a paused, failed or cancelled item from its partial data.
    /// </summary>
    [RelayCommand]
    private void ResumeItem(DownloadItemModel? item)
    {
        if (item == null || !item.CanResume)
            return;

        item.State = DownloadItemState.Queued;
        item.ErrorMessage = null;
        item.EndTime = null;
        item.PauseRequested = false;
        if (!ActiveDownloads.Contains(item))
            ActiveDownloads.Add(item);

        // A paused item may still be waiting in the queue; don't enqueue it twice.
        if (!_pendingQueue.Contains(item))
            _pendingQueue.Enqueue(item);
        QueuedCount = _pendingQueue.Count(i => i.State == DownloadItemState.Queued);
        StatusMessage = $"Resuming {item.DisplayName}...";

        if (!IsDownloading)
            _ = ProcessQueueAsync();
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

        if (CompletedDownloads.Remove(item))
        {
            CompletedCount = CompletedDownloads.Count;
        }
        else if (item.State == DownloadItemState.Downloading)
        {
            // Cancel the transfer; the handler in DownloadItemAsync removes the item from the list.
            item.RemoveRequested = true;
            try
            {
                item.ItemCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The transfer finished at the same moment, so remove the item directly.
                ActiveDownloads.Remove(item);
                QueuedCount = _pendingQueue.Count(i => i.State == DownloadItemState.Queued);
            }
        }
        else if (ActiveDownloads.Remove(item))
        {
            // Mark removed queued items so the queue processor skips them.
            // Any partial data stays on disk and is resumed if the file is queued again.
            if (item.State == DownloadItemState.Queued)
                item.State = DownloadItemState.Cancelled;
            QueuedCount = _pendingQueue.Count(i => i.State == DownloadItemState.Queued);
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
