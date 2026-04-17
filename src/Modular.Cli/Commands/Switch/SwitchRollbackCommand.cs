using System.ComponentModel;
using Modular.Switch.Installer;
using Modular.Switch.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// modular switch rollback --game &lt;TITLE_ID&gt; [--mods &lt;ModA,...&gt;]
///
/// Restores the pre-install snapshot for each specified mod.
/// If --mods is omitted, rolls back ALL mods with a snapshot for this game.
/// </summary>
[Description("Rollback Switch mods to their pre-install snapshot")]
public sealed class SwitchRollbackCommand : AsyncCommand<SwitchRollbackCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <TITLE_ID>")]
        public string TitleId { get; init; } = string.Empty;

        [CommandOption("--mods <MODS>")]
        [Description("Comma-separated mod names/keys; omit to roll back all mods with a snapshot")]
        public string? Mods { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        if (!SwitchTitleId.TryParse(settings.TitleId, out var titleId))
        {
            AnsiConsole.MarkupLine($"[red]Invalid TitleID:[/] {settings.TitleId}");
            return 1;
        }

        var state = await SwitchInstallState.LoadAsync();
        var gameMods = state.ForTitle(titleId)
            .Where(m => !string.IsNullOrEmpty(m.SnapshotPath))
            .ToList();

        if (gameMods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No snapshots available for TitleID {titleId}.[/]");
            return 0;
        }

        List<SwitchMod> targets;
        if (!string.IsNullOrEmpty(settings.Mods))
        {
            var names = settings.Mods
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            targets = gameMods.Where(m =>
                names.Any(n =>
                    m.ModKey.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        else
        {
            targets = gameMods;
        }

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching mods with snapshots found.[/]");
            return 0;
        }

        var installer = new SwitchModInstaller();
        int ok = 0, fail = 0;

        foreach (var mod in targets)
        {
            AnsiConsole.MarkupLine($"[grey]Rolling back:[/] {mod.Name}");
            var result = await installer.RollbackAsync(mod);
            if (result.Success)
            {
                mod.IsInstalled   = false;
                mod.InstalledHash = string.Empty;
                mod.SnapshotPath  = string.Empty;
                state.Upsert(mod);
                AnsiConsole.MarkupLine($"  [green]✓[/] {mod.Name}");
                ok++;
            }
            else
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {mod.Name}: {result.Error}");
                fail++;
            }
        }

        await state.SaveAsync();
        AnsiConsole.MarkupLine($"\n[grey]{ok} rolled back, {fail} failed.[/]");
        return fail > 0 ? 1 : 0;
    }
}
