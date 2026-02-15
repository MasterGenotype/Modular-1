using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Diagnostics;
using Modular.Core.Plugins;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Diagnostics;

/// <summary>
/// Validates a plugin manifest.
/// </summary>
public sealed class ValidatePluginCommand : AsyncCommand<ValidatePluginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<plugin-path>")]
        [Description("Path to plugin directory or manifest file")]
        public required string PluginPath { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(true);
            var pluginLoader = new PluginLoader();
            var diagnosticService = new DiagnosticService(pluginLoader, loggerFactory.CreateLogger<DiagnosticService>());

            // If path is a directory, look for plugin.json
            var manifestPath = settings.PluginPath;
            if (Directory.Exists(settings.PluginPath))
            {
                manifestPath = Path.Combine(settings.PluginPath, "plugin.json");
            }

            LiveProgressDisplay.ShowInfo($"Validating: {manifestPath}");
            var result = diagnosticService.ValidatePlugin(manifestPath);

            if (result.IsValid)
            {
                LiveProgressDisplay.ShowSuccess("Plugin manifest is valid");
                if (result.Manifest != null)
                {
                    Console.WriteLine($"  ID: {result.Manifest.Id}");
                    Console.WriteLine($"  Name: {result.Manifest.DisplayName}");
                    Console.WriteLine($"  Version: {result.Manifest.Version}");
                    Console.WriteLine($"  Entry: {result.Manifest.EntryAssembly}");
                }
            }
            else
            {
                LiveProgressDisplay.ShowError("Validation failed:");
                foreach (var error in result.Errors)
                    Console.WriteLine($"  [ERROR] {error}");
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("Warnings:");
                foreach (var warning in result.Warnings)
                    Console.WriteLine($"  [WARN] {warning}");
            }

            return result.IsValid ? 0 : 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Validation failed: {ex.Message}");
            return 1;
        }
    }
}
