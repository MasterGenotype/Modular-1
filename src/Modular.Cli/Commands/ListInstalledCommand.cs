using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.InGame;
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

            var installService = services.CreateInstallationService(db);

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
                        AnsiConsole.MarkupLine($"[red]Could not find game:[/] {Markup.Escape(settings.Game)}");
                        return 1;
                    }
                }
            }

            var installed = await installService.ListInstalledAsync(targetDir, cts.Token);
            var interrupted = await installService.ListInterruptedAsync(targetDir, cts.Token);

            if (installed.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No installed mods found.[/]");
                WriteInterrupted(interrupted);
                await WriteGameOwnedAsync(db, installService, settings.Game, cts.Token);
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
                    Markup.Escape(changeset.ChangesetId),
                    changeset.ModId != null ? Markup.Escape(changeset.ModId) : "[grey]-[/]",
                    changeset.ArchivePath != null ? Markup.Escape(Path.GetFileName(changeset.ArchivePath)) : "[grey]-[/]",
                    changeset.TargetDirectory != null ? Markup.Escape(ShortenPath(changeset.TargetDirectory)) : "[grey]-[/]",
                    FormatDate(changeset.CreatedAtUtc));
            }

            AnsiConsole.MarkupLine($"[bold]Installed mods ({installed.Count}):[/]");
            AnsiConsole.Write(table);
            WriteInterrupted(interrupted);
            await WriteGameOwnedAsync(db, installService, settings.Game, cts.Token);

            await db.DisposeAsync();
            services.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
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

    /// <summary>
    /// Names the installs that stopped before committing, which no other listing shows.
    /// </summary>
    // Mods Baldur's Gate 3's own mod manager installs from mod.io subscriptions: Modular
    // records them but never deploys them, so they have no changeset.
    private static async Task WriteGameOwnedAsync(
        ModularDatabase db, ModInstallationService installService, string? game, CancellationToken ct)
    {
        var gameKey = InGameModManagers.BaldursGate3SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (game != null && !Directory.Exists(game))
        {
            var installation = await installService.Deployment.ResolveInstallationAsync(game, ct);
            if (installation == null || InGameModManagers.For(installation) == null)
                return;
            gameKey = installation.GameKey;
        }

        var owned = await new GameOwnedModRepository(db).ListAsync(gameKey, ct);
        if (owned.Count == 0)
            return;

        var table = new Table().AddColumns("mod.io id", "Mod", "File", "Version", "State");
        foreach (var mod in owned)
        {
            table.AddRow(
                Markup.Escape(mod.RemoteModId),
                Markup.Escape(mod.DisplayName ?? "-"),
                Markup.Escape(mod.FileName ?? "-"),
                InGameModService.FormatVersion(mod.Version64),
                mod.State == GameOwnedModState.Installed ? "[green]in game[/]" : "[yellow]downloads on next launch[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Installed by Baldur's Gate 3's mod manager ({owned.Count}):[/] [grey]as of the last 'modular sync'[/]");
        AnsiConsole.Write(table);
    }

    private static void WriteInterrupted(List<ChangesetRecord> interrupted)
    {
        if (interrupted.Count == 0)
            return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]{interrupted.Count} install(s) stopped before finishing[/] " +
            "[grey](clear one with 'modular uninstall <changeset>')[/]:");
        foreach (var changeset in interrupted)
        {
            AnsiConsole.MarkupLine(
                $"  {Markup.Escape(changeset.ChangesetId)} " +
                $"{Markup.Escape(changeset.ModId ?? Path.GetFileName(changeset.ArchivePath ?? "?"))} " +
                $"[grey]({changeset.State.ToString().ToLowerInvariant()}, {FormatDate(changeset.CreatedAtUtc)})[/]");
        }
    }
}
