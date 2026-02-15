using Modular.Cli.UI;
using Modular.Core.Telemetry;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Telemetry;

/// <summary>
/// Clears all telemetry data.
/// </summary>
public sealed class TelemetryClearCommand : AsyncCommand
{
    public override Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            Console.Write("Are you sure you want to delete all telemetry data? [y/N]: ");
            var response = Console.ReadLine();
            if (response?.ToLowerInvariant() != "y")
            {
                LiveProgressDisplay.ShowInfo("Cancelled");
                return Task.FromResult(0);
            }

            var telemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "telemetry");

            var telemetryService = new TelemetryService(telemetryPath);
            telemetryService.ClearData();

            LiveProgressDisplay.ShowSuccess("Telemetry data cleared");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to clear telemetry: {ex.Message}");
            return Task.FromResult(1);
        }
    }
}
