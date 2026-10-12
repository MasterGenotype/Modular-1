using Microsoft.Extensions.Logging;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;

namespace Modular.Core.Installers;

/// <summary>
/// Carries out an install plan whose operations each extract one archive entry.
/// </summary>
/// <remarks>
/// The game-specific installers differ in how they read an archive and decide where its
/// files go. Once a plan exists, extracting it is the same job for all of them, so it
/// lives here once instead of being copied into each.
/// </remarks>
internal static class ArchivePlanExecutor
{
    private const string BackupSuffix = ".modular.bak";

    /// <summary>
    /// Extracts every operation in a plan to its destination.
    /// </summary>
    /// <param name="archiveReaderFactory">Opens the plan's source archive.</param>
    /// <param name="plan">The plan to carry out.</param>
    /// <param name="installerId">Id of the installer the plan came from, for the manifest.</param>
    /// <param name="progressLabel">What progress reports call the work in hand.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome. A failure is reported in the result, not thrown.</returns>
    /// <remarks>
    /// A plan that has been redirected into staging has every destination under its target
    /// directory already. One that has not may still name a root other than the game
    /// folder for some files, and those are resolved through
    /// <see cref="InstallPlan.RootDirectories"/>; a file whose root cannot be resolved
    /// fails the install rather than landing in the game folder by default.
    /// </remarks>
    public static async Task<InstallResult> ExecuteAsync(
        IArchiveReaderFactory archiveReaderFactory,
        InstallPlan plan,
        string installerId,
        string progressLabel,
        ILogger? logger,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var result = new InstallResult { Success = false };
        var installedFiles = new List<string>();
        var backedUpFiles = new List<string>();
        var createdDirectories = new List<string>();

        try
        {
            using var reader = archiveReaderFactory.Open(plan.SourcePath)
                ?? throw new InvalidOperationException(
                    $"Unable to open archive: {plan.SourcePath}");

            if (!Directory.Exists(plan.TargetDirectory))
            {
                Directory.CreateDirectory(plan.TargetDirectory);
                createdDirectories.Add(plan.TargetDirectory);
            }

            var filesProcessed = 0;
            long bytesProcessed = 0;

            foreach (var operation in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();

                var entry = reader.Entries
                    .FirstOrDefault(e => e.FullName == operation.SourcePath);
                if (entry == null)
                    continue;

                var (baseDirectory, isGameRelative) = ResolveBaseDirectory(plan, operation);

                string destPath;
                try
                {
                    destPath = PathSanitizer.SanitizeEntryPath(
                        operation.DestinationPath, baseDirectory);
                }
                catch (InvalidOperationException ex)
                {
                    logger?.LogWarning(
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
                    var backupPath = destPath + BackupSuffix;
                    File.Copy(destPath, backupPath, overwrite: true);
                    backedUpFiles.Add(backupPath);
                    logger?.LogDebug("Backed up: {Path}", destPath);
                }

                await reader.ExtractEntryAsync(entry, destPath, overwrite: true, ct);

                // Paths under the plan's target stay relative to it, as uninstall expects;
                // one under another root has no such base, so it is recorded in full.
                installedFiles.Add(isGameRelative ? operation.DestinationPath : destPath);

                filesProcessed++;
                bytesProcessed += operation.SizeBytes;

                progress?.Report(new InstallProgress
                {
                    CurrentOperation = progressLabel,
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
                InstallerId = installerId,
                Files = installedFiles,
                Directories = createdDirectories,
                Backups = backedUpFiles.ToDictionary(f => f, f => f)
            };

            logger?.LogInformation(
                "{Installer} install complete: {Count} files to {Dir}",
                installerId, installedFiles.Count, plan.TargetDirectory);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            logger?.LogError(ex, "{Installer} installation failed for {Archive}",
                installerId, plan.SourcePath);
        }

        return result;
    }

    private static (string Directory, bool IsGameRelative) ResolveBaseDirectory(
        InstallPlan plan, FileOperation operation)
    {
        // Redirected into staging, or aimed at the game folder: the target is the base.
        if (plan.RootDirectories == null ||
            string.IsNullOrEmpty(operation.TargetRoot) ||
            string.Equals(operation.TargetRoot, ModRoots.Game, StringComparison.OrdinalIgnoreCase))
        {
            return (plan.TargetDirectory, true);
        }

        if (plan.RootDirectories.TryGetValue(operation.TargetRoot, out var root) &&
            !string.IsNullOrEmpty(root))
        {
            return (root, false);
        }

        throw new InvalidOperationException(
            $"'{operation.DestinationPath}' belongs under '{operation.TargetRoot}', " +
            "which could not be located for this game.");
    }
}
