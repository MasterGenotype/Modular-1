using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Sdk.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Deployment;

/// <summary>
/// Shows a game's load order, and the engine order each module derives from it.
/// </summary>
public sealed class OrderListCommand : AsyncCommand<OrderListCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game whose order to show.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

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

            var mods = await installService.Deployment.Mods.ListAsync(installation.GameKey);

            if (mods.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]No mods are deployed for {Markup.Escape(installation.DisplayName ?? settings.Game)}.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("Mod");
            table.AddColumn("Enabled");
            table.AddColumn("Installer");
            table.AddColumn("Source");

            foreach (var mod in mods)
            {
                table.AddRow(
                    (mod.OrderIndex ?? 0).ToString(),
                    Markup.Escape(mod.DisplayName ?? mod.ModId),
                    mod.Enabled ? "[green]yes[/]" : "[grey]no[/]",
                    Markup.Escape(mod.InstallerId ?? "—"),
                    Markup.Escape(mod.Source));
            }

            AnsiConsole.MarkupLine(
                $"[bold]Load order for {Markup.Escape(installation.DisplayName ?? installation.GameId)}[/] " +
                "[grey](later wins)[/]");
            AnsiConsole.Write(table);

            RenderEngineOrder(installService, installation, mods);
            return 0;
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

    /// <summary>
    /// Shows the engine's own ordering next to Modular's, when the module translates them
    /// differently.
    /// </summary>
    /// <remarks>
    /// Cyberpunk's legacy archives are the case that makes this worth showing: the first
    /// archive loaded wins, so the manifest Modular writes is Modular's order reversed.
    /// Seeing both side by side is the only way that translation is visible.
    /// </remarks>
    private static void RenderEngineOrder(
        ModInstallationService installService,
        GameInstallation installation,
        IReadOnlyList<GameModRecord> mods)
    {
        var module = installService.ModuleRegistry.GetByModuleId(installation.ModuleId);
        if (module == null)
            return;

        var deployed = mods
            .Where(m => m.Enabled)
            .Select((m, i) => new DeployedMod
            {
                ModId = m.ModId,
                DisplayName = m.DisplayName,
                OrderIndex = i,
                Enabled = true
            })
            .ToList();

        if (EngineLoadOrder.Describe(module, installation, deployed) is not { } engineOrder)
            return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Engine order[/] [grey]({Markup.Escape(engineOrder.Rule)})[/]");

        if (engineOrder.Mechanism != null)
            AnsiConsole.MarkupLine($"[grey]via {Markup.Escape(engineOrder.Mechanism)}[/]");

        for (var i = 0; i < engineOrder.Entries.Count; i++)
            AnsiConsole.MarkupLine($"  {i}. {Markup.Escape(engineOrder.Entries[i])}");
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
