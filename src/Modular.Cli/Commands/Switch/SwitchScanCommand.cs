using System.ComponentModel;
using Modular.Switch.Models;
using Modular.Switch.Scanner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// modular switch scan [&lt;search-path&gt;] [--game &lt;TITLE_ID&gt;]
///
/// Scans a directory tree for Switch mods and prints discovered metadata.
/// Results are persisted into the Switch state file so subsequent commands
/// can reference them by name / key.
/// </summary>
[Description("Discover and catalogue Switch mods in a directory")]
public sealed class SwitchScanCommand : AsyncCommand<SwitchScanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[search-path]")]
        [Description("Root directory to scan for mods (default: current directory)")]
        public string SearchPath { get; init; } = Directory.GetCurrentDirectory();

        [CommandOption("--game <TITLE_ID>")]
        [Description("Filter results to a specific 16-digit hex TitleID")]
        public string? TitleId { get; init; }

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        SwitchTitleId? filter = null;
        if (settings.TitleId != null)
        {
            if (!SwitchTitleId.TryParse(settings.TitleId, out var tid))
            {
                AnsiConsole.MarkupLine($"[red]Invalid TitleID:[/] {settings.TitleId}");
                return 1;
            }
            filter = tid;
        }

        var searchPath = Path.GetFullPath(settings.SearchPath);
        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
        {
            AnsiConsole.MarkupLine($"[red]Path not found:[/] {searchPath}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[grey]Scanning:[/] {searchPath}");
        AnsiConsole.WriteLine();

        var scanner = new SwitchModScanner();
        List<SwitchMod> mods = [];

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scanning for Switch mods...", async ctx2 =>
            {
                var progress = new Progress<string>(msg => ctx2.Status(msg));
                mods = await scanner.ScanAsync(searchPath, filter, progress, cts.Token);
            });

        if (mods.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Switch mods discovered.[/]");
            AnsiConsole.MarkupLine("[grey]Tip: Mods need a TitleID in their name, a manifest.json, or romfs/exefs/cheats sub-directories.[/]");
            return 0;
        }

        // Render table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Mod Name[/]")
            .AddColumn("[bold]TitleID[/]")
            .AddColumn("[bold]Category[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]Source[/]");

        foreach (var mod in mods)
        {
            table.AddRow(
                Markup.Escape(mod.Name),
                $"[cyan]{mod.TitleId}[/]",
                $"[yellow]{mod.Category}[/]",
                mod.Version,
                $"[grey]{Path.GetFileName(mod.SourcePath)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[green]{mods.Count} mod(s) discovered.[/]");

        // Persist to state
        var state = await SwitchInstallState.LoadAsync(ct: cts.Token);
        foreach (var mod in mods)
            state.Upsert(mod);
        await state.SaveAsync(ct: cts.Token);
        AnsiConsole.MarkupLine("[grey]State updated.[/]");

        return 0;
    }
}
