using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// Top-level branch command for all Nintendo Switch mod operations.
/// Registered as "switch" in Program.cs.
///
/// Usage:
///   modular switch scan    [&lt;path&gt;] [--game &lt;TITLE_ID&gt;]
///   modular switch resolve --game &lt;TITLE_ID&gt; [--mods &lt;A,B&gt;]
///   modular switch install --game &lt;TITLE_ID&gt; [--mods &lt;A,B&gt;] [--runner lutris] [--dry-run]
///   modular switch remove  --game &lt;TITLE_ID&gt; (--mods &lt;A,B&gt; | --all)
///   modular switch rollback --game &lt;TITLE_ID&gt; [--mods &lt;A,B&gt;]
///   modular switch status  --game &lt;TITLE_ID&gt;
/// </summary>
[Description("Manage Nintendo Switch mods for Yuzu-emulated games")]
public sealed class SwitchCommand : Command<SwitchCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        AnsiConsole.MarkupLine("[bold]Modular — Switch Mod Pipeline[/]");
        AnsiConsole.MarkupLine("\n[grey]Available sub-commands:[/]");
        AnsiConsole.MarkupLine("  [cyan]scan[/]      Discover mods in a directory");
        AnsiConsole.MarkupLine("  [cyan]resolve[/]   Show resolved dependency / load order");
        AnsiConsole.MarkupLine("  [cyan]install[/]   Install mods into Yuzu's load directory");
        AnsiConsole.MarkupLine("  [cyan]remove[/]    Remove installed mods");
        AnsiConsole.MarkupLine("  [cyan]rollback[/]  Restore pre-install snapshots");
        AnsiConsole.MarkupLine("  [cyan]status[/]    Show per-game mod status");
        AnsiConsole.MarkupLine("\nRun [grey]modular switch <sub-command> --help[/] for details.");
        return 0;
    }
}
