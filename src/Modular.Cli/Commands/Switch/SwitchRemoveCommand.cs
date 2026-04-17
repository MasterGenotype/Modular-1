using System.ComponentModel;
using Modular.Switch.Installer;
using Modular.Switch.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// modular switch remove --game &lt;TITLE_ID&gt; --mods &lt;ModA,ModB,...&gt;
///
/// Removes one or more mod slot directories from Yuzu's load directory.
/// The mod remains known to Modular (state is preserved but IsInstalled → false)
/// so it can be re-installed later.
/// </summary>
[Description("Remove installed Switch mods from Yuzu's load directory")]
public sealed class SwitchRemoveCommand : AsyncCommand<SwitchRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <TITLE_ID>")]
        [Description("16-digit hex TitleID")]
        public string TitleId { get; init; } = string.Empty;

        [CommandOption("--mods <MODS>")]
        [Description("Comma-separated mod names/keys to remove")]
        public string Mods { get; init; } = string.Empty;

        [CommandOption("--all")]
        [Description("Remove ALL installed mods for this game")]
        public bool All { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        if (!SwitchTitleId.TryParse(settings.TitleId, out var titleId))
        {
            AnsiConsole.MarkupLine($"[red]Invalid TitleID:[/] {settings.TitleId}");
            return 1;
        }

        if (string.IsNullOrEmpty(settings.Mods) && !settings.All)
        {
            AnsiConsole.MarkupLine("[red]Specify --mods <names> or --all[/]");
            return 1;
        }

        var state = await SwitchInstallState.LoadAsync();
        var gameMods = state.ForTitle(titleId).Where(m => m.IsInstalled).ToList();

        List<SwitchMod> targets;
        if (settings.All)
        {
            targets = gameMods;
        }
        else
        {
            var names = settings.Mods
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            targets = gameMods.Where(m =>
                names.Any(n =>
                    m.ModKey.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();

            var notFound = names.Where(n =>
                !targets.Any(m =>
                    m.ModKey.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();

            if (notFound.Count > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Mods not found (or not installed):[/] {string.Join(", ", notFound)}");
            }
        }

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No installed mods matched the selection.[/]");
            return 0;
        }

        var installer = new SwitchModInstaller();
        int removed = 0, failed = 0;

        foreach (var mod in targets)
        {
            var result = await installer.RemoveAsync(mod);
            if (result.Success || result.Skipped)
            {
                mod.IsInstalled   = false;
                mod.InstalledHash = string.Empty;
                mod.SnapshotPath  = string.Empty;
                state.Upsert(mod);
                AnsiConsole.MarkupLine($"[green]Removed:[/] {mod.Name}");
                removed++;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to remove {mod.Name}:[/] {result.Error}");
                failed++;
            }
        }

        await state.SaveAsync();
        AnsiConsole.MarkupLine($"\n[grey]{removed} removed, {failed} failed.[/]");
        return failed > 0 ? 1 : 0;
    }
}
