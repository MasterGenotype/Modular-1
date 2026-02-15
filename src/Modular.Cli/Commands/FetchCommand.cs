using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Services;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Fetches and caches mod metadata without renaming.
/// </summary>
public sealed class FetchCommand : AsyncCommand<FetchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[domain]")]
        [Description("Game domain to fetch metadata for")]
        public string? Domain { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;

        try
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                LiveProgressDisplay.ShowWarning("Cancellation requested, cleaning up...");
            };
            Console.CancelKeyPress += cancelHandler;

            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var renameService = new RenameService(services.Settings, services.RateLimiter, services.MetadataCache,
                services.LoggerFactory?.CreateLogger<RenameService>());

            IEnumerable<string> domains;
            if (!string.IsNullOrEmpty(settings.Domain))
            {
                domains = [settings.Domain];
            }
            else
            {
                domains = renameService.GetGameDomainNames();
            }

            foreach (var d in domains)
            {
                var gameDomainPath = Path.Combine(services.Settings.ModsDirectory, d);
                if (!Directory.Exists(gameDomainPath))
                {
                    LiveProgressDisplay.ShowWarning($"Directory not found: {gameDomainPath}");
                    continue;
                }

                LiveProgressDisplay.ShowInfo($"Fetching metadata for {d}...");
                var fetched = await renameService.FetchAndCacheMetadataAsync(gameDomainPath, d, cts.Token);
                LiveProgressDisplay.ShowSuccess($"Cached metadata for {fetched} mods in {d}");
            }

            await services.SaveStateAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Operation cancelled by user.");
            return 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }
}
