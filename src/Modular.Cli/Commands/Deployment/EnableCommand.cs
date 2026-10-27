using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Deployment;

/// <summary>
/// Turns mods back on and redeploys.
/// </summary>
public sealed class EnableCommand : SetEnabledCommand
{
    /// <inheritdoc />
    protected override bool Enabled => true;
}

/// <summary>
/// Turns mods off and redeploys. A disabled mod keeps its staging and its place in the
/// order; only its files leave the game folder.
/// </summary>
public sealed class DisableCommand : SetEnabledCommand
{
    /// <inheritdoc />
    protected override bool Enabled => false;
}

/// <summary>
/// Shared body of <c>enable</c> and <c>disable</c>.
/// </summary>
public abstract class SetEnabledCommand : AsyncCommand<SetEnabledCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game whose mods to change.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>The mods to change.</summary>
        [CommandArgument(0, "[mod-ids]")]
        [Description("Mod ids, as 'modular order list' shows them")]
        public string[] ModIds { get; init; } = [];

        /// <summary>Whether to change every mod of the game.</summary>
        [CommandOption("--all")]
        [Description("Every mod of the game")]
        public bool All { get; init; }

        /// <summary>Whether to enable verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("List every file rather than counts")]
        public bool Verbose { get; init; }
    }

    /// <summary>Whether this command enables rather than disables.</summary>
    protected abstract bool Enabled { get; }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var verb = Enabled ? "enable" : "disable";

        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            AnsiConsole.MarkupLine("[red]--game is required.[/]");
            return 1;
        }

        if (settings.All == (settings.ModIds.Length > 0))
        {
            AnsiConsole.MarkupLine($"[red]Give the mod ids to {verb}, or --all, but not both.[/]");
            return 1;
        }

        var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
        var db = await DeployCommand.OpenDatabaseAsync();

        try
        {
            var installService = services.CreateInstallationService(db);
            var installation = await installService.Deployment.ResolveInstallationAsync(settings.Game, cts.Token);

            if (installation == null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not resolve game:[/] {Markup.Escape(settings.Game)}");
                return 1;
            }

            var known = await installService.Deployment.Mods.ListAsync(
                installation.GameKey, enabledOnly: false, cts.Token);

            var requested = settings.All
                ? known.Select(m => m.ModId).ToList()
                : settings.ModIds.ToList();

            var unknown = requested
                .Where(id => !known.Any(m => string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (unknown.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Not installed for this game:[/] {Markup.Escape(string.Join(", ", unknown))}");
                AnsiConsole.MarkupLine("[grey]Run 'modular order list --game <g>' to see the mod ids.[/]");
                return 1;
            }

            var already = known
                .Where(m => m.Enabled == Enabled &&
                            requested.Contains(m.ModId, StringComparer.OrdinalIgnoreCase))
                .Select(m => m.DisplayName ?? m.ModId)
                .ToList();
            var changing = requested.Count - already.Count;

            if (changing == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]Nothing to change: {(requested.Count == 1 ? "the mod is" : $"all {requested.Count} are")} " +
                    $"already {verb}d.[/]");
                return 0;
            }

            var result = await installService.Deployment.SetEnabledAsync(
                installation, requested, Enabled, cts.Token);

            if (!result.Success)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Deploy failed, nothing was {verb}d:[/] {Markup.Escape(result.Error ?? "unknown error")}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]{(Enabled ? "Enabled" : "Disabled")}[/] {changing} mod(s)");
            if (already.Count > 0)
                AnsiConsole.MarkupLine($"[grey]{already.Count} were already {verb}d.[/]");

            DeploymentReport.Render(result, settings.Verbose);
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            await db.DisposeAsync();
            services.Dispose();
        }
    }
}
