using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Authentication;
using Modular.Core.Diagnostics;
using Modular.Core.GameDetection;
using Modular.Core.Installers;
using Modular.Core.Plugins;
using Modular.Core.Profiles;
using Modular.Core.RateLimiting;
using Modular.Core.Snapshots;
using Modular.Core.Services;
using Modular.Core.Telemetry;
using Modular.Gui.Services;
using Modular.Gui.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Modular.Gui;

sealed class Program
{
    public static IServiceProvider? Services { get; private set; }

    // Cached instances loaded during async initialization
    private static AppSettings? _settings;
    private static DownloadDatabase? _database;
    private static ModMetadataCache? _metadataCache;
    private static DownloadHistoryService? _downloadHistory;

    private const string MutexName = "Global\\Modular_ModManager_SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);

        if (!mutex.WaitOne(millisecondsTimeout: 0, exitContext: false))
        {
            Console.Error.WriteLine("Modular is already running.");
            return;
        }

        try
        {
            // Run async initialization on a background thread to avoid deadlock issues
            // This ensures async operations complete before any UI context is created
            Task.Run(InitializeServicesAsync).GetAwaiter().GetResult();

            // Build DI container with pre-loaded instances
            Services = ConfigureServices();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            throw;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Performs async initialization on a background thread before the UI starts.
    /// This avoids deadlock risks from blocking async calls on the UI thread.
    /// </summary>
    private static async Task InitializeServicesAsync()
    {
        // Load configuration
        var configService = new ConfigurationService();
        _settings = await configService.LoadAsync();

        // Load database
        _database = new DownloadDatabase(_settings.DatabasePath);
        await _database.LoadAsync();

        // Load metadata cache
        var cachePath = Path.Combine(
            Path.GetDirectoryName(_settings.DatabasePath) ?? Environment.CurrentDirectory,
            "metadata_cache.json");
        _metadataCache = new ModMetadataCache(cachePath);
        await _metadataCache.LoadAsync();

        // Load download history
        var historyPath = Path.Combine(
            Path.GetDirectoryName(_settings.DatabasePath) ?? Environment.CurrentDirectory,
            "download_history.json");
        _downloadHistory = new DownloadHistoryService(historyPath);
        await _downloadHistory.LoadAsync();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Core services - use pre-loaded instances to avoid async-over-sync
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton(_settings!);
        services.AddSingleton(_database!);
        services.AddSingleton(_metadataCache!);
        services.AddSingleton<IRateLimiter, NexusRateLimiter>();

        // Backend services
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var rateLimiter = sp.GetRequiredService<IRateLimiter>();
            var database = sp.GetRequiredService<DownloadDatabase>();
            var metadataCache = sp.GetRequiredService<ModMetadataCache>();
            var logger = sp.GetService<ILogger<NexusModsBackend>>();
            return new NexusModsBackend(settings, rateLimiter, database, metadataCache, logger);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var logger = sp.GetService<ILogger<GameBananaBackend>>();
            return new GameBananaBackend(settings, logger);
        });
        services.AddSingleton(sp =>
        {
            var registry = new BackendRegistry();
            registry.Register(sp.GetRequiredService<NexusModsBackend>());
            registry.Register(sp.GetRequiredService<GameBananaBackend>());
            return registry;
        });

        // Other core services
        services.AddSingleton<IRenameService, RenameService>();

        // Plugin services
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<PluginLoader>>();
            return new PluginLoader(logger: logger);
        });
        services.AddSingleton(sp =>
        {
            var pluginLoader = sp.GetRequiredService<PluginLoader>();
            var logger = sp.GetService<ILogger<PluginComposer>>();
            return new PluginComposer(pluginLoader, logger);
        });

        // Authentication
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var logger = sp.GetService<ILogger<NexusSsoClient>>();
            return new NexusSsoClient(settings.NexusApplicationSlug ?? "vortex", logger);
        });

        // Game detection
        services.AddSingleton<SteamGameScanner>();

        // Installation services
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var dbPath = Path.Combine(
                Path.GetDirectoryName(settings.DatabasePath) ?? Environment.CurrentDirectory,
                "modular.db");
            var db = new ModularDatabase(dbPath);
            db.InitializeAsync().GetAwaiter().GetResult();
            return db;
        });
        services.AddSingleton(sp =>
        {
            var db = sp.GetRequiredService<ModularDatabase>();
            var changesetManager = new ChangesetManager(db);
            var logger = sp.GetService<ILogger<SnapshotManager>>();
            return new SnapshotManager(db, changesetManager, logger);
        });
        services.AddSingleton(sp =>
        {
            var db = sp.GetRequiredService<ModularDatabase>();
            var telemetry = sp.GetService<TelemetryService>();
            var snapshotManager = sp.GetRequiredService<SnapshotManager>();
            var logger = sp.GetService<ILogger<ModInstallationService>>();
            return new ModInstallationService(db, telemetry, snapshotManager, logger);
        });

        // Profiles
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<ProfileExporter>>();
            return new ProfileExporter(logger);
        });

        // Diagnostics
        services.AddSingleton(sp =>
        {
            var pluginLoader = sp.GetRequiredService<PluginLoader>();
            var logger = sp.GetService<ILogger<DiagnosticService>>();
            return new DiagnosticService(pluginLoader, logger);
        });

        // Telemetry
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var telemetryPath = Path.Combine(
                Path.GetDirectoryName(settings.DatabasePath) ?? Environment.CurrentDirectory,
                "telemetry");
            var logger = sp.GetService<ILogger<TelemetryService>>();
            return new TelemetryService(telemetryPath, logger: logger);
        });

        // GUI services - use pre-loaded instance
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton(_downloadHistory!);
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var cacheDir = Path.Combine(
                Path.GetDirectoryName(settings.DatabasePath) ?? Environment.CurrentDirectory,
                "thumbnails");
            return new ThumbnailService(cacheDir);
        });

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<ModListViewModel>();
        services.AddTransient<DownloadQueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<GameBananaViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<PluginsViewModel>();
        services.AddTransient<GameDetectionViewModel>();
        services.AddTransient<NexusSearchViewModel>();
        services.AddTransient<GameBananaSearchViewModel>();
        // Sub-ViewModels for combined panels
        services.AddTransient<InstallViewModel>();
        services.AddTransient<InstalledModsViewModel>();
        services.AddTransient<ProfilesViewModel>();
        services.AddTransient<CollectionViewModel>();
        // Combined wrapper ViewModels
        services.AddTransient<NexusModsViewModel>();
        services.AddTransient<GameBananaPanelViewModel>();
        services.AddTransient<BackupsViewModel>();
        services.AddTransient<SnapshotViewModel>();
        services.AddTransient<ModManagerViewModel>();

        return services.BuildServiceProvider();
    }
}
