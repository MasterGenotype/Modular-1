using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Sdk.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Deployment;

/// <summary>
/// Shows a game's install order and, for a game that has one, its load order.
/// </summary>
public sealed class OrderListCommand : AsyncCommand<OrderListCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game whose orders to show.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>Show only the install order.</summary>
        [CommandOption("--install")]
        [Description("Show only the install order (which mod wins a file both deploy)")]
        public bool InstallOnly { get; init; }

        /// <summary>Show only the load order.</summary>
        [CommandOption("--load")]
        [Description("Show only the game's load order")]
        public bool LoadOnly { get; init; }

        /// <summary>Whether to enable verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            AnsiConsole.MarkupLine("[red]--game is required.[/]");
            return 1;
        }

        var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
        var db = await DeployCommand.OpenDatabaseAsync();

        try
        {
            var installService = services.CreateInstallationService(db);
            var installation = await installService.Deployment.ResolveInstallationAsync(settings.Game);

            if (installation == null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not resolve game:[/] {Markup.Escape(settings.Game)}");
                return 1;
            }

            var orders = await installService.LoadOrder.GetAsync(installation);
            var gameName = installation.DisplayName ?? installation.GameId;

            if (!settings.LoadOnly)
            {
                if (orders.Install.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]Modular has no mods for {Markup.Escape(gameName)}.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"[bold]Install order for {Markup.Escape(gameName)}[/] " +
                        "[grey](which mod's file wins when two deploy the same one; later wins)[/]");
                    AnsiConsole.Write(OrderRendering.Table(orders.Install, showOwner: false));
                }
            }

            if (!settings.InstallOnly)
            {
                if (!settings.LoadOnly)
                    AnsiConsole.WriteLine();

                if (orders.Kind == LoadOrderKind.None)
                {
                    AnsiConsole.MarkupLine(
                        $"[bold]Load order[/] [grey]— {Markup.Escape(gameName)} applies mods in one order, " +
                        "the install order.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"[bold]Load order for {Markup.Escape(gameName)}[/] [grey]({Markup.Escape(orders.Rule)})[/]");

                    if (orders.Mechanism != null)
                        AnsiConsole.MarkupLine($"[grey]kept in {Markup.Escape(orders.Mechanism)}[/]");

                    if (orders.Load.Count == 0)
                        AnsiConsole.MarkupLine("[grey]  (empty)[/]");
                    else
                        AnsiConsole.Write(OrderRendering.Table(orders.Load, showOwner: orders.Kind == LoadOrderKind.External));
                }
            }

            return 0;
        }
        catch (LoadOrderException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
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

/// <summary>
/// Moves a mod to a new place in a game's install order or load order.
/// </summary>
public sealed class OrderMoveCommand : AsyncCommand<OrderMoveCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>What to move.</summary>
        [CommandArgument(0, "<mod>")]
        [Description("Mod id, name, part of a name, or a position such as #3")]
        public string Mod { get; init; } = string.Empty;

        /// <summary>The game.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>Change the load order rather than the install order.</summary>
        [CommandOption("--load")]
        [Description("Move it in the game's load order instead of the install order")]
        public bool Load { get; init; }

        /// <summary>Put it directly before this entry.</summary>
        [CommandOption("--before <MOD>")]
        [Description("Put it directly before this entry (so this entry wins over it)")]
        public string? Before { get; init; }

        /// <summary>Put it directly after this entry.</summary>
        [CommandOption("--after <MOD>")]
        [Description("Put it directly after this entry (so it wins over this entry)")]
        public string? After { get; init; }

        /// <summary>Put it at a position.</summary>
        [CommandOption("--to <POSITION>")]
        [Description("Put it at this 1-based position")]
        public int? To { get; init; }

        /// <summary>Put it first.</summary>
        [CommandOption("--top")]
        [Description("Put it first, where everything else wins over it")]
        public bool Top { get; init; }

        /// <summary>Put it last.</summary>
        [CommandOption("--bottom")]
        [Description("Put it last, where it wins over everything")]
        public bool Bottom { get; init; }

        /// <summary>Whether to skip the deployment afterwards.</summary>
        [CommandOption("--no-deploy")]
        [Description("Store the new order without redeploying")]
        public bool NoDeploy { get; init; }

        /// <summary>Whether to enable verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Game))
                return ValidationResult.Error("--game is required.");

            return OrderRendering.PlacementFrom(Before, After, To, Top, Bottom, "", required: true) is { Error: { } error }
                ? ValidationResult.Error(error)
                : ValidationResult.Success();
        }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var placement = OrderRendering.PlacementFrom(
            settings.Before, settings.After, settings.To, settings.Top, settings.Bottom, "", required: true).Placement!;

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

            var list = settings.Load ? OrderListKind.Load : OrderListKind.Install;
            var result = await installService.LoadOrder.MoveAsync(
                installation, list, settings.Mod, placement, deploy: !settings.NoDeploy, cts.Token);

            var what = list == OrderListKind.Install ? "install order" : "load order";
            var moved = string.Join(", ", result.Moved.Select(m => $"'{m.Name}'"));

            if (!result.Changed)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(moved)} is already there in the {what}.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[green]Moved[/] {Markup.Escape(moved)} in the {what}:");
            OrderRendering.Neighbourhood(result.Order, result.Moved);

            if (result.Deployment is { } deployment)
            {
                if (!deployment.Success)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Deploy failed:[/] {Markup.Escape(deployment.Error ?? "unknown error")}");
                    return 1;
                }

                DeploymentReport.Render(deployment, settings.Verbose);
            }
            else if (settings.NoDeploy)
            {
                AnsiConsole.MarkupLine("[grey]Not deployed. Run 'modular deploy --game <g>' to apply it.[/]");
            }

            return 0;
        }
        catch (LoadOrderException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
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

/// <summary>
/// Shared rendering and option parsing for the order commands and install's placement flags.
/// </summary>
internal static class OrderRendering
{
    /// <summary>
    /// A numbered table of an order.
    /// </summary>
    /// <param name="items">The order, first to last.</param>
    /// <param name="showOwner">Whether to show which entries Modular installed.</param>
    /// <returns>The table.</returns>
    public static Table Table(IReadOnlyList<OrderItem> items, bool showOwner)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("Name");
        table.AddColumn(showOwner ? "Modular mod" : "Mod id");
        table.AddColumn(showOwner ? "Note" : "Enabled");

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var note = showOwner
                ? item.Locked ? "[grey]locked[/]" : item.Managed ? string.Empty : "[grey]not Modular's[/]"
                : item.Enabled ? "[green]yes[/]" : "[grey]no[/]";

            table.AddRow(
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(item.Name),
                Markup.Escape(item.ModId ?? "—"),
                note);
        }

        return table;
    }

    /// <summary>
    /// Prints the moved entries with their new neighbours.
    /// </summary>
    /// <param name="order">The order after the move.</param>
    /// <param name="moved">The entries that moved.</param>
    public static void Neighbourhood(IReadOnlyList<OrderItem> order, IReadOnlyList<OrderItem> moved)
    {
        var keys = moved.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var first = order.ToList().FindIndex(i => keys.Contains(i.Key));
        var last = order.ToList().FindLastIndex(i => keys.Contains(i.Key));

        for (var i = Math.Max(0, first - 1); i <= Math.Min(order.Count - 1, last + 1); i++)
        {
            var mark = keys.Contains(order[i].Key) ? "[green]→[/]" : " ";
            AnsiConsole.MarkupLine($"  {mark} {i + 1,4}. {Markup.Escape(order[i].Name)}");
        }
    }

    /// <summary>
    /// Turns placement options into a placement.
    /// </summary>
    /// <param name="before">Entry to go before.</param>
    /// <param name="after">Entry to go after.</param>
    /// <param name="to">1-based position.</param>
    /// <param name="top">Go first.</param>
    /// <param name="bottom">Go last.</param>
    /// <param name="prefix">Option name prefix, such as <c>load-</c>, for messages.</param>
    /// <param name="required">Whether giving none of the options is an error.</param>
    /// <param name="positionName">The position option's name without prefix, such as <c>to</c>.</param>
    /// <param name="offerBottom">Whether the command has a bottom option to mention.</param>
    /// <returns>The placement, <c>null</c> when no option was given, or an error.</returns>
    public static (OrderPlacement? Placement, string? Error) PlacementFrom(
        string? before, string? after, int? to, bool top, bool bottom, string prefix,
        bool required = false, string positionName = "to", bool offerBottom = true)
    {
        var options = $"--{prefix}before, --{prefix}after, --{prefix}{positionName}" +
                      (offerBottom ? $", --{prefix}top or --{prefix}bottom" : $" or --{prefix}top");

        var given = new List<OrderPlacement>();

        if (!string.IsNullOrWhiteSpace(before))
            given.Add(OrderPlacement.Before(before));
        if (!string.IsNullOrWhiteSpace(after))
            given.Add(OrderPlacement.After(after));
        if (to is { } position)
            given.Add(OrderPlacement.At(position));
        if (top)
            given.Add(OrderPlacement.Top);
        if (bottom)
            given.Add(OrderPlacement.Bottom);

        if (given.Count > 1)
        {
            return (null, $"Give only one of {options}.");
        }

        if (to is < 1)
            return (null, $"--{prefix}{positionName} counts from 1.");

        if (given.Count == 0)
        {
            return required
                ? (null, $"Say where to put it: {options}.")
                : (null, null);
        }

        return (given[0], null);
    }
}

/// <summary>
/// Sets a game's load order.
/// </summary>
public sealed class OrderSetCommand : AsyncCommand<OrderSetCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game whose order to set.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>The mod ids, first to last.</summary>
        [CommandArgument(0, "<mod-ids>")]
        [Description("Mod ids in order, first to last; the last one wins")]
        public string[] ModIds { get; init; } = [];

        /// <summary>Whether to deploy after reordering.</summary>
        [CommandOption("--no-deploy")]
        [Description("Store the new order without redeploying")]
        public bool NoDeploy { get; init; }

        /// <summary>Whether to enable verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            AnsiConsole.MarkupLine("[red]--game is required.[/]");
            return 1;
        }

        if (settings.ModIds.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Give at least one mod id.[/]");
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

            var unknown = settings.ModIds
                .Where(id => !known.Any(m =>
                    string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (unknown.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Not deployed for this game:[/] {Markup.Escape(string.Join(", ", unknown))}");
                AnsiConsole.MarkupLine("[grey]Run 'modular order list --game <g>' to see the mod ids.[/]");
                return 1;
            }

            await installService.Deployment.Mods.SetOrderAsync(
                installation.GameKey, settings.ModIds, cts.Token);

            AnsiConsole.MarkupLine(
                $"[green]Order updated[/] [grey](mods you did not name keep their relative order after these)[/]");

            if (settings.NoDeploy)
            {
                AnsiConsole.MarkupLine(
                    "[grey]Not deployed. Run 'modular deploy --game <g>' to apply it.[/]");
                return 0;
            }

            var result = await installService.Deployment.DeployAsync(installation, ct: cts.Token);

            if (!result.Success)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Deploy failed:[/] {Markup.Escape(result.Error ?? "unknown error")}");
                return 1;
            }

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
