using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk uninstall &lt;mod&gt;</c>: removes a mod from the store and every
/// profile, then merges again.
/// </summary>
[Description("Uninstall a TotK mod from every profile, then merge again")]
public sealed class TotkUninstallCommand : AsyncCommand<TotkUninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<mod>")]
        [Description("Mod name or ID")]
        public string Mod { get; init; } = string.Empty;

        [CommandOption("-v|--verbose")]
        public bool Verbose { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var ct = TotkCli.CancelOnCtrlC();
        var cli = await TotkCli.OpenAsync(settings.Verbose);
        if (cli.FindModOrReport(settings.Mod) is not { } mod)
            return 1;

        await cli.Service.UninstallAsync(mod, TotkCli.ConsoleProgress, ct);
        AnsiConsole.MarkupLine($"[green]Uninstalled[/] {Markup.Escape(mod.Name)} [grey]and merged again[/]");
        return 0;
    });
}
