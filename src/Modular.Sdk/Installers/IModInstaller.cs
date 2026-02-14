namespace Modular.Sdk.Installers;

/// <summary>
/// Interface for mod installation workflows.
/// Plugins can implement this to provide custom installation logic.
/// </summary>
public interface IModInstaller
{
    /// <summary>
    /// Unique identifier for this installer type.
    /// </summary>
    string InstallerId { get; }

    /// <summary>
    /// Display name for this installer.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Priority for installer selection (higher = preferred).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Detects if this installer can handle the given archive.
    /// </summary>
    Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default);

    /// <summary>
    /// Analyzes archive layout and prepares installation plan.
    /// </summary>
    Task<InstallPlan> AnalyzeAsync(string archivePath, InstallContext context, CancellationToken ct = default);

    /// <summary>
    /// Executes the installation.
    /// </summary>
    Task<InstallResult> InstallAsync(InstallPlan plan, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Result of installer detection.
/// </summary>
public class InstallDetectionResult
{
    /// <summary>
    /// Whether this installer can handle the archive.
    /// </summary>
    public bool CanHandle { get; set; }

    /// <summary>
    /// Confidence level (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Detected installer type.
    /// </summary>
    public string? InstallerType { get; set; }

    /// <summary>
    /// Reason for detection result.
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Context for installation operations.
/// </summary>
public class InstallContext
{
    /// <summary>
    /// Target game directory.
    /// </summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Game identifier.
    /// </summary>
    public string? GameId { get; set; }

    /// <summary>
    /// Mod identifier.
    /// </summary>
    public string? ModId { get; set; }

    /// <summary>
    /// Whether to overwrite existing files.
    /// </summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>
    /// Whether to create backups.
    /// </summary>
    public bool CreateBackups { get; set; } = true;

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Installation plan with file mappings.
/// </summary>
public class InstallPlan
{
    /// <summary>
    /// Installer that created this plan.
    /// </summary>
    public string InstallerId { get; set; } = string.Empty;

    /// <summary>
    /// Source archive path.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// File operations to perform.
    /// </summary>
    public List<FileOperation> Operations { get; set; } = new();

    /// <summary>
    /// Total bytes to install.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Whether user confirmation is required.
    /// </summary>
    public bool RequiresUserInput { get; set; }

    /// <summary>
    /// Installation options (for installers like FOMOD).
    /// </summary>
    public Dictionary<string, object>? Options { get; set; }
}

/// <summary>
/// File operation for installation.
/// </summary>
public class FileOperation
{
    /// <summary>
    /// Type of operation.
    /// </summary>
    public FileOperationType Type { get; set; }

    /// <summary>
    /// Source path (within archive or filesystem).
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Destination path (relative to game directory).
    /// </summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Whether this is a critical file.
    /// </summary>
    public bool IsCritical { get; set; }
}

/// <summary>
/// Type of file operation.
/// </summary>
public enum FileOperationType
{
    /// <summary>Copy file from archive to destination.</summary>
    Copy,

    /// <summary>Extract file from archive.</summary>
    Extract,

    /// <summary>Create directory.</summary>
    CreateDirectory,

    /// <summary>Apply patch to existing file.</summary>
    Patch,

    /// <summary>Merge with existing file.</summary>
    Merge,

    /// <summary>Create symbolic link.</summary>
    Symlink
}

/// <summary>
/// Result of installation.
/// </summary>
public class InstallResult
{
    /// <summary>
    /// Whether installation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Files that were installed.
    /// </summary>
    public List<string> InstalledFiles { get; set; } = new();

    /// <summary>
    /// Files that were backed up.
    /// </summary>
    public List<string> BackedUpFiles { get; set; } = new();

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Installation manifest for uninstall.
    /// </summary>
    public InstallManifest? Manifest { get; set; }
}

/// <summary>
/// Progress information for installation.
/// </summary>
public class InstallProgress
{
    /// <summary>
    /// Current operation being performed.
    /// </summary>
    public string CurrentOperation { get; set; } = string.Empty;

    /// <summary>
    /// Current file being processed.
    /// </summary>
    public string? CurrentFile { get; set; }

    /// <summary>
    /// Files processed so far.
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// Total files to process.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Bytes processed.
    /// </summary>
    public long BytesProcessed { get; set; }

    /// <summary>
    /// Total bytes to process.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Percentage complete (0-100).
    /// </summary>
    public double Percentage => TotalFiles > 0 ? (double)FilesProcessed / TotalFiles * 100 : 0;
}

/// <summary>
/// Manifest tracking installed files for uninstall.
/// </summary>
public class InstallManifest
{
    /// <summary>
    /// Mod identifier.
    /// </summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>
    /// When installation occurred.
    /// </summary>
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Installer used.
    /// </summary>
    public string InstallerId { get; set; } = string.Empty;

    /// <summary>
    /// Files installed (relative to game directory).
    /// </summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// Directories created.
    /// </summary>
    public List<string> Directories { get; set; } = new();

    /// <summary>
    /// Backup locations.
    /// </summary>
    public Dictionary<string, string> Backups { get; set; } = new();
}
