using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Telemetry;

/// <summary>
/// Clears all telemetry data.
/// </summary>
public sealed class TelemetryClearCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            Console.Write("Are you sure you want to delete all telemetry data? [y/N]: ");
            var response = Console.ReadLine();
            if (response?.ToLowerInvariant() != "y")
            {
                LiveProgressDisplay.ShowInfo("Cancelled");
                return 0;
            }

            using var services = await RuntimeServices.InitializeMinimalAsync();
            services.Telemetry.ClearData();

            LiveProgressDisplay.ShowSuccess("Telemetry data cleared");
            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Failed to clear telemetry: {ex.Message}");
            return 1;
        }
    }
}
