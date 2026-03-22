using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Dependencies;
using Modular.Core.Installers;
using Modular.Sdk.Installers;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Installs a mod archive into a game directory with staging, conflict detection, and backup.
/// </summary>
public sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<archive>")]
        [Description("Path to the mod archive (.zip, .7z, .tar.gz, etc.)")]
        public string ArchivePath { get; init; } = string.Empty;

        [CommandArgument(1, "<game-directory>")]
        [Description("Path to the game installation directory")]
        public string GameDirectory { get; init; } = string.Empty;

        [CommandOption("--mod-id")]
        [Description("Unique identifier for this mod (defaults to archive filename)")]
        public string? ModId { get; init; }

        [CommandOption("--mod-name")]
        [Description("Human-readable mod name")]
        public string? ModName { get; init; }

        [CommandOption("--version")]
        [Description("Mod version string")]
        public string? Version { get; init; }

        [CommandOption("--game-domain")]
        [Description("Game domain (e.g., skyrimspecialedition)")]
        public string? GameDomain { get; init; }

        [CommandOption("--installer")]
        [Description("Force a specific installer (fomod, bepinex, loose, steam)")]
        public string? Installer { get; init; }

        [CommandOption("--conflict-policy")]
        [Description("Conflict handling: fail, overwrite, ask (default: fail)")]
        [DefaultValue("fail")]
        public string ConflictPolicyStr { get; init; } = "fail";

        [CommandOption("--no-backup")]
        [Description("Skip creating backups of overwritten files")]
        public bool NoBackup { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be installed without modifying files")]
        public bool DryRun { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;

        try
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                LiveProgressDisplay.ShowWarning("Cancellation requested, cleaning up...");
            };
            Console.CancelKeyPress += cancelHandler;

            if (!File.Exists(settings.ArchivePath))
            {
                LiveProgressDisplay.ShowError($"Archive not found: {settings.ArchivePath}");
                return 1;
            }

            if (!Directory.Exists(settings.GameDirectory))
            {
                LiveProgressDisplay.ShowError($"Game directory not found: {settings.GameDirectory}");
                return 1;
            }

            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var modularDb = new ModularDatabase(services.Settings.DatabasePath);
            await modularDb.InitializeAsync();

            var tracker = new InstallationTracker(modularDb,
                services.LoggerFactory?.CreateLogger<InstallationTracker>());

            var modId = settings.ModId ?? Path.GetFileNameWithoutExtension(settings.ArchivePath);

            // Check if already installed.
            var existing = await tracker.GetInstalledModAsync(modId, cts.Token);
            if (existing != null)
            {
                LiveProgressDisplay.ShowWarning($"Mod '{modId}' is already installed (version: {existing.Version ?? "unknown"}).");
                if (!LiveProgressDisplay.Confirm("Reinstall and overwrite?", defaultValue: false))
                    return 0;
            }

            var conflictPolicy = settings.ConflictPolicyStr.ToLowerInvariant() switch
            {
                "overwrite" or "lastwriterwins" => ConflictPolicy.LastWriterWins,
                "ask" => ConflictPolicy.AskUser,
                _ => ConflictPolicy.FailOnConflict
            };

            var installContext = new InstallContext
            {
                GameDirectory = settings.GameDirectory,
                ModId = modId,
                GameId = settings.GameDomain,
                CreateBackups = !settings.NoBackup,
                ConflictPolicy = conflictPolicy
            };

            var installerManager = new InstallerManager(
                services.LoggerFactory?.CreateLogger<InstallerManager>(),
                services.Telemetry);

            // Select installer.
            IModInstaller? preferredInstaller = null;
            if (!string.IsNullOrEmpty(settings.Installer))
            {
                preferredInstaller = installerManager.GetInstaller(settings.Installer);
                if (preferredInstaller == null)
                {
                    LiveProgressDisplay.ShowError($"Unknown installer: {settings.Installer}");
                    LiveProgressDisplay.ShowInfo($"Available: {string.Join(", ", installerManager.GetInstallers().Select(i => i.InstallerId))}");
                    return 1;
                }
            }

            // Create install plan.
            LiveProgressDisplay.ShowInfo("Analyzing archive...");
            var plan = await installerManager.CreateInstallPlanAsync(
                settings.ArchivePath, installContext, preferredInstaller, cts.Token);

            LiveProgressDisplay.ShowInfo($"Installer: {plan.InstallerId}");
            LiveProgressDisplay.ShowInfo($"Files: {plan.Operations.Count} ({plan.TotalBytes:N0} bytes)");

            // Conflict detection.
            var conflictIndex = new FileConflictIndex();
            foreach (var op in plan.Operations.Where(o => o.Type == FileOperationType.Copy || o.Type == FileOperationType.Extract))
            {
                if (op.DestinationPath != null)
                    conflictIndex.RegisterFile(op.DestinationPath, modId, Path.GetFileName(op.DestinationPath));
            }

            // Check against existing installed mods.
            var installedMods = await tracker.GetInstalledModsAsync(settings.GameDomain, cts.Token);
            foreach (var installed in installedMods)
            {
                foreach (var file in installed.InstalledFiles)
                {
                    var relativePath = Path.GetRelativePath(settings.GameDirectory, file);
                    conflictIndex.RegisterFile(relativePath, installed.ModId, Path.GetFileName(file));
                }
            }

            var conflicts = conflictIndex.DetectConflicts();
            if (conflicts.Count > 0)
            {
                LiveProgressDisplay.ShowWarning($"Detected {conflicts.Count} file conflict(s):");
                foreach (var conflict in conflicts.Take(10))
                    Console.WriteLine($"  {conflict}");

                if (conflicts.Count > 10)
                    Console.WriteLine($"  ... and {conflicts.Count - 10} more");

                if (conflictPolicy == ConflictPolicy.FailOnConflict)
                {
                    LiveProgressDisplay.ShowError("Aborting due to conflict policy. Use --conflict-policy=overwrite to force.");
                    return 1;
                }
            }

            if (settings.DryRun)
            {
                LiveProgressDisplay.ShowInfo("Dry run — no files will be modified:");
                foreach (var op in plan.Operations)
                    Console.WriteLine($"  {op.Type}: {op.SourcePath} -> {op.DestinationPath}");
                return 0;
            }

            // Execute installation.
            var progress = new Progress<InstallProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    Console.WriteLine($"  [{p.FilesProcessed}/{p.TotalFiles}] {p.CurrentFile}");
            });

            var result = await installerManager.ExecuteInstallAsync(plan, progress, cts.Token);

            if (!result.Success)
            {
                LiveProgressDisplay.ShowError($"Installation failed: {result.Error}");
                return 1;
            }

            // Record installation.
            var backupFiles = new Dictionary<string, string>();
            if (result.Manifest?.Backups != null)
            {
                foreach (var (original, backup) in result.Manifest.Backups)
                    backupFiles[original] = backup;
            }

            await tracker.RecordInstallationAsync(new InstalledModRecord
            {
                ModId = modId,
                ModName = settings.ModName ?? modId,
                Version = settings.Version,
                GameDomain = settings.GameDomain,
                TargetDirectory = settings.GameDirectory,
                ArchivePath = Path.GetFullPath(settings.ArchivePath),
                InstallerId = plan.InstallerId,
                InstalledFiles = result.InstalledFiles,
                BackupFiles = backupFiles
            }, cts.Token);

            LiveProgressDisplay.ShowSuccess(
                $"Installed {result.InstalledFiles.Count} files for '{modId}' successfully.");

            if (result.BackedUpFiles.Count > 0)
                LiveProgressDisplay.ShowInfo($"Backed up {result.BackedUpFiles.Count} existing files.");

            return 0;
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Operation cancelled by user.");
            return 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }
}
