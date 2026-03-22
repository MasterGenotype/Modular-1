using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Uninstalls a previously installed mod, removing its files and restoring backups.
/// </summary>
public sealed class UninstallCommand : AsyncCommand<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<mod-id>")]
        [Description("ID of the mod to uninstall")]
        public string ModId { get; init; } = string.Empty;

        [CommandOption("--no-restore")]
        [Description("Skip restoring backup files")]
        public bool NoRestore { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be removed without modifying files")]
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

            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var modularDb = new ModularDatabase(services.Settings.DatabasePath);
            await modularDb.InitializeAsync();

            var tracker = new InstallationTracker(modularDb,
                services.LoggerFactory?.CreateLogger<InstallationTracker>());
            var uninstaller = new UninstallService(tracker,
                services.LoggerFactory?.CreateLogger<UninstallService>());

            if (settings.DryRun)
            {
                var dryResult = await uninstaller.DryRunAsync(settings.ModId, cts.Token);
                if (!dryResult.Success)
                {
                    LiveProgressDisplay.ShowError(dryResult.Error ?? "Unknown error");
                    return 1;
                }

                LiveProgressDisplay.ShowInfo("Dry run — no files will be modified:");
                Console.WriteLine($"  Files to remove: {dryResult.RemovedFiles.Count}");
                Console.WriteLine($"  Files already missing: {dryResult.AlreadyMissingFiles.Count}");
                Console.WriteLine($"  Backups to restore: {dryResult.RestoredFiles.Count}");

                if (settings.Verbose)
                {
                    foreach (var f in dryResult.RemovedFiles)
                        Console.WriteLine($"    REMOVE: {f}");
                    foreach (var f in dryResult.RestoredFiles)
                        Console.WriteLine($"    RESTORE: {f}");
                }

                return 0;
            }

            if (!LiveProgressDisplay.Confirm($"Uninstall mod '{settings.ModId}'?"))
                return 0;

            var progress = new Progress<UninstallProgress>(p =>
            {
                Console.WriteLine($"  [{p.Current}/{p.Total}] {p.Phase}: {p.CurrentFile ?? ""}");
            });

            var result = await uninstaller.UninstallAsync(
                settings.ModId,
                restoreBackups: !settings.NoRestore,
                progress: progress,
                ct: cts.Token);

            if (!result.Success)
            {
                LiveProgressDisplay.ShowError(result.Error ?? "Unknown error");
                return 1;
            }

            LiveProgressDisplay.ShowSuccess(
                $"Uninstalled '{settings.ModId}': {result.RemovedFiles.Count} files removed, {result.RestoredFiles.Count} backups restored.");

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
