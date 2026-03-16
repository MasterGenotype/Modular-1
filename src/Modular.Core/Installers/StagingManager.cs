using Microsoft.Extensions.Logging;
using Modular.Core.Utilities;

namespace Modular.Core.Installers;

/// <summary>
/// Manages staging directories for a two-phase install pipeline (stage → commit).
/// Files are first extracted into a temporary staging directory, verified, and then
/// atomically moved to the target directory.
/// </summary>
public class StagingManager
{
    private readonly string _baseStagingPath;
    private readonly ILogger<StagingManager>? _logger;

    /// <summary>
    /// Creates a new staging manager.
    /// </summary>
    /// <param name="baseStagingPath">Root directory for staging areas (e.g., ~/.config/Modular/staging/).</param>
    /// <param name="logger">Optional logger.</param>
    public StagingManager(string baseStagingPath, ILogger<StagingManager>? logger = null)
    {
        _baseStagingPath = baseStagingPath;
        _logger = logger;
        Directory.CreateDirectory(_baseStagingPath);
    }

    /// <summary>
    /// Creates a new staging area for an installation changeset.
    /// </summary>
    /// <returns>A StagingSession that tracks the staging directory and provides commit/rollback.</returns>
    public StagingSession CreateSession()
    {
        var changesetId = Guid.NewGuid().ToString("N")[..12];
        var stagingDir = Path.Combine(_baseStagingPath, changesetId);
        Directory.CreateDirectory(stagingDir);

        _logger?.LogInformation("Created staging session {ChangesetId} at {Path}", changesetId, stagingDir);
        return new StagingSession(changesetId, stagingDir, _logger);
    }

    /// <summary>
    /// Cleans up any abandoned staging directories older than the specified age.
    /// </summary>
    public void CleanupAbandoned(TimeSpan maxAge)
    {
        if (!Directory.Exists(_baseStagingPath))
            return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var dir in Directory.GetDirectories(_baseStagingPath))
        {
            var info = new DirectoryInfo(dir);
            if (info.CreationTimeUtc < cutoff)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _logger?.LogInformation("Cleaned up abandoned staging directory: {Path}", dir);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to clean up staging directory: {Path}", dir);
                }
            }
        }
    }
}

/// <summary>
/// Represents an active staging session. Files are extracted here, verified,
/// then committed to the final target directory.
/// </summary>
public class StagingSession : IDisposable
{
    private readonly ILogger? _logger;
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// Unique identifier for this changeset.
    /// </summary>
    public string ChangesetId { get; }

    /// <summary>
    /// Path to the staging directory.
    /// </summary>
    public string StagingDirectory { get; }

    /// <summary>
    /// Files staged so far (relative path → full staged path).
    /// </summary>
    public Dictionary<string, string> StagedFiles { get; } = new();

    internal StagingSession(string changesetId, string stagingDirectory, ILogger? logger)
    {
        ChangesetId = changesetId;
        StagingDirectory = stagingDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves and creates the staging path for a relative file entry.
    /// </summary>
    /// <param name="relativePath">Relative path within the staging area.</param>
    /// <returns>Full path within the staging directory.</returns>
    public string GetStagedPath(string relativePath)
    {
        var fullPath = PathSanitizer.SanitizeEntryPath(relativePath, StagingDirectory);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return fullPath;
    }

    /// <summary>
    /// Records that a file has been staged.
    /// </summary>
    public void RecordStagedFile(string relativePath, string stagedFullPath)
    {
        StagedFiles[relativePath] = stagedFullPath;
    }

    /// <summary>
    /// Commits all staged files to the target directory by moving them.
    /// Creates backups of existing files.
    /// </summary>
    /// <param name="targetDirectory">The final installation target directory.</param>
    /// <param name="createBackups">Whether to back up existing files before overwriting.</param>
    /// <returns>Commit result with lists of installed and backed-up files.</returns>
    public StagingCommitResult Commit(string targetDirectory, bool createBackups = true)
    {
        var result = new StagingCommitResult();

        try
        {
            foreach (var (relativePath, stagedPath) in StagedFiles)
            {
                var destPath = PathSanitizer.SanitizeEntryPath(relativePath, targetDirectory);
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                // Backup existing file
                if (File.Exists(destPath) && createBackups)
                {
                    var backupPath = destPath + ".backup";
                    File.Copy(destPath, backupPath, true);
                    result.BackedUpFiles.Add(backupPath);
                }

                // Move staged file to target
                File.Move(stagedPath, destPath, overwrite: true);
                result.InstalledFiles.Add(destPath);
            }

            _committed = true;
            result.Success = true;
            _logger?.LogInformation(
                "Committed staging session {ChangesetId}: {Count} files",
                ChangesetId, result.InstalledFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Failed to commit staging session {ChangesetId}", ChangesetId);
        }

        return result;
    }

    /// <summary>
    /// Rolls back the staging session by deleting the staging directory.
    /// </summary>
    public void Rollback()
    {
        if (_committed)
            return;

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
                _logger?.LogInformation("Rolled back staging session {ChangesetId}", ChangesetId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clean up staging directory during rollback");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_committed)
            Rollback();
    }
}

/// <summary>
/// Result of committing a staging session.
/// </summary>
public class StagingCommitResult
{
    public bool Success { get; set; }
    public List<string> InstalledFiles { get; set; } = new();
    public List<string> BackedUpFiles { get; set; } = new();
    public string? Error { get; set; }
}
