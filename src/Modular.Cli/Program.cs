using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Cli.UI;
using Modular.Core.Authentication;
using Modular.Core.Backends;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Diagnostics;
using Modular.Core.Plugins;
using Modular.Core.Profiles;
using Modular.Core.Dependencies;
using Modular.Core.RateLimiting;
using Modular.Core.Services;
using Modular.Core.Telemetry;

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

        // Login command for SSO authentication
        var loginCommand = new Command("login", "Authenticate with NexusMods via browser SSO");
        loginCommand.SetHandler(async () =>
        {
            await RunLoginCommand();
        });
        rootCommand.AddCommand(loginCommand);

        // Diagnostics command
        var diagnosticsCommand = new Command("diagnostics", "Run system diagnostics");
        var jsonOption = new Option<bool>("--json", "Output as JSON");
        diagnosticsCommand.AddOption(jsonOption);
        diagnosticsCommand.SetHandler(async (json) =>
        {
            await RunDiagnosticsCommand(json);
        }, jsonOption);

        var validateCommand = new Command("validate", "Validate a plugin manifest");
        var pluginPathArg = new Argument<string>("plugin-path", "Path to plugin directory or manifest file");
        validateCommand.AddArgument(pluginPathArg);
        validateCommand.SetHandler(async (pluginPath) =>
        {
            await RunValidatePluginCommand(pluginPath);
        }, pluginPathArg);
        diagnosticsCommand.AddCommand(validateCommand);
        rootCommand.AddCommand(diagnosticsCommand);

        // Profile command group
        var profileCommand = new Command("profile", "Manage mod profiles");

        var profileExportCommand = new Command("export", "Export a mod profile");
        var profileNameArg = new Argument<string>("name", "Profile name");
        var outputOption = new Option<string?>("--output", "Output file path");
        var formatOption = new Option<string>("--format", () => "json", "Export format (json or archive)");
        profileExportCommand.AddArgument(profileNameArg);
        profileExportCommand.AddOption(outputOption);
        profileExportCommand.AddOption(formatOption);
        profileExportCommand.SetHandler(async (name, output, format) =>
        {
            await RunProfileExportCommand(name, output, format);
        }, profileNameArg, outputOption, formatOption);
        profileCommand.AddCommand(profileExportCommand);

        var profileImportCommand = new Command("import", "Import a mod profile");
        var importPathArg = new Argument<string>("path", "Path to profile file");
        var resolveOption = new Option<bool>("--resolve", "Resolve dependencies after import");
        profileImportCommand.AddArgument(importPathArg);
        profileImportCommand.AddOption(resolveOption);
        profileImportCommand.SetHandler(async (path, resolve) =>
        {
            await RunProfileImportCommand(path, resolve);
        }, importPathArg, resolveOption);
        profileCommand.AddCommand(profileImportCommand);

        var profileListCommand = new Command("list", "List available profiles");
        profileListCommand.SetHandler(async () =>
        {
            await RunProfileListCommand();
        });
        profileCommand.AddCommand(profileListCommand);
        rootCommand.AddCommand(profileCommand);

        // Plugins command group
        var pluginsCommand = new Command("plugins", "Manage plugins");

        var pluginsListCommand = new Command("list", "List installed and available plugins");
        var marketplaceOption = new Option<bool>("--marketplace", "Show plugins from marketplace");
        pluginsListCommand.AddOption(marketplaceOption);
        pluginsListCommand.SetHandler(async (marketplace) =>
        {
            await RunPluginsListCommand(marketplace);
        }, marketplaceOption);
        pluginsCommand.AddCommand(pluginsListCommand);

        var pluginsInstallCommand = new Command("install", "Install a plugin from marketplace");
        var pluginIdArg = new Argument<string>("plugin-id", "Plugin ID to install");
        pluginsInstallCommand.AddArgument(pluginIdArg);
        pluginsInstallCommand.SetHandler(async (pluginId) =>
        {
            await RunPluginsInstallCommand(pluginId);
        }, pluginIdArg);
        pluginsCommand.AddCommand(pluginsInstallCommand);

        var pluginsUpdateCommand = new Command("update", "Check for and apply plugin updates");
        pluginsUpdateCommand.SetHandler(async () =>
        {
            await RunPluginsUpdateCommand();
        });
        pluginsCommand.AddCommand(pluginsUpdateCommand);

        var pluginsRemoveCommand = new Command("remove", "Remove an installed plugin");
        var removePluginIdArg = new Argument<string>("plugin-id", "Plugin ID to remove");
        pluginsRemoveCommand.AddArgument(removePluginIdArg);
        pluginsRemoveCommand.SetHandler(async (pluginId) =>
        {
            await RunPluginsRemoveCommand(pluginId);
        }, removePluginIdArg);
        pluginsCommand.AddCommand(pluginsRemoveCommand);
        rootCommand.AddCommand(pluginsCommand);

        // Telemetry command group
        var telemetryCommand = new Command("telemetry", "Manage telemetry data");

        var telemetrySummaryCommand = new Command("summary", "Show telemetry summary");
        var daysOption = new Option<int>("--days", () => 30, "Number of days to summarize");
        telemetrySummaryCommand.AddOption(daysOption);
        telemetrySummaryCommand.SetHandler(async (days) =>
        {
            await RunTelemetrySummaryCommand(days);
        }, daysOption);
        telemetryCommand.AddCommand(telemetrySummaryCommand);

        var telemetryExportCommand = new Command("export", "Export telemetry data");
        var telemetryOutputOption = new Option<string?>("--output", "Output file path");
        telemetryExportCommand.AddOption(telemetryOutputOption);
        telemetryExportCommand.SetHandler(async (output) =>
        {
            await RunTelemetryExportCommand(output);
        }, telemetryOutputOption);
        telemetryCommand.AddCommand(telemetryExportCommand);

        var telemetryClearCommand = new Command("clear", "Clear all telemetry data");
        telemetryClearCommand.SetHandler(async () =>
        {
            await RunTelemetryClearCommand();
        });
        telemetryCommand.AddCommand(telemetryClearCommand);
        rootCommand.AddCommand(telemetryCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task RunInteractiveMode()
    {
        while (true)
        {
            try
            {
                var (settings, rateLimiter, database, metadataCache) = await InitializeServicesMinimal();
                var registry = InitializeBackends(settings, rateLimiter, database, metadataCache, false);
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
            var (settings, rateLimiter, database, metadataCache) = await InitializeServices();
            settings.Verbose = verbose;

            var backend = new NexusModsBackend(settings, rateLimiter, database, metadataCache,
                verbose ? CreateLogger<NexusModsBackend>() : null);
            var renameService = new RenameService(settings, rateLimiter, metadataCache,
                verbose ? CreateLogger<RenameService>() : null);

            // Build download options
            var options = new DownloadOptions
            {
                DryRun = dryRun,
                Force = force,
                Filter = categories.Length > 0
                    ? new FileFilter { Categories = categories.ToList() }
                    : FileFilter.MainAndOptional,
                VerifyDownloads = settings.VerifyDownloads,
                StatusCallback = status => Console.WriteLine($"[SCAN] {status}")
            };

            // Download mods using the backend
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Phase == DownloadPhase.Downloading && p.Total > 0)
                    Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Status}");
                else if (p.Phase == DownloadPhase.Scanning)
                    Console.WriteLine($"[SCAN] {p.Status}");
            });

            await backend.DownloadModsAsync(settings.ModsDirectory, domain, options, progress, cts.Token);

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
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    static async Task RunRenameCommand(string? domain, bool organize)
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
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    static async Task RunFetchCommand(string? domain)
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
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    static async Task RunLoginCommand()
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();

            Console.WriteLine("Opening browser for NexusMods authorization...");
            Console.WriteLine("Please log in and authorize Modular when prompted.");
            Console.WriteLine();

            var ssoClient = new NexusSsoClient(
                settings.NexusApplicationSlug,
                settings.Verbose ? CreateLogger<NexusSsoClient>() : null);

            settings.NexusApiKey = await ssoClient.AuthenticateAsync();
            await configService.SaveAsync(settings);

            LiveProgressDisplay.ShowSuccess("Login successful! API key saved to config.");
        }
        catch (TimeoutException)
        {
            LiveProgressDisplay.ShowError("SSO authorization timed out.");
            Console.Error.WriteLine("You can set the API key manually:");
            Console.Error.WriteLine("  export NEXUS_API_KEY=your_key_here");
            Console.Error.WriteLine("  or add 'nexus_api_key' to ~/.config/Modular/config.json");
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

        // If no API key and SSO is enabled, run the SSO flow
        if (string.IsNullOrWhiteSpace(settings.NexusApiKey) && settings.NexusSsoEnabled)
        {
            Console.WriteLine("No NexusMods API key found. Starting browser authorization...");
            Console.WriteLine("A browser window will open. Please log in and authorize Modular.");
            Console.WriteLine();

            var ssoClient = new NexusSsoClient(
                settings.NexusApplicationSlug,
                settings.Verbose ? CreateLogger<NexusSsoClient>() : null);

            try
            {
                settings.NexusApiKey = await ssoClient.AuthenticateAsync();
                await configService.SaveAsync(settings);
                LiveProgressDisplay.ShowSuccess("Authorization successful! API key saved to config.");
            }
            catch (TimeoutException)
            {
                LiveProgressDisplay.ShowError("SSO authorization timed out.");
                Console.Error.WriteLine("You can set the API key manually:");
                Console.Error.WriteLine("  export NEXUS_API_KEY=your_key_here");
                Console.Error.WriteLine("  or add 'nexus_api_key' to ~/.config/Modular/config.json");
                throw;
            }
        }

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
        ModMetadataCache metadataCache,
        bool verbose)
    {
        var registry = new BackendRegistry();

        if (settings.EnabledBackends.Contains("nexusmods", StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new NexusModsBackend(
                settings,
                rateLimiter,
                database,
                metadataCache,
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
            var (settings, rateLimiter, database, metadataCache) = await InitializeServicesMinimal();
            settings.Verbose = verbose;

            var registry = InitializeBackends(settings, rateLimiter, database, metadataCache, verbose);

            // Determine which backends to use
            IEnumerable<Modular.Core.Backends.IModBackend> backends;
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
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Execute download for a single backend.
    /// </summary>
    static async Task RunBackendDownload(
        Modular.Core.Backends.IModBackend backend,
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

    // Static logger factory kept alive for the duration of the application
    // to avoid disposing it while loggers are still in use
    private static readonly Lazy<ILoggerFactory> _loggerFactory = new(() =>
        LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        }));

    static ILogger<T>? CreateLogger<T>()
    {
        return _loggerFactory.Value.CreateLogger<T>();
    }

    // ============ Diagnostics Commands ============

    static async Task RunDiagnosticsCommand(bool asJson)
    {
        try
        {
            var pluginLoader = new PluginLoader();
            var diagnosticService = new DiagnosticService(pluginLoader, CreateLogger<DiagnosticService>());

            LiveProgressDisplay.ShowInfo("Running system diagnostics...");
            var healthReport = await diagnosticService.RunHealthCheckAsync();
            var report = diagnosticService.GenerateReport();

            if (asJson)
            {
                var output = new
                {
                    Health = healthReport,
                    Report = report
                };
                Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                // Display health report
                Console.WriteLine();
                Console.WriteLine($"=== System Health: {healthReport.OverallStatus} ===");
                Console.WriteLine();

                foreach (var check in healthReport.Checks)
                {
                    var statusIcon = check.Status switch
                    {
                        HealthStatus.Healthy => "[OK]",
                        HealthStatus.Degraded => "[WARN]",
                        HealthStatus.Unhealthy => "[FAIL]",
                        _ => "[??]"
                    };
                    Console.WriteLine($"  {statusIcon} {check.Name}: {check.Message}");
                }

                Console.WriteLine();
                Console.WriteLine($"=== System Information ===");
                Console.WriteLine($"  Host Version: {report.HostVersion}");
                Console.WriteLine($"  Runtime: {report.Runtime}");
                Console.WriteLine($"  Platform: {report.Platform}");
                Console.WriteLine($"  Working Directory: {report.WorkingDirectory}");
                Console.WriteLine();
                Console.WriteLine($"=== Components ===");
                Console.WriteLine($"  Plugins: {report.LoadedPlugins.Count}");
                Console.WriteLine($"  Installers: {report.TotalInstallers}");
                Console.WriteLine($"  Enrichers: {report.TotalEnrichers}");
                Console.WriteLine($"  UI Extensions: {report.TotalUiExtensions}");

                if (report.LoadedPlugins.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== Loaded Plugins ===");
                    foreach (var plugin in report.LoadedPlugins)
                    {
                        Console.WriteLine($"  - {plugin.DisplayName} v{plugin.Version} ({plugin.Id})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Diagnostics failed: {ex.Message}");
        }
    }

    static async Task RunValidatePluginCommand(string pluginPath)
    {
        try
        {
            var pluginLoader = new PluginLoader();
            var diagnosticService = new DiagnosticService(pluginLoader, CreateLogger<DiagnosticService>());

            // If path is a directory, look for plugin.json
            var manifestPath = pluginPath;
            if (Directory.Exists(pluginPath))
            {
                manifestPath = Path.Combine(pluginPath, "plugin.json");
            }

            LiveProgressDisplay.ShowInfo($"Validating: {manifestPath}");
            var result = diagnosticService.ValidatePlugin(manifestPath);

            if (result.IsValid)
            {
                LiveProgressDisplay.ShowSuccess($"Plugin manifest is valid");
                if (result.Manifest != null)
                {
                    Console.WriteLine($"  ID: {result.Manifest.Id}");
                    Console.WriteLine($"  Name: {result.Manifest.DisplayName}");
                    Console.WriteLine($"  Version: {result.Manifest.Version}");
                    Console.WriteLine($"  Entry: {result.Manifest.EntryAssembly}");
                }
            }
            else
            {
                LiveProgressDisplay.ShowError("Validation failed:");
                foreach (var error in result.Errors)
                    Console.WriteLine($"  [ERROR] {error}");
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("Warnings:");
                foreach (var warning in result.Warnings)
                    Console.WriteLine($"  [WARN] {warning}");
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Validation failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    // ============ Profile Commands ============

    static async Task RunProfileExportCommand(string name, string? outputPath, string format)
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();
            var profileExporter = new ProfileExporter(CreateLogger<ProfileExporter>());

            // Create a profile from the current mod library
            var profile = new ModProfile
            {
                Name = name,
                Description = $"Exported on {DateTime.Now:yyyy-MM-dd HH:mm}",
                CreatedAt = DateTime.UtcNow,
                Mods = new List<ProfileMod>()
            };

            // Scan the mods directory for installed mods
            if (Directory.Exists(settings.ModsDirectory))
            {
                foreach (var gameDir in Directory.GetDirectories(settings.ModsDirectory))
                {
                    var gameDomain = Path.GetFileName(gameDir);
                    foreach (var modDir in Directory.GetDirectories(gameDir))
                    {
                        var modName = Path.GetFileName(modDir);
                        profile.Mods.Add(new ProfileMod
                        {
                            CanonicalId = $"{gameDomain}/{modName}",
                            DisplayName = modName,
                            Enabled = true
                        });
                    }
                }
            }

            var lockfile = new ModLockfile
            {
                GeneratedAt = DateTime.UtcNow,
                Mods = profile.Mods.ToDictionary(
                    m => m.CanonicalId,
                    m => new LockfileMod { Version = "unknown" }
                )
            };

            // Determine output path
            var extension = format.ToLowerInvariant() == "archive" ? ".zip" : ".json";
            outputPath ??= Path.Combine(Environment.CurrentDirectory, $"{name}{extension}");

            var exportFormat = format.ToLowerInvariant() == "archive" ? ExportFormat.Archive : ExportFormat.Json;
            var options = new ExportOptions { Format = exportFormat };

            var result = await profileExporter.ExportProfileAsync(profile, lockfile, outputPath, options);

            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess($"Exported profile to: {result.OutputPath}");
                Console.WriteLine($"  Mods: {profile.Mods.Count}");
                Console.WriteLine($"  Size: {result.FileSize} bytes");
            }
            else
            {
                LiveProgressDisplay.ShowError($"Export failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Export failed: {ex.Message}");
        }
    }

    static async Task RunProfileImportCommand(string path, bool resolve)
    {
        try
        {
            var profileExporter = new ProfileExporter(CreateLogger<ProfileExporter>());

            LiveProgressDisplay.ShowInfo($"Importing profile from: {path}");
            var result = await profileExporter.ImportProfileAsync(path);

            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess($"Imported profile: {result.Profile?.Name}");
                Console.WriteLine($"  Mods: {result.Profile?.Mods.Count ?? 0}");

                if (result.ValidationWarnings.Count > 0)
                {
                    Console.WriteLine("Warnings:");
                    foreach (var warning in result.ValidationWarnings)
                        Console.WriteLine($"  [WARN] {warning}");
                }

                if (resolve)
                {
                    LiveProgressDisplay.ShowInfo("Dependency resolution not yet implemented");
                }
            }
            else
            {
                LiveProgressDisplay.ShowError($"Import failed: {result.Error}");
                if (result.ValidationErrors.Count > 0)
                {
                    foreach (var error in result.ValidationErrors)
                        Console.WriteLine($"  [ERROR] {error}");
                }
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Import failed: {ex.Message}");
        }
    }

    static async Task RunProfileListCommand()
    {
        try
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "profiles");

            if (!Directory.Exists(configDir))
            {
                LiveProgressDisplay.ShowInfo("No profiles found");
                return;
            }

            var profiles = Directory.GetFiles(configDir, "*.json")
                .Concat(Directory.GetFiles(configDir, "*.zip"))
                .Concat(Directory.GetFiles(configDir, "*.modpack"));

            if (!profiles.Any())
            {
                LiveProgressDisplay.ShowInfo("No profiles found");
                return;
            }

            Console.WriteLine("Available profiles:");
            foreach (var profile in profiles)
            {
                var name = Path.GetFileNameWithoutExtension(profile);
                var size = new FileInfo(profile).Length;
                Console.WriteLine($"  - {name} ({size} bytes)");
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to list profiles: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    // ============ Plugins Commands ============

    static async Task RunPluginsListCommand(bool showMarketplace)
    {
        try
        {
            var pluginLoader = new PluginLoader();
            var manifests = pluginLoader.DiscoverPlugins();

            Console.WriteLine("Installed plugins:");
            if (manifests.Count == 0)
            {
                Console.WriteLine("  (none)");
            }
            else
            {
                foreach (var manifest in manifests)
                {
                    Console.WriteLine($"  - {manifest.DisplayName} v{manifest.Version} ({manifest.Id})");
                    if (!string.IsNullOrEmpty(manifest.Description))
                        Console.WriteLine($"      {manifest.Description}");
                }
            }

            if (showMarketplace)
            {
                var configService = new ConfigurationService();
                var settings = await configService.LoadAsync();

                if (string.IsNullOrEmpty(settings.PluginMarketplaceUrl))
                {
                    LiveProgressDisplay.ShowWarning("No marketplace URL configured");
                    Console.WriteLine("Set 'plugin_marketplace_url' in config to enable marketplace");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Marketplace plugins:");
                    using var httpClient = new HttpClient();
                    var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, CreateLogger<PluginMarketplace>());
                    var index = await marketplace.FetchIndexAsync(settings.PluginMarketplaceUrl);

                    if (index == null || index.Plugins.Count == 0)
                    {
                        Console.WriteLine("  (none available)");
                    }
                    else
                    {
                        foreach (var plugin in index.Plugins)
                        {
                            var installed = manifests.Any(m => m.Id == plugin.Id);
                            var status = installed ? "[installed]" : "";
                            Console.WriteLine($"  - {plugin.Name} v{plugin.Version} ({plugin.Id}) {status}");
                            if (!string.IsNullOrEmpty(plugin.Description))
                                Console.WriteLine($"      {plugin.Description}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to list plugins: {ex.Message}");
        }
    }

    static async Task RunPluginsInstallCommand(string pluginId)
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();

            if (string.IsNullOrEmpty(settings.PluginMarketplaceUrl))
            {
                LiveProgressDisplay.ShowError("No marketplace URL configured");
                Console.WriteLine("Set 'plugin_marketplace_url' in config to enable marketplace");
                return;
            }

            using var httpClient = new HttpClient();
            var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, CreateLogger<PluginMarketplace>());

            LiveProgressDisplay.ShowInfo($"Fetching marketplace index...");
            var index = await marketplace.FetchIndexAsync(settings.PluginMarketplaceUrl);

            if (index == null)
            {
                LiveProgressDisplay.ShowError("Failed to fetch marketplace index");
                return;
            }

            var plugin = index.Plugins.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin == null)
            {
                LiveProgressDisplay.ShowError($"Plugin not found: {pluginId}");
                return;
            }

            LiveProgressDisplay.ShowInfo($"Installing {plugin.Name} v{plugin.Version}...");
            var progress = new Progress<double>(p => Console.Write($"\rDownloading: {p:F0}%"));
            var result = await marketplace.InstallPluginAsync(plugin, progress);

            Console.WriteLine();
            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess($"Installed {plugin.Name} to {result.InstalledPath}");
            }
            else
            {
                LiveProgressDisplay.ShowError($"Installation failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Installation failed: {ex.Message}");
        }
    }

    static async Task RunPluginsUpdateCommand()
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();

            if (string.IsNullOrEmpty(settings.PluginMarketplaceUrl))
            {
                LiveProgressDisplay.ShowError("No marketplace URL configured");
                return;
            }

            var pluginLoader = new PluginLoader();
            var installed = pluginLoader.DiscoverPlugins();

            using var httpClient = new HttpClient();
            var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, CreateLogger<PluginMarketplace>());

            LiveProgressDisplay.ShowInfo("Checking for updates...");
            var index = await marketplace.FetchIndexAsync(settings.PluginMarketplaceUrl);

            if (index == null)
            {
                LiveProgressDisplay.ShowError("Failed to fetch marketplace index");
                return;
            }

            var updates = await marketplace.CheckUpdatesAsync(index, installed);

            if (updates.Count == 0)
            {
                LiveProgressDisplay.ShowSuccess("All plugins are up to date");
                return;
            }

            Console.WriteLine("Available updates:");
            foreach (var update in updates)
            {
                Console.WriteLine($"  - {update.PluginName}: {update.CurrentVersion} -> {update.AvailableVersion}");
            }

            Console.Write("Install updates? [y/N]: ");
            var response = Console.ReadLine();
            if (response?.ToLowerInvariant() == "y")
            {
                foreach (var update in updates)
                {
                    if (update.IndexEntry == null)
                    {
                        LiveProgressDisplay.ShowError($"Missing index entry for {update.PluginName}");
                        continue;
                    }
                    LiveProgressDisplay.ShowInfo($"Updating {update.PluginName}...");
                    var result = await marketplace.InstallPluginAsync(update.IndexEntry);
                    if (result.Success)
                        LiveProgressDisplay.ShowSuccess($"Updated {update.PluginName}");
                    else
                        LiveProgressDisplay.ShowError($"Failed to update {update.PluginName}: {result.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Update check failed: {ex.Message}");
        }
    }

    static async Task RunPluginsRemoveCommand(string pluginId)
    {
        try
        {
            using var httpClient = new HttpClient();
            var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, CreateLogger<PluginMarketplace>());

            LiveProgressDisplay.ShowInfo($"Removing plugin: {pluginId}");
            var success = await marketplace.UninstallPluginAsync(pluginId);

            if (success)
            {
                LiveProgressDisplay.ShowSuccess($"Removed plugin: {pluginId}");
            }
            else
            {
                LiveProgressDisplay.ShowError($"Plugin not found or could not be removed: {pluginId}");
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Remove failed: {ex.Message}");
        }
    }

    // ============ Telemetry Commands ============

    static async Task RunTelemetrySummaryCommand(int days)
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();

            var telemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "telemetry");

            var telemetryService = new TelemetryService(telemetryPath,
                new TelemetryConfig { Enabled = settings.TelemetryEnabled },
                CreateLogger<TelemetryService>());

            var startDate = DateTime.UtcNow.AddDays(-days);
            var summary = telemetryService.GetSummary(startDate, DateTime.UtcNow);

            Console.WriteLine($"Telemetry Summary (last {days} days)");
            Console.WriteLine($"================================");
            Console.WriteLine($"Total events: {summary.TotalEvents}");
            Console.WriteLine($"Downloads: {summary.TotalDownloads}");
            Console.WriteLine($"Bytes downloaded: {summary.TotalBytesDownloaded:N0}");
            Console.WriteLine($"Installer successes: {summary.InstallerSuccesses}");
            Console.WriteLine($"Installer failures: {summary.InstallerFailures}");
            Console.WriteLine($"Plugin crashes: {summary.PluginCrashes}");

            if (summary.EventsByType.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Events by type:");
                foreach (var (type, count) in summary.EventsByType)
                    Console.WriteLine($"  {type}: {count}");
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to get summary: {ex.Message}");
        }
    }

    static async Task RunTelemetryExportCommand(string? outputPath)
    {
        try
        {
            var telemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "telemetry");

            outputPath ??= Path.Combine(Environment.CurrentDirectory, $"telemetry-export-{DateTime.Now:yyyyMMdd}.json");

            if (!Directory.Exists(telemetryPath))
            {
                LiveProgressDisplay.ShowInfo("No telemetry data to export");
                return;
            }

            // Export all telemetry files
            var allData = new List<object>();
            foreach (var file in Directory.GetFiles(telemetryPath, "*.json"))
            {
                var json = await File.ReadAllTextAsync(file);
                var data = JsonSerializer.Deserialize<object>(json);
                if (data != null)
                    allData.Add(data);
            }

            var exportJson = JsonSerializer.Serialize(allData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(outputPath, exportJson);

            LiveProgressDisplay.ShowSuccess($"Exported telemetry to: {outputPath}");
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Export failed: {ex.Message}");
        }
    }

    static async Task RunTelemetryClearCommand()
    {
        try
        {
            Console.Write("Are you sure you want to delete all telemetry data? [y/N]: ");
            var response = Console.ReadLine();
            if (response?.ToLowerInvariant() != "y")
            {
                LiveProgressDisplay.ShowInfo("Cancelled");
                return;
            }

            var telemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "telemetry");

            var telemetryService = new TelemetryService(telemetryPath);
            telemetryService.ClearData();

            LiveProgressDisplay.ShowSuccess("Telemetry data cleared");
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to clear telemetry: {ex.Message}");
        }

        await Task.CompletedTask;
    }
}
