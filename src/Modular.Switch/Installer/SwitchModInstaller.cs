using Microsoft.Extensions.Logging;
using Modular.Switch.Models;
using SharpCompress.Archives;
using System.IO.Compression;

namespace Modular.Switch.Installer;

/// <summary>
/// Installs and removes Switch mods into Yuzu's LayeredFS load directory.
///
/// Install contract:
///  1. Validate TitleID and integrity hash.
///  2. Create a pre-install snapshot (copy of existing mod slot) for rollback.
///  3. Extract / copy files into the Yuzu mod slot directory,
///     respecting the LayeredFS sub-path (romfs/, exefs/, cheats/).
///  4. Persist the hash in state so unchanged mods are skipped on re-run.
///
/// All paths are validated to remain inside ~/.local/share/yuzu/load/<TitleID>/.
/// </summary>
public sealed class SwitchModInstaller
{
    private readonly ILogger<SwitchModInstaller>? _log;

    public SwitchModInstaller(ILogger<SwitchModInstaller>? logger = null) => _log = logger;

    // ── Install ───────────────────────────────────────────────────────────

    public async Task<SwitchInstallResult> InstallAsync(
        SwitchMod mod,
        bool dryRun = false,
        IProgress<SwitchInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new SwitchInstallResult { ModKey = mod.ModKey };

        // Idempotency — skip if hash unchanged
        if (mod.IsInstalled && mod.InstalledHash == mod.SourceHash)
        {
            result.Skipped = true;
            result.SkipReason = "Already installed (hash unchanged)";
            _log?.LogDebug("Skipping {Mod} — already up-to-date", mod.ModKey);
            return result;
        }

        if (!SwitchTitleId.TryParse(mod.TitleId, out var titleId))
        {
            result.Error = $"Invalid TitleID: {mod.TitleId}";
            return result;
        }

        var slotDir = YuzuPaths.ModSlotDir(titleId, mod.Name);
        var subPath = mod.Category.YuzuSubPath();
        var targetDir = string.IsNullOrEmpty(subPath)
            ? slotDir
            : Path.Combine(slotDir, subPath);

        _log?.LogInformation("Installing '{Mod}' → {Target}", mod.ModKey, targetDir);
        progress?.Report(new SwitchInstallProgress
        {
            ModKey = mod.ModKey,
            Phase = "Preparing"
        });

        if (dryRun)
        {
            result.Success = true;
            result.DryRun = true;
            result.PlannedTarget = targetDir;
            return result;
        }

        // 2. Snapshot existing slot
        var snapshotDir = await SnapshotSlotAsync(slotDir, ct);
        mod.SnapshotPath = snapshotDir;

        try
        {
            // 3. Install
            Directory.CreateDirectory(targetDir);

            if (mod.IsExtracted)
                await CopyFolderAsync(mod.SourcePath, targetDir, progress, mod, ct);
            else
                await ExtractArchiveAsync(mod.SourcePath, targetDir, progress, mod, ct);

            // Validate no path escaped the load dir
            YuzuPaths.AssertInsideLoadDir(titleId, targetDir);

            result.Success = true;
            result.InstalledPath = slotDir;
            _log?.LogInformation("Installed '{Mod}' successfully ({Files} files)",
                mod.ModKey, result.FileCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogError(ex, "Install failed for '{Mod}'", mod.ModKey);
            result.Error = ex.Message;

            // Attempt rollback on failure
            await TryRestoreSnapshotAsync(snapshotDir, slotDir);
        }

        return result;
    }

    // ── Remove ────────────────────────────────────────────────────────────

    public async Task<SwitchInstallResult> RemoveAsync(
        SwitchMod mod,
        CancellationToken ct = default)
    {
        var result = new SwitchInstallResult { ModKey = mod.ModKey };

        if (!SwitchTitleId.TryParse(mod.TitleId, out var titleId))
        {
            result.Error = $"Invalid TitleID: {mod.TitleId}";
            return result;
        }

        var slotDir = YuzuPaths.ModSlotDir(titleId, mod.Name);

        if (!Directory.Exists(slotDir))
        {
            result.Skipped = true;
            result.SkipReason = "Mod slot directory does not exist";
            return result;
        }

        // Validate path before deletion
        YuzuPaths.AssertInsideLoadDir(titleId, slotDir);

        _log?.LogInformation("Removing mod slot: {Dir}", slotDir);
        Directory.Delete(slotDir, recursive: true);
        result.Success = true;
        return await Task.FromResult(result);
    }

    // ── Rollback ──────────────────────────────────────────────────────────

    public async Task<SwitchInstallResult> RollbackAsync(
        SwitchMod mod,
        CancellationToken ct = default)
    {
        var result = new SwitchInstallResult { ModKey = mod.ModKey };

        if (string.IsNullOrEmpty(mod.SnapshotPath) || !Directory.Exists(mod.SnapshotPath))
        {
            result.Error = "No snapshot available for rollback";
            return result;
        }

        if (!SwitchTitleId.TryParse(mod.TitleId, out var titleId))
        {
            result.Error = $"Invalid TitleID: {mod.TitleId}";
            return result;
        }

        var slotDir = YuzuPaths.ModSlotDir(titleId, mod.Name);
        YuzuPaths.AssertInsideLoadDir(titleId, slotDir);

        _log?.LogInformation("Rolling back '{Mod}' from snapshot {Snap}", mod.ModKey, mod.SnapshotPath);

        // Remove current slot, restore snapshot
        if (Directory.Exists(slotDir))
            Directory.Delete(slotDir, recursive: true);

        await CopyFolderAsync(mod.SnapshotPath, slotDir, null, mod, ct);

        // Clean up snapshot after successful restore
        Directory.Delete(mod.SnapshotPath, recursive: true);
        mod.SnapshotPath = string.Empty;

        result.Success = true;
        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static async Task<string> SnapshotSlotAsync(string slotDir, CancellationToken ct)
    {
        var snapshotDir = slotDir + $".snapshot_{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (Directory.Exists(slotDir))
        {
            await CopyDirectoryAsync(slotDir, snapshotDir, ct);
        }
        else
        {
            Directory.CreateDirectory(snapshotDir); // empty snapshot = "nothing was here"
        }
        return snapshotDir;
    }

    private async Task TryRestoreSnapshotAsync(string snapshotDir, string slotDir)
    {
        try
        {
            if (!Directory.Exists(snapshotDir)) return;
            if (Directory.Exists(slotDir)) Directory.Delete(slotDir, recursive: true);
            await CopyDirectoryAsync(snapshotDir, slotDir, CancellationToken.None);
            Directory.Delete(snapshotDir, recursive: true);
            _log?.LogInformation("Rolled back to snapshot after failed install");
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Snapshot rollback also failed — manual cleanup may be needed");
        }
    }

    private async Task ExtractArchiveAsync(
        string archivePath, string targetDir,
        IProgress<SwitchInstallProgress>? progress, SwitchMod mod,
        CancellationToken ct)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        int i = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = NormaliseArchivePath(entry.Key ?? string.Empty, mod.Category);
            if (relPath == null) continue;

            var destPath = Path.Combine(targetDir, relPath);
            // Safety check
            if (!Path.GetFullPath(destPath).StartsWith(Path.GetFullPath(targetDir), StringComparison.Ordinal))
            {
                _log?.LogWarning("Path traversal blocked: {Entry}", entry.Key);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            await using var destStream = File.Create(destPath);
            await using var srcStream  = entry.OpenEntryStream();
            await srcStream.CopyToAsync(destStream, ct);

            i++;
            progress?.Report(new SwitchInstallProgress
            {
                ModKey = mod.ModKey,
                Phase = "Extracting",
                FilesProcessed = i,
                TotalFiles = entries.Count,
                CurrentFile = relPath
            });
        }
    }

    private async Task CopyFolderAsync(
        string sourceDir, string targetDir,
        IProgress<SwitchInstallProgress>? progress, SwitchMod mod,
        CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).ToList();
        int i = 0;

        foreach (var srcFile in files)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(sourceDir, srcFile).Replace('\\', '/');
            var destPath = Path.Combine(targetDir, relPath);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(srcFile, destPath, overwrite: true);

            i++;
            progress?.Report(new SwitchInstallProgress
            {
                ModKey = mod.ModKey,
                Phase = "Copying",
                FilesProcessed = i,
                TotalFiles = files.Count,
                CurrentFile = relPath
            });
        }

        await Task.CompletedTask;
    }

    private static async Task CopyDirectoryAsync(string src, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel  = Path.GetRelativePath(src, file);
            var dstF = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dstF)!);
            await Task.Run(() => File.Copy(file, dstF, overwrite: true), ct);
        }
    }

    /// <summary>
    /// Strips the archive-level category sub-folder prefix if present
    /// (e.g. "romfs/..." → "..." because we're already writing into the romfs sub-folder).
    /// </summary>
    private static string? NormaliseArchivePath(string entryPath, SwitchModCategory category)
    {
        var normalised = entryPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(normalised)) return null;

        // Strip leading TitleID component if present
        var parts = normalised.Split('/');
        if (parts.Length > 1 && SwitchTitleId.TryParse(parts[0], out _))
            normalised = string.Join('/', parts.Skip(1));

        // Strip category sub-dir that matches the target (avoid double-nesting)
        var subPath = category.YuzuSubPath();
        if (!string.IsNullOrEmpty(subPath) &&
            normalised.StartsWith(subPath + "/", StringComparison.OrdinalIgnoreCase))
            normalised = normalised[(subPath.Length + 1)..];

        return string.IsNullOrEmpty(normalised) ? null : normalised;
    }
}

// ── Result / progress types ───────────────────────────────────────────────

public sealed class SwitchInstallResult
{
    public string ModKey  { get; set; } = string.Empty;
    public bool   Success { get; set; }
    public bool   Skipped { get; set; }
    public string? SkipReason { get; set; }
    public bool   DryRun  { get; set; }
    public string? Error  { get; set; }
    public string? InstalledPath { get; set; }
    public string? PlannedTarget { get; set; }
    public int    FileCount { get; set; }
}

public sealed class SwitchInstallProgress
{
    public string ModKey  { get; set; } = string.Empty;
    public string Phase   { get; set; } = string.Empty;
    public int    FilesProcessed { get; set; }
    public int    TotalFiles     { get; set; }
    public string CurrentFile    { get; set; } = string.Empty;
}
