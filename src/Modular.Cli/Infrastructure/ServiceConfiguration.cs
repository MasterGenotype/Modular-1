using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modular.Core.Authentication;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.ModIo;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Dependencies;
using Modular.Core.Diagnostics;
using Modular.Core.Downloads;
using Modular.Core.Plugins;
using Modular.Core.Profiles;
using Modular.Core.RateLimiting;
using Modular.Core.Services;
using Modular.Core.Telemetry;
using Modular.Sdk.Backends;
using Modular.Core.Installers;
using Modular.Core.Metadata;

namespace Modular.Cli.Infrastructure;

/// <summary>
/// Configures services for CLI dependency injection.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Registers all CLI services with the DI container.
    /// </summary>
    public static IServiceCollection ConfigureServices(this IServiceCollection services, bool verbose = false)
    {
        // Logging
        services.AddLogging(builder =>
        {
            if (verbose)
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            }
            else
            {
                builder.SetMinimumLevel(LogLevel.Warning);
            }
        });

        // Core services (singletons loaded lazily)
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<PluginLoader>();

        // Services that require async initialization are handled in commands
        // since Spectre.Console.Cli doesn't support async service resolution

        return services;
    }

    /// <summary>
    /// Creates a logger factory for manual logger creation.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(bool verbose)
    {
        return LoggerFactory.Create(builder =>
        {
            if (verbose)
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            }
            else
            {
                builder.SetMinimumLevel(LogLevel.Warning);
            }
        });
    }
}

/// <summary>
/// Holds runtime state that requires async initialization.
/// Created by commands that need these services.
/// </summary>
public sealed class RuntimeServices : IDisposable
{
    public required AppSettings Settings { get; init; }
    public required NexusRateLimiter RateLimiter { get; init; }
    public required DownloadDatabase Database { get; init; }
    public required ModMetadataCache MetadataCache { get; init; }
    public required ConfigurationService ConfigService { get; init; }
    public ILoggerFactory? LoggerFactory { get; init; }
    public TelemetryService Telemetry { get; init; } = null!;

    private ResumableDownloader? _downloader;

    /// <summary>
    /// Shared resumable downloader used by all backends.
    /// </summary>
    public ResumableDownloader Downloader =>
        _downloader ??= new ResumableDownloader(logger: LoggerFactory?.CreateLogger<ResumableDownloader>());

    /// <summary>
    /// Initializes runtime services without requiring NexusMods API key.
    /// </summary>
    public static async Task<RuntimeServices> InitializeMinimalAsync(bool verbose = false)
    {
        var configService = new ConfigurationService();
        var settings = await configService.LoadAsync();
        settings.Verbose = verbose;

        var loggerFactory = verbose ? ServiceConfiguration.CreateLoggerFactory(verbose) : null;

        var rateLimiter = new NexusRateLimiter(loggerFactory?.CreateLogger<NexusRateLimiter>());
        await rateLimiter.LoadStateAsync(settings.RateLimitStatePath);

        var database = new DownloadDatabase(settings.DatabasePath);
        await database.LoadAsync();

        var metadataCache = new ModMetadataCache(settings.MetadataCachePath);
        await metadataCache.LoadAsync();

        var telemetry = new TelemetryService(
            settings.TelemetryPath,
            new TelemetryConfig { Enabled = settings.TelemetryEnabled },
            loggerFactory?.CreateLogger<TelemetryService>());

        return new RuntimeServices
        {
            Settings = settings,
            RateLimiter = rateLimiter,
            Database = database,
            MetadataCache = metadataCache,
            ConfigService = configService,
            LoggerFactory = loggerFactory,
            Telemetry = telemetry
        };
    }

    /// <summary>
    /// Initializes runtime services with NexusMods API key validation/SSO.
    /// </summary>
    public static async Task<RuntimeServices> InitializeAsync(bool verbose = false)
    {
        var configService = new ConfigurationService();
        var settings = await configService.LoadAsync();
        settings.Verbose = verbose;

        var loggerFactory = verbose ? ServiceConfiguration.CreateLoggerFactory(verbose) : null;

        // If no API key and SSO is enabled, run the SSO flow
        if (string.IsNullOrWhiteSpace(settings.NexusApiKey) && settings.NexusSsoEnabled)
        {
            Console.WriteLine("No NexusMods API key found. Starting browser authorization...");
            Console.WriteLine("A browser window will open. Please log in and authorize Modular.");
            Console.WriteLine();

            var ssoClient = new NexusSsoClient(
                settings.NexusApplicationSlug,
                loggerFactory?.CreateLogger<NexusSsoClient>());

            settings.NexusApiKey = await ssoClient.AuthenticateAsync(
                url => Console.WriteLine($"If the browser didn't open, visit: {url}"));
            await configService.SaveAsync(settings);
            Console.WriteLine("Authorization successful! API key saved to config.");
        }

        configService.Validate(settings, requireNexusKey: true);

        var rateLimiter = new NexusRateLimiter(loggerFactory?.CreateLogger<NexusRateLimiter>());
        await rateLimiter.LoadStateAsync(settings.RateLimitStatePath);

        var database = new DownloadDatabase(settings.DatabasePath);
        await database.LoadAsync();

        var metadataCache = new ModMetadataCache(settings.MetadataCachePath);
        await metadataCache.LoadAsync();

        var telemetry = new TelemetryService(
            settings.TelemetryPath,
            new TelemetryConfig { Enabled = settings.TelemetryEnabled },
            loggerFactory?.CreateLogger<TelemetryService>());

        return new RuntimeServices
        {
            Settings = settings,
            RateLimiter = rateLimiter,
            Database = database,
            MetadataCache = metadataCache,
            ConfigService = configService,
            LoggerFactory = loggerFactory,
            Telemetry = telemetry
        };
    }

    /// <summary>
    /// Saves state for all services that require persistence.
    /// </summary>
    public async Task SaveStateAsync()
    {
        await RateLimiter.SaveStateAsync(Settings.RateLimitStatePath);
        await MetadataCache.SaveAsync();
    }

    /// <summary>
    /// Creates a backend registry with configured backends.
    /// </summary>
    public BackendRegistry CreateBackendRegistry()
    {
        var registry = new BackendRegistry();

        if (Settings.EnabledBackends.Contains(BackendIds.NexusMods, StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new NexusModsBackend(
                Settings,
                RateLimiter,
                Database,
                MetadataCache,
                LoggerFactory?.CreateLogger<NexusModsBackend>(),
                Downloader));
        }

        if (Settings.EnabledBackends.Contains(BackendIds.GameBanana, StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new GameBananaBackend(
                Settings,
                LoggerFactory?.CreateLogger<GameBananaBackend>(),
                Downloader));
        }

        if (Settings.EnabledBackends.Contains(BackendIds.ModIo, StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new ModIoBackend(
                Settings,
                LoggerFactory?.CreateLogger<ModIoBackend>(),
                Downloader));
        }

        return registry;
    }

    /// <summary>
    /// Creates the installation service every command shares.
    /// </summary>
    /// <param name="database">The Modular database.</param>
    /// <param name="pluginLoader">Loaded plugins, for commands that install or deploy.</param>
    /// <remarks>
    /// Built in one place so that every command plans a deployment the same way. A game
    /// module may name deployed files after a mod's hub category; a command that left the
    /// category source out would rename them on its next deployment.
    /// </remarks>
    public ModInstallationService CreateInstallationService(
        ModularDatabase database, PluginLoader? pluginLoader = null) =>
        new(
            database,
            Telemetry,
            pluginLoader: pluginLoader,
            settings: Settings,
            categorySource: CreateCategorySource());

    /// <summary>
    /// Creates the source game modules ask for a mod's hub category, or <c>null</c> when
    /// NexusMods is not enabled.
    /// </summary>
    public IModCategorySource? CreateCategorySource() =>
        CreateBackendRegistry().Get(BackendIds.NexusMods) is NexusModsBackend nexus
            ? new NexusModCategorySource(nexus, MetadataCache)
            : null;

    /// <summary>
    /// Opens the install records, so moving a downloaded archive can repoint the installed
    /// mods that came from it.
    /// </summary>
    public async Task<ChangesetManager> OpenChangesetsAsync()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "modular.db");
        var db = new ModularDatabase(dbPath);
        await db.InitializeAsync();
        return new ChangesetManager(db);
    }

    /// <summary>
    /// Sorts a GameBanana or mod.io backend's downloads into category folders, or out of
    /// them, as the settings say, keeping installed mods pointed at their archives.
    /// </summary>
    /// <returns>How many mod folders moved.</returns>
    public async Task<int> SortBackendDownloadsAsync(Modular.Sdk.Backends.IModBackend backend, CancellationToken ct)
    {
        var changesets = await OpenChangesetsAsync();
        return await ModLibrarySorter.SortBackendAsync(
            backend, Settings, Settings.ModsDirectory, changesets.RelocateArchiveFolderAsync, ct);
    }

    /// <summary>
    /// Creates an aggregate version provider with all enabled backend providers.
    /// </summary>
    public AggregateVersionProvider CreateVersionProvider(string defaultGameDomain = "")
    {
        var provider = new AggregateVersionProvider(LoggerFactory?.CreateLogger<AggregateVersionProvider>());

        if (Settings.EnabledBackends.Contains(BackendIds.NexusMods, StringComparer.OrdinalIgnoreCase))
        {
            var nexusBackend = new NexusModsBackend(
                Settings,
                RateLimiter,
                Database,
                MetadataCache,
                LoggerFactory?.CreateLogger<NexusModsBackend>());

            provider.Register(BackendIds.NexusMods, new NexusModsVersionProvider(
                nexusBackend,
                defaultGameDomain,
                LoggerFactory?.CreateLogger<NexusModsVersionProvider>()));
        }

        if (Settings.EnabledBackends.Contains(BackendIds.GameBanana, StringComparer.OrdinalIgnoreCase))
        {
            var gbBackend = new GameBananaBackend(
                Settings,
                LoggerFactory?.CreateLogger<GameBananaBackend>());

            provider.Register(BackendIds.GameBanana, new GameBananaVersionProvider(
                gbBackend,
                LoggerFactory?.CreateLogger<GameBananaVersionProvider>()));
        }

        if (Settings.EnabledBackends.Contains(BackendIds.ModIo, StringComparer.OrdinalIgnoreCase))
        {
            provider.Register(BackendIds.ModIo, new ModIoVersionProvider(
                new ModIoBackend(Settings, LoggerFactory?.CreateLogger<ModIoBackend>()),
                LoggerFactory?.CreateLogger<ModIoVersionProvider>()));
        }

        return provider;
    }

    public void Dispose()
    {
        _downloader?.Dispose();
        LoggerFactory?.Dispose();
    }
}
