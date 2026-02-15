using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Diagnostics;
using Modular.Core.Plugins;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Diagnostics;

/// <summary>
/// Runs system diagnostics.
/// </summary>
public sealed class DiagnosticsCommand : AsyncCommand<DiagnosticsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Output as JSON")]
        public bool AsJson { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(true);
            var pluginLoader = new PluginLoader();
            var diagnosticService = new DiagnosticService(pluginLoader, loggerFactory.CreateLogger<DiagnosticService>());

            LiveProgressDisplay.ShowInfo("Running system diagnostics...");
            var healthReport = await diagnosticService.RunHealthCheckAsync();
            var report = diagnosticService.GenerateReport();

            if (settings.AsJson)
            {
                var output = new
                {
                    Health = healthReport,
                    Report = report
                };
                Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                // Display health report
                Console.WriteLine();
                Console.WriteLine($"=== System Health: {healthReport.OverallStatus} ===");
                Console.WriteLine();

                foreach (var check in healthReport.Checks)
                {
                    var statusIcon = check.Status switch
                    {
                        HealthStatus.Healthy => "[OK]",
                        HealthStatus.Degraded => "[WARN]",
                        HealthStatus.Unhealthy => "[FAIL]",
                        _ => "[??]"
                    };
                    Console.WriteLine($"  {statusIcon} {check.Name}: {check.Message}");
                }

                Console.WriteLine();
                Console.WriteLine($"=== System Information ===");
                Console.WriteLine($"  Host Version: {report.HostVersion}");
                Console.WriteLine($"  Runtime: {report.Runtime}");
                Console.WriteLine($"  Platform: {report.Platform}");
                Console.WriteLine($"  Working Directory: {report.WorkingDirectory}");
                Console.WriteLine();
                Console.WriteLine($"=== Components ===");
                Console.WriteLine($"  Plugins: {report.LoadedPlugins.Count}");
                Console.WriteLine($"  Installers: {report.TotalInstallers}");
                Console.WriteLine($"  Enrichers: {report.TotalEnrichers}");
                Console.WriteLine($"  UI Extensions: {report.TotalUiExtensions}");

                if (report.LoadedPlugins.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== Loaded Plugins ===");
                    foreach (var plugin in report.LoadedPlugins)
                    {
                        Console.WriteLine($"  - {plugin.DisplayName} v{plugin.Version} ({plugin.Id})");
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Diagnostics failed: {ex.Message}");
            return 1;
        }
    }
}
