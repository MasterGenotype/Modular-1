using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modular.Core.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Modular.Core.Services;
using Modular.Core.Utilities;
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
    private readonly HttpClient _httpClient;
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
        _httpClient = new HttpClient();
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
        DownloadHistoryService historyService)
    {
        _nexusBackend = nexusBackend;
        _gameBananaBackend = gameBananaBackend;
        _renameService = renameService;
        _settings = settings;
        _historyService = historyService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Modular/1.0");
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.State = DownloadItemState.Downloading;
            item.StartTime = DateTime.UtcNow;
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
                        ct);
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

            // Download with progress reporting
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? item.TotalBytes;
            if (totalBytes > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.TotalBytes = totalBytes);
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long downloadedBytes = 0;
            int bytesRead;
            var lastProgressUpdate = DateTime.UtcNow;
            var lastBytes = 0L;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloadedBytes += bytesRead;

                // Update progress every 100ms
                var now = DateTime.UtcNow;
                if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
                {
                    var progress = totalBytes > 0 ? (downloadedBytes * 100.0 / totalBytes) : 0;
                    var elapsed = (now - lastProgressUpdate).TotalSeconds;
                    var speed = elapsed > 0 ? (downloadedBytes - lastBytes) / elapsed : 0;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Progress = progress;
                        item.BytesDownloaded = downloadedBytes;
                        item.SpeedBytesPerSecond = (long)speed;
                    });

                    lastProgressUpdate = now;
                    lastBytes = downloadedBytes;
                }
            }

            // Final update
            var finalSize = new FileInfo(outputPath).Length;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = DownloadItemState.Completed;
                item.Progress = 100;
                item.BytesDownloaded = finalSize;
                item.TotalBytes = finalSize;
                item.EndTime = DateTime.UtcNow;
                ActiveDownloads.Remove(item);
                CompletedDownloads.Insert(0, item);
                CompletedCount = CompletedDownloads.Count;
                QueuedCount = _pendingQueue.Count + ActiveDownloads.Count(d => d.State == DownloadItemState.Queued);
            });

            StatusMessage = $"Downloaded {item.DisplayName} to {outputPath}";

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
            
            // Track domain for reorganization (NexusMods only - GameBanana already uses mod names)
            if (item.Mod.BackendId == "nexusmods" && !string.IsNullOrEmpty(item.GameDomain))
            {
                _domainsToReorganize.Add(item.GameDomain);
            }
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
