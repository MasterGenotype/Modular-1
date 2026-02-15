using Microsoft.Extensions.DependencyInjection;
using Modular.Cli.Commands;
using Modular.Cli.Commands.Diagnostics;
using Modular.Cli.Commands.Plugins;
using Modular.Cli.Commands.Profile;
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
        });

        return app.Run(args);
    }
}
