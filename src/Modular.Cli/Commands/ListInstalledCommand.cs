using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Lists installed mods and their changesets, optionally filtered by game.
/// </summary>
public class ListInstalledCommand : AsyncCommand<ListInstalledCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <GAME>")]
        [Description("Filter by game (Steam AppID, game name, or directory path)")]
        public string? Game { get; init; }

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
            var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);

            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "modular.db");
            var db = new ModularDatabase(dbPath);
            await db.InitializeAsync();

            var installService = new ModInstallationService(db, services.Telemetry);

            // Resolve game directory if specified
            string? targetDir = null;
            if (settings.Game != null)
            {
                if (Directory.Exists(settings.Game))
                {
                    targetDir = Path.GetFullPath(settings.Game);
                }
                else
                {
                    targetDir = await ModInstallationService.ResolveGameDirectoryAsync(
                        settings.Game, cts.Token);
                    if (targetDir == null)
                    {
                        AnsiConsole.MarkupLine($"[red]Could not find game:[/] {settings.Game}");
                        return 1;
                    }
                }
            }

            var installed = await installService.ListInstalledAsync(targetDir, cts.Token);

            if (installed.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No installed mods found.[/]");
                await db.DisposeAsync();
                services.Dispose();
                return 0;
            }

            var table = new Table();
            table.AddColumn("Changeset");
            table.AddColumn("Mod");
            table.AddColumn("Archive");
            table.AddColumn("Target");
            table.AddColumn("Installed");

            foreach (var changeset in installed)
            {
                table.AddRow(
                    changeset.ChangesetId,
                    changeset.ModId ?? "[grey]-[/]",
                    changeset.ArchivePath != null ? Path.GetFileName(changeset.ArchivePath) : "[grey]-[/]",
                    changeset.TargetDirectory != null ? ShortenPath(changeset.TargetDirectory) : "[grey]-[/]",
                    FormatDate(changeset.CreatedAtUtc));
            }

            AnsiConsole.MarkupLine($"[bold]Installed mods ({installed.Count}):[/]");
            AnsiConsole.Write(table);

            await db.DisposeAsync();
            services.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            if (settings.Verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home)
            ? "~" + path[home.Length..]
            : path;
    }

    private static string FormatDate(string isoDate)
    {
        return DateTime.TryParse(isoDate, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : isoDate;
    }
}
