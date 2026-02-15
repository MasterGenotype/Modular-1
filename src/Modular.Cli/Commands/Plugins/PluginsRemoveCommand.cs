using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Plugins;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Plugins;

/// <summary>
/// Removes an installed plugin.
/// </summary>
public sealed class PluginsRemoveCommand : AsyncCommand<PluginsRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<plugin-id>")]
        [Description("Plugin ID to remove")]
        public required string PluginId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var httpClient = new HttpClient();
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(true);
            var marketplace = new PluginMarketplace(httpClient, PluginLoader.DefaultPluginDirectory, loggerFactory.CreateLogger<PluginMarketplace>());

            LiveProgressDisplay.ShowInfo($"Removing plugin: {settings.PluginId}");
            var success = await marketplace.UninstallPluginAsync(settings.PluginId);

            if (success)
            {
                LiveProgressDisplay.ShowSuccess($"Removed plugin: {settings.PluginId}");
                return 0;
            }
            else
            {
                LiveProgressDisplay.ShowError($"Plugin not found or could not be removed: {settings.PluginId}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Remove failed: {ex.Message}");
            return 1;
        }
    }
}
