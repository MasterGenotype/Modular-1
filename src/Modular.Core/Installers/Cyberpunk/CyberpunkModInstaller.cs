using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Cyberpunk;

/// <summary>
/// Installer for Cyberpunk 2077 mods. Uses <see cref="CyberpunkArchiveAnalyzer"/>
/// to fingerprint archive contents and automatically route files to the correct
/// game subdirectories based on detected <see cref="CyberpunkInstallType"/>s.
///
/// Supports all 11 install types identified from the Cyberpunk 2077 modding
/// ecosystem, including multi-path mods that span multiple directories.
///
/// Detection priority is 90 — above generic loose-file (1) and BepInEx (80),
/// but below FOMOD (100) since FOMOD archives carry an explicit manifest.
/// </summary>
public class CyberpunkModInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<CyberpunkModInstaller>? _logger;

    public string InstallerId => "cyberpunk2077";
    public string DisplayName => "Cyberpunk 2077 Installer";
    public int Priority => 90;

    public CyberpunkModInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<CyberpunkModInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    /// <summary>
    /// Opens the archive, runs <see cref="CyberpunkArchiveAnalyzer.Analyze"/>,
    /// and returns a detection result reflecting the confidence and type.
    /// </summary>
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

            var layout = CyberpunkArchiveAnalyzer.Analyze(reader.Entries);

            if (layout.Types == CyberpunkInstallType.Unknown)
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
                "Cyberpunk analyzer: {Types} ({Confidence:P0}) — {Reason}",
                layout.Types, layout.Confidence, layout.Reason);

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = layout.Confidence,
                InstallerType = layout.IsSingleType
                    ? layout.Types.ToString()
                    : "cyberpunk-multipath",
                Reason = layout.Reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Cyberpunk detection failed for {Path}", archivePath);
            return new InstallDetectionResult
            {
                CanHandle = false,
                Confidence = 0,
                Reason = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Builds an <see cref="InstallPlan"/> using the file routes computed by
    /// <see cref="CyberpunkArchiveAnalyzer"/>. Each file operation maps an
    /// archive entry to its correct game-relative destination path.
    /// </summary>
    public async Task<InstallPlan> AnalyzeAsync(
        string archivePath,
        InstallContext context,
        CancellationToken ct = default)
    {
        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var layout = CyberpunkArchiveAnalyzer.Analyze(reader.Entries);

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = context.GameDirectory,
            Operations = new List<FileOperation>(),
            Options = new Dictionary<string, object>
            {
                ["cyberpunk_types"] = layout.Types.ToString(),
                ["cyberpunk_is_multipath"] = layout.IsMultiPath,
                ["cyberpunk_stripped_prefix"] = layout.StrippedPrefix,
                ["cyberpunk_framework_hints"] = layout.FrameworkHints
                    .Select(h => new Dictionary<string, object>
                    {
                        ["source"] = h.SourceEntry,
                        ["dest"] = h.DestinationRelative,
                        ["framework"] = h.FrameworkId,
                        ["critical"] = h.IsCritical
                    }).ToList()
            }
        };

        long totalBytes = 0;

        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            // Look up the routed destination for this entry
            if (!layout.FileRoutes.TryGetValue(entry.FullName, out var destination))
            {
                // Fallback: place unrecognized files at game root maintaining relative path
                destination = entry.FullName;
                if (!string.IsNullOrEmpty(layout.StrippedPrefix) &&
                    destination.StartsWith(layout.StrippedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    destination = destination[layout.StrippedPrefix.Length..];
                }
            }

            // Normalize path separators
            destination = destination.Replace('\\', '/');

            // Determine criticality
            var isCritical = IsCriticalFile(entry.FullName, layout);

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
            "Cyberpunk install plan: {Count} files, {Types}, {Size:N0} bytes",
            plan.Operations.Count, layout.Types, totalBytes);

        return await Task.FromResult(plan);
    }

    /// <summary>
    /// Executes the install plan, extracting each file to its computed destination.
    /// Critical files (DLLs, loaders) get automatic backups.
    /// </summary>
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
            using var reader = _archiveReaderFactory.Open(plan.SourcePath)
                ?? throw new InvalidOperationException($"Unable to open archive: {plan.SourcePath}");

            int filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var operation in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();

                var entry = reader.Entries.FirstOrDefault(e => e.FullName == operation.SourcePath);
                if (entry == null)
                    continue;

                var destPath = Path.Combine(plan.TargetDirectory, operation.DestinationPath);
                destPath = Path.GetFullPath(destPath);

                // Safety: ensure we never write outside the target directory
                if (!destPath.StartsWith(Path.GetFullPath(plan.TargetDirectory)))
                {
                    _logger?.LogWarning(
                        "Skipping path traversal attempt: {Source} -> {Dest}",
                        operation.SourcePath, destPath);
                    continue;
                }

                // Create directory structure
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                // Backup existing files (always for critical, optional for others)
                if (File.Exists(destPath) && operation.IsCritical)
                {
                    var backupPath = destPath + ".modular.bak";
                    File.Copy(destPath, backupPath, overwrite: true);
                    backedUpFiles.Add(backupPath);
                    _logger?.LogDebug("Backed up critical file: {Path}", destPath);
                }

                // Extract
                await reader.ExtractEntryAsync(entry, destPath, overwrite: true, ct);
                installedFiles.Add(operation.DestinationPath); // store relative path

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
                Backups = backedUpFiles.ToDictionary(f => f, f => f)
            };

            _logger?.LogInformation(
                "Cyberpunk install complete: {Count} files to {Dir}",
                installedFiles.Count, plan.TargetDirectory);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "Cyberpunk installation failed for {Archive}", plan.SourcePath);
        }

        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// A file is critical if it's a framework loader DLL, an ASI plugin,
    /// or marked critical by the analyzer.
    /// </summary>
    private static bool IsCriticalFile(string entryPath, CyberpunkInstallLayout layout)
    {
        // Framework-level files marked by the analyzer
        if (layout.FrameworkHints.Any(h =>
            h.SourceEntry.Equals(entryPath, StringComparison.OrdinalIgnoreCase) && h.IsCritical))
            return true;

        var lower = entryPath.ToLowerInvariant();
        return lower.EndsWith(".dll") || lower.EndsWith(".asi");
    }

    /// <summary>
    /// Build a human-readable progress label based on detected types.
    /// </summary>
    private static string BuildProgressLabel(InstallPlan plan)
    {
        if (plan.Options?.TryGetValue("cyberpunk_types", out var typesObj) == true &&
            typesObj is string types)
        {
            if (types.Contains(','))
                return "Installing Cyberpunk 2077 multi-path mod";

            return types switch
            {
                nameof(CyberpunkInstallType.Red4ExtPlugin) => "Installing RED4ext plugin",
                nameof(CyberpunkInstallType.CetMod) => "Installing CET mod",
                nameof(CyberpunkInstallType.RedscriptMod) => "Installing redscript mod",
                nameof(CyberpunkInstallType.LegacyArchive) => "Installing legacy archive",
                nameof(CyberpunkInstallType.RedMod) => "Installing REDmod",
                nameof(CyberpunkInstallType.TweakMod) => "Installing TweakDB mod",
                nameof(CyberpunkInstallType.IniTweak) => "Installing config tweak",
                nameof(CyberpunkInstallType.FrameworkRoot) => "Installing framework",
                nameof(CyberpunkInstallType.StandaloneExe) => "Extracting standalone tool",
                nameof(CyberpunkInstallType.InputMapping) => "Installing input mappings",
                nameof(CyberpunkInstallType.AsiPlugin) => "Installing ASI plugin",
                _ => "Installing Cyberpunk 2077 mod"
            };
        }

        return "Installing Cyberpunk 2077 mod";
    }
}
