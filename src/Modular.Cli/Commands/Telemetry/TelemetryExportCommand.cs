using System.ComponentModel;
using System.Text.Json;
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
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var telemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "telemetry");

            var outputPath = settings.OutputPath ?? Path.Combine(Environment.CurrentDirectory, $"telemetry-export-{DateTime.Now:yyyyMMdd}.json");

            if (!Directory.Exists(telemetryPath))
            {
                LiveProgressDisplay.ShowInfo("No telemetry data to export");
                return 0;
            }

            // Export all telemetry files
            var allData = new List<object>();
            foreach (var file in Directory.GetFiles(telemetryPath, "*.json"))
            {
                var json = await File.ReadAllTextAsync(file);
                var data = JsonSerializer.Deserialize<object>(json);
                if (data != null)
                    allData.Add(data);
            }

            var exportJson = JsonSerializer.Serialize(allData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(outputPath, exportJson);

            LiveProgressDisplay.ShowSuccess($"Exported telemetry to: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Export failed: {ex.Message}");
            return 1;
        }
    }
}
