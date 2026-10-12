using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Dependencies;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Lists what installed mods need and do not have.
/// </summary>
public class DependenciesCommand : AsyncCommand<DependenciesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <GAME>")]
        [Description("Check only this game's mods (Steam AppID, game name, or directory path)")]
        public string? Game { get; init; }

        [CommandOption("--mod <NAME>")]
        [Description("Check only mods whose name contains this text")]
        public string? Mod { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var services = await RuntimeServices.InitializeAsync(settings.Verbose);

            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "modular.db");
            var db = new ModularDatabase(dbPath);
            await db.InitializeAsync();

            var installService = services.CreateInstallationService(db);

            string? targetDir = null;
            if (settings.Game != null)
            {
                targetDir = Directory.Exists(settings.Game)
                    ? Path.GetFullPath(settings.Game)
                    : await ModInstallationService.ResolveGameDirectoryAsync(settings.Game, cts.Token);

                if (targetDir == null)
                {
                    AnsiConsole.MarkupLine($"[red]Could not find game:[/] {Markup.Escape(settings.Game)}");
                    return 1;
                }
            }

            var installed = await installService.ListInstalledAsync(ct: cts.Token);
            var inGame = targetDir == null
                ? installed
                : await installService.ListInstalledAsync(targetDir, cts.Token);

            var targets = settings.Mod == null
                ? inGame
                : inGame.Where(c =>
                    (c.ModId ?? Path.GetFileName(c.ArchivePath) ?? string.Empty)
                    .Contains(settings.Mod, StringComparison.OrdinalIgnoreCase)).ToList();

            if (targets.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No installed mods to check.[/]");
                await db.DisposeAsync();
                services.Dispose();
                return 0;
            }

            var service = new MissingDependencyService(
                services.CreateBackendRegistry().GetAll(),
                installService.ResolveGameAsync,
                services.Settings.ModsDirectory);

            MissingDependencyReport report = null!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Checking {targets.Count} mod(s)...", async _ =>
                {
                    // A check narrowed to one mod reports that mod's needs; a check of
                    // everything also covers mods other managers put in place.
                    report = await service.FindAsync(
                        targets, installed, includeUnmanaged: settings.Mod == null, cts.Token);
                });

            Render(report);

            await services.SaveStateAsync();
            await db.DisposeAsync();
            services.Dispose();

            // Non-zero when something is missing, so a script can act on it.
            return report.Items.Count == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            if (settings.Verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static void Render(MissingDependencyReport report)
    {
        if (report.Items.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[green]No missing dependencies[/] [grey]({report.ModsChecked} mod(s) checked on a mod hub)[/]");
        }
        else
        {
            var table = new Table();
            table.AddColumn("Missing");
            table.AddColumn("Needed by");
            table.AddColumn("How to get it");

            foreach (var item in report.Items)
            {
                var how = item.Kind switch
                {
                    MissingDependencyKind.Downloadable when item.DownloadedArchives.Count > 0 =>
                        "already downloaded, not installed: " +
                        string.Join("; ", item.DownloadedArchives.Select(Path.GetFileName)),
                    MissingDependencyKind.Downloadable => item.Url ?? $"{item.BackendId} mod {item.ModId}",
                    MissingDependencyKind.External => item.Url ?? "no link given",
                    _ => item.Candidates.Count > 0
                        ? "possibly: " + string.Join("; ", item.Candidates.Select(c => c.Url ?? c.Name))
                        : "not found by name"
                };

                if (item.Notes.Count > 0)
                    how += "\n" + string.Join("\n", item.Notes);

                table.AddRow(
                    Markup.Escape(item.Optional ? $"{item.Name} (optional)" : item.Name),
                    Markup.Escape(string.Join(", ", item.RequiredBy)),
                    Markup.Escape(how));
            }

            AnsiConsole.MarkupLine($"[bold]Missing dependencies ({report.Items.Count}):[/]");
            AnsiConsole.Write(table);
        }

        foreach (var conflict in report.Conflicts)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]![/] '{Markup.Escape(conflict.DeclaredBy)}' says it cannot be used with " +
                $"'{Markup.Escape(conflict.ConflictsWith)}'");
        }

        if (report.SatisfiedElsewhere.Count > 0)
        {
            AnsiConsole.MarkupLine(
                "[grey]Taken as installed, by name, from the game's mod folder: " +
                Markup.Escape(string.Join(", ", report.SatisfiedElsewhere)) + "[/]");
        }

        if (report.Untraceable.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{report.Untraceable.Count} mod(s) could not be traced to a mod page, " +
                "so their listed requirements are unknown.[/]");
        }

        foreach (var error in report.Errors)
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(error)}");
    }
}
