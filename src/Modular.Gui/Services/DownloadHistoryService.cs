using System.Collections.ObjectModel;
using System.Text.Json;
using Modular.Gui.Models;

namespace Modular.Gui.Services;

/// <summary>
/// Service for tracking download history and statistics.
/// </summary>
public class DownloadHistoryService
{
    private readonly string _historyPath;
    private readonly ObservableCollection<DownloadHistoryEntry> _history = [];
    private readonly object _lock = new();

    public IReadOnlyCollection<DownloadHistoryEntry> History => _history;

    public int TotalDownloads => _history.Count;
    public int SuccessfulDownloads => _history.Count(h => h.IsSuccess);
    public int FailedDownloads => _history.Count(h => !h.IsSuccess);
    public long TotalBytesDownloaded => _history.Where(h => h.IsSuccess).Sum(h => h.FileSize);

    public DownloadHistoryService(string historyPath)
    {
        _historyPath = historyPath;
    }

    /// <summary>
    /// Adds a download entry to the history.
    /// </summary>
    public void AddEntry(DownloadHistoryEntry entry)
    {
        lock (_lock)
        {
            _history.Insert(0, entry); // Most recent first
        }
    }

    /// <summary>
    /// Creates and adds a success entry.
    /// </summary>
    public void RecordSuccess(string modName, string fileName, string gameDomain, long fileSize, TimeSpan duration)
    {
        AddEntry(new DownloadHistoryEntry
        {
            ModName = modName,
            FileName = fileName,
            GameDomain = gameDomain,
            FileSize = fileSize,
            DownloadTime = DateTime.Now,
            Duration = duration,
            IsSuccess = true
        });
    }

    /// <summary>
    /// Creates and adds a failure entry.
    /// </summary>
    public void RecordFailure(string modName, string fileName, string gameDomain, string errorMessage)
    {
        AddEntry(new DownloadHistoryEntry
        {
            ModName = modName,
            FileName = fileName,
            GameDomain = gameDomain,
            DownloadTime = DateTime.Now,
            IsSuccess = false,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// Clears all history entries.
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _history.Clear();
        }
    }

    /// <summary>
    /// Gets history entries filtered by date range.
    /// </summary>
    public IEnumerable<DownloadHistoryEntry> GetHistoryByDateRange(DateTime start, DateTime end)
    {
        lock (_lock)
        {
            return _history.Where(h => h.DownloadTime >= start && h.DownloadTime <= end).ToList();
        }
    }

    /// <summary>
    /// Gets today's download statistics.
    /// </summary>
    public (int Total, int Success, int Failed, long Bytes) GetTodayStats()
    {
        var today = DateTime.Today;
        var todayEntries = _history.Where(h => h.DownloadTime.Date == today).ToList();
        return (
            todayEntries.Count,
            todayEntries.Count(h => h.IsSuccess),
            todayEntries.Count(h => !h.IsSuccess),
            todayEntries.Where(h => h.IsSuccess).Sum(h => h.FileSize)
        );
    }

    /// <summary>
    /// Loads history from disk.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(_historyPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_historyPath);
            var entries = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json);
            if (entries != null)
            {
                lock (_lock)
                {
                    _history.Clear();
                    foreach (var entry in entries.OrderByDescending(e => e.DownloadTime))
                    {
                        _history.Add(entry);
                    }
                }
            }
        }
        catch
        {
            // Ignore load errors - start fresh
        }
    }

    /// <summary>
    /// Saves history to disk.
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            List<DownloadHistoryEntry> snapshot;
            lock (_lock)
            {
                snapshot = [.. _history];
            }

            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_historyPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}

/// <summary>
/// Represents a single download history entry.
/// </summary>
public class DownloadHistoryEntry
{
    public string ModName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string GameDomain { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime DownloadTime { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Human-readable file size.
    /// </summary>
    public string FileSizeFormatted => FormatBytes(FileSize);

    /// <summary>
    /// Human-readable duration.
    /// </summary>
    public string DurationFormatted => Duration.TotalSeconds < 60
        ? $"{Duration.TotalSeconds:F1}s"
        : $"{Duration.TotalMinutes:F1}m";

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
