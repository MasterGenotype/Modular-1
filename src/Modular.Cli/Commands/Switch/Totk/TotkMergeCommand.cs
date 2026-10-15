using System.ComponentModel;
using Modular.Switch.Totk;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk merge [--dry-run]</c>: merges the current profile's enabled mods.
/// </summary>
[Description("Merge the current profile's enabled TotK mods into the merge output")]
public sealed class TotkMergeCommand : AsyncCommand<TotkMergeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dry-run")]
        [Description("Show what would be merged and replaced, and check the game, without writing anything")]
        public bool DryRun { get; init; }

        [CommandOption("-v|--verbose")]
        public bool Verbose { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var ct = TotkCli.CancelOnCtrlC();
        var cli = await TotkCli.OpenAsync(settings.Verbose);
        var service = cli.Service;
        var profile = service.CurrentProfile;
        var output = service.MergedOutputFolder;

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[bold]Profile:[/] {Markup.Escape(profile.Name)}");
            AnsiConsole.MarkupLine($"[bold]Output:[/] {Markup.Escape(output)}");
            AnsiConsole.MarkupLine("[bold]Mods, highest priority first:[/]");
            foreach (var pm in profile.Mods.Where(m => m.IsEnabled))
                AnsiConsole.MarkupLine($"  {Markup.Escape(pm.Mod.Name)}");

            var replaced = TotkMergeSnapshot.MergeTargets
                .Where(t => File.Exists(Path.Combine(output, t)) || Directory.Exists(Path.Combine(output, t)))
                .ToList();
            AnsiConsole.MarkupLine(replaced.Count == 0
                ? "[grey]Nothing in the output would be replaced.[/]"
                : $"[bold]Would replace:[/] {Markup.Escape(string.Join(", ", replaced))} [grey](snapshotted first)[/]");
            if (service.Settings.UseRomfslite)
                AnsiConsole.MarkupLine("[grey]romfs would then be renamed to romfslite.[/]");

            return cli.ReportRom() ? 0 : 1;
        }

        await service.MergeAsync(progress: TotkCli.ConsoleProgress, ct: ct);
        AnsiConsole.MarkupLine($"[green]Merged[/] {Markup.Escape(profile.Name)} [grey]into {Markup.Escape(output)}[/]");
        AnsiConsole.MarkupLine("[grey]Undo with: modular switch rollback --game 0100F2C0115B6000. " +
                               "Expose it to the emulator with: modular switch totk deploy[/]");
        return 0;
    });
}
