using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Basic installer for loose file mods.
/// Extracts all files directly to game directory.
/// </summary>
public class LooseFileInstaller : IModInstaller
{
    private readonly ILogger<LooseFileInstaller>? _logger;

    public string InstallerId => "loose-file";
    public string DisplayName => "Loose File Installer";
    public int Priority => 1; // Lowest priority (fallback)

    public LooseFileInstaller(ILogger<LooseFileInstaller>? logger = null)
    {
        _logger = logger;
    }

    public async Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            // Always can handle any archive as fallback
            using var archive = ZipFile.OpenRead(archivePath);
            
            // Check if it's truly a simple loose file structure
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var hasNestedStructure = entries.Any(e => e.FullName.Count(c => c == '/') > 2);

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = hasNestedStructure ? 0.3 : 0.7,
                InstallerType = "loose-file",
                Reason = hasNestedStructure 
                    ? "Can extract as loose files but may need directory adjustments"
                    : "Simple file structure suitable for direct extraction"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to detect archive type for {Path}", archivePath);
            return new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error reading archive: {ex.Message}"
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
        
        // Find common root directory
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var commonRoot = FindCommonRoot(entries.Select(e => e.FullName).ToList());

        long totalBytes = 0;
        foreach (var entry in entries)
        {
            // Strip common root if present
            var relativePath = entry.FullName;
            if (!string.IsNullOrEmpty(commonRoot))
                relativePath = relativePath.Substring(commonRoot.Length).TrimStart('/');

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = relativePath,
                SizeBytes = entry.Length,
                IsCritical = false
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;
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

                // Backup existing file
                if (File.Exists(destPath))
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
                    CurrentOperation = "Extracting files",
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

            _logger?.LogInformation("Successfully installed {Count} files", installedFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Installation failed");
        }

        return await Task.FromResult(result);
    }

    private string FindCommonRoot(List<string> paths)
    {
        if (paths.Count == 0)
            return string.Empty;

        var firstPath = paths[0].Split('/');
        var commonParts = new List<string>();

        for (int i = 0; i < firstPath.Length - 1; i++)
        {
            var part = firstPath[i];
            if (paths.All(p => p.Split('/').Length > i && p.Split('/')[i] == part))
                commonParts.Add(part);
            else
                break;
        }

        return commonParts.Count > 0 ? string.Join("/", commonParts) + "/" : string.Empty;
    }
}
