using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Rolls back installed mods by uninstalling them and restoring backups.
/// </summary>
public sealed class RollbackCommand : AsyncCommand<RollbackCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--mod")]
        [Description("Specific mod ID to rollback (rolls back all if omitted)")]
        public string? ModId { get; init; }

        [CommandOption("--game-domain")]
        [Description("Filter rollback to a specific game domain")]
        public string? GameDomain { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be rolled back without modifying files")]
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
                LiveProgressDisplay.ShowWarning("Cancellation requested...");
            };
            Console.CancelKeyPress += cancelHandler;

            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var modularDb = new ModularDatabase(services.Settings.DatabasePath);
            await modularDb.InitializeAsync();

            var tracker = new InstallationTracker(modularDb,
                services.LoggerFactory?.CreateLogger<InstallationTracker>());
            var uninstaller = new UninstallService(tracker,
                services.LoggerFactory?.CreateLogger<UninstallService>());

            if (!string.IsNullOrEmpty(settings.ModId))
            {
                // Rollback a single mod.
                if (settings.DryRun)
                {
                    var dryResult = await uninstaller.DryRunAsync(settings.ModId, cts.Token);
                    if (!dryResult.Success)
                    {
                        LiveProgressDisplay.ShowError(dryResult.Error ?? "Unknown error");
                        return 1;
                    }

                    LiveProgressDisplay.ShowInfo($"Would remove {dryResult.RemovedFiles.Count} files and restore {dryResult.RestoredFiles.Count} backups.");
                    return 0;
                }

                if (!LiveProgressDisplay.Confirm($"Rollback mod '{settings.ModId}'? This will restore backed-up files."))
                    return 0;

                var result = await uninstaller.UninstallAsync(settings.ModId, restoreBackups: true, ct: cts.Token);
                if (result.Success)
                {
                    LiveProgressDisplay.ShowSuccess(
                        $"Rolled back '{settings.ModId}': {result.RemovedFiles.Count} files removed, {result.RestoredFiles.Count} restored.");
                    return 0;
                }

                LiveProgressDisplay.ShowError(result.Error ?? "Unknown error");
                return 1;
            }

            // Rollback all mods.
            var mods = await tracker.GetInstalledModsAsync(settings.GameDomain, cts.Token);
            if (mods.Count == 0)
            {
                LiveProgressDisplay.ShowInfo("No mods installed to rollback.");
                return 0;
            }

            var scope = settings.GameDomain != null ? $" for '{settings.GameDomain}'" : "";
            if (settings.DryRun)
            {
                LiveProgressDisplay.ShowInfo($"Would rollback {mods.Count} mod(s){scope}:");
                foreach (var mod in mods)
                    Console.WriteLine($"  - {mod.ModId} ({mod.InstalledFiles.Count} files)");
                return 0;
            }

            if (!LiveProgressDisplay.Confirm($"Rollback ALL {mods.Count} mod(s){scope}? This will restore all backed-up files."))
                return 0;

            var progress = new Progress<UninstallProgress>(p =>
            {
                Console.WriteLine($"  [{p.Current}/{p.Total}] {p.Phase}");
            });

            var results = await uninstaller.RollbackAllAsync(settings.GameDomain, progress, cts.Token);

            var totalRemoved = results.Sum(r => r.RemovedFiles.Count);
            var totalRestored = results.Sum(r => r.RestoredFiles.Count);
            var failedCount = results.Count(r => !r.Success);

            if (failedCount == 0)
            {
                LiveProgressDisplay.ShowSuccess(
                    $"Rolled back {results.Count} mod(s): {totalRemoved} files removed, {totalRestored} restored.");
            }
            else
            {
                LiveProgressDisplay.ShowWarning(
                    $"Rolled back {results.Count - failedCount}/{results.Count} mod(s). {failedCount} failed.");
                foreach (var failed in results.Where(r => !r.Success))
                    LiveProgressDisplay.ShowError($"  {failed.ModId}: {failed.Error}");
            }

            return failedCount == 0 ? 0 : 1;
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
