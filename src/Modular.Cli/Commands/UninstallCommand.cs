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
        [CommandArgument(0, "<changeset-id>")]
        [Description("Changeset ID from the install operation")]
        public string ChangesetId { get; init; } = string.Empty;

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

            AnsiConsole.MarkupLine($"[bold]Uninstalling changeset:[/] {settings.ChangesetId}");
            AnsiConsole.WriteLine();

            var result = await installService.UninstallAsync(settings.ChangesetId, cts.Token);

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
