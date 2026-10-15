using System.ComponentModel;
using Modular.Switch.Totk;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk list [--all]</c>: the current profile's mods in priority order.
/// </summary>
[Description("List the current profile's TotK mods in priority order")]
public sealed class TotkListCommand : AsyncCommand<TotkListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--all")]
        [Description("List every installed mod, including those not in the current profile")]
        public bool All { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        PrintProfile(cli.Service, settings.All);
        return 0;
    });

    /// <summary>Prints the current profile's mods (and, with <paramref name="all"/>, the rest).</summary>
    internal static void PrintProfile(TotkMergeService service, bool all)
    {
        var profile = service.CurrentProfile;

        AnsiConsole.MarkupLine($"[bold]Profile:[/] {Markup.Escape(profile.Name)}  [grey](1 = highest priority)[/]");
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("[bold]#[/]").AddColumn("[bold]On[/]").AddColumn("[bold]Mod[/]")
            .AddColumn("[bold]Version[/]").AddColumn("[bold]Options[/]").AddColumn("[bold]ID[/]");

        for (var i = 0; i < profile.Mods.Count; i++)
        {
            var pm = profile.Mods[i];
            var options = pm.SelectedOptions.Values.SelectMany(o => o).Select(o => o.Name).ToList();
            table.AddRow(
                (i + 1).ToString(),
                pm.IsEnabled ? "[green]✓[/]" : "[grey]✗[/]",
                Markup.Escape(pm.Mod.Name),
                Markup.Escape(pm.Mod.Version),
                options.Count == 0 ? "[grey]—[/]" : Markup.Escape(string.Join(", ", options)),
                $"[grey]{pm.Mod.Id}[/]");
        }

        if (profile.Mods.Count == 0)
            table.AddRow("", "", "[grey](no mods; install with: modular switch totk install <path>)[/]", "", "", "");
        AnsiConsole.Write(table);

        if (all)
        {
            var others = service.ModManager.Mods
                .Where(m => TotkMergeService.FindProfileMod(profile, m) is null)
                .ToList();
            AnsiConsole.MarkupLine(others.Count == 0
                ? "[grey]Every installed mod is in this profile.[/]"
                : "[bold]Installed, not in this profile:[/]");
            foreach (var mod in others)
                AnsiConsole.MarkupLine($"  {Markup.Escape(mod.Name)} [grey]{mod.Id}[/]");
        }
    }
}
