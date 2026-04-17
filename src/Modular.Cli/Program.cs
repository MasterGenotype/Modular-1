using Microsoft.Extensions.DependencyInjection;
using Modular.Cli.Commands;
using Modular.Cli.Commands.Diagnostics;
using Modular.Cli.Commands.GameDetection;
using Modular.Cli.Commands.Plugins;
using Modular.Cli.Commands.Profile;
using Modular.Cli.Commands.Switch;
using Modular.Cli.Commands.Telemetry;
using Modular.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Modular.Cli;

class Program
{
    static int Main(string[] args)
    {
        var services = new ServiceCollection();
        services.ConfigureServices();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.SetApplicationName("modular");
            config.SetApplicationVersion("1.0.0");

            // Default command when no arguments provided runs interactive mode
            config.AddCommand<InteractiveCommand>("")
                .IsHidden();

            // Download command
            config.AddCommand<DownloadCommand>("download")
                .WithDescription("Download mods from a backend")
                .WithExample("download", "stardewvalley")
                .WithExample("download", "--backend", "nexusmods", "skyrimspecialedition")
                .WithExample("download", "--all");

            // Rename command
            config.AddCommand<RenameCommand>("rename")
                .WithDescription("Rename mod folders to human-readable names")
                .WithExample("rename", "stardewvalley")
                .WithExample("rename", "--organize-by-category");

            // Fetch command
            config.AddCommand<FetchCommand>("fetch")
                .WithDescription("Fetch and cache mod metadata without renaming")
                .WithExample("fetch", "stardewvalley");

            // Login command
            config.AddCommand<LoginCommand>("login")
                .WithDescription("Authenticate with NexusMods via browser SSO");

            // Diagnostics command group
            config.AddBranch("diagnostics", diagnostics =>
            {
                diagnostics.SetDescription("Run system diagnostics");

                diagnostics.AddCommand<DiagnosticsCommand>("run")
                    .WithDescription("Run system diagnostics")
                    .WithExample("diagnostics", "run")
                    .WithExample("diagnostics", "run", "--json");

                diagnostics.AddCommand<ValidatePluginCommand>("validate")
                    .WithDescription("Validate a plugin manifest")
                    .WithExample("diagnostics", "validate", "./my-plugin");
            });

            // Profile command group
            config.AddBranch("profile", profile =>
            {
                profile.SetDescription("Manage mod profiles");

                profile.AddCommand<ProfileExportCommand>("export")
                    .WithDescription("Export a mod profile")
                    .WithExample("profile", "export", "my-profile")
                    .WithExample("profile", "export", "my-profile", "--format", "archive");

                profile.AddCommand<ProfileImportCommand>("import")
                    .WithDescription("Import a mod profile")
                    .WithExample("profile", "import", "./my-profile.json");

                profile.AddCommand<ProfileListCommand>("list")
                    .WithDescription("List available profiles");
            });

            // Plugins command group
            config.AddBranch("plugins", plugins =>
            {
                plugins.SetDescription("Manage plugins");

                plugins.AddCommand<PluginsListCommand>("list")
                    .WithDescription("List installed and available plugins")
                    .WithExample("plugins", "list")
                    .WithExample("plugins", "list", "--marketplace");

                plugins.AddCommand<PluginsInstallCommand>("install")
                    .WithDescription("Install a plugin from marketplace")
                    .WithExample("plugins", "install", "my-plugin");

                plugins.AddCommand<PluginsUpdateCommand>("update")
                    .WithDescription("Check for and apply plugin updates");

                plugins.AddCommand<PluginsRemoveCommand>("remove")
                    .WithDescription("Remove an installed plugin")
                    .WithExample("plugins", "remove", "my-plugin");
            });

            // Search command
            config.AddCommand<SearchCommand>("search")
                .WithDescription("Search for mods on a backend")
                .WithExample("search", "SKSE64", "--game", "skyrimspecialedition")
                .WithExample("search", "armor retexture", "--sort", "downloads", "--limit", "10");

            // Browse command
            config.AddCommand<BrowseCommand>("browse")
                .WithDescription("Browse trending, latest, or recently updated mods")
                .WithExample("browse", "trending", "--game", "skyrimspecialedition")
                .WithExample("browse", "latest", "--game", "stardewvalley")
                .WithExample("browse", "updated", "--game", "skyrimspecialedition", "--period", "1w");

            // Collection command group
            config.AddBranch("collection", collection =>
            {
                collection.SetDescription("Manage mod collections");

                collection.AddCommand<CollectionCreateCommand>("create")
                    .WithDescription("Create a new collection")
                    .WithExample("collection", "create", "My Skyrim Build", "--game", "skyrimspecialedition");

                collection.AddCommand<CollectionListCommand>("list")
                    .WithDescription("List all collections");

                collection.AddCommand<CollectionShowCommand>("show")
                    .WithDescription("Show collection details")
                    .WithExample("collection", "show", "My Skyrim Build");

                collection.AddCommand<CollectionAddCommand>("add")
                    .WithDescription("Add a mod to a collection")
                    .WithExample("collection", "add", "My Skyrim Build", "1234", "--file-id", "5678");

                collection.AddCommand<CollectionRemoveCommand>("remove")
                    .WithDescription("Remove a mod from a collection")
                    .WithExample("collection", "remove", "My Skyrim Build", "1234");

                collection.AddCommand<CollectionDownloadCommand>("download")
                    .WithDescription("Download all mods in a collection")
                    .WithExample("collection", "download", "My Skyrim Build", "--verify");

                collection.AddCommand<CollectionVerifyCommand>("verify")
                    .WithDescription("Verify downloaded collection files")
                    .WithExample("collection", "verify", "My Skyrim Build");

                collection.AddCommand<CollectionExportCommand>("export")
                    .WithDescription("Export a collection to a JSON file")
                    .WithExample("collection", "export", "My Skyrim Build", "--output", "./export.json");

                collection.AddCommand<CollectionImportCommand>("import")
                    .WithDescription("Import a collection from a JSON file")
                    .WithExample("collection", "import", "./export.json");

                collection.AddCommand<CollectionCheckUpdatesCommand>("check-updates")
                    .WithDescription("Check for updates to mods in a collection")
                    .WithExample("collection", "check-updates", "My Skyrim Build");
            });

            // Mod installation commands
            config.AddCommand<InstallCommand>("install")
                .WithDescription("Install a mod archive to a game directory")
                .WithExample("install", "mod.zip", "--game", "730")
                .WithExample("install", "mod.zip", "--game", "Counter-Strike")
                .WithExample("install", "mod.zip", "--game", "/path/to/game", "--dry-run")
                .WithExample("install", "mod.zip", "--game", "730", "--force");

            config.AddCommand<UninstallCommand>("uninstall")
                .WithDescription("Uninstall a previously installed mod by changeset ID")
                .WithExample("uninstall", "a1b2c3d4e5f6");

            config.AddCommand<ListInstalledCommand>("installed")
                .WithDescription("List installed mods")
                .WithExample("installed")
                .WithExample("installed", "--game", "730");

            // Game detection commands
            config.AddCommand<DetectGamesCommand>("detect-games")
                .WithDescription("Scan for installed Steam games")
                .WithExample("detect-games")
                .WithExample("detect-games", "--engines")
                .WithExample("detect-games", "--engines", "--verbose");

            config.AddCommand<DetectEngineCommand>("detect-engine")
                .WithDescription("Detect game engine for a specific game")
                .WithExample("detect-engine", "730")
                .WithExample("detect-engine", "/path/to/game", "--all");

            // Steam mod installer command
            config.AddCommand<SteamInstallCommand>("steam-install")
                .WithDescription("Install Steam game mods with dependency resolution")
                .WithExample("steam-install", "/path/to/game", "--manifest", "mods.json")
                .WithExample("steam-install", "/path/to/game", "--archive", "mod.zip")
                .WithExample("steam-install", "/path/to/game", "--manifest", "mods.json", "--dry-run");

            // Telemetry command group
            config.AddBranch("telemetry", telemetry =>
            {
                telemetry.SetDescription("Manage telemetry data");

                telemetry.AddCommand<TelemetrySummaryCommand>("summary")
                    .WithDescription("Show telemetry summary")
                    .WithExample("telemetry", "summary")
                    .WithExample("telemetry", "summary", "--days", "7");

                telemetry.AddCommand<TelemetryExportCommand>("export")
                    .WithDescription("Export telemetry data")
                    .WithExample("telemetry", "export", "--output", "./telemetry.json");

                telemetry.AddCommand<TelemetryClearCommand>("clear")
                    .WithDescription("Clear all telemetry data");
            });

            // Switch mod pipeline commands
            config.AddBranch("switch", switchBranch =>
            {
                switchBranch.SetDescription("Manage Nintendo Switch mods for Yuzu-emulated games");

                switchBranch.AddCommand<SwitchScanCommand>("scan")
                    .WithDescription("Discover and catalogue Switch mods in a directory")
                    .WithExample("switch", "scan", "./mods")
                    .WithExample("switch", "scan", "--game", "0100F2C0115B6000");

                switchBranch.AddCommand<SwitchResolveCommand>("resolve")
                    .WithDescription("Resolve mod dependency graph and load order")
                    .WithExample("switch", "resolve", "--game", "0100F2C0115B6000");

                switchBranch.AddCommand<SwitchInstallCommand>("install")
                    .WithDescription("Install Switch mods into Yuzu's load directory")
                    .WithExample("switch", "install", "--game", "0100F2C0115B6000")
                    .WithExample("switch", "install", "--game", "0100F2C0115B6000", "--runner", "lutris");

                switchBranch.AddCommand<SwitchRemoveCommand>("remove")
                    .WithDescription("Remove installed Switch mods")
                    .WithExample("switch", "remove", "--game", "0100F2C0115B6000", "--mods", "MyMod");

                switchBranch.AddCommand<SwitchRollbackCommand>("rollback")
                    .WithDescription("Rollback Switch mods to pre-install snapshot")
                    .WithExample("switch", "rollback", "--game", "0100F2C0115B6000");

                switchBranch.AddCommand<SwitchStatusCommand>("status")
                    .WithDescription("Show installation status of Switch mods")
                    .WithExample("switch", "status", "--game", "0100F2C0115B6000");
            });
        });

        return app.Run(args);
    }
}
