using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Installer for FOMOD (Fallout Mod Manager) format mods.
/// Supports ModuleConfig.xml parsing and conditional installs.
/// </summary>
public class FomodInstaller : IModInstaller
{
    private readonly ILogger<FomodInstaller>? _logger;

    public string InstallerId => "fomod";
    public string DisplayName => "FOMOD Installer";
    public int Priority => 100; // High priority

    public FomodInstaller(ILogger<FomodInstaller>? logger = null)
    {
        _logger = logger;
    }

    public async Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            
            // Look for FOMOD descriptor files
            var hasFomodConfig = archive.Entries.Any(e => 
                e.FullName.EndsWith("ModuleConfig.xml", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith("fomod/ModuleConfig.xml", StringComparison.OrdinalIgnoreCase));

            var hasInfo = archive.Entries.Any(e => 
                e.FullName.EndsWith("fomod/info.xml", StringComparison.OrdinalIgnoreCase));

            if (hasFomodConfig)
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = true,
                    Confidence = 1.0,
                    InstallerType = "fomod",
                    Reason = "Contains FOMOD ModuleConfig.xml"
                });
            }

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = "No FOMOD configuration found"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to detect FOMOD for {Path}", archivePath);
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
            RequiresUserInput = true, // FOMOD typically needs user choices
            Operations = new List<FileOperation>()
        };

        using var archive = ZipFile.OpenRead(archivePath);
        
        // Find and parse ModuleConfig.xml
        var configEntry = archive.Entries.FirstOrDefault(e => 
            e.FullName.EndsWith("ModuleConfig.xml", StringComparison.OrdinalIgnoreCase));

        if (configEntry == null)
        {
            throw new InvalidOperationException("No ModuleConfig.xml found");
        }

        using var configStream = configEntry.Open();
        var doc = await XDocument.LoadAsync(configStream, LoadOptions.None, ct);

        // Parse FOMOD structure
        var config = ParseFomodConfig(doc);
        plan.Options = new Dictionary<string, object>
        {
            ["fomod_config"] = config,
            ["requires_ui"] = true
        };

        // For now, create a basic file list (UI would handle user selections)
        var allFiles = archive.Entries.Where(e => 
            !string.IsNullOrEmpty(e.Name) && 
            !e.FullName.Contains("fomod/", StringComparison.OrdinalIgnoreCase));

        long totalBytes = 0;
        foreach (var entry in allFiles)
        {
            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = entry.FullName,
                SizeBytes = entry.Length,
                IsCritical = false
            });
            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;
        return plan;
    }

    public async Task<InstallResult> InstallAsync(
        InstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Note: Full FOMOD install requires UI for user selections
        // This is a simplified implementation
        
        var result = new InstallResult { Success = false };
        var installedFiles = new List<string>();

        try
        {
            using var archive = ZipFile.OpenRead(plan.SourcePath);
            
            // If options contain user selections, use those
            var selectedFiles = plan.Options?.ContainsKey("selected_files") == true
                ? (List<string>)plan.Options["selected_files"]
                : plan.Operations.Select(o => o.SourcePath).ToList();

            int filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var filePath in selectedFiles)
            {
                ct.ThrowIfCancellationRequested();

                var entry = archive.GetEntry(filePath);
                if (entry == null)
                    continue;

                var destPath = Path.Combine(
                    Path.GetDirectoryName(plan.SourcePath) ?? string.Empty,
                    entry.Name);

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                entry.ExtractToFile(destPath, true);
                installedFiles.Add(destPath);

                filesProcessed++;
                bytesProcessed += entry.Length;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = "Installing FOMOD",
                    CurrentFile = entry.Name,
                    FilesProcessed = filesProcessed,
                    TotalFiles = selectedFiles.Count,
                    BytesProcessed = bytesProcessed,
                    TotalBytes = plan.TotalBytes
                });
            }

            result.Success = true;
            result.InstalledFiles = installedFiles;
            result.Manifest = new InstallManifest
            {
                InstallerId = InstallerId,
                Files = installedFiles
            };

            _logger?.LogInformation("FOMOD installation completed: {Count} files", installedFiles.Count);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "FOMOD installation failed");
        }

        return await Task.FromResult(result);
    }

    private Dictionary<string, object> ParseFomodConfig(XDocument doc)
    {
        var config = new Dictionary<string, object>();

        var root = doc.Root;
        if (root == null)
            return config;

        // Parse module name
        var moduleName = root.Element("moduleName")?.Value;
        if (moduleName != null)
            config["module_name"] = moduleName;

        // Parse install steps
        var installSteps = root.Descendants("installStep").Select(step => new
        {
            Name = step.Attribute("name")?.Value ?? "Step",
            Groups = step.Descendants("group").Select(g => new
            {
                Name = g.Attribute("name")?.Value ?? "Group",
                Type = g.Attribute("type")?.Value ?? "SelectExactlyOne",
                Plugins = g.Descendants("plugin").Select(p => new
                {
                    Name = p.Attribute("name")?.Value ?? "Plugin",
                    Description = p.Element("description")?.Value,
                    Files = p.Descendants("file").Select(f => new
                    {
                        Source = f.Attribute("source")?.Value,
                        Destination = f.Attribute("destination")?.Value
                    }).ToList()
                }).ToList()
            }).ToList()
        }).ToList();

        config["install_steps"] = installSteps;
        return config;
    }
}
