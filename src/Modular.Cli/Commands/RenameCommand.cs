using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Core.Services;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Moves downloads into <c>&lt;backend&gt;/&lt;game&gt;/&lt;mod&gt;</c> folders, then renames
/// NexusMods mod folders to human-readable names.
/// </summary>
public sealed class RenameCommand : AsyncCommand<RenameCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[domain]")]
        [Description("Game domain to rename")]
        public string? Domain { get; init; }

        [CommandOption("--organize-by-category")]
        [Description("Sort mods into category subdirectories instead of <game>/<mod>")]
        public bool OrganizeByCategory { get; init; }

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
            var renameService = new RenameService(services.Settings, services.RateLimiter, services.MetadataCache,
                services.LoggerFactory?.CreateLogger<RenameService>());

            // Installed mods keep the path of the archive they came from; every move updates it.
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "modular.db");
            var db = new ModularDatabase(dbPath);
            await db.InitializeAsync();
            var changesets = new ChangesetManager(db);

            // Downloads from before backend folders existed move first, so the NexusMods
            // games among them are found below.
            var legacyFlat = ModLibraryMigrator.LegacyFlatFolders(services.Settings);
            if (ModLibraryMigrator.NeedsMigration(services.Settings.ModsDirectory, legacyFlat))
            {
                LiveProgressDisplay.ShowInfo("Moving downloads into <backend>/<game> folders...");
                var migration = await new ModLibraryMigrator(
                        new BackendLibraryGameLookup(services.CreateBackendRegistry().GetAll(), services.Settings),
                        changesets.RelocateArchiveFolderAsync,
                        services.LoggerFactory?.CreateLogger<ModLibraryMigrator>())
                    .MigrateAsync(services.Settings.ModsDirectory, legacyFlat, cts.Token);

                LiveProgressDisplay.ShowSuccess($"Moved {migration.Moved} folder(s)");
                foreach (var unresolved in migration.Unresolved)
                    LiveProgressDisplay.ShowWarning($"Left in place, game unknown: {unresolved}");
                foreach (var error in migration.Errors)
                    LiveProgressDisplay.ShowError(error);
            }

            IEnumerable<string> domains;
            if (!string.IsNullOrEmpty(settings.Domain))
            {
                domains = [settings.Domain];
            }
            else
            {
                domains = renameService.GetGameDomainNames();
            }

            foreach (var d in domains)
            {
                var gameDomainPath = renameService.GetGameDomainPath(d);
                if (!Directory.Exists(gameDomainPath))
                {
                    LiveProgressDisplay.ShowWarning($"Directory not found: {gameDomainPath}");
                    continue;
                }

                LiveProgressDisplay.ShowInfo($"Processing {d}...");
                var renamed = await renameService.ReorganizeAndRenameModsAsync(
                    gameDomainPath, settings.OrganizeByCategory, cts.Token, changesets.RelocateArchiveFolderAsync);
                LiveProgressDisplay.ShowSuccess($"Renamed {renamed} mods in {d}");

                if (settings.OrganizeByCategory)
                {
                    var catRenamed = await renameService.RenameCategoryFoldersAsync(
                        gameDomainPath, cts.Token, changesets.RelocateArchiveFolderAsync);
                    LiveProgressDisplay.ShowSuccess($"Renamed {catRenamed} category folders");
                }
            }

            await services.SaveStateAsync();
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
