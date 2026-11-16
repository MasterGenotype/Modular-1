using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Brings a game's archives down to one copy of each installed mod (<see cref="ArchivePruner"/>).
/// </summary>
public class ArchivesPruneCommand : AsyncCommand<ArchivesPruneCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game whose archives to prune.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Game key (Steam AppID) whose archives to prune")]
        public string? Game { get; init; }

        /// <summary>Also delete archives no installed mod came from.</summary>
        [CommandOption("--remove-unused")]
        [Description("Also delete the game's other archives: older versions, uninstalled mods, downloads never installed")]
        public bool RemoveUnused { get; init; }

        /// <summary>Act instead of reporting.</summary>
        [CommandOption("--apply")]
        [Description("Release and delete; without it, only report what would be done")]
        public bool Apply { get; init; }

        [CommandOption("--verbose")]
        [Description("List every archive")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            AnsiConsole.MarkupLine("[red]Give the game with --game.[/]");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "modular.db");
        await using var db = new ModularDatabase(dbPath);
        await db.InitializeAsync();

        var progress = new Progress<string>(m => { if (settings.Verbose) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(m)}[/]"); });
        var result = await new ArchivePruner(db).PruneAsync(
            settings.Game, services.Settings.ModsDirectory, archiveStorePath: null,
            settings.RemoveUnused, settings.Apply, progress, cts.Token);

        if (settings.Verbose)
        {
            foreach (var item in result.Items)
            {
                AnsiConsole.MarkupLine(
                    $"{(item.Action == ArchivePruneAction.Release ? "[blue]release[/]" : "[red]remove [/]")} " +
                    $"{FormatSize(item.Bytes),10}  {Markup.Escape(item.Path)}");
            }
        }

        var released = result.Items.Where(i => i.Action == ArchivePruneAction.Release).ToList();
        var removed = result.Items.Where(i => i.Action == ArchivePruneAction.Remove).ToList();
        var verb = settings.Apply ? "" : "would be ";
        AnsiConsole.MarkupLine(
            $"[bold]{released.Count}[/] archive(s) of installed mods {verb}released ({FormatSize(released.Sum(i => i.Bytes))})");
        if (settings.RemoveUnused)
        {
            AnsiConsole.MarkupLine(
                $"[bold]{removed.Count}[/] unused archive(s) {verb}removed ({FormatSize(removed.Sum(i => i.Bytes))})");
        }

        AnsiConsole.MarkupLine($"[green]{FormatSize(result.BytesFreed)}[/] {(settings.Apply ? "freed" : "to free")}");

        if (result.Unstaged.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{result.Unstaged.Count} installed mod(s) have no staged files; their archives were kept:[/] " +
                Markup.Escape(string.Join(", ", result.Unstaged.Take(20))));
        }

        foreach (var error in result.Errors)
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");

        if (!settings.Apply)
            AnsiConsole.MarkupLine("[grey]Nothing was changed. Run again with --apply.[/]");

        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GiB" : $"{bytes / (double)(1L << 20):F1} MiB";
}
