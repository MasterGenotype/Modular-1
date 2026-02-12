using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Modular.Core.Services;
using Modular.Gui.Services;
using Modular.Gui.ViewModels;
using System;

namespace Modular.Gui;

sealed class Program
{
    public static IServiceProvider? Services { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            Services = ConfigureServices();
            
            // Pre-resolve all singletons before Avalonia starts to avoid deadlocks
            // These services load data synchronously which would deadlock on UI thread
            _ = Services.GetRequiredService<AppSettings>();
            _ = Services.GetRequiredService<DownloadDatabase>();
            _ = Services.GetRequiredService<ModMetadataCache>();
            _ = Services.GetRequiredService<BackendRegistry>();
            _ = Services.GetRequiredService<DownloadHistoryService>();
            
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            throw;
        }
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

        // Core services - configuration
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton(sp =>
        {
            var configService = sp.GetRequiredService<ConfigurationService>();
            return configService.LoadAsync().GetAwaiter().GetResult();
        });

        // Core services - database and rate limiting
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var db = new DownloadDatabase(settings.DatabasePath);
            db.LoadAsync().GetAwaiter().GetResult();
            return db;
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var cachePath = Path.Combine(
                Path.GetDirectoryName(settings.DatabasePath) ?? Environment.CurrentDirectory,
                "metadata_cache.json");
            var cache = new ModMetadataCache(cachePath);
            cache.LoadAsync().GetAwaiter().GetResult();
            return cache;
        });
        services.AddSingleton<IRateLimiter, NexusRateLimiter>();

        // Backend services
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var rateLimiter = sp.GetRequiredService<IRateLimiter>();
            var database = sp.GetRequiredService<DownloadDatabase>();
            var logger = sp.GetService<ILogger<NexusModsBackend>>();
            return new NexusModsBackend(settings, rateLimiter, database, logger);
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

        // GUI services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var historyPath = Path.Combine(
                Path.GetDirectoryName(settings.DatabasePath) ?? Environment.CurrentDirectory,
                "download_history.json");
            var service = new DownloadHistoryService(historyPath);
            service.LoadAsync().GetAwaiter().GetResult();
            return service;
        });
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

        return services.BuildServiceProvider();
    }
}
