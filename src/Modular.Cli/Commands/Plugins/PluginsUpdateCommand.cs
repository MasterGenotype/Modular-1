using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Configuration;
using Modular.Core.Plugins;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Plugins;

/// <summary>
/// Checks for and applies plugin updates.
/// </summary>
public sealed class PluginsUpdateCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            var configService = new ConfigurationService();
            var appSettings = await configService.LoadAsync();

            if (string.IsNullOrEmpty(appSettings.PluginMarketplaceUrl))
            {
                LiveProgressDisplay.ShowError("No marketplace URL configured");
                return 1;
            }

            var pluginLoader = new PluginLoader();
            var installed = pluginLoader.DiscoverPlugins();

            using var httpClient = new HttpClient();
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(appSettings.Verbose);
            var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, loggerFactory.CreateLogger<PluginMarketplace>());

            LiveProgressDisplay.ShowInfo("Checking for updates...");
            var index = await marketplace.FetchIndexAsync(appSettings.PluginMarketplaceUrl);

            if (index == null)
            {
                LiveProgressDisplay.ShowError("Failed to fetch marketplace index");
                return 1;
            }

            var updates = await marketplace.CheckUpdatesAsync(index, installed);

            if (updates.Count == 0)
            {
                LiveProgressDisplay.ShowSuccess("All plugins are up to date");
                return 0;
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

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Update check failed: {ex.Message}");
            return 1;
        }
    }
}
