using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modular.Core.Authentication;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Diagnostics;
using Modular.Core.Plugins;
using Modular.Core.Profiles;
using Modular.Core.RateLimiting;
using Modular.Core.Services;
using Modular.Core.Telemetry;

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

        return new RuntimeServices
        {
            Settings = settings,
            RateLimiter = rateLimiter,
            Database = database,
            MetadataCache = metadataCache,
            ConfigService = configService,
            LoggerFactory = loggerFactory
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

            settings.NexusApiKey = await ssoClient.AuthenticateAsync();
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

        return new RuntimeServices
        {
            Settings = settings,
            RateLimiter = rateLimiter,
            Database = database,
            MetadataCache = metadataCache,
            ConfigService = configService,
            LoggerFactory = loggerFactory
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

        if (Settings.EnabledBackends.Contains("nexusmods", StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new NexusModsBackend(
                Settings,
                RateLimiter,
                Database,
                MetadataCache,
                LoggerFactory?.CreateLogger<NexusModsBackend>()));
        }

        if (Settings.EnabledBackends.Contains("gamebanana", StringComparer.OrdinalIgnoreCase))
        {
            registry.Register(new GameBananaBackend(
                Settings,
                LoggerFactory?.CreateLogger<GameBananaBackend>()));
        }

        return registry;
    }

    public void Dispose()
    {
        LoggerFactory?.Dispose();
    }
}
