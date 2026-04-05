using Microsoft.Extensions.Logging;
using Modular.Core.Archives;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers;

/// <summary>
/// Installer for Unreal Engine 4 .pak mods.
/// Extracts pak files (and associated .utoc/.ucas/.sig files) into the
/// game's Content/Paks/~mods/ directory, preserving archive structure.
/// </summary>
public class UnrealPakInstaller : IModInstaller
{
    private readonly IArchiveReaderFactory _archiveReaderFactory;
    private readonly ILogger<UnrealPakInstaller>? _logger;

    /// <summary>
    /// File extensions recognized as UE4 pak-related assets.
    /// </summary>
    private static readonly HashSet<string> PakExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pak", ".utoc", ".ucas", ".sig"
    };

    /// <summary>
    /// Known Paks-relative directory segments in UE4 game layouts.
    /// Detection scans for these to locate the Content/Paks boundary.
    /// </summary>
    private static readonly string[] PaksPathSegments =
    {
        "Content/Paks",
        "Content\\Paks"
    };

    /// <summary>
    /// Candidate sub-paths under the game directory where Content/Paks may live.
    /// Probed in order; the first match on disk wins.
    /// Examples:
    ///   FF7 Rebirth  → End/Content/Paks
    ///   Snowbreak    → Game/Content/Paks
    ///   Generic UE4  → Content/Paks
    /// </summary>
    private static readonly string[][] PaksSearchPaths =
    {
        new[] { "End", "Content", "Paks" },
        new[] { "Game", "Content", "Paks" },
        new[] { "Content", "Paks" },
    };

    public string InstallerId => "unreal-pak";
    public string DisplayName => "Unreal Engine Pak Installer";
    public int Priority => 90; // Above BepInEx, below FOMOD

    public UnrealPakInstaller(
        IArchiveReaderFactory? archiveReaderFactory = null,
        ILogger<UnrealPakInstaller>? logger = null)
    {
        _archiveReaderFactory = archiveReaderFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    public async Task<InstallDetectionResult> DetectAsync(string archivePath, CancellationToken ct = default)
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

            var fileEntries = reader.Entries.Where(e => !e.IsDirectory).ToList();

            var pakFiles = fileEntries
                .Where(e => PakExtensions.Contains(Path.GetExtension(e.FullName)))
                .ToList();

            if (pakFiles.Count == 0)
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "No .pak/.utoc/.ucas files found"
                });
            }

            var hasPakFile = pakFiles.Any(e =>
                e.FullName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));

            if (!hasPakFile)
            {
                return await Task.FromResult(new InstallDetectionResult
                {
                    CanHandle = false,
                    Confidence = 0,
                    Reason = "Contains .utoc/.ucas/.sig but no .pak file"
                });
            }

            // Higher confidence if archive already contains a ~mods or Paks path hint
            var hasModsFolder = fileEntries.Any(e =>
                e.FullName.Contains("~mods", StringComparison.OrdinalIgnoreCase));
            var hasPaksPath = fileEntries.Any(e =>
                PaksPathSegments.Any(seg => e.FullName.Contains(seg, StringComparison.OrdinalIgnoreCase)));

            double confidence = 0.9;
            string reason = "Contains UE4 .pak files";

            if (hasModsFolder)
            {
                confidence = 0.98;
                reason = "Contains UE4 .pak files in ~mods folder structure";
            }
            else if (hasPaksPath)
            {
                confidence = 0.95;
                reason = "Contains UE4 .pak files with Content/Paks structure";
            }

            return await Task.FromResult(new InstallDetectionResult
            {
                CanHandle = true,
                Confidence = confidence,
                InstallerType = "unreal-pak",
                Reason = reason
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to detect UE4 pak mod for {Path}", archivePath);
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
        var paksDir = ResolvePaksDirectory(context.GameDirectory);
        var paksModsDir = Path.Combine(paksDir, "~mods");

        var plan = new InstallPlan
        {
            InstallerId = InstallerId,
            SourcePath = archivePath,
            TargetDirectory = paksModsDir,
            Operations = new List<FileOperation>()
        };

        using var reader = _archiveReaderFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unable to open archive: {archivePath}");

        var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();

        // Determine the root to strip from archive paths.
        // Archives may contain paths like:
        //   SomeMod/Content/Paks/~mods/mod_P.pak  -> strip up to ~mods/
        //   Content/Paks/mod_P.pak                 -> strip Content/Paks/
        //   ~mods/mod_P.pak                        -> strip ~mods/
        //   mod_P.pak                              -> keep as-is
        //   SomeFolder/mod_P.pak                   -> keep relative path
        var stripPrefix = DetectStripPrefix(entries);

        long totalBytes = 0;

        // Create the ~mods directory operation first
        var mkdirOp = new FileOperation
        {
            Type = FileOperationType.CreateDirectory,
            SourcePath = string.Empty,
            DestinationPath = string.Empty, // The target directory itself
            SizeBytes = 0
        };
        plan.Operations.Add(mkdirOp);

        foreach (var entry in entries)
        {
            var relativePath = entry.FullName;

            // Strip detected prefix
            if (!string.IsNullOrEmpty(stripPrefix) &&
                relativePath.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath[stripPrefix.Length..].TrimStart('/', '\\');
            }

            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            plan.Operations.Add(new FileOperation
            {
                Type = FileOperationType.Extract,
                SourcePath = entry.FullName,
                DestinationPath = relativePath,
                SizeBytes = entry.Length,
                IsCritical = entry.FullName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase),
                DependsOn = new List<string> { mkdirOp.OperationId }
            });

            totalBytes += entry.Length;
        }

        plan.TotalBytes = totalBytes;
        plan.Options = new Dictionary<string, object>
        {
            ["paks_mods_dir"] = paksModsDir
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
        var createdDirectories = new List<string>();

        try
        {
            var targetDir = plan.TargetDirectory;

            // Ensure ~mods directory exists
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                createdDirectories.Add(targetDir);
                _logger?.LogInformation("Created ~mods directory: {Path}", targetDir);
            }

            using var reader = _archiveReaderFactory.Open(plan.SourcePath)
                ?? throw new InvalidOperationException($"Unable to open archive: {plan.SourcePath}");

            var extractOps = plan.Operations
                .Where(op => op.Type == FileOperationType.Extract)
                .ToList();

            int filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var operation in extractOps)
            {
                ct.ThrowIfCancellationRequested();

                var entry = reader.Entries.FirstOrDefault(e => e.FullName == operation.SourcePath);
                if (entry == null)
                    continue;

                var destPath = PathSanitizer.SanitizeEntryPath(operation.DestinationPath, targetDir);

                // Create subdirectories as needed
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    createdDirectories.Add(destDir);
                }

                // Backup existing file
                if (File.Exists(destPath))
                {
                    var backupPath = destPath + ".backup";
                    File.Copy(destPath, backupPath, true);
                    backedUpFiles.Add(backupPath);
                }

                // Extract file
                await reader.ExtractEntryAsync(entry, destPath, overwrite: true, ct);
                installedFiles.Add(destPath);

                filesProcessed++;
                bytesProcessed += operation.SizeBytes;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = "Installing UE4 pak mod",
                    CurrentFile = operation.DestinationPath,
                    FilesProcessed = filesProcessed,
                    TotalFiles = extractOps.Count,
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
                "Successfully installed UE4 pak mod: {Count} files to {Target}",
                installedFiles.Count, targetDir);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger?.LogError(ex, "UE4 pak mod installation failed");
        }

        return result;
    }

    /// <summary>
    /// Determines the prefix to strip from archive entry paths so that
    /// pak files land directly in ~mods (preserving any subfolder structure
    /// below the detected boundary).
    /// </summary>
    private static string DetectStripPrefix(List<ArchiveEntry> entries)
    {
        var paths = entries.Select(e => e.FullName).ToList();

        // Check for ~mods/ in paths — strip everything up to and including ~mods/
        var modsEntry = paths.FirstOrDefault(p =>
            p.Contains("~mods/", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("~mods\\", StringComparison.OrdinalIgnoreCase));

        if (modsEntry != null)
        {
            var idx = modsEntry.IndexOf("~mods", StringComparison.OrdinalIgnoreCase);
            // Include the ~mods/ segment itself so we don't duplicate it
            return modsEntry[..(idx + "~mods/".Length)];
        }

        // Check for Content/Paks/ — strip up to and including Paks/
        var paksEntry = paths.FirstOrDefault(p =>
            PaksPathSegments.Any(seg => p.Contains(seg, StringComparison.OrdinalIgnoreCase)));

        if (paksEntry != null)
        {
            foreach (var seg in PaksPathSegments)
            {
                var idx = paksEntry.IndexOf(seg, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    return paksEntry[..(idx + seg.Length + 1)]; // +1 for trailing separator
            }
        }

        // No known structure — find common root folder (if all files share one)
        return FindCommonRoot(paths);
    }

    /// <summary>
    /// Probes the game directory for known UE4 Content/Paks layouts.
    /// Returns the first existing path, or falls back to &lt;gameDir&gt;/Content/Paks.
    /// </summary>
    private string ResolvePaksDirectory(string gameDirectory)
    {
        foreach (var segments in PaksSearchPaths)
        {
            var candidate = Path.Combine(
                new[] { gameDirectory }.Concat(segments).ToArray());

            if (Directory.Exists(candidate))
            {
                _logger?.LogInformation("Found UE4 Paks directory: {Path}", candidate);
                return candidate;
            }
        }

        // Default fallback — the caller's game directory + Content/Paks
        var fallback = Path.Combine(gameDirectory, "Content", "Paks");
        _logger?.LogWarning(
            "No existing Content/Paks directory found under {GameDir}, defaulting to {Fallback}",
            gameDirectory, fallback);
        return fallback;
    }

    private static string FindCommonRoot(List<string> paths)
    {
        if (paths.Count == 0)
            return string.Empty;

        var firstPath = paths[0].Split('/', '\\');
        var commonParts = new List<string>();

        for (int i = 0; i < firstPath.Length - 1; i++)
        {
            var part = firstPath[i];
            if (paths.All(p =>
            {
                var segments = p.Split('/', '\\');
                return segments.Length > i &&
                       string.Equals(segments[i], part, StringComparison.OrdinalIgnoreCase);
            }))
            {
                commonParts.Add(part);
            }
            else
            {
                break;
            }
        }

        // Only strip if there's exactly one common root folder (avoid stripping meaningful structure)
        if (commonParts.Count == 1)
            return commonParts[0] + "/";

        return string.Empty;
    }
}
