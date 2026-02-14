using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Installer for BepInEx Unity modding framework mods.
/// Handles both BepInEx core installation and plugin installations.
/// </summary>
public class BepInExInstaller : IModInstaller
{
    private readonly ILogger<BepInExInstaller>? _logger;

    public string InstallerId => "bepinex";
    public string DisplayName => "BepInEx Installer";
    public int Priority => 80; // High priority

    public BepInExInstaller(ILogger<BepInExInstaller>? logger = null)
    {
        _logger = logger;
    }

    public async Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            
            // Check for BepInEx core files
            var hasBepInExCore = archive.Entries.Any(e => 
                e.FullName.Contains("BepInEx/core/", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith("BepInEx.dll", StringComparison.OrdinalIgnoreCase));

            // Check for BepInEx plugins
            var hasPlugins = archive.Entries.Any(e => 
                e.FullName.Contains("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Contains("plugins/", StringComparison.OrdinalIgnoreCase));

            // Check for doorstop/preloader files
            var hasDoorstop = archive.Entries.Any(e => 
                e.FullName.Contains("doorstop", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith("winhttp.dll", StringComparison.OrdinalIgnoreCase));

            if (hasBepInExCore || (hasPlugins && hasDoorstop))
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = true,
                    Confidence = 0.95,
                    InstallerType = "bepinex",
                    Reason = hasBepInExCore 
                        ? "Contains BepInEx core framework files"
                        : "Contains BepInEx plugin structure"
                });
            }

            // Check for standalone BepInEx plugin (single DLL in plugins folder structure)
            if (hasPlugins)
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = true,
                    Confidence = 0.8,
                    InstallerType = "bepinex-plugin",
                    Reason = "Likely BepInEx plugin"
                });
            }

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = "No BepInEx structure detected"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to detect BepInEx for {Path}", archivePath);
            return new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error: {ex.Message}"
            };
        }
    }

    public async Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            Operations = new List<FileOperation>()
        };

        using var archive = ZipFile.OpenRead(archivePath);
        
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        
        // Detect if this is BepInEx core or just a plugin
        var isCoreInstall = entries.Any(e => 
            e.FullName.Contains("BepInEx/core/", StringComparison.OrdinalIgnoreCase));

        long totalBytes = 0;

        foreach (var entry in entries)
        {
            var destPath = entry.FullName;

            // Normalize path for BepInEx structure
            if (isCoreInstall)
            {
                // Extract maintaining BepInEx directory structure
                // Files should go to game root (e.g., BepInEx/, doorstop files)
                destPath = entry.FullName;
            }
            else
            {
                // Plugin install - ensure files go to BepInEx/plugins/
                if (!entry.FullName.Contains("BepInEx/", StringComparison.OrdinalIgnoreCase))
                {
                    destPath = Path.Combine("BepInEx", "plugins", entry.FullName);
                }
            }

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = destPath,
                SizeBytes = entry.Length,
                IsCritical = entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;
        plan.Options = new Dictionary<string, object>
        {
            ["is_core_install"] = isCoreInstall
        };

        return await Task.FromResult(plan);
    }

    public async Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new InstallResult { Success = false };
        var installedFiles = new List<string>();
        var backedUpFiles = new List<string>();

        try
        {
            using var archive = ZipFile.OpenRead(plan.SourcePath);
            
            int filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var operation in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();

                var entry = archive.GetEntry(operation.SourcePath);
                if (entry == null)
                    continue;

                var destPath = Path.Combine(
                    Path.GetDirectoryName(plan.SourcePath) ?? string.Empty,
                    operation.DestinationPath);

                // Create directory
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                // Backup existing critical files
                if (File.Exists(destPath) && operation.IsCritical)
                {
                    var backupPath = destPath + ".backup";
                    File.Copy(destPath, backupPath, true);
                    backedUpFiles.Add(backupPath);
                }

                // Extract file
                entry.ExtractToFile(destPath, true);
                installedFiles.Add(destPath);

                filesProcessed++;
                bytesProcessed += operation.SizeBytes;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = "Installing BepInEx mod",
                    CurrentFile = operation.DestinationPath,
                    FilesProcessed = filesProcessed,
                    TotalFiles = plan.Operations.Count,
                    BytesProcessed = bytesProcessed,
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

            var isCoreInstall = plan.Options?.ContainsKey("is_core_install") == true && 
                               (bool)plan.Options["is_core_install"];

            _logger?.LogInformation(
                "Successfully installed BepInEx {Type}: {Count} files",
                isCoreInstall ? "core" : "plugin",
                installedFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "BepInEx installation failed");
        }

        return await Task.FromResult(result);
    }
}
