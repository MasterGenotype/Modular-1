using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Database;
using Modular.Core.Dependencies;
using Modular.Core.GameDetection;
using Modular.Core.Snapshots;
using Modular.Core.Telemetry;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Orchestrates the full mod installation lifecycle:
/// archive registration → installer selection → dependency resolution →
/// staging → conflict detection → atomic commit → rollback support.
/// </summary>
public class ModInstallationService
{
    private readonly InstallerManager _installerManager;
    private readonly StagingManager _stagingManager;
    private readonly ChangesetManager _changesetManager;
    private readonly ArchiveInventoryService _archiveInventory;
    private readonly FileConflictIndex _conflictIndex;
    private readonly ModularDatabase _database;
    private readonly TelemetryService? _telemetry;
    private readonly SnapshotManager? _snapshotManager;
    private readonly ILogger<ModInstallationService>? _logger;

    public ModInstallationService(
        ModularDatabase database,
        TelemetryService? telemetry = null,
        SnapshotManager? snapshotManager = null,
        ILogger<ModInstallationService>? logger = null)
    {
        _database = database;
        _telemetry = telemetry;
        _snapshotManager = snapshotManager;
        _logger = logger;

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular");
        var stagingPath = Path.Combine(configDir, "staging");

        _installerManager = new InstallerManager(
            archiveReaderFactory: new ArchiveReaderFactory(),
            logger: logger != null ? null : null,
            telemetry: telemetry);
        _stagingManager = new StagingManager(stagingPath);
        _changesetManager = new ChangesetManager(database);
        _archiveInventory = new ArchiveInventoryService(database);
        _conflictIndex = new FileConflictIndex();
    }

    /// <summary>
    /// Installs a mod archive to a target game directory with full lifecycle tracking.
    /// </summary>
    public async Task<ModInstallationResult> InstallAsync(
        string archivePath,
        string targetDirectory,
        ModInstallationOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ModInstallationOptions();
        var result = new ModInstallationResult { ArchivePath = archivePath, TargetDirectory = targetDirectory };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Validate inputs
        if (!File.Exists(archivePath))
        {
            result.Error = $"Archive not found: {archivePath}";
            return result;
        }

        if (!Directory.Exists(targetDirectory))
        {
            result.Error = $"Target directory not found: {targetDirectory}";
            return result;
        }

        // Step 1: Create changeset record
        var changesetId = await _changesetManager.CreateChangesetAsync(
            modId: options.ModId,
            archivePath: archivePath,
            targetDirectory: targetDirectory,
            ct: ct);
        result.ChangesetId = changesetId;
        _logger?.LogInformation("Created changeset {Id} for {Archive}", changesetId, Path.GetFileName(archivePath));

        try
        {
            // Step 2: Register archive in database
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Staging, ct: ct);
            progress?.Report(new InstallProgress { CurrentOperation = "Scanning archive..." });

            var inventory = await _archiveInventory.GetInventoryAsync(archivePath, ct);
            _logger?.LogInformation("Archive contains {Count} entries", inventory.Count);

            // Step 3: Select installer
            progress?.Report(new InstallProgress { CurrentOperation = "Detecting installer type..." });
            var selection = await _installerManager.SelectInstallerAsync(archivePath, ct);

            if (selection == null)
            {
                result.Error = "No suitable installer found for this archive";
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: ct);
                return result;
            }

            _logger?.LogInformation("Selected installer: {Name} (confidence: {Confidence:P0})",
                selection.Installer.DisplayName, selection.DetectionResult.Confidence);
            result.InstallerUsed = selection.Installer.DisplayName;

            // Step 4: Create install plan
            progress?.Report(new InstallProgress { CurrentOperation = "Analyzing mod structure..." });
            var context = new InstallContext
            {
                GameDirectory = targetDirectory,
                ModId = options.ModId,
                AllowOverwrite = options.AllowOverwrite,
                CreateBackups = options.CreateBackups,
                ConflictPolicy = options.AllowOverwrite ? ConflictPolicy.LastWriterWins : ConflictPolicy.FailOnConflict
            };

            var plan = await _installerManager.CreateInstallPlanAsync(archivePath, context, ct: ct);

            if (options.DryRun)
            {
                result.Success = true;
                result.DryRun = true;
                result.PlannedOperations = plan.Operations.Select(o => o.DestinationPath).ToList();
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.RolledBack, ct: ct);
                return result;
            }

            // Step 5: Check for file conflicts
            progress?.Report(new InstallProgress { CurrentOperation = "Checking for conflicts..." });
            foreach (var op in plan.Operations.Where(o => o.Type == FileOperationType.Extract || o.Type == FileOperationType.Copy))
            {
                var destPath = Path.Combine(targetDirectory, op.DestinationPath);
                if (File.Exists(destPath) && !options.AllowOverwrite)
                {
                    _conflictIndex.RegisterFile(op.DestinationPath, options.ModId ?? "current", op.SourcePath, null);
                }
            }

            // Step 6: Sort operations by dependencies (DAG)
            var sortedOps = OperationGraph.Sort(plan.Operations);
            if (sortedOps == null)
            {
                result.Error = "Circular dependency detected in file operations";
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: ct);
                return result;
            }
            plan.Operations = sortedOps;

            // Step 7: Stage and install
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Ready, ct: ct);

            progress?.Report(new InstallProgress
            {
                CurrentOperation = "Installing files...",
                TotalFiles = plan.Operations.Count
            });

            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Committing, ct: ct);

            var installResult = await _installerManager.ExecuteInstallAsync(plan, progress, ct);

            if (!installResult.Success)
            {
                result.Error = installResult.Error;
                await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: ct);
                return result;
            }

            // Step 8: Record operations for rollback
            var operationsData = JsonSerializer.Serialize(new
            {
                installedFiles = installResult.InstalledFiles,
                backedUpFiles = installResult.BackedUpFiles,
                manifest = installResult.Manifest
            });
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Committed, operationsData, ct);

            // Step 9: Record telemetry
            stopwatch.Stop();
            _telemetry?.RecordInstallerResult(
                selection.Installer.InstallerId,
                true,
                stopwatch.Elapsed);

            result.Success = true;
            result.InstalledFiles = installResult.InstalledFiles;
            result.BackedUpFiles = installResult.BackedUpFiles;

            _logger?.LogInformation(
                "Installation complete: {Count} files installed in {Duration:F1}s",
                installResult.InstalledFiles.Count, stopwatch.Elapsed.TotalSeconds);

            // Auto-snapshot after successful install
            if (options.AutoSnapshot && _snapshotManager != null)
            {
                try
                {
                    var gameInfo = await TryDetectGameForPathAsync(targetDirectory, ct);
                    if (gameInfo != null)
                    {
                        await _snapshotManager.CreateSnapshotAsync(
                            gameInfo.Value.appId, gameInfo.Value.name, targetDirectory,
                            SnapshotTrigger.AutoInstall,
                            name: $"Auto — {options.ModId ?? Path.GetFileName(archivePath)} installed",
                            ct: ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Auto-snapshot failed after install (non-fatal)");
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Installation cancelled";
            result.Cancelled = true;
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: CancellationToken.None);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.Failed, ct: CancellationToken.None);
            _logger?.LogError(ex, "Installation failed for {Archive}", archivePath);
            _telemetry?.RecordInstallerResult("unknown", false, stopwatch.Elapsed);
            return result;
        }
    }

    /// <summary>
    /// Uninstalls a previously installed changeset by restoring backups and removing installed files.
    /// </summary>
    public async Task<ModUninstallResult> UninstallAsync(
        string changesetId,
        CancellationToken ct = default)
    {
        var result = new ModUninstallResult { ChangesetId = changesetId };

        var changeset = await _changesetManager.GetChangesetAsync(changesetId, ct);
        if (changeset == null)
        {
            result.Error = $"Changeset '{changesetId}' not found";
            return result;
        }

        if (changeset.State != ChangesetState.Committed)
        {
            result.Error = $"Changeset '{changesetId}' is in state '{changeset.State}', expected 'Committed'";
            return result;
        }

        if (string.IsNullOrEmpty(changeset.OperationsJson) || string.IsNullOrEmpty(changeset.TargetDirectory))
        {
            result.Error = "Changeset missing operations data or target directory";
            return result;
        }

        try
        {
            var ops = JsonSerializer.Deserialize<JsonElement>(changeset.OperationsJson);
            var installedFiles = new List<string>();
            var backedUpFiles = new List<string>();

            if (ops.TryGetProperty("installedFiles", out var installed))
            {
                foreach (var f in installed.EnumerateArray())
                    installedFiles.Add(f.GetString()!);
            }
            if (ops.TryGetProperty("backedUpFiles", out var backed))
            {
                foreach (var f in backed.EnumerateArray())
                    backedUpFiles.Add(f.GetString()!);
            }

            var removedCount = 0;
            var restoredCount = 0;

            // Remove installed files
            foreach (var file in installedFiles)
            {
                var fullPath = Path.Combine(changeset.TargetDirectory, file);
                var backupPath = fullPath + ".backup";

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    removedCount++;
                }

                // Restore backup if one exists
                if (File.Exists(backupPath))
                {
                    File.Move(backupPath, fullPath);
                    restoredCount++;
                }
            }

            // Clean up empty directories (bottom-up)
            var dirs = installedFiles
                .Select(f => Path.GetDirectoryName(Path.Combine(changeset.TargetDirectory, f)))
                .Where(d => d != null)
                .Distinct()
                .OrderByDescending(d => d!.Length)
                .ToList();

            foreach (var dir in dirs)
            {
                if (Directory.Exists(dir!) &&
                    !Directory.EnumerateFileSystemEntries(dir!).Any() &&
                    dir != changeset.TargetDirectory)
                {
                    Directory.Delete(dir!);
                }
            }

            await _changesetManager.UpdateStateAsync(changesetId, ChangesetState.RolledBack, ct: ct);

            result.Success = true;
            result.FilesRemoved = removedCount;
            result.FilesRestored = restoredCount;

            _logger?.LogInformation(
                "Uninstall complete: {Removed} files removed, {Restored} backups restored",
                removedCount, restoredCount);

            // Auto-snapshot after successful uninstall
            if (_snapshotManager != null && changeset.TargetDirectory != null)
            {
                try
                {
                    var gameInfo = await TryDetectGameForPathAsync(changeset.TargetDirectory, ct);
                    if (gameInfo != null)
                    {
                        await _snapshotManager.CreateSnapshotAsync(
                            gameInfo.Value.appId, gameInfo.Value.name, changeset.TargetDirectory,
                            SnapshotTrigger.AutoUninstall,
                            name: $"Auto — {changeset.ModId ?? changesetId} uninstalled",
                            ct: ct);
                    }
                }
                catch (Exception ex2)
                {
                    _logger?.LogWarning(ex2, "Auto-snapshot failed after uninstall (non-fatal)");
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger?.LogError(ex, "Uninstall failed for changeset {Id}", changesetId);
        }

        return result;
    }

    /// <summary>
    /// Lists all committed (installed) changesets, optionally filtered by target directory.
    /// </summary>
    public async Task<List<ChangesetRecord>> ListInstalledAsync(
        string? targetDirectory = null,
        CancellationToken ct = default)
    {
        var committed = await _changesetManager.ListByStateAsync(ChangesetState.Committed, ct);

        if (targetDirectory != null)
        {
            var fullTarget = Path.GetFullPath(targetDirectory);
            committed = committed.Where(c =>
                c.TargetDirectory != null &&
                Path.GetFullPath(c.TargetDirectory).Equals(fullTarget, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return committed;
    }

    /// <summary>
    /// Resolves a Steam AppID or game name to an install directory.
    /// </summary>
    public static async Task<string?> ResolveGameDirectoryAsync(
        string gameIdentifier,
        CancellationToken ct = default)
    {
        var scanner = new SteamGameScanner(new SteamLibraryScanner(new SteamLocator()));
        var games = await scanner.ScanAllAsync(ct);

        // Try exact AppID match
        if (int.TryParse(gameIdentifier, out var appId))
        {
            var game = games.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
                return game.InstallPath;
        }

        // Try case-insensitive name match
        var byName = games.FirstOrDefault(g =>
            g.DisplayName != null &&
            g.DisplayName.Contains(gameIdentifier, StringComparison.OrdinalIgnoreCase));

        return byName?.InstallPath;
    }

    /// <summary>
    /// Tries to detect the game for a target directory by querying the detected_games table.
    /// </summary>
    private async Task<(int appId, string name)?> TryDetectGameForPathAsync(
        string targetDirectory, CancellationToken ct)
    {
        var connection = await _database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT steam_appid, display_name FROM detected_games
            WHERE install_path = @path
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@path", targetDirectory);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return (reader.GetInt32(0), reader.IsDBNull(1) ? "Unknown Game" : reader.GetString(1));
        }

        return null;
    }
}

/// <summary>
/// Options for mod installation.
/// </summary>
public class ModInstallationOptions
{
    /// <summary>Optional mod identifier for tracking.</summary>
    public string? ModId { get; set; }

    /// <summary>Whether to overwrite existing files.</summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>Whether to create backups of overwritten files.</summary>
    public bool CreateBackups { get; set; } = true;

    /// <summary>Show what would be installed without actually installing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Whether to automatically create a snapshot after installation.</summary>
    public bool AutoSnapshot { get; set; } = true;
}

/// <summary>
/// Result of a mod installation operation.
/// </summary>
public class ModInstallationResult
{
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public bool Cancelled { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string? ChangesetId { get; set; }
    public string? InstallerUsed { get; set; }
    public List<string> InstalledFiles { get; set; } = new();
    public List<string> BackedUpFiles { get; set; } = new();
    public List<string> PlannedOperations { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Result of a mod uninstallation operation.
/// </summary>
public class ModUninstallResult
{
    public bool Success { get; set; }
    public string ChangesetId { get; set; } = string.Empty;
    public int FilesRemoved { get; set; }
    public int FilesRestored { get; set; }
    public string? Error { get; set; }
}
