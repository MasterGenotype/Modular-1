using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Authentication;
using Modular.Core.Configuration;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Authenticates with NexusMods via browser SSO.
/// </summary>
public sealed class LoginCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            var configService = new ConfigurationService();
            var settings = await configService.LoadAsync();

            Console.WriteLine("Opening browser for NexusMods authorization...");
            Console.WriteLine("Please log in and authorize Modular when prompted.");
            Console.WriteLine();

            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(settings.Verbose);
            var login = new NexusLogin(settings, configService, new NexusSsoClient(
                settings.NexusApplicationSlug,
                loggerFactory.CreateLogger<NexusSsoClient>()));
            await login.LoginAsync(url => Console.WriteLine($"If the browser didn't open, visit: {url}"));

            LiveProgressDisplay.ShowSuccess("Login successful! API key saved to config.");
            return 0;
        }
        catch (TimeoutException)
        {
            LiveProgressDisplay.ShowError("SSO authorization timed out. Run 'modular login' to try again.");
            Console.Error.WriteLine("Without SSO, you can set an API key by hand:");
            Console.Error.WriteLine("  export NEXUS_API_KEY=your_key_here");
            Console.Error.WriteLine("  or add 'nexus_api_key' to ~/.config/Modular/config.json");
            return 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
