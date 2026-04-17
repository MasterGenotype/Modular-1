using System.ComponentModel;
using Modular.Switch.Installer;
using Modular.Switch.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// modular switch status --game &lt;TITLE_ID&gt;
///
/// Prints a table of all mods known for the game, showing install status,
/// hash match, Yuzu slot presence, and snapshot availability.
/// </summary>
[Description("Show installation status of Switch mods for a game")]
public sealed class SwitchStatusCommand : AsyncCommand<SwitchStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <TITLE_ID>")]
        public string TitleId { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        if (!SwitchTitleId.TryParse(settings.TitleId, out var titleId))
        {
            AnsiConsole.MarkupLine($"[red]Invalid TitleID:[/] {settings.TitleId}");
            return 1;
        }

        var state = await SwitchInstallState.LoadAsync();
        var gameMods = state.ForTitle(titleId).OrderBy(m => m.LoadOrder).ThenBy(m => m.Name).ToList();

        if (gameMods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No mods registered for {titleId}.[/]");
            AnsiConsole.MarkupLine("[grey]Run: modular switch scan <path> --game <TITLE_ID>[/]");
            return 0;
        }

        // Header info
        var yuzu = YuzuPaths.DataRoot;
        AnsiConsole.MarkupLine($"[bold]TitleID:[/] {titleId}");
        AnsiConsole.MarkupLine($"[bold]Yuzu load dir:[/] {YuzuPaths.LoadDir(titleId)}");
        AnsiConsole.MarkupLine($"[bold]Known mods:[/] {gameMods.Count}");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Mod[/]")
            .AddColumn("[bold]Category[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Hash Match[/]")
            .AddColumn("[bold]Slot Dir[/]")
            .AddColumn("[bold]Snapshot[/]");

        foreach (var mod in gameMods)
        {
            var slotDir  = YuzuPaths.ModSlotDir(titleId, mod.Name);
            var slotExist = Directory.Exists(slotDir);
            var hashMatch = mod.IsInstalled && mod.InstalledHash == mod.SourceHash;
            var hasSnap  = !string.IsNullOrEmpty(mod.SnapshotPath) && Directory.Exists(mod.SnapshotPath);

            string statusMarkup = mod.IsInstalled
                ? "[green]Installed[/]"
                : "[grey]Not installed[/]";

            string hashMarkup = mod.IsInstalled
                ? (hashMatch ? "[green]✓[/]" : "[yellow]Changed[/]")
                : "[grey]—[/]";

            string slotMarkup = slotExist ? "[green]✓[/]" : "[grey]✗[/]";
            string snapMarkup = hasSnap   ? "[cyan]✓[/]"  : "[grey]—[/]";

            table.AddRow(
                Markup.Escape(mod.Name),
                mod.Category.ToString(),
                mod.Version,
                statusMarkup,
                hashMarkup,
                slotMarkup,
                snapMarkup);
        }

        AnsiConsole.Write(table);

        var installed = gameMods.Count(m => m.IsInstalled);
        AnsiConsole.MarkupLine(
            $"\n[green]{installed} installed[/] / [grey]{gameMods.Count - installed} not installed[/]");

        return 0;
    }
}
