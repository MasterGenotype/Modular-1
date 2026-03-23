using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Modular.Core.Dependencies;
using Modular.Core.Utilities;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Steam;

/// <summary>
/// Dependency-aware Steam mod installer for Linux. Implements the full installation pipeline:
/// dependency resolution → checksum verification → staging → conflict detection → backup → commit/rollback.
/// </summary>
public class SteamModInstaller : IModInstaller
{
    private readonly ILogger<SteamModInstaller>? _logger;
    private readonly SteamConstraintSolver _solver;

    public string InstallerId => "steam-mod";
    public string DisplayName => "Steam Mod Installer";
    public int Priority => 5; // Higher than LooseFile (1), lower than specialized installers

    public SteamModInstaller(ILogger<SteamModInstaller>? logger = null)
    {
        _logger = logger;
        _solver = new SteamConstraintSolver(logger != null
            ? new LoggerFactory().CreateLogger<SteamConstraintSolver>()
            : null);
    }

    public SteamModInstaller(
        SteamConstraintSolver solver,
        ILogger<SteamModInstaller>? logger = null)
    {
        _solver = solver;
        _logger = logger;
    }

    /// <summary>
    /// Detects if the archive looks like a Steam mod (contains typical Steam Workshop structure).
    /// </summary>
    public Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            var extension = Path.GetExtension(archivePath).ToLowerInvariant();
            var isSupportedFormat = extension is ".zip" or ".tar" or ".gz" or ".tgz";

            if (!isSupportedFormat)
            {
                return Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = $"Unsupported archive format: {extension}"
                });
            }

            // Check for Steam mod indicators
            var confidence = 0.5;
            var reason = "Supported archive format for Steam mod installation";

            if (extension == ".zip")
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var entries = archive.Entries.Select(e => e.FullName.ToLowerInvariant()).ToList();

                // Higher confidence if it has typical Steam workshop structure
                if (entries.Any(e => e.Contains("workshop") || e.Contains("steam")))
                    confidence = 0.85;
                else if (entries.Any(e => e.EndsWith(".dll") || e.EndsWith(".so")))
                    confidence = 0.65;
            }

            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = confidence,
                InstallerType = InstallerId,
                Reason = reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Detection failed for {Path}", archivePath);
            return Task.FromResult(new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error reading archive: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Analyzes an archive and creates an installation plan with file operations.
    /// </summary>
    public Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = context.GameDirectory,
            Operations = new List<FileOperation>()
        };

        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        long totalBytes = 0;

        if (extension == ".zip")
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

            foreach (var entry in entries)
            {
                plan.Operations.Add(new FileOperation
                {
                    Type = FileOperationType.Extract,
                    SourcePath = entry.FullName,
                    DestinationPath = entry.FullName,
                    SizeBytes = entry.Length
                });
                totalBytes += entry.Length;
            }
        }
        else if (extension is ".tar" or ".gz" or ".tgz")
        {
            // For tar/tar.gz, we list entries by opening the archive
            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = archivePath,
                DestinationPath = ".",
                SizeBytes = new FileInfo(archivePath).Length
            });
            totalBytes = new FileInfo(archivePath).Length;
        }

        plan.TotalBytes = totalBytes;
        return Task.FromResult(plan);
    }

    /// <summary>
    /// Executes the installation plan using two-phase staging with backup and rollback.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var targetDir = !string.IsNullOrEmpty(plan.TargetDirectory)
            ? plan.TargetDirectory
            : Path.GetDirectoryName(plan.SourcePath) ?? ".";
        var stagingDir = Path.Combine(Path.GetTempPath(), "modular-steam-staging", Guid.NewGuid().ToString("N")[..12]);

        try
        {
            Directory.CreateDirectory(stagingDir);
            _logger?.LogInformation("Staging to {StagingDir}", stagingDir);

            // Phase 1: Extract to staging
            progress?.Report(new InstallProgress
            {
                CurrentOperation = "Extracting to staging area",
                TotalFiles = plan.Operations.Count
            });

            await ExtractToStagingAsync(plan.SourcePath, stagingDir, ct);

            // Phase 2: Commit from staging to target
            var result = CommitStagedFiles(stagingDir, targetDir, progress, plan);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Installation cancelled, cleaning up staging directory");
            CleanupDirectory(stagingDir);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Installation failed, cleaning up staging directory");
            CleanupDirectory(stagingDir);
            return new InstallResult
            {
                Success = false,
                Error = $"Installation failed: {ex.Message}"
            };
        }
        finally
        {
            CleanupDirectory(stagingDir);
        }
    }

    /// <summary>
    /// Runs the full Steam mod installation pipeline for a batch of mods:
    /// resolve dependencies → verify checksums → stage → detect conflicts → backup → commit.
    /// </summary>
    /// <param name="mods">Mods to install.</param>
    /// <param name="gameDirectory">Steam game installation directory.</param>
    /// <param name="activeConditions">Active conditions for conditional dependencies.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Batch installation result.</returns>
    public async Task<SteamBatchInstallResult> InstallModsAsync(
        IReadOnlyList<SteamModMetadata> mods,
        string gameDirectory,
        IReadOnlySet<string>? activeConditions = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var batchResult = new SteamBatchInstallResult();

        _logger?.LogInformation("Starting Steam mod installation: {Count} mods targeting {GameDir}",
            mods.Count, gameDirectory);

        // Step 1: Resolve dependencies and compute install order
        progress?.Report(new InstallProgress { CurrentOperation = "Resolving dependencies" });
        var resolution = _solver.Resolve(mods, activeConditions);

        if (!resolution.Success)
        {
            _logger?.LogError("Dependency resolution failed: {Errors}",
                string.Join("; ", resolution.Errors));
            batchResult.Errors.AddRange(resolution.Errors);
            return batchResult;
        }

        batchResult.Warnings.AddRange(resolution.Warnings);
        batchResult.InstallOrder = resolution.InstallOrder.Select(m => m.Name).ToList();
        _logger?.LogInformation("Install order: {Order}",
            string.Join(" -> ", batchResult.InstallOrder));

        // Step 2: Verify archives exist and checksums match
        progress?.Report(new InstallProgress { CurrentOperation = "Verifying archives" });
        foreach (var mod in resolution.InstallOrder)
        {
            if (!File.Exists(mod.ArchivePath))
            {
                batchResult.Errors.Add($"Archive not found for '{mod.Name}': {mod.ArchivePath}");
                return batchResult;
            }

            if (!VerifyChecksum(mod))
            {
                batchResult.Errors.Add($"Checksum verification failed for '{mod.Name}'");
                return batchResult;
            }
        }

        // Step 3: Stage all mods into temporary directories
        var stagingBase = Path.Combine(Path.GetTempPath(), "modular-steam-staging", Guid.NewGuid().ToString("N")[..12]);
        var modStagingDirs = new Dictionary<string, string>();

        try
        {
            Directory.CreateDirectory(stagingBase);

            foreach (var mod in resolution.InstallOrder)
            {
                ct.ThrowIfCancellationRequested();

                var modStagingDir = Path.Combine(stagingBase, mod.Name);
                Directory.CreateDirectory(modStagingDir);
                modStagingDirs[mod.Name] = modStagingDir;

                _logger?.LogInformation("Staging '{Mod}' from {Archive}", mod.Name, mod.ArchivePath);
                progress?.Report(new InstallProgress
                {
                    CurrentOperation = $"Staging {mod.Name}",
                    CurrentFile = mod.ArchivePath
                });

                await ExtractToStagingAsync(mod.ArchivePath, modStagingDir, ct);
            }

            // Step 4: Detect file conflicts across all staged mods
            progress?.Report(new InstallProgress { CurrentOperation = "Detecting conflicts" });
            var conflictIndex = new FileConflictIndex();

            foreach (var (modName, stagingDir) in modStagingDirs)
            {
                var stagedFiles = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
                foreach (var file in stagedFiles)
                {
                    var relativePath = Path.GetRelativePath(stagingDir, file);
                    conflictIndex.RegisterFile(relativePath, modName, Path.GetFileName(file));
                }
            }

            var conflicts = conflictIndex.DetectConflicts();
            if (conflicts.Count > 0)
            {
                _logger?.LogError("File conflicts detected: {Count}", conflicts.Count);
                foreach (var conflict in conflicts)
                {
                    var msg = $"File conflict on '{conflict.GamePath}': " +
                              $"provided by {string.Join(", ", conflict.ConflictingMods)}";
                    batchResult.Errors.Add(msg);
                    _logger?.LogError("{Conflict}", msg);
                }

                batchResult.FileConflicts = conflicts.Select(c => new SteamFileConflict
                {
                    FilePath = c.GamePath,
                    ConflictingMods = c.ConflictingMods
                }).ToList();

                return batchResult;
            }

            // Step 5: Backup existing files and commit
            progress?.Report(new InstallProgress { CurrentOperation = "Backing up existing files" });
            var backupDir = Path.Combine(gameDirectory, ".modular-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(gameDirectory);

            var installedFiles = new List<string>();
            var backedUpFiles = new List<string>();

            foreach (var mod in resolution.InstallOrder)
            {
                ct.ThrowIfCancellationRequested();

                var modStagingDir = modStagingDirs[mod.Name];
                var stagedFiles = Directory.GetFiles(modStagingDir, "*", SearchOption.AllDirectories);
                var modInstalled = 0;

                foreach (var stagedFile in stagedFiles)
                {
                    var relativePath = Path.GetRelativePath(modStagingDir, stagedFile);
                    var destPath = PathSanitizer.SanitizeEntryPath(relativePath, gameDirectory);
                    var destDir = Path.GetDirectoryName(destPath);

                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    // Backup existing file
                    if (File.Exists(destPath))
                    {
                        Directory.CreateDirectory(backupDir);
                        var backupPath = Path.Combine(backupDir, relativePath);
                        var backupFileDir = Path.GetDirectoryName(backupPath);
                        if (!string.IsNullOrEmpty(backupFileDir) && !Directory.Exists(backupFileDir))
                            Directory.CreateDirectory(backupFileDir);

                        File.Copy(destPath, backupPath, overwrite: true);
                        backedUpFiles.Add(backupPath);
                        _logger?.LogDebug("Backed up {Path} -> {Backup}", destPath, backupPath);
                    }

                    // Copy from staging to game directory
                    File.Copy(stagedFile, destPath, overwrite: true);
                    installedFiles.Add(destPath);
                    modInstalled++;
                }

                _logger?.LogInformation("Installed '{Mod}': {Count} files", mod.Name, modInstalled);
                progress?.Report(new InstallProgress
                {
                    CurrentOperation = $"Installed {mod.Name}",
                    FilesProcessed = installedFiles.Count,
                    TotalFiles = resolution.InstallOrder.Sum(_ => 1)
                });

                batchResult.ModResults[mod.Name] = new InstallResult
                {
                    Success = true,
                    InstalledFiles = installedFiles.ToList()
                };
            }

            batchResult.Success = true;
            batchResult.InstalledFiles = installedFiles;
            batchResult.BackedUpFiles = backedUpFiles;
            batchResult.BackupDirectory = backedUpFiles.Count > 0 ? backupDir : null;

            _logger?.LogInformation(
                "Steam mod installation complete: {Installed} files installed, {Backed} files backed up",
                installedFiles.Count, backedUpFiles.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Batch installation failed");
            batchResult.Errors.Add($"Installation failed: {ex.Message}");
        }
        finally
        {
            CleanupDirectory(stagingBase);
        }

        return batchResult;
    }

    /// <summary>
    /// Rolls back an installation by restoring backed-up files.
    /// </summary>
    /// <param name="backupDirectory">Path to the backup directory.</param>
    /// <param name="gameDirectory">Target game directory.</param>
    /// <param name="installedFiles">List of files that were installed.</param>
    public void Rollback(string backupDirectory, string gameDirectory, IReadOnlyList<string> installedFiles)
    {
        _logger?.LogInformation("Rolling back installation from backup: {BackupDir}", backupDirectory);

        // Remove installed files
        foreach (var file in installedFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    _logger?.LogDebug("Removed installed file: {Path}", file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to remove installed file: {Path}", file);
            }
        }

        // Restore backed-up files
        if (Directory.Exists(backupDirectory))
        {
            var backupFiles = Directory.GetFiles(backupDirectory, "*", SearchOption.AllDirectories);
            foreach (var backupFile in backupFiles)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(backupDirectory, backupFile);
                    var destPath = PathSanitizer.SanitizeEntryPath(relativePath, gameDirectory);
                    var destDir = Path.GetDirectoryName(destPath);

                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    File.Copy(backupFile, destPath, overwrite: true);
                    _logger?.LogDebug("Restored {Path} from backup", destPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to restore backup file: {Path}", backupFile);
                }
            }

            _logger?.LogInformation("Rollback complete: {Count} files restored", backupFiles.Length);
        }
    }

    /// <summary>
    /// Verifies a mod archive's SHA256 checksum against the expected value.
    /// Returns true if no checksum is specified (verification skipped).
    /// </summary>
    internal static bool VerifyChecksum(SteamModMetadata mod)
    {
        if (string.IsNullOrEmpty(mod.Checksum))
            return true; // No checksum specified, skip verification

        using var stream = File.OpenRead(mod.ArchivePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(hash, mod.Checksum, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts an archive to the staging directory, supporting .zip, .tar, and .tar.gz formats.
    /// </summary>
    internal static async Task ExtractToStagingAsync(
        string archivePath,
        string stagingDir,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        var secondExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(archivePath)).ToLowerInvariant();

        if (extension == ".zip")
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, stagingDir, overwriteFiles: true), ct);
        }
        else if (extension == ".tar")
        {
            await using var stream = File.OpenRead(archivePath);
            await TarFile.ExtractToDirectoryAsync(stream, stagingDir, overwriteFiles: true, cancellationToken: ct);
        }
        else if ((extension == ".gz" && secondExtension == ".tar") || extension == ".tgz")
        {
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzipStream, stagingDir, overwriteFiles: true, cancellationToken: ct);
        }
        else
        {
            throw new NotSupportedException($"Unsupported archive format: {extension}");
        }
    }

    /// <summary>
    /// Commits staged files to the target directory with backup of existing files.
    /// </summary>
    private InstallResult CommitStagedFiles(
        string stagingDir,
        string targetDir,
        IProgress<InstallProgress>? progress,
        InstallPlan plan)
    {
        var result = new InstallResult { Success = false };
        var installedFiles = new List<string>();
        var backedUpFiles = new List<string>();

        try
        {
            var stagedFiles = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
            int processed = 0;

            foreach (var stagedFile in stagedFiles)
            {
                var relativePath = Path.GetRelativePath(stagingDir, stagedFile);
                var destPath = PathSanitizer.SanitizeEntryPath(relativePath, targetDir);
                var destDir = Path.GetDirectoryName(destPath);

                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                // Backup existing file
                if (File.Exists(destPath))
                {
                    var backupPath = destPath + ".backup";
                    File.Copy(destPath, backupPath, overwrite: true);
                    backedUpFiles.Add(backupPath);
                }

                File.Copy(stagedFile, destPath, overwrite: true);
                installedFiles.Add(destPath);
                processed++;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = "Installing files",
                    CurrentFile = relativePath,
                    FilesProcessed = processed,
                    TotalFiles = stagedFiles.Length,
                    BytesProcessed = processed,
                    TotalBytes = plan.TotalBytes
                });
            }

            result.Success = true;
            result.InstalledFiles = installedFiles;
            result.BackedUpFiles = backedUpFiles;
            result.Manifest = new InstallManifest
            {
                InstallerId = InstallerId,
                Files = installedFiles,
                Backups = backedUpFiles.ToDictionary(f => f, f => f)
            };

            _logger?.LogInformation("Committed {Count} files", installedFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Commit failed");
        }

        return result;
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

/// <summary>
/// Result of a batch Steam mod installation.
/// </summary>
public class SteamBatchInstallResult
{
    /// <summary>
    /// Whether the entire batch installation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Ordered list of mod names as they were installed.
    /// </summary>
    public List<string> InstallOrder { get; set; } = new();

    /// <summary>
    /// Files that were installed to the game directory.
    /// </summary>
    public List<string> InstalledFiles { get; set; } = new();

    /// <summary>
    /// Files that were backed up before overwriting.
    /// </summary>
    public List<string> BackedUpFiles { get; set; } = new();

    /// <summary>
    /// Path to the backup directory (null if no backups were needed).
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>
    /// Per-mod installation results.
    /// </summary>
    public Dictionary<string, InstallResult> ModResults { get; set; } = new();

    /// <summary>
    /// File conflicts that prevented installation.
    /// </summary>
    public List<SteamFileConflict> FileConflicts { get; set; } = new();

    /// <summary>
    /// Error messages if installation failed.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Non-fatal warnings.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Represents a file conflict between Steam mods.
/// </summary>
public class SteamFileConflict
{
    /// <summary>
    /// Relative file path where the conflict occurs.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Mod names that provide conflicting versions of this file.
    /// </summary>
    public List<string> ConflictingMods { get; set; } = new();
}
