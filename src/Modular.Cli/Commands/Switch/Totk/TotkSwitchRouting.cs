using Modular.Switch.Models;
using Modular.Switch.Totk;
using Spectre.Console;
using TkSharp.Core.Models;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// What <c>switch install/remove/status/rollback --game 0100F2C0115B6000</c> do for Tears of
/// the Kingdom, which is merged rather than installed into per-mod slots.
/// </summary>
internal static class TotkSwitchRouting
{
    /// <summary>
    /// Imports the scanned mods into the TotK store and merges. A mod installed before is
    /// replaced in place (same TkSharp ID), and skipped when its source is unchanged.
    /// </summary>
    public static Task<int> InstallAsync(
        SwitchInstallState state, IReadOnlyList<SwitchMod> mods, bool dryRun, bool force, CancellationToken ct) =>
        TotkCli.RunAsync(async () =>
        {
            var cli = await TotkCli.OpenAsync();
            var service = cli.Service;
            var installed = 0;

            AnsiConsole.MarkupLine("[grey]Tears of the Kingdom: mods are merged into one output (TKMM rc1 model).[/]");

            foreach (var mod in mods)
            {
                if (!service.CanInstall(mod.SourcePath))
                {
                    AnsiConsole.MarkupLine($"[yellow]Skipped[/] {Markup.Escape(mod.Name)} [grey](not a folder, .zip, .rar, .7z or .tkcl)[/]");
                    continue;
                }

                var existing = ExistingMod(service, mod);
                if (existing != null && mod.IsInstalled && mod.InstalledHash == mod.SourceHash && !force)
                {
                    AnsiConsole.MarkupLine($"[grey]Unchanged:[/] {Markup.Escape(mod.Name)}");
                    continue;
                }

                if (dryRun)
                {
                    AnsiConsole.MarkupLine($"[cyan]Would {(existing is null ? "install" : "reinstall")}:[/] {Markup.Escape(mod.Name)}");
                    continue;
                }

                var result = await service.InstallAsync(mod.SourcePath, id: existing?.Id, progress: TotkCli.ConsoleProgress, ct: ct);
                if (result is null)
                {
                    AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(mod.Name)}");
                    continue;
                }

                mod.TotkModId = result.Id.ToString();
                mod.IsInstalled = true;
                mod.InstalledHash = mod.SourceHash;
                mod.InstalledAt = DateTime.UtcNow;
                state.Upsert(mod);
                installed++;
            }

            if (dryRun)
                return 0;

            await state.SaveAsync(ct: ct);
            await cli.SaveSettingsAsync();

            if (installed == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing new to install; not merging.[/]");
                return 0;
            }

            await service.MergeAsync(progress: TotkCli.ConsoleProgress, ct: ct);
            AnsiConsole.MarkupLine($"[green]{installed} installed and merged[/] [grey]into {Markup.Escape(service.MergedOutputFolder)}[/]");
            return 0;
        });

    /// <summary>
    /// Uninstalls the named mods (or all of them) from the TotK store, then merges once.
    /// Names match scanned mods (by key or name) and installed mods (by name or ID).
    /// </summary>
    public static Task<int> RemoveAsync(SwitchInstallState state, string? names, bool all, CancellationToken ct) =>
        TotkCli.RunAsync(async () =>
        {
            var cli = await TotkCli.OpenAsync();
            var service = cli.Service;
            var stateMods = state.ForTitle(TotkTitle.TitleId).ToList();
            var targets = new List<TkMod>();

            if (all)
            {
                targets.AddRange(service.ModManager.Mods);
            }
            else
            {
                foreach (var name in (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var fromState = stateMods.FirstOrDefault(m =>
                        m.ModKey.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                    var mod = fromState is null ? null : ExistingMod(service, fromState);
                    mod ??= cli.FindModOrReport(name);
                    if (mod != null && !targets.Contains(mod))
                        targets.Add(mod);
                }
            }

            if (targets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No installed mods matched the selection.[/]");
                return 0;
            }

            foreach (var mod in targets)
            {
                service.ModManager.Uninstall(mod);
                AnsiConsole.MarkupLine($"[green]Removed:[/] {Markup.Escape(mod.Name)}");

                foreach (var stateMod in stateMods.Where(m => m.TotkModId == mod.Id.ToString()))
                {
                    stateMod.IsInstalled = false;
                    stateMod.InstalledHash = string.Empty;
                    stateMod.TotkModId = null;
                    state.Upsert(stateMod);
                }
            }

            await state.SaveAsync(ct: ct);
            await service.MergeAsync(progress: TotkCli.ConsoleProgress, ct: ct);
            AnsiConsole.MarkupLine($"[grey]{targets.Count} removed; merged again.[/]");
            return 0;
        });

    /// <summary>Prints the current profile, the merge output, its snapshot and the deploy target.</summary>
    public static Task<int> StatusAsync() => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        var service = cli.Service;
        var target = TotkDeployer.ResolveTarget(service.Settings);
        var link = new DirectoryInfo(target).LinkTarget;

        AnsiConsole.MarkupLine($"[bold]TitleID:[/] {TotkTitle.TitleIdValue} [grey](Tears of the Kingdom, merge installer)[/]");
        AnsiConsole.MarkupLine($"[bold]Merge output:[/] {Markup.Escape(service.MergedOutputFolder)}");
        AnsiConsole.MarkupLine($"[bold]Emulator mod folder:[/] {Markup.Escape(target)} " + (
            link != null ? $"[green](linked -> {Markup.Escape(link)})[/]"
            : Directory.Exists(target) ? "[yellow](a folder, not a link)[/]"
            : "[grey](not deployed)[/]"));
        AnsiConsole.MarkupLine(service.Snapshot.Info is { } snapshot
            ? $"[bold]Rollback snapshot:[/] {snapshot.TakenAt.ToLocalTime():g} [grey]of {Markup.Escape(snapshot.MergeOutput)}[/]"
            : "[bold]Rollback snapshot:[/] [grey]none[/]");
        AnsiConsole.WriteLine();

        TotkListCommand.PrintProfile(service, all: true);
        return 0;
    });

    /// <summary>Puts back the merge output as it was before the latest merge.</summary>
    public static Task<int> RollbackAsync() => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.Service.RollbackMerge())
        {
            AnsiConsole.MarkupLine($"[green]Rolled back[/] {Markup.Escape(cli.Service.MergedOutputFolder)} [grey]to before the latest merge[/]");
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]No snapshot of the current merge output to roll back to.[/]");
        return 0;
    });

    private static TkMod? ExistingMod(TotkMergeService service, SwitchMod mod) =>
        Ulid.TryParse(mod.TotkModId, out var id) ? service.ModManager.Mods.FirstOrDefault(m => m.Id == id) : null;
}
