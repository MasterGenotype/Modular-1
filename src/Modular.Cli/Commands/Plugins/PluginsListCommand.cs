using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Configuration;
using Modular.Core.Plugins;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Plugins;

/// <summary>
/// Lists installed and available plugins.
/// </summary>
public sealed class PluginsListCommand : AsyncCommand<PluginsListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--marketplace")]
        [Description("Show plugins from marketplace")]
        public bool ShowMarketplace { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
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

            if (settings.ShowMarketplace)
            {
                var configService = new ConfigurationService();
                var appSettings = await configService.LoadAsync();

                if (string.IsNullOrEmpty(appSettings.PluginMarketplaceUrl))
                {
                    LiveProgressDisplay.ShowWarning("No marketplace URL configured");
                    Console.WriteLine("Set 'plugin_marketplace_url' in config to enable marketplace");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Marketplace plugins:");
                    using var httpClient = new HttpClient();
                    using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(appSettings.Verbose);
                    var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, loggerFactory.CreateLogger<PluginMarketplace>());
                    var index = await marketplace.FetchIndexAsync(appSettings.PluginMarketplaceUrl);

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

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to list plugins: {ex.Message}");
            return 1;
        }
    }
}
