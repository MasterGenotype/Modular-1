using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk export-sd &lt;drive&gt; [--yes]</c>: merges and copies the result to
/// an Atmosphère SD card.
/// </summary>
[Description("Merge and export TotK mods to an Atmosphère SD card")]
public sealed class TotkExportSdCommand : AsyncCommand<TotkExportSdCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<drive>")]
        [Description("Root of the SD card (must contain an atmosphere folder)")]
        public string Drive { get; init; } = string.Empty;

        [CommandOption("-y|--yes")]
        [Description("Don't ask before clearing the card's existing TotK contents")]
        public bool Yes { get; init; }

        [CommandOption("-v|--verbose")]
        public bool Verbose { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var ct = TotkCli.CancelOnCtrlC();
        var cli = await TotkCli.OpenAsync(settings.Verbose);

        var exported = await cli.Service.ExportToSdCardAsync(
            Path.GetFullPath(settings.Drive),
            (output, _) => Task.FromResult(settings.Yes || AnsiConsole.Confirm(
                $"Delete the existing romfs, romfslite, exefs and romfs_metadata.bin in {Markup.Escape(output)}?",
                defaultValue: false)),
            progress: TotkCli.ConsoleProgress,
            ct: ct);

        AnsiConsole.MarkupLine(exported
            ? "[green]Exported[/] to the SD card"
            : "[yellow]Not exported:[/] the merge and IPS patches were written, the card's contents were left as they were.");
        return exported ? 0 : 1;
    });
}
