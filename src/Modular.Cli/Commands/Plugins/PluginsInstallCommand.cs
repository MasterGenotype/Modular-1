using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Configuration;
using Modular.Core.Plugins;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Plugins;

/// <summary>
/// Installs a plugin from the marketplace.
/// </summary>
public sealed class PluginsInstallCommand : AsyncCommand<PluginsInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<plugin-id>")]
        [Description("Plugin ID to install")]
        public required string PluginId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var configService = new ConfigurationService();
            var appSettings = await configService.LoadAsync();

            if (string.IsNullOrEmpty(appSettings.PluginMarketplaceUrl))
            {
                LiveProgressDisplay.ShowError("No marketplace URL configured");
                Console.WriteLine("Set 'plugin_marketplace_url' in config to enable marketplace");
                return 1;
            }

            using var httpClient = new HttpClient();
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(appSettings.Verbose);
            var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, loggerFactory.CreateLogger<PluginMarketplace>());

            LiveProgressDisplay.ShowInfo("Fetching marketplace index...");
            var index = await marketplace.FetchIndexAsync(appSettings.PluginMarketplaceUrl);

            if (index == null)
            {
                LiveProgressDisplay.ShowError("Failed to fetch marketplace index");
                return 1;
            }

            var plugin = index.Plugins.FirstOrDefault(p => p.Id.Equals(settings.PluginId, StringComparison.OrdinalIgnoreCase));
            if (plugin == null)
            {
                LiveProgressDisplay.ShowError($"Plugin not found: {settings.PluginId}");
                return 1;
            }

            LiveProgressDisplay.ShowInfo($"Installing {plugin.Name} v{plugin.Version}...");
            var progress = new Progress<double>(p => Console.Write($"\rDownloading: {p:F0}%"));
            var result = await marketplace.InstallPluginAsync(plugin, progress);

            Console.WriteLine();
            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess($"Installed {plugin.Name} to {result.InstalledPath}");
                return 0;
            }
            else
            {
                LiveProgressDisplay.ShowError($"Installation failed: {result.Error}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Installation failed: {ex.Message}");
            return 1;
        }
    }
}
