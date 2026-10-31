using System.ComponentModel;
using Modular.Switch.Totk;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk deploy [--target &lt;dir&gt;]</c>: links the emulator's TotK mod
/// folder to the merge output.
/// </summary>
[Description("Expose the TotK merge output to the emulator as one mod folder (symlink, or copy)")]
public sealed class TotkDeployCommand : AsyncCommand<TotkDeployCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--target <DIR>")]
        [Description("Mod folder to create (default: the emulator's load/0100F2C0115B6000/TKMM)")]
        public string? Target { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        var result = cli.Service.Deploy(settings.Target is null ? null : Path.GetFullPath(settings.Target));
        var target = Markup.Escape(result.Target);

        switch (result.Outcome)
        {
            case TotkDeployOutcome.Linked:
                AnsiConsole.MarkupLine($"[green]Linked[/] {Markup.Escape(target)} [grey]-> {Markup.Escape(cli.Service.MergedOutputFolder)}[/]");
                return 0;
            case TotkDeployOutcome.AlreadyLinked:
                AnsiConsole.MarkupLine($"[green]Already linked:[/] {Markup.Escape(target)}");
                return 0;
            case TotkDeployOutcome.Copied:
                AnsiConsole.MarkupLine($"[yellow]Copied[/] the merge output to {Markup.Escape(target)} [grey](symlinks unavailable; deploy again after each merge)[/]");
                return 0;
            case TotkDeployOutcome.Skipped:
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(result.Message ?? "Nothing to do.")}[/]");
                return 0;
            default:
                AnsiConsole.MarkupLine($"[red]Not deployed:[/] {Markup.Escape(result.Message ?? "")}");
                return 1;
        }
    });
}
