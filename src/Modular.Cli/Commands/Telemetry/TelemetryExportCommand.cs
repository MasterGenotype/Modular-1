using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Telemetry;

/// <summary>
/// Exports telemetry data.
/// </summary>
public sealed class TelemetryExportCommand : AsyncCommand<TelemetryExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--output")]
        [Description("Output file path")]
        public string? OutputPath { get; init; }

        [CommandOption("--days")]
        [Description("Number of days to export")]
        [DefaultValue(30)]
        public int Days { get; init; } = 30;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync();

            var outputPath = settings.OutputPath
                ?? Path.Combine(Environment.CurrentDirectory, $"telemetry-export-{DateTime.Now:yyyyMMdd}.json");

            var startDate = DateTime.UtcNow.AddDays(-settings.Days);
            var success = await services.Telemetry.ExportDataAsync(outputPath, startDate, DateTime.UtcNow);

            if (success)
                LiveProgressDisplay.ShowSuccess($"Exported telemetry to: {outputPath}");
            else
                LiveProgressDisplay.ShowError("Export failed");

            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Export failed: {ex.Message}");
            return 1;
        }
    }
}
