using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk install &lt;path&gt; [--merge]</c>: imports a mod into the current
/// profile.
/// </summary>
[Description("Import a TotK mod (folder, .zip, .rar, .7z or .tkcl) into the current profile")]
public sealed class TotkInstallCommand : AsyncCommand<TotkInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Mod folder (containing romfs/exefs/cheats) or archive")]
        public string Path { get; init; } = string.Empty;

        [CommandOption("--merge")]
        [Description("Merge the profile afterwards")]
        public bool Merge { get; init; }

        [CommandOption("-v|--verbose")]
        public bool Verbose { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var ct = TotkCli.CancelOnCtrlC();
        var input = System.IO.Path.GetFullPath(settings.Path);
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Not found:[/] {Markup.Escape(input)}");
            return 1;
        }

        var cli = await TotkCli.OpenAsync(settings.Verbose);
        var mod = await cli.Service.InstallAsync(input, progress: TotkCli.ConsoleProgress, ct: ct);
        await cli.SaveSettingsAsync();

        if (mod is null)
        {
            AnsiConsole.MarkupLine($"[red]Could not install[/] {Markup.Escape(input)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Installed[/] {Markup.Escape(mod.Name)} [grey]({mod.Id}) into {Markup.Escape(cli.Service.CurrentProfile.Name)}[/]");

        if (!settings.Merge)
        {
            AnsiConsole.MarkupLine("[grey]Merge with: modular switch totk merge[/]");
            return 0;
        }

        await cli.Service.MergeAsync(progress: TotkCli.ConsoleProgress, ct: ct);
        AnsiConsole.MarkupLine($"[green]Merged[/] [grey]into {Markup.Escape(cli.Service.MergedOutputFolder)}[/]");
        return 0;
    });
}
