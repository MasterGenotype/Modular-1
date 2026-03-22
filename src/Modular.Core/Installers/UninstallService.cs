using Microsoft.Extensions.Logging;

namespace Modular.Core.Installers;

/// <summary>
/// Handles uninstallation of previously installed mods, including file removal and backup restoration.
/// </summary>
public class UninstallService
{
    private readonly InstallationTracker _tracker;
    private readonly ILogger<UninstallService>? _logger;

    public UninstallService(InstallationTracker tracker, ILogger<UninstallService>? logger = null)
    {
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>
    /// Uninstalls a mod by removing its files and restoring backups.
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(
        string modId,
        bool restoreBackups = true,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new UninstallResult { ModId = modId };

        var record = await _tracker.GetInstalledModAsync(modId, ct);
        if (record == null)
        {
            result.Success = false;
            result.Error = $"Mod '{modId}' is not installed";
            return result;
        }

        var totalFiles = record.InstalledFiles.Count;
        var processed = 0;

        try
        {
            // Phase 1: Remove installed files.
            foreach (var filePath in record.InstalledFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    result.RemovedFiles.Add(filePath);
                    _logger?.LogDebug("Removed: {Path}", filePath);
                }
                else
                {
                    result.AlreadyMissingFiles.Add(filePath);
                }

                processed++;
                progress?.Report(new UninstallProgress
                {
                    Phase = "Removing files",
                    Current = processed,
                    Total = totalFiles,
                    CurrentFile = filePath
                });
            }

            // Phase 2: Restore backups.
            if (restoreBackups && record.BackupFiles.Count > 0)
            {
                foreach (var (originalPath, backupPath) in record.BackupFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    if (File.Exists(backupPath))
                    {
                        var dir = Path.GetDirectoryName(originalPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.Move(backupPath, originalPath, overwrite: true);
                        result.RestoredFiles.Add(originalPath);
                        _logger?.LogDebug("Restored backup: {Path}", originalPath);
                    }
                }
            }

            // Phase 3: Clean up empty directories.
            CleanEmptyDirectories(record.TargetDirectory, record.InstalledFiles);

            // Phase 4: Remove installation record.
            await _tracker.RemoveInstallationAsync(modId, ct);

            result.Success = true;
            _logger?.LogInformation(
                "Uninstalled {ModId}: {Removed} files removed, {Restored} backups restored",
                modId, result.RemovedFiles.Count, result.RestoredFiles.Count);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Uninstall cancelled";
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Failed to uninstall {ModId}", modId);
        }

        return result;
    }

    /// <summary>
    /// Performs a dry-run uninstall showing what would be removed.
    /// </summary>
    public async Task<UninstallResult> DryRunAsync(string modId, CancellationToken ct = default)
    {
        var result = new UninstallResult { ModId = modId, IsDryRun = true };

        var record = await _tracker.GetInstalledModAsync(modId, ct);
        if (record == null)
        {
            result.Success = false;
            result.Error = $"Mod '{modId}' is not installed";
            return result;
        }

        foreach (var filePath in record.InstalledFiles)
        {
            if (File.Exists(filePath))
                result.RemovedFiles.Add(filePath);
            else
                result.AlreadyMissingFiles.Add(filePath);
        }

        foreach (var (originalPath, backupPath) in record.BackupFiles)
        {
            if (File.Exists(backupPath))
                result.RestoredFiles.Add(originalPath);
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// Rolls back all mods to restore the game directory to its original state.
    /// </summary>
    public async Task<List<UninstallResult>> RollbackAllAsync(
        string? gameDomain = null,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var mods = await _tracker.GetInstalledModsAsync(gameDomain, ct);
        var results = new List<UninstallResult>();

        for (int i = 0; i < mods.Count; i++)
        {
            progress?.Report(new UninstallProgress
            {
                Phase = $"Uninstalling {mods[i].ModId}",
                Current = i + 1,
                Total = mods.Count,
                CurrentFile = mods[i].ModId
            });

            var result = await UninstallAsync(mods[i].ModId, restoreBackups: true, ct: ct);
            results.Add(result);
        }

        return results;
    }

    private void CleanEmptyDirectories(string targetDirectory, List<string> installedFiles)
    {
        var directories = installedFiles
            .Select(f => Path.GetDirectoryName(f))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .OrderByDescending(d => d!.Length)
            .ToList();

        foreach (var dir in directories)
        {
            if (dir == null) continue;

            try
            {
                if (Directory.Exists(dir) &&
                    !Directory.EnumerateFileSystemEntries(dir).Any() &&
                    dir != targetDirectory)
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }
}

/// <summary>
/// Result of an uninstall operation.
/// </summary>
public class UninstallResult
{
    public string ModId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool IsDryRun { get; set; }
    public string? Error { get; set; }
    public List<string> RemovedFiles { get; set; } = new();
    public List<string> RestoredFiles { get; set; } = new();
    public List<string> AlreadyMissingFiles { get; set; } = new();
}

/// <summary>
/// Progress information for an uninstall operation.
/// </summary>
public class UninstallProgress
{
    public string Phase { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string? CurrentFile { get; set; }
}
