using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Uninstalls a previously installed mod by rolling back a changeset.
/// Removes installed files and restores backups.
/// </summary>
public class UninstallCommand : AsyncCommand<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[changeset-id]")]
        [Description("Changeset ID from the install operation")]
        public string? ChangesetId { get; init; }

        /// <summary>The game to remove a mod from.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Game to remove a mod from, used with --mod")]
        public string? Game { get; init; }

        /// <summary>The mod to remove.</summary>
        [CommandOption("--mod <MOD_ID>")]
        [Description("Mod id to remove from --game")]
        public string? ModId { get; init; }

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

            ModUninstallResult result;

            if (settings.Game != null || settings.ModId != null)
            {
                if (settings.Game == null || settings.ModId == null)
                {
                    AnsiConsole.MarkupLine("[red]--game and --mod must be given together.[/]");
                    await db.DisposeAsync();
                    services.Dispose();
                    return 1;
                }

                AnsiConsole.MarkupLine(
                    $"[bold]Removing[/] {Markup.Escape(settings.ModId)} " +
                    $"[bold]from[/] {Markup.Escape(settings.Game)}");
                AnsiConsole.WriteLine();

                // The same code path a changeset uninstall takes for a managed mod, so the
                // two cannot diverge.
                result = await installService.UninstallModAsync(
                    settings.Game, settings.ModId, cts.Token);
            }
            else if (!string.IsNullOrWhiteSpace(settings.ChangesetId))
            {
                AnsiConsole.MarkupLine($"[bold]Uninstalling changeset:[/] {settings.ChangesetId}");
                AnsiConsole.WriteLine();

                result = await installService.UninstallAsync(settings.ChangesetId, cts.Token);
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[red]Give a changeset id, or --game and --mod.[/]");
                await db.DisposeAsync();
                services.Dispose();
                return 1;
            }

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[green]Uninstall complete[/]");
                AnsiConsole.MarkupLine($"  Files removed:  {result.FilesRemoved}");
                AnsiConsole.MarkupLine($"  Files restored: {result.FilesRestored}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Uninstall failed:[/] {result.Error}");
                await db.DisposeAsync();
                services.Dispose();
                return 1;
            }

            await db.DisposeAsync();
            services.Dispose();
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Uninstall cancelled.[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            if (settings.Verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
