using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Modular.Core.Backends.Common;

namespace Modular.Gui.Models;

/// <summary>
/// Display model for mods in the UI.
/// Wraps BackendMod with additional UI-specific state.
/// </summary>
public partial class ModDisplayModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ModDownloadStatus _status = ModDownloadStatus.Unknown;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _downloadedVersion;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private DateTime? _downloadedDate;

    [ObservableProperty]
    private int? _latestFileId;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isLoadingThumbnail;

    /// <summary>
    /// The underlying mod data from the backend.
    /// </summary>
    public BackendMod Mod { get; }

    /// <summary>
    /// Thumbnail URL from the backend.
    /// </summary>
    public string? ThumbnailUrl => Mod.ThumbnailUrl;

    /// <summary>
    /// Mod ID (string to support both numeric and non-numeric IDs).
    /// </summary>
    public string ModId => Mod.ModId;

    /// <summary>
    /// Display name of the mod.
    /// </summary>
    public string Name => Mod.Name;

    /// <summary>
    /// Game domain (e.g., "skyrimspecialedition").
    /// </summary>
    public string? GameDomain => Mod.GameDomain;

    /// <summary>
    /// Backend identifier (e.g., "nexusmods", "gamebanana").
    /// </summary>
    public string BackendId => Mod.BackendId;

    /// <summary>
    /// URL to the mod page.
    /// </summary>
    public string? Url => Mod.Url;

    /// <summary>
    /// Mod author.
    /// </summary>
    public string? Author => Mod.Author;

    /// <summary>
    /// Brief summary/description.
    /// </summary>
    public string? Summary => Mod.Summary;

    /// <summary>
    /// Last update time.
    /// </summary>
    public DateTime? UpdatedAt => Mod.UpdatedAt;

    public ModDisplayModel(BackendMod mod)
    {
        Mod = mod;
    }
}

/// <summary>
/// Download status for a mod.
/// </summary>
public enum ModDownloadStatus
{
    /// <summary>Status not yet determined.</summary>
    Unknown,

    /// <summary>Mod has not been downloaded.</summary>
    NotDownloaded,

    /// <summary>Mod has been downloaded.</summary>
    Downloaded,

    /// <summary>A newer version is available.</summary>
    UpdateAvailable,

    /// <summary>Currently downloading.</summary>
    Downloading,

    /// <summary>Download failed.</summary>
    Failed
}
