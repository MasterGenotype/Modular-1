using System.CommandLine;
using Microsoft.Extensions.Logging;
using Modular.Cli.UI;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Modular.Core.Services;

namespace Modular.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Build root command
        var rootCommand = new RootCommand("Modular - Game mod download manager");

        // Domain argument
        var domainArg = new Argument<string?>("domain", () => null, "Game domain (e.g., skyrimspecialedition, stardewvalley)");

        // Options
        var categoriesOption = new Option<string[]>("--categories", "Filter by file categories (e.g., main, optional)") { AllowMultipleArgumentsPerToken = true };
        var dryRunOption = new Option<bool>("--dry-run", "Show what would be downloaded without downloading");
        var forceOption = new Option<bool>("--force", "Re-download existing files");
        var organizeOption = new Option<bool>("--organize-by-category", "Create category subdirectories");
        var verboseOption = new Option<bool>("--verbose", "Enable verbose output");

        rootCommand.AddArgument(domainArg);
        rootCommand.AddOption(categoriesOption);
        rootCommand.AddOption(dryRunOption);
        rootCommand.AddOption(forceOption);
        rootCommand.AddOption(organizeOption);
        rootCommand.AddOption(verboseOption);

        rootCommand.SetHandler(async (domain, categories, dryRun, force, organize, verbose) =>
        {
            if (string.IsNullOrEmpty(domain))
            {
                await RunInteractiveMode();
            }
            else
            {
                await RunCommandMode(domain, categories, dryRun, force, organize, verbose);
            }
        }, domainArg, categoriesOption, dryRunOption, forceOption, organizeOption, verboseOption);

        // Add subcommands
        var renameCommand = new Command("rename", "Rename mod folders to human-readable names");
        renameCommand.AddArgument(new Argument<string?>("domain", () => null, "Game domain to rename"));
        renameCommand.AddOption(organizeOption);
        renameCommand.SetHandler(async (domain, organize) =>
        {
            await RunRenameCommand(domain, organize);
        }, domainArg, organizeOption);
        rootCommand.AddCommand(renameCommand);

        var gameBananaCommand = new Command("gamebanana", "Download mods from GameBanana");
        gameBananaCommand.SetHandler(async () =>
        {
            await RunGameBananaCommand();
        });
        rootCommand.AddCommand(gameBananaCommand);

        // Fetch command to pre-populate metadata cache
        var fetchCommand = new Command("fetch", "Fetch and cache mod metadata without renaming");
        fetchCommand.AddArgument(new Argument<string?>("domain", () => null, "Game domain to fetch metadata for"));
        fetchCommand.SetHandler(async (domain) =>
        {
            await RunFetchCommand(domain);
        }, domainArg);
        rootCommand.AddCommand(fetchCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunInteractiveMode()
    {
        while (true)
        {
            var choice = LiveProgressDisplay.ShowNumberedMenu("Modular", ["NexusMods", "GameBanana", "Rename"]);

            switch (choice)
            {
                case 0:
                    return;
                case 1:
                    var domain = LiveProgressDisplay.AskString("Enter game domain (e.g., stardewvalley):");
                    await RunCommandMode(domain, [], false, false, true, false);
                    break;
                case 2:
                    await RunGameBananaCommand();
                    break;
                case 3:
                    var renameDomain = LiveProgressDisplay.AskString("Enter game domain to rename:");
                    await RunRenameCommand(renameDomain, true);
                    break;
            }
        }
    }

    static async Task RunCommandMode(string domain, string[] categories, bool dryRun, bool force, bool organize, bool verbose)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            LiveProgressDisplay.ShowWarning("Cancellation requested, cleaning up...");
        };

        try
        {
            var (settings, rateLimiter, database, metadataCache) = await InitializeServices();

            if (categories.Length > 0)
                settings.DefaultCategories = categories.ToList();
            settings.Verbose = verbose;

            var nexusService = new NexusModsService(settings, rateLimiter, database,
                verbose ? CreateLogger<NexusModsService>() : null);
            var renameService = new RenameService(settings, rateLimiter, metadataCache,
                verbose ? CreateLogger<RenameService>() : null);

            // Download mods - scanning phase with status output
            await nexusService.DownloadFilesAsync(
                domain,
                progress: new Progress<(string status, int completed, int total)>(p =>
                {
                    // Progress updates go to console during download phase
                    if (p.total > 0)
                        Console.WriteLine($"[{p.completed}/{p.total}] {p.status}");
                }),
                statusCallback: status => Console.WriteLine($"[SCAN] {status}"),
                dryRun: dryRun,
                force: force
            );

            // Auto-rename if enabled
            if (settings.AutoRename && !dryRun)
            {
                LiveProgressDisplay.ShowInfo($"Auto-organizing and renaming mods in {domain}...");
                var gameDomainPath = Path.Combine(settings.ModsDirectory, domain);
                var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, organize, cts.Token);
                LiveProgressDisplay.ShowSuccess($"Successfully processed {renamed} mods in {domain}");

                // Rename category folders
                LiveProgressDisplay.ShowInfo($"Fetching category names for {domain}...");
                var catRenamed = await renameService.RenameCategoryFoldersAsync(gameDomainPath, cts.Token);
                LiveProgressDisplay.ShowSuccess($"Renamed {catRenamed} category folders in {domain}");
            }

            // Save state
            await rateLimiter.SaveStateAsync(settings.RateLimitStatePath);
            await metadataCache.SaveAsync();
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
        }
    }

    static async Task RunRenameCommand(string? domain, bool organize)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            LiveProgressDisplay.ShowWarning("Cancellation requested, cleaning up...");
        };

        try
        {
            var (settings, rateLimiter, _, metadataCache) = await InitializeServices();

            var renameService = new RenameService(settings, rateLimiter, metadataCache, CreateLogger<RenameService>());

            IEnumerable<string> domains;
            if (!string.IsNullOrEmpty(domain))
            {
                domains = [domain];
            }
            else
            {
                domains = renameService.GetGameDomainNames();
            }

            foreach (var d in domains)
            {
                var gameDomainPath = Path.Combine(settings.ModsDirectory, d);
                if (!Directory.Exists(gameDomainPath))
                {
                    LiveProgressDisplay.ShowWarning($"Directory not found: {gameDomainPath}");
                    continue;
                }

                LiveProgressDisplay.ShowInfo($"Processing {d}...");
                var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, organize, cts.Token);
                LiveProgressDisplay.ShowSuccess($"Renamed {renamed} mods in {d}");

                var catRenamed = await renameService.RenameCategoryFoldersAsync(gameDomainPath, cts.Token);
                LiveProgressDisplay.ShowSuccess($"Renamed {catRenamed} category folders");
            }

            await rateLimiter.SaveStateAsync(settings.RateLimitStatePath);
            await metadataCache.SaveAsync();
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
        }
    }

    static async Task RunFetchCommand(string? domain)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            LiveProgressDisplay.ShowWarning("Cancellation requested, cleaning up...");
        };

        try
        {
            var (settings, rateLimiter, _, metadataCache) = await InitializeServices();
            var renameService = new RenameService(settings, rateLimiter, metadataCache, CreateLogger<RenameService>());

            IEnumerable<string> domains;
            if (!string.IsNullOrEmpty(domain))
            {
                domains = [domain];
            }
            else
            {
                domains = renameService.GetGameDomainNames();
            }

            foreach (var d in domains)
            {
                var gameDomainPath = Path.Combine(settings.ModsDirectory, d);
                if (!Directory.Exists(gameDomainPath))
                {
                    LiveProgressDisplay.ShowWarning($"Directory not found: {gameDomainPath}");
                    continue;
                }

                LiveProgressDisplay.ShowInfo($"Fetching metadata for {d}...");
                var fetched = await renameService.FetchAndCacheMetadataAsync(gameDomainPath, d, cts.Token);
                LiveProgressDisplay.ShowSuccess($"Cached metadata for {fetched} mods in {d}");
            }

            await rateLimiter.SaveStateAsync(settings.RateLimitStatePath);
            await metadataCache.SaveAsync();
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
        }
    }

    static async Task RunGameBananaCommand()
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();
            configService.Validate(settings, requireGameBananaId: true);

            var gbService = new GameBananaService(settings, CreateLogger<GameBananaService>());

            var outputDir = Path.Combine(settings.ModsDirectory, "gamebanana");

            await LiveProgressDisplay.RunWithProgressAsync("Downloading GameBanana mods", async progress =>
            {
                await gbService.DownloadAllSubscribedModsAsync(outputDir, progress);
            });

            LiveProgressDisplay.ShowSuccess("GameBanana downloads complete");
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
        }
    }

    static async Task<(AppSettings settings, NexusRateLimiter rateLimiter, DownloadDatabase database, ModMetadataCache metadataCache)> InitializeServices()
    {
        var configService = new ConfigurationService();
        var settings = await configService.LoadAsync();
        configService.Validate(settings, requireNexusKey: true);

        var rateLimiter = new NexusRateLimiter(settings.Verbose ? CreateLogger<NexusRateLimiter>() : null);
        await rateLimiter.LoadStateAsync(settings.RateLimitStatePath);

        var database = new DownloadDatabase(settings.DatabasePath);
        await database.LoadAsync();

        var metadataCache = new ModMetadataCache(settings.MetadataCachePath);
        await metadataCache.LoadAsync();

        return (settings, rateLimiter, database, metadataCache);
    }

    static ILogger<T>? CreateLogger<T>()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        return loggerFactory.CreateLogger<T>();
    }
}
