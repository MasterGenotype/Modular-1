using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Utilities;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.HorizonZeroDawn;

/// <summary>
/// Installer for Horizon Zero Dawn mods. Uses <see cref="HZDArchiveAnalyzer"/>
/// to fingerprint archive contents and route files to the correct game
/// subdirectories based on detected <see cref="HZDInstallType"/>s.
///
/// HZD uses the Decima engine. The dominant mod type is Patch_*.bin files
/// placed in <c>Packed_DX12/</c>. The installer also handles DLL hooks,
/// GPU utility mods, binary replacements, and config files.
///
/// Priority 91 — above generic loose-file (1) and BepInEx (80), below
/// FOMOD (100) and FF7R (92). On par with Cyberpunk (90) and UnrealPak (90)
/// but wins due to higher priority when HZD anchors are detected.
/// </summary>
public class HZDModInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<HZDModInstaller>? _logger;

    public string InstallerId => "horizon-zero-dawn";
    public string DisplayName => "Horizon Zero Dawn Installer";
    public int Priority => 91;
    public IReadOnlyList<string> SupportedGameIds { get; } = [GameIds.HorizonZeroDawn, "1151640"];

    public HZDModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<HZDModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    public async Task<InstallDetectionResult> DetectAsync(
        string archivePath, CancellationToken ct = default)
    {
        try
        {
            using var reader = _archiveReaderFactory.Open(archivePath);
            if (reader == null)
            {
                return new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "Unable to open archive"
                };
            }

            var layout = HZDArchiveAnalyzer.Analyze(reader.Entries);

            if (layout.Types == HZDInstallType.Unknown)
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    InstallerType = "unknown",
                    Reason = layout.Reason
                });
            }

            _logger?.LogDebug(
                "HZD analyzer: {Types} ({Confidence:P0}) — {Reason}",
                layout.Types, layout.Confidence, layout.Reason);

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = layout.Confidence,
                InstallerType = layout.IsSingleType
                    ? layout.Types.ToString()
                    : "hzd-multipath",
                Reason = layout.Reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HZD detection failed for {Path}", archivePath);
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
        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = HZDArchiveAnalyzer.Analyze(reader.Entries);

        // Always use game root — all routes are fully qualified from game root.
        var targetDir = context.GameDirectory;

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = targetDir,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["hzd_types"] = layout.Types.ToString(),
                ["hzd_is_multipath"] = layout.IsMultiPath,
                ["hzd_stripped_prefix"] = layout.StrippedPrefix
            }
        };

        long totalBytes = 0;

        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            if (!layout.FileRoutes.TryGetValue(entry.FullName, out var destination))
            {
                destination = entry.FullName;
                if (!string.IsNullOrEmpty(layout.StrippedPrefix) &&
                    destination.StartsWith(layout.StrippedPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    destination = destination[layout.StrippedPrefix.Length..];
                }
            }

            destination = destination.Replace('\\', '/');

            var isCritical =
                destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                destination.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = destination,
                SizeBytes = entry.Length,
                IsCritical = isCritical
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;

        _logger?.LogInformation(
            "HZD install plan: {Count} files, {Types}, {Size:N0} bytes",
            plan.Operations.Count, layout.Types, totalBytes);

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
        var createdDirectories = new List<string>();

        try
        {
            using var reader = _archiveReaderFactory.Open(plan.SourcePath)
                ?? throw new InvalidOperationException(
                    $"Unable to open archive: {plan.SourcePath}");

            if (!Directory.Exists(plan.TargetDirectory))
            {
                Directory.CreateDirectory(plan.TargetDirectory);
                createdDirectories.Add(plan.TargetDirectory);
            }

            int filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var operation in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();

                var entry = reader.Entries
                    .FirstOrDefault(e => e.FullName == operation.SourcePath);
                if (entry == null)
                    continue;

                string destPath;
                try
                {
                    destPath = PathSanitizer.SanitizeEntryPath(
                        operation.DestinationPath, plan.TargetDirectory);
                }
                catch (InvalidOperationException ex)
                {
                    _logger?.LogWarning(
                        "Skipping unsafe entry: {Source} — {Reason}",
                        operation.SourcePath, ex.Message);
                    continue;
                }

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    createdDirectories.Add(destDir);
                }

                if (File.Exists(destPath) && operation.IsCritical)
                {
                    var backupPath = destPath + ".modular.bak";
                    File.Copy(destPath, backupPath, overwrite: true);
                    backedUpFiles.Add(backupPath);
                    _logger?.LogDebug("Backed up: {Path}", destPath);
                }

                await reader.ExtractEntryAsync(entry, destPath, overwrite: true, ct);
                installedFiles.Add(operation.DestinationPath);

                filesProcessed++;
                bytesProcessed += operation.SizeBytes;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = BuildProgressLabel(plan),
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
                Directories = createdDirectories,
                Backups = backedUpFiles.ToDictionary(f => f, f => f)
            };

            _logger?.LogInformation(
                "HZD install complete: {Count} files to {Dir}",
                installedFiles.Count, plan.TargetDirectory);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "HZD installation failed for {Archive}",
                plan.SourcePath);
        }

        return result;
    }

    private static string BuildProgressLabel(InstallPlan plan)
    {
        if (plan.Options?.TryGetValue("hzd_types", out var typesObj) == true &&
            typesObj is string types)
        {
            if (types.Contains(','))
                return "Installing HZD multi-path mod";

            return types switch
            {
                nameof(HZDInstallType.DecimaPatch) => "Installing Decima patch",
                nameof(HZDInstallType.DllHook) => "Installing DLL hook",
                nameof(HZDInstallType.GpuUtility) => "Installing GPU utility mod",
                nameof(HZDInstallType.BinaryReplacement) => "Installing binary replacement",
                nameof(HZDInstallType.ConfigFile) => "Installing config files",
                _ => "Installing Horizon Zero Dawn mod"
            };
        }

        return "Installing Horizon Zero Dawn mod";
    }
}
