using System.CommandLine;
using Microsoft.Extensions.Logging;
using Modular.Cli.UI;
using Modular.Core.Backends;
using Modular.Core.Backends.Common;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Modular.Core.Services;

// Legacy commands still use old services for backward compatibility
// TODO: Migrate remaining commands to use backend system
#pragma warning disable CS0618 // Type or member is obsolete

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

        var gameBananaCommand = new Command("gamebanana", "Download mods from GameBanana (legacy, use 'download --backend gamebanana' instead)");
        gameBananaCommand.SetHandler(async () =>
        {
            await RunGameBananaCommand();
        });
        rootCommand.AddCommand(gameBananaCommand);

        // Generic download command - works with any backend
        var downloadCommand = new Command("download", "Download mods from a backend");
        var backendOption = new Option<string?>("--backend", "Backend to use (e.g., nexusmods, gamebanana)");
        var allBackendsOption = new Option<bool>("--all", "Download from all configured backends");
        var downloadDomainArg = new Argument<string?>("domain", () => null, "Game domain (required for some backends)");
        downloadCommand.AddArgument(downloadDomainArg);
        downloadCommand.AddOption(backendOption);
        downloadCommand.AddOption(allBackendsOption);
        downloadCommand.AddOption(dryRunOption);
        downloadCommand.AddOption(forceOption);
        downloadCommand.AddOption(categoriesOption);
        downloadCommand.AddOption(verboseOption);
        downloadCommand.SetHandler(async (domain, backend, all, dryRun, force, categories, verbose) =>
        {
            await RunDownloadCommand(domain, backend, all, dryRun, force, categories, verbose);
        }, downloadDomainArg, backendOption, allBackendsOption, dryRunOption, forceOption, categoriesOption, verboseOption);
        rootCommand.AddCommand(downloadCommand);

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
            try
            {
                var (settings, rateLimiter, database, metadataCache) = await InitializeServicesMinimal();
                var registry = InitializeBackends(settings, rateLimiter, database, false);
                var configured = registry.GetConfigured();

                // Build menu dynamically from configured backends + Rename option
                var menuItems = configured.Select(b => b.DisplayName).Append("Rename").ToArray();
                var choice = LiveProgressDisplay.ShowNumberedMenu("Modular", menuItems);

                if (choice == 0)
                    return;

                if (choice <= configured.Count)
                {
                    // Selected a backend
                    var backend = configured[choice - 1];
                    string? gameDomain = null;

                    // Prompt for game domain if backend requires it
                    if (backend.Capabilities.HasFlag(BackendCapabilities.GameDomains))
                    {
                        gameDomain = LiveProgressDisplay.AskString("Enter game domain (e.g., stardewvalley):");
                        if (string.IsNullOrWhiteSpace(gameDomain))
                        {
                            LiveProgressDisplay.ShowWarning("Game domain is required for this backend.");
                            continue;
                        }
                    }

                    await RunBackendDownload(backend, settings, gameDomain, new DownloadOptions
                    {
                        DryRun = false,
                        Force = false,
                        OrganizeByCategory = true,
                        AutoRename = settings.AutoRename,
                        Filter = FileFilter.MainAndOptional
                    });

                    // Auto-rename for NexusMods if enabled
                    if (backend.Id == "nexusmods" && settings.AutoRename && !string.IsNullOrEmpty(gameDomain))
                    {
                        var renameService = new RenameService(settings, rateLimiter, metadataCache, null);
                        LiveProgressDisplay.ShowInfo($"Auto-organizing and renaming mods in {gameDomain}...");
                        var gameDomainPath = Path.Combine(settings.ModsDirectory, gameDomain);
                        var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, true, CancellationToken.None);
                        LiveProgressDisplay.ShowSuccess($"Successfully processed {renamed} mods in {gameDomain}");
                    }

                    await rateLimiter.SaveStateAsync(settings.RateLimitStatePath);
                    await metadataCache.SaveAsync();
                }
                else if (choice == configured.Count + 1)
                {
                    // Rename option
                    var renameDomain = LiveProgressDisplay.AskString("Enter game domain to rename:");
                    await RunRenameCommand(renameDomain, true);
                }
            }
            catch (Exception ex)
            {
                LiveProgressDisplay.ShowError(ex.Message);
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

    /// <summary>
    /// Initialize services without requiring NexusMods API key validation.
    /// Used for interactive mode where we determine backend at runtime.
    /// </summary>
    static async Task<(AppSettings settings, NexusRateLimiter rateLimiter, DownloadDatabase database, ModMetadataCache metadataCache)> InitializeServicesMinimal()
    {
        var configService = new ConfigurationService();
        var settings = await configService.LoadAsync();
        // Don't validate - let individual backends validate their own config

        var rateLimiter = new NexusRateLimiter(settings.Verbose ? CreateLogger<NexusRateLimiter>() : null);
        await rateLimiter.LoadStateAsync(settings.RateLimitStatePath);

        var database = new DownloadDatabase(settings.DatabasePath);
        await database.LoadAsync();

        var metadataCache = new ModMetadataCache(settings.MetadataCachePath);
        await metadataCache.LoadAsync();

        return (settings, rateLimiter, database, metadataCache);
    }

    /// <summary>
    /// Initialize the backend registry with enabled backends.
    /// </summary>
    static BackendRegistry InitializeBackends(
        AppSettings settings,
        NexusRateLimiter rateLimiter,
        DownloadDatabase database,
        bool verbose)
    {
        var registry = new BackendRegistry();

        if (settings.EnabledBackends.Contains("nexusmods", StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new NexusModsBackend(
                settings,
                rateLimiter,
                database,
                verbose ? CreateLogger<NexusModsBackend>() : null));
        }

        if (settings.EnabledBackends.Contains("gamebanana", StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new GameBananaBackend(
                settings,
                verbose ? CreateLogger<GameBananaBackend>() : null));
        }

        return registry;
    }

    /// <summary>
    /// Generic download command handler.
    /// </summary>
    static async Task RunDownloadCommand(
        string? domain,
        string? backendId,
        bool all,
        bool dryRun,
        bool force,
        string[] categories,
        bool verbose)
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
            var (settings, rateLimiter, database, metadataCache) = await InitializeServicesMinimal();
            settings.Verbose = verbose;

            var registry = InitializeBackends(settings, rateLimiter, database, verbose);

            // Determine which backends to use
            IEnumerable<IModBackend> backends;
            if (all)
            {
                backends = registry.GetConfigured();
                if (!backends.Any())
                {
                    LiveProgressDisplay.ShowError("No backends are properly configured.");
                    var errors = registry.GetAllConfigurationErrors();
                    foreach (var (id, errs) in errors)
                        LiveProgressDisplay.ShowWarning($"  {id}: {string.Join(", ", errs)}");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(backendId))
            {
                var backend = registry.Get(backendId);
                if (backend == null)
                {
                    LiveProgressDisplay.ShowError($"Unknown backend: {backendId}");
                    LiveProgressDisplay.ShowInfo($"Available backends: {string.Join(", ", registry.GetIds())}");
                    return;
                }
                backends = [backend];
            }
            else
            {
                // Interactive selection
                var configured = registry.GetConfigured();
                if (!configured.Any())
                {
                    LiveProgressDisplay.ShowError("No backends are properly configured.");
                    return;
                }

                var menuItems = configured.Select(b => b.DisplayName).ToArray();
                var choice = LiveProgressDisplay.ShowNumberedMenu("Select backend", menuItems);
                if (choice == 0) return;
                backends = [configured[choice - 1]];
            }

            // Build download options
            var options = new DownloadOptions
            {
                DryRun = dryRun,
                Force = force,
                Filter = categories.Length > 0
                    ? new FileFilter { Categories = categories.ToList() }
                    : FileFilter.MainAndOptional,
                VerifyDownloads = settings.VerifyDownloads,
                AutoRename = settings.AutoRename,
                OrganizeByCategory = settings.OrganizeByCategory,
                StatusCallback = status => Console.WriteLine($"[SCAN] {status}")
            };

            foreach (var backend in backends)
            {
                // Validate backend configuration
                var errors = backend.ValidateConfiguration();
                if (errors.Count > 0)
                {
                    LiveProgressDisplay.ShowWarning($"Skipping {backend.DisplayName}: {string.Join(", ", errors)}");
                    continue;
                }

                // Determine game domain
                var gameDomain = domain;
                if (backend.Capabilities.HasFlag(BackendCapabilities.GameDomains) && string.IsNullOrEmpty(gameDomain))
                {
                    gameDomain = LiveProgressDisplay.AskString($"Enter game domain for {backend.DisplayName} (e.g., stardewvalley):");
                    if (string.IsNullOrWhiteSpace(gameDomain))
                    {
                        LiveProgressDisplay.ShowWarning($"Skipping {backend.DisplayName}: game domain is required.");
                        continue;
                    }
                }

                LiveProgressDisplay.ShowInfo($"Downloading from {backend.DisplayName}...");
                await RunBackendDownload(backend, settings, gameDomain, options, cts.Token);

                // Auto-rename for NexusMods if enabled
                if (backend.Id == "nexusmods" && settings.AutoRename && !dryRun && !string.IsNullOrEmpty(gameDomain))
                {
                    var renameService = new RenameService(settings, rateLimiter, metadataCache,
                        verbose ? CreateLogger<RenameService>() : null);
                    LiveProgressDisplay.ShowInfo($"Auto-organizing and renaming mods in {gameDomain}...");
                    var gameDomainPath = Path.Combine(settings.ModsDirectory, gameDomain);
                    var renamed = await renameService.ReorganizeAndRenameModsAsync(gameDomainPath, settings.OrganizeByCategory, cts.Token);
                    LiveProgressDisplay.ShowSuccess($"Successfully processed {renamed} mods in {gameDomain}");

                    var catRenamed = await renameService.RenameCategoryFoldersAsync(gameDomainPath, cts.Token);
                    LiveProgressDisplay.ShowSuccess($"Renamed {catRenamed} category folders");
                }
            }

            await rateLimiter.SaveStateAsync(settings.RateLimitStatePath);
            await metadataCache.SaveAsync();
            LiveProgressDisplay.ShowSuccess("Download complete.");
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

    /// <summary>
    /// Execute download for a single backend.
    /// </summary>
    static async Task RunBackendDownload(
        IModBackend backend,
        AppSettings settings,
        string? gameDomain,
        DownloadOptions options,
        CancellationToken ct = default)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Phase == DownloadPhase.Downloading && p.Total > 0)
                Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Status}");
            else if (p.Phase == DownloadPhase.Scanning)
                Console.WriteLine($"[SCAN] {p.Status}");
        });

        await backend.DownloadModsAsync(
            settings.ModsDirectory,
            gameDomain,
            options,
            progress,
            ct);
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
