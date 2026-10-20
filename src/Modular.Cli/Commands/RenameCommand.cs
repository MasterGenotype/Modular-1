using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Core.Services;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
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
        [Description("Sort mods into category subdirectories (also on when organize_by_category is set)")]
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
            // One lookup for the run, so each backend's subscriptions are listed once.
            var lookup = new BackendLibraryGameLookup(services.CreateBackendRegistry().GetAll(), services.Settings);
            var byCategory = settings.OrganizeByCategory || services.Settings.OrganizeByCategory;

            var legacyFlat = ModLibraryMigrator.LegacyFlatFolders(services.Settings);
            if (ModLibraryMigrator.NeedsMigration(services.Settings.ModsDirectory, legacyFlat))
            {
                LiveProgressDisplay.ShowInfo("Moving downloads into <backend>/<game> folders...");
                var migration = await new ModLibraryMigrator(
                        lookup,
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
                    gameDomainPath, byCategory, cts.Token, changesets.RelocateArchiveFolderAsync);
                LiveProgressDisplay.ShowSuccess($"Renamed {renamed} mods in {d}");

                if (byCategory)
                {
                    var catRenamed = await renameService.RenameCategoryFoldersAsync(
                        gameDomainPath, cts.Token, changesets.RelocateArchiveFolderAsync);
                    LiveProgressDisplay.ShowSuccess($"Renamed {catRenamed} category folders");
                }
            }

            // GameBanana and mod.io folders are named after the mod already; they only sort.
            if (string.IsNullOrEmpty(settings.Domain))
            {
                var sorter = new ModLibrarySorter(
                    lookup, changesets.RelocateArchiveFolderAsync, services.LoggerFactory?.CreateLogger<ModLibrarySorter>());

                foreach (var game in ModLibraryLayout.ListGames(services.Settings.ModsDirectory)
                             .Where(g => g.BackendId != BackendIds.NexusMods))
                {
                    LiveProgressDisplay.ShowInfo($"Sorting {game.BackendId}/{game.Game}...");
                    var result = await sorter.SortAsync(game.BackendId, game.Path, byCategory, cts.Token);
                    LiveProgressDisplay.ShowSuccess($"Moved {result.Moved} mods in {game.BackendId}/{game.Game}");

                    foreach (var mod in result.Uncategorized)
                        LiveProgressDisplay.ShowWarning($"Left unsorted, category unknown: {mod}");
                    foreach (var error in result.Errors)
                        LiveProgressDisplay.ShowError(error);
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
