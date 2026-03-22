using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Sdk.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Installs a mod archive to a Steam game directory with dependency tracking and rollback support.
/// </summary>
public class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<archive>")]
        [Description("Path to mod archive file (.zip, .7z, .rar)")]
        public string ArchivePath { get; init; } = string.Empty;

        [CommandOption("--game <GAME>")]
        [Description("Target game (Steam AppID, game name, or directory path)")]
        public string? Game { get; init; }

        [CommandOption("--mod-id <ID>")]
        [Description("Optional mod identifier for tracking")]
        public string? ModId { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be installed without installing")]
        public bool DryRun { get; init; }

        [CommandOption("--force")]
        [Description("Overwrite existing files")]
        public bool Force { get; init; }

        [CommandOption("--no-backup")]
        [Description("Do not create backups of overwritten files")]
        public bool NoBackup { get; init; }

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
            // Validate archive exists
            var archivePath = Path.GetFullPath(settings.ArchivePath);
            if (!File.Exists(archivePath))
            {
                AnsiConsole.MarkupLine($"[red]Archive not found:[/] {archivePath}");
                return 1;
            }

            // Resolve target game directory
            string targetDirectory;
            if (settings.Game != null)
            {
                if (Directory.Exists(settings.Game))
                {
                    targetDirectory = Path.GetFullPath(settings.Game);
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]Scanning Steam libraries...[/]");
                    var resolved = await ModInstallationService.ResolveGameDirectoryAsync(settings.Game, cts.Token);
                    if (resolved == null)
                    {
                        AnsiConsole.MarkupLine($"[red]Could not find Steam game:[/] {settings.Game}");
                        AnsiConsole.MarkupLine("[grey]Tip: Use a Steam AppID, game name, or full directory path.[/]");
                        return 1;
                    }
                    targetDirectory = resolved;
                    AnsiConsole.MarkupLine($"[green]Found game at:[/] {targetDirectory}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[red]--game is required.[/] Specify a Steam AppID, game name, or directory path.");
                return 1;
            }

            // Initialize services
            var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);

            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "modular.db");
            var db = new ModularDatabase(dbPath);
            await db.InitializeAsync();

            var installService = new ModInstallationService(db, services.Telemetry);

            var options = new ModInstallationOptions
            {
                ModId = settings.ModId ?? Path.GetFileNameWithoutExtension(archivePath),
                AllowOverwrite = settings.Force,
                CreateBackups = !settings.NoBackup,
                DryRun = settings.DryRun
            };

            // Show install header
            AnsiConsole.MarkupLine($"[bold]Installing:[/] {Path.GetFileName(archivePath)}");
            AnsiConsole.MarkupLine($"[bold]Target:[/]     {targetDirectory}");
            if (settings.DryRun)
                AnsiConsole.MarkupLine("[yellow]DRY RUN — no files will be modified[/]");
            AnsiConsole.WriteLine();

            // Execute installation with progress
            ModInstallationResult? result = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Installing...", async ctx =>
                {
                    var progress = new Progress<InstallProgress>(p =>
                    {
                        var status = p.CurrentFile != null
                            ? $"{p.CurrentOperation} ({p.FilesProcessed}/{p.TotalFiles}) {p.CurrentFile}"
                            : p.CurrentOperation;
                        ctx.Status(status);
                    });

                    result = await installService.InstallAsync(
                        archivePath, targetDirectory, options, progress, cts.Token);
                });

            // Display results
            if (result!.DryRun && result.Success)
            {
                AnsiConsole.MarkupLine($"[green]Would install {result.PlannedOperations.Count} files:[/]");
                foreach (var file in result.PlannedOperations.Take(20))
                    AnsiConsole.MarkupLine($"  [grey]{file}[/]");
                if (result.PlannedOperations.Count > 20)
                    AnsiConsole.MarkupLine($"  [grey]... and {result.PlannedOperations.Count - 20} more[/]");
            }
            else if (result.Success)
            {
                AnsiConsole.MarkupLine($"[green]Installed {result.InstalledFiles.Count} files[/]");
                if (result.BackedUpFiles.Count > 0)
                    AnsiConsole.MarkupLine($"[grey]Backed up {result.BackedUpFiles.Count} existing files[/]");
                AnsiConsole.MarkupLine($"[grey]Changeset: {result.ChangesetId} (use to uninstall later)[/]");
                AnsiConsole.MarkupLine($"[grey]Installer: {result.InstallerUsed}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Installation failed:[/] {result.Error}");
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
            AnsiConsole.MarkupLine("[yellow]Installation cancelled.[/]");
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
