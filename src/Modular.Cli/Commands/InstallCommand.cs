using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Core.Plugins;
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
        [Description("Path to mod archive file (.zip, .7z, .rar) or a bare .pak")]
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

        /// <summary>Whether to stage the mod without deploying it.</summary>
        [CommandOption("--no-deploy")]
        [Description("Stage and register the mod without placing files in the game")]
        public bool NoDeploy { get; init; }

        /// <summary>Answers to the choices an archive offers.</summary>
        [CommandOption("--option <OPTION>")]
        [Description("Version to install when the archive offers several (e.g. \"Option A\"); repeatable")]
        public string[] Options { get; init; } = [];

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

            // Loading plugins here is what makes plugin-supplied installers and
            // game modules candidates for a real install.
            var pluginLoader = new PluginLoader();
            pluginLoader.LoadAllPlugins();

            var installService = services.CreateInstallationService(db, pluginLoader);

            var options = new ModInstallationOptions
            {
                ModId = settings.ModId ?? Path.GetFileNameWithoutExtension(archivePath),
                GameId = settings.Game,
                AllowOverwrite = settings.Force,
                CreateBackups = !settings.NoBackup,
                DryRun = settings.DryRun,
                NoDeploy = settings.NoDeploy
            };

            // Show install header
            AnsiConsole.MarkupLine($"[bold]Installing:[/] {Path.GetFileName(archivePath)}");
            AnsiConsole.MarkupLine($"[bold]Target:[/]     {targetDirectory}");
            if (settings.DryRun)
                AnsiConsole.MarkupLine("[yellow]DRY RUN — no files will be modified[/]");
            AnsiConsole.WriteLine();

            // Settle what the archive leaves to the user before the spinner starts: a
            // prompt cannot be shown while it is running.
            if (!settings.DryRun &&
                !await ResolveChoicesAsync(installService, archivePath, targetDirectory, options, settings, cts.Token))
            {
                AnsiConsole.MarkupLine("[yellow]Installation cancelled.[/]");
                await db.DisposeAsync();
                services.Dispose();
                return 1;
            }

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
                if (result.Deployed)
                {
                    AnsiConsole.MarkupLine($"[green]Installed {result.InstalledFiles.Count} files[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"[green]Staged {result.StagedFiles.Count} files[/] " +
                        "[grey](not deployed)[/]");
                    AnsiConsole.MarkupLine(
                        $"[grey]Run 'modular deploy --game {Markup.Escape(settings.Game!)}' to apply it.[/]");
                }

                if (result.BackedUpFiles.Count > 0)
                    AnsiConsole.MarkupLine($"[grey]Backed up {result.BackedUpFiles.Count} existing files[/]");
                AnsiConsole.MarkupLine($"[grey]Changeset: {result.ChangesetId} (use to uninstall later)[/]");
                AnsiConsole.MarkupLine($"[grey]Installer: {result.InstallerUsed}[/]");
                if (result.ArchivePath != archivePath)
                    AnsiConsole.MarkupLine($"[grey]Archive kept at: {Markup.Escape(result.ArchivePath)}[/]");
                if (result.ModuleId != null)
                    AnsiConsole.MarkupLine($"[grey]Module: {Markup.Escape(result.ModuleId)}[/]");

                foreach (var diagnostic in result.Diagnostics)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]![/] {Markup.Escape(diagnostic.Code)}: " +
                        Markup.Escape(diagnostic.Message));
                }

                foreach (var warning in result.Warnings)
                    AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");

                if (result.Deployment != null)
                    DeploymentReport.RenderLaunchRequirements(result.Deployment);
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

    /// <summary>
    /// Finds out whether the archive offers alternatives and settles each one, from
    /// <c>--option</c> or by asking.
    /// </summary>
    /// <returns>False when an <c>--option</c> matched nothing, so the install should stop.</returns>
    private static async Task<bool> ResolveChoicesAsync(
        ModInstallationService installService,
        string archivePath,
        string targetDirectory,
        ModInstallationOptions options,
        Settings settings,
        CancellationToken ct)
    {
        // A dry run plans the install and reports its choices without touching anything.
        var preview = await installService.InstallAsync(
            archivePath,
            targetDirectory,
            new ModInstallationOptions
            {
                ModId = options.ModId,
                GameId = options.GameId,
                AllowOverwrite = options.AllowOverwrite,
                DryRun = true
            },
            ct: ct);

        foreach (var choice in preview.Choices)
        {
            var wanted = choice.Options.FirstOrDefault(o => settings.Options.Any(given =>
                string.Equals(given, o.Label, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(given, o.Id, StringComparison.OrdinalIgnoreCase)));

            if (wanted == null && settings.Options.Length > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(choice.Title)}[/] None of the --option values match. Available: " +
                    Markup.Escape(string.Join(", ", choice.Options.Select(o => o.Label))));
                return false;
            }

            if (wanted == null && AnsiConsole.Profile.Capabilities.Interactive)
            {
                wanted = AnsiConsole.Prompt(
                    new SelectionPrompt<InstallChoiceOption>()
                        .Title(Markup.Escape(choice.Title))
                        .UseConverter(o => Markup.Escape($"{o.Label} ({o.FileCount} file(s))"))
                        .AddChoices(choice.Options));
            }

            // With nobody to ask and nothing given, the installer's default stands and
            // the install report says so.
            if (wanted != null)
                options.Selections[choice.Id] = wanted.Id;
        }

        return true;
    }
}
