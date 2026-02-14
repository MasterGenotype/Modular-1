using CommunityToolkit.Mvvm.ComponentModel;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.Models;

/// <summary>
/// Model representing a download item in the queue.
/// </summary>
public partial class DownloadItemModel : ObservableObject
{
    [ObservableProperty]
    private DownloadItemState _state = DownloadItemState.Queued;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private long _bytesDownloaded;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _speedBytesPerSecond;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private DateTime _startTime;

    [ObservableProperty]
    private DateTime? _endTime;

    /// <summary>
    /// Unique identifier for this download item.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The mod being downloaded.
    /// </summary>
    public BackendMod Mod { get; }

    /// <summary>
    /// The file being downloaded.
    /// </summary>
    public BackendModFile? File { get; set; }

    /// <summary>
    /// Display name for the download.
    /// </summary>
    public string DisplayName
    {
        get
        {
            // Prefer file display name, then filename, then mod name
            if (!string.IsNullOrWhiteSpace(File?.DisplayName))
                return File.DisplayName;
            if (!string.IsNullOrWhiteSpace(File?.FileName))
                return File.FileName;
            if (!string.IsNullOrWhiteSpace(Mod.Name))
                return Mod.Name;
            return $"Mod {Mod.ModId}";
        }
    }

    /// <summary>
    /// Mod name for display (separate from file name).
    /// </summary>
    public string ModName => !string.IsNullOrWhiteSpace(Mod.Name) ? Mod.Name : $"Mod {Mod.ModId}";

    /// <summary>
    /// File name being downloaded.
    /// </summary>
    public string FileName => File?.FileName ?? "Unknown";

    /// <summary>
    /// Game domain for the download.
    /// </summary>
    public string? GameDomain => Mod.GameDomain;

    /// <summary>
    /// Formatted speed string.
    /// </summary>
    public string SpeedText => FormatSpeed(SpeedBytesPerSecond);

    /// <summary>
    /// Formatted progress string.
    /// </summary>
    public string ProgressText => TotalBytes > 0
        ? $"{FormatBytes(BytesDownloaded)} / {FormatBytes(TotalBytes)}"
        : FormatBytes(BytesDownloaded);

    /// <summary>
    /// Estimated time remaining.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            if (SpeedBytesPerSecond <= 0 || TotalBytes <= 0 || BytesDownloaded >= TotalBytes)
                return null;

            var remainingBytes = TotalBytes - BytesDownloaded;
            var seconds = remainingBytes / SpeedBytesPerSecond;
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public DownloadItemModel(BackendMod mod)
    {
        Mod = mod;
        StartTime = DateTime.UtcNow;
    }

    public DownloadItemModel(BackendMod mod, BackendModFile file) : this(mod)
    {
        File = file;
        TotalBytes = file.SizeBytes ?? 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:F1} {suffixes[i]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "--";
        return $"{FormatBytes((long)bytesPerSecond)}/s";
    }
}

/// <summary>
/// State of a download item.
/// </summary>
public enum DownloadItemState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}
