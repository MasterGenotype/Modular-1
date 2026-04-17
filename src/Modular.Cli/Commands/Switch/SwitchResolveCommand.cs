using System.ComponentModel;
using Modular.Switch.DependencyResolver;
using Modular.Switch.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// modular switch resolve --game &lt;TITLE_ID&gt; [--mods &lt;ModA,ModB,...&gt;]
///
/// Builds the dependency graph for a game, detects conflicts and cycles,
/// and prints the resolved install order.
/// </summary>
[Description("Resolve mod dependency graph and load order for a Switch game")]
public sealed class SwitchResolveCommand : AsyncCommand<SwitchResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <TITLE_ID>")]
        [Description("16-digit hex TitleID of the target Switch game")]
        public string TitleId { get; init; } = string.Empty;

        [CommandOption("--mods <MODS>")]
        [Description("Comma-separated mod keys (or names) to resolve; omit to resolve all known mods")]
        public string? Mods { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        if (!SwitchTitleId.TryParse(settings.TitleId, out var titleId))
        {
            AnsiConsole.MarkupLine($"[red]Invalid TitleID:[/] {settings.TitleId}");
            return 1;
        }

        var state = await SwitchInstallState.LoadAsync();
        var gameMods = state.ForTitle(titleId).ToList();

        if (gameMods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No mods found for TitleID {titleId}.[/]");
            AnsiConsole.MarkupLine("[grey]Run: modular switch scan <path> --game <TITLE_ID> first.[/]");
            return 1;
        }

        var graph = new SwitchDependencyGraph();
        graph.AddRange(gameMods);

        // Determine which mod keys to resolve
        IEnumerable<string> keys;
        if (!string.IsNullOrEmpty(settings.Mods))
        {
            keys = settings.Mods
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name =>
                {
                    // Allow partial name match if not an exact key
                    var exact = gameMods.FirstOrDefault(m =>
                        m.ModKey.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (exact != null) return exact.ModKey;

                    var partial = gameMods.FirstOrDefault(m =>
                        m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                    return partial?.ModKey ?? name;
                });
        }
        else
        {
            keys = gameMods.Select(m => m.ModKey);
        }

        var result = graph.Resolve(keys);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Resolution failed:[/] {result.Error}");

            if (result.MissingDependencies.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[yellow]Missing dependencies:[/]");
                foreach (var dep in result.MissingDependencies)
                    AnsiConsole.MarkupLine($"  [grey]• {dep}[/]");
            }
            if (result.Conflicts.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[red]Conflicts:[/]");
                foreach (var (a, b) in result.Conflicts)
                    AnsiConsole.MarkupLine($"  [red]• {a} ↔ {b}[/]");
            }
            if (result.Cycles.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[red]Circular dependencies:[/]");
                foreach (var c in result.Cycles)
                    AnsiConsole.MarkupLine($"  [red]• {c}[/]");
            }
            return 1;
        }

        // Success — print install order
        AnsiConsole.MarkupLine($"[green]Resolved {result.InstallOrder.Count} mod(s) for {titleId}[/]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Order[/]")
            .AddColumn("[bold]Mod Key[/]")
            .AddColumn("[bold]Category[/]")
            .AddColumn("[bold]Deps[/]");

        int order = 1;
        foreach (var mod in result.InstallOrder)
        {
            table.AddRow(
                $"[cyan]{order++}[/]",
                Markup.Escape(mod.ModKey),
                $"[yellow]{mod.Category}[/]",
                mod.Dependencies.Count > 0
                    ? Markup.Escape(string.Join(", ", mod.Dependencies))
                    : "[grey]—[/]");
        }
        AnsiConsole.Write(table);

        return 0;
    }
}
