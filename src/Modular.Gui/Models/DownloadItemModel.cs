using CommunityToolkit.Mvvm.ComponentModel;
using Modular.Sdk.Backends.Common;

namespace Modular.Gui.Models;

/// <summary>
/// Model representing a download item in the queue.
/// </summary>
public partial class DownloadItemModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private DownloadItemState _state = DownloadItemState.Queued;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private long _bytesDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _speedBytesPerSecond;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private DateTime _startTime;

    [ObservableProperty]
    private DateTime? _endTime;

    /// <summary>
    /// Where the finished download was written. Null until the download completes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string? _filePath;

    /// <summary>
    /// Whether the item has a downloaded file that file operations can act on.
    /// </summary>
    public bool HasFile => State == DownloadItemState.Completed && !string.IsNullOrEmpty(FilePath);

    /// <summary>
    /// Unique identifier for this download item.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Cancellation source for the in-flight transfer of this item (used for pausing).
    /// </summary>
    public CancellationTokenSource? ItemCts { get; set; }

    /// <summary>
    /// Set when the user pauses this item, to distinguish a pause from a cancellation.
    /// </summary>
    public bool PauseRequested { get; set; }

    /// <summary>
    /// Set when the user removes this item while it is downloading, to distinguish a
    /// removal from a pause or a batch cancellation.
    /// </summary>
    public bool RemoveRequested { get; set; }

    /// <summary>
    /// Set for a file fetched to be installed, such as a missing dependency: once it has
    /// downloaded it is added to the installer's list.
    /// </summary>
    public bool AddToInstaller { get; set; }

    /// <summary>
    /// Whether the item can currently be paused.
    /// </summary>
    public bool CanPause => State is DownloadItemState.Downloading or DownloadItemState.Queued;

    /// <summary>
    /// Whether the item can be resumed (paused, failed or cancelled items keep their partial data).
    /// </summary>
    public bool CanResume => State is DownloadItemState.Paused or DownloadItemState.Failed or DownloadItemState.Cancelled;

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
