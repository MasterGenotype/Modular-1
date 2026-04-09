using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.FF7Remake;

/// <summary>
/// Installer for Final Fantasy VII Remake Intergrade mods. Uses
/// <see cref="FF7RArchiveAnalyzer"/> to fingerprint archive contents and
/// automatically route files to the correct game subdirectories based on
/// detected <see cref="FF7RInstallType"/>s.
///
/// Supports 7 install types: pak mods (~mods or Paks root), DLL hooks,
/// 3DMigoto framework + sub-mods, DXVK wrappers, and INI config files.
///
/// Priority 92 — above the generic UnrealPakInstaller (90). When the
/// analyzer detects FF7R-specific structure, this installer wins; archives
/// that only contain bare .pak files without any FF7R anchor fall through
/// to the generic UE4 handler.
/// </summary>
public class FF7RModInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<FF7RModInstaller>? _logger;

    public string InstallerId => "ff7remake";
    public string DisplayName => "Final Fantasy VII Remake Installer";
    public int Priority => 92;

    /// <summary>
    /// The UE4 paks sub-path specific to FF7R Intergrade.
    /// </summary>
    private const string PaksSubPath = "End/Content/Paks";
    private const string PaksModsSubPath = "End/Content/Paks/~mods";
    private const string BinariesSubPath = "End/Binaries/Win64";

    public FF7RModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<FF7RModInstaller>? logger = null)
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

            var layout = FF7RArchiveAnalyzer.Analyze(reader.Entries);

            if (layout.Types == FF7RInstallType.Unknown)
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
                "FF7R analyzer: {Types} ({Confidence:P0}) — {Reason}",
                layout.Types, layout.Confidence, layout.Reason);

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = layout.Confidence,
                InstallerType = layout.IsSingleType
                    ? layout.Types.ToString()
                    : "ff7r-multipath",
                Reason = layout.Reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "FF7R detection failed for {Path}", archivePath);
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

        var layout = FF7RArchiveAnalyzer.Analyze(reader.Entries);

        // Determine primary target directory based on dominant type
        var targetDir = context.GameDirectory;
        if (layout.IsSingleType && layout.Types == FF7RInstallType.PakMod)
            targetDir = Path.Combine(context.GameDirectory, PaksModsSubPath);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = targetDir,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["ff7r_types"] = layout.Types.ToString(),
                ["ff7r_is_multipath"] = layout.IsMultiPath,
                ["ff7r_requires_dx11"] = layout.RequiresDx11,
                ["ff7r_stripped_prefix"] = layout.StrippedPrefix
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

            // For pure PakMod archives routed to ~mods, destinations are
            // relative to ~mods (just the filename). For multi-type archives,
            // destinations are relative to game root.
            var isCritical =
                destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                destination.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) ||
                destination.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

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
            "FF7R install plan: {Count} files, {Types}, {Size:N0} bytes, DX11={Dx11}",
            plan.Operations.Count, layout.Types, totalBytes, layout.RequiresDx11);

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

            // Ensure target directory exists
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

                // Sanitize path to prevent traversal
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

                // Create directory structure
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    createdDirectories.Add(destDir);
                }

                // Backup critical files (DLLs, ASIs)
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
                "FF7R install complete: {Count} files to {Dir}",
                installedFiles.Count, plan.TargetDirectory);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "FF7R installation failed for {Archive}",
                plan.SourcePath);
        }

        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static string BuildProgressLabel(InstallPlan plan)
    {
        if (plan.Options?.TryGetValue("ff7r_types", out var typesObj) == true &&
            typesObj is string types)
        {
            if (types.Contains(','))
                return "Installing FF7R multi-path mod";

            return types switch
            {
                nameof(FF7RInstallType.PakMod) => "Installing FF7R pak mod",
                nameof(FF7RInstallType.PakRoot) => "Installing FF7R pak (root)",
                nameof(FF7RInstallType.DllHook) => "Installing FF7R DLL hook",
                nameof(FF7RInstallType.ThreeDMigoto) => "Installing 3DMigoto framework",
                nameof(FF7RInstallType.ThreeDMigotoMod) => "Installing 3DMigoto mod",
                nameof(FF7RInstallType.DxvkWrapper) => "Installing DXVK wrapper",
                nameof(FF7RInstallType.IniConfig) => "Installing config files",
                _ => "Installing FF7 Remake mod"
            };
        }

        return "Installing FF7 Remake mod";
    }
}
