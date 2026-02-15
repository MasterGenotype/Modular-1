using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Configuration;
using Modular.Core.Telemetry;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Telemetry;

/// <summary>
/// Shows a telemetry summary.
/// </summary>
public sealed class TelemetrySummaryCommand : AsyncCommand<TelemetrySummaryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--days")]
        [Description("Number of days to summarize")]
        [DefaultValue(30)]
        public int Days { get; init; } = 30;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var configService = new ConfigurationService();
            var appSettings = await configService.LoadAsync();

            var telemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "telemetry");

            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(appSettings.Verbose);
            var telemetryService = new TelemetryService(telemetryPath,
                new TelemetryConfig { Enabled = appSettings.TelemetryEnabled },
                loggerFactory.CreateLogger<TelemetryService>());

            var startDate = DateTime.UtcNow.AddDays(-settings.Days);
            var summary = telemetryService.GetSummary(startDate, DateTime.UtcNow);

            Console.WriteLine($"Telemetry Summary (last {settings.Days} days)");
            Console.WriteLine("================================");
            Console.WriteLine($"Total events: {summary.TotalEvents}");
            Console.WriteLine($"Downloads: {summary.TotalDownloads}");
            Console.WriteLine($"Bytes downloaded: {summary.TotalBytesDownloaded:N0}");
            Console.WriteLine($"Installer successes: {summary.InstallerSuccesses}");
            Console.WriteLine($"Installer failures: {summary.InstallerFailures}");
            Console.WriteLine($"Plugin crashes: {summary.PluginCrashes}");

            if (summary.EventsByType.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Events by type:");
                foreach (var (type, count) in summary.EventsByType)
                    Console.WriteLine($"  {type}: {count}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to get summary: {ex.Message}");
            return 1;
        }
    }
}
