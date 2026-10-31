using System.ComponentModel;
using Modular.Cli.Commands.Deployment;
using Modular.Cli.Infrastructure;
using Modular.Core.Deployment;
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

        /// <summary>Install-order placement: directly before this mod.</summary>
        [CommandOption("--before <MOD>")]
        [Description("Place it in the install order directly before this mod")]
        public string? Before { get; init; }

        /// <summary>Install-order placement: directly after this mod.</summary>
        [CommandOption("--after <MOD>")]
        [Description("Place it in the install order directly after this mod")]
        public string? After { get; init; }

        /// <summary>Install-order placement: at a 1-based position.</summary>
        [CommandOption("--at <POSITION>")]
        [Description("Place it at this 1-based position in the install order")]
        public int? At { get; init; }

        /// <summary>Install-order placement: first.</summary>
        [CommandOption("--top")]
        [Description("Place it first in the install order")]
        public bool Top { get; init; }

        /// <summary>Load-order placement: directly before this entry.</summary>
        [CommandOption("--load-before <MOD>")]
        [Description("Place it in the game's load order directly before this entry")]
        public string? LoadBefore { get; init; }

        /// <summary>Load-order placement: directly after this entry.</summary>
        [CommandOption("--load-after <MOD>")]
        [Description("Place it in the game's load order directly after this entry")]
        public string? LoadAfter { get; init; }

        /// <summary>Load-order placement: at a 1-based position.</summary>
        [CommandOption("--load-at <POSITION>")]
        [Description("Place it at this 1-based position in the game's load order")]
        public int? LoadAt { get; init; }

        /// <summary>Load-order placement: first.</summary>
        [CommandOption("--load-top")]
        [Description("Place it first in the game's load order")]
        public bool LoadTop { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }

        /// <summary>Where the mod goes in the install order, when asked.</summary>
        internal (OrderPlacement? Placement, string? Error) InstallPlacement =>
            OrderRendering.PlacementFrom(Before, After, At, Top, bottom: false, prefix: "", positionName: "at", offerBottom: false);

        /// <summary>Where the mod goes in the load order, when asked.</summary>
        internal (OrderPlacement? Placement, string? Error) LoadPlacement =>
            OrderRendering.PlacementFrom(LoadBefore, LoadAfter, LoadAt, LoadTop, bottom: false, prefix: "load-", positionName: "at", offerBottom: false);

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (InstallPlacement.Error is { } installError)
                return ValidationResult.Error(installError);

            if (LoadPlacement.Error is { } loadError)
                return ValidationResult.Error(loadError);

            return ValidationResult.Success();
        }
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
                AnsiConsole.MarkupLine($"[red]Archive not found:[/] {Markup.Escape(archivePath)}");
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
                        AnsiConsole.MarkupLine($"[red]Could not find Steam game:[/] {Markup.Escape(settings.Game!)}");
                        AnsiConsole.MarkupLine("[grey]Tip: Use a Steam AppID, game name, or full directory path.[/]");
                        return 1;
                    }
                    targetDirectory = resolved;
                    AnsiConsole.MarkupLine($"[green]Found game at:[/] {Markup.Escape(targetDirectory)}");
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

            var installPlacement = settings.InstallPlacement.Placement;
            var loadPlacement = settings.LoadPlacement.Placement;
            var placing = installPlacement != null || loadPlacement != null;

            // A game with one order has no load order to place the mod in; say so before
            // anything is installed.
            var loadKind = Modular.Sdk.Modules.LoadOrderKind.None;
            if (loadPlacement != null)
            {
                var gameInstallation = await installService.Deployment.ResolveInstallationAsync(settings.Game!, cts.Token);
                loadKind = gameInstallation == null
                    ? Modular.Sdk.Modules.LoadOrderKind.None
                    : installService.Deployment.GetModule(gameInstallation).LoadOrderKind;

                if (loadKind == Modular.Sdk.Modules.LoadOrderKind.None)
                {
                    AnsiConsole.MarkupLine(
                        "[red]This game applies mods in one order, the install order.[/] " +
                        "Use --before, --after, --at or --top instead of the --load- options.");
                    await db.DisposeAsync();
                    services.Dispose();
                    return 1;
                }
            }

            var options = new ModInstallationOptions
            {
                ModId = settings.ModId ?? Path.GetFileNameWithoutExtension(archivePath),
                MatchInstalledByFiles = settings.ModId == null,
                GameId = settings.Game,
                AllowOverwrite = settings.Force,
                CreateBackups = !settings.NoBackup,
                DryRun = settings.DryRun,

                // Placing the mod comes before its single deployment.
                NoDeploy = settings.NoDeploy || (placing && !settings.DryRun)
            };

            // Show install header
            AnsiConsole.MarkupLine($"[bold]Installing:[/] {Markup.Escape(Path.GetFileName(archivePath))}");
            AnsiConsole.MarkupLine($"[bold]Target:[/]     {Markup.Escape(targetDirectory)}");
            if (settings.DryRun)
                AnsiConsole.MarkupLine("[yellow]DRY RUN — no files will be modified[/]");
            AnsiConsole.WriteLine();

            // Settle what the archive leaves to the user before the spinner starts: a
            // prompt cannot be shown while it is running.
            // A dry run applies --option but asks nothing.
            if (!await ResolveChoicesAsync(
                    installService, archivePath, targetDirectory, options, settings,
                    interactive: !settings.DryRun, cts.Token))
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
                        // The status line is markup, parsed on Spectre's refresh thread: a file
                        // name such as "Mod [No Scars].pak" read as a style tag there throws
                        // where nothing can catch it, killing the install mid-commit.
                        var status = p.CurrentFile != null
                            ? $"{p.CurrentOperation} ({p.FilesProcessed}/{p.TotalFiles}) {p.CurrentFile}"
                            : p.CurrentOperation;
                        ctx.Status(Markup.Escape(status ?? string.Empty));
                    });

                    result = await installService.InstallAsync(
                        archivePath, targetDirectory, options, progress, cts.Token);
                });

            // Display results
            if (result!.DryRun && result.Success)
            {
                foreach (var choice in result.Choices)
                {
                    var picked = choice.Options.FirstOrDefault(o => o.Id == choice.SelectedOptionId);
                    AnsiConsole.MarkupLine(
                        $"[bold]{Markup.Escape(choice.Title)}[/] {Markup.Escape(picked?.Label ?? choice.SelectedOptionId)}" +
                        (choice.IsResolved ? string.Empty : " [grey](default; pass --option to pick: " +
                            Markup.Escape(string.Join(", ", choice.Options.Select(o => o.Label))) + ")[/]"));
                }

                if (result.ReplacedModId != null)
                    AnsiConsole.MarkupLine($"[bold]Would replace:[/] {Markup.Escape(result.ReplacedModId)} [grey](deploys the same files)[/]");

                AnsiConsole.MarkupLine($"[green]Would install {result.PlannedOperations.Count} files:[/]");
                foreach (var file in result.PlannedOperations.Take(20))
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(file)}[/]");
                if (result.PlannedOperations.Count > 20)
                    AnsiConsole.MarkupLine($"  [grey]... and {result.PlannedOperations.Count - 20} more[/]");
                foreach (var warning in result.Warnings)
                    AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");

                if (installPlacement != null)
                    AnsiConsole.MarkupLine($"[bold]Install order:[/] would go {Markup.Escape(installPlacement.Describe())}");
                if (loadPlacement != null)
                    AnsiConsole.MarkupLine($"[bold]Load order:[/] would go {Markup.Escape(loadPlacement.Describe())}");
            }
            else if (result.Success && placing &&
                     !await PlaceAsync(installService, result, options, settings, installPlacement, loadPlacement, loadKind, cts.Token))
            {
                await db.DisposeAsync();
                services.Dispose();
                return 1;
            }
            else if (result.Success)
            {
                if (result.Deployed)
                {
                    // A placed install deploys afterwards, so its count is what it staged.
                    var count = result.InstalledFiles.Count > 0 ? result.InstalledFiles.Count : result.StagedFiles.Count;
                    AnsiConsole.MarkupLine($"[green]Installed {count} files[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"[green]Staged {result.StagedFiles.Count} files[/] " +
                        "[grey](not deployed)[/]");
                    AnsiConsole.MarkupLine(
                        $"[grey]Run 'modular deploy --game {Markup.Escape(settings.Game!)}' to apply it.[/]");
                }

                if (result.ReplacedModId != null)
                    AnsiConsole.MarkupLine($"Replaced {Markup.Escape(result.ReplacedModId)} [grey](deploys the same files; keeps its place in the load order)[/]");

                if (result.BackedUpFiles.Count > 0)
                    AnsiConsole.MarkupLine($"[grey]Backed up {result.BackedUpFiles.Count} existing files[/]");
                AnsiConsole.MarkupLine($"[grey]Changeset: {result.ChangesetId} (use to uninstall later)[/]");
                AnsiConsole.MarkupLine($"[grey]Installer: {Markup.Escape(result.InstallerUsed ?? string.Empty)}[/]");
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
                AnsiConsole.MarkupLine($"[red]Installation failed:[/] {Markup.Escape(result.Error ?? string.Empty)}");
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
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            if (settings.Verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Puts a just-installed mod where the placement options asked, then deploys it once.
    /// </summary>
    /// <returns>False when a placement could not be made; the mod stays installed.</returns>
    /// <remarks>
    /// The install order, and a load order Modular keeps, are stored before the deploy so
    /// it renders them. A load order the game keeps can only be changed after the deploy,
    /// which is what adds the mod's entry to it.
    /// </remarks>
    private static async Task<bool> PlaceAsync(
        ModInstallationService installService,
        ModInstallationResult result,
        ModInstallationOptions options,
        Settings settings,
        OrderPlacement? installPlacement,
        OrderPlacement? loadPlacement,
        Modular.Sdk.Modules.LoadOrderKind loadKind,
        CancellationToken ct)
    {
        var installation = await installService.Deployment.ResolveInstallationAsync(
            result.GameKey ?? settings.Game!, ct);
        if (installation == null)
        {
            AnsiConsole.MarkupLine("[red]Installed, but the game could not be resolved to place it.[/]");
            return false;
        }

        var modId = result.ReplacedModId ?? options.ModId!;
        var loadOrder = installService.LoadOrder;

        try
        {
            if (installPlacement != null)
            {
                await loadOrder.MoveAsync(installation, OrderListKind.Install, modId, installPlacement, deploy: false, ct);
                AnsiConsole.MarkupLine($"[grey]Install order: placed {Markup.Escape(installPlacement.Describe())}[/]");
            }

            if (loadPlacement != null && loadKind == Modular.Sdk.Modules.LoadOrderKind.Managed)
            {
                await loadOrder.MoveAsync(installation, OrderListKind.Load, modId, loadPlacement, deploy: false, ct);
                AnsiConsole.MarkupLine($"[grey]Load order: placed {Markup.Escape(loadPlacement.Describe())}[/]");
            }

            if (settings.NoDeploy)
            {
                if (loadPlacement != null && loadKind == Modular.Sdk.Modules.LoadOrderKind.External)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]![/] The game's load order only lists the mod once it is deployed, so " +
                        "--load- placement was not applied. Deploy, then use 'modular order move --load'.");
                }

                return true;
            }

            var deployment = await installService.Deployment.DeployAsync(installation, ct: ct);
            result.Deployment = deployment;
            result.Deployed = deployment.Success;

            if (!deployment.Success)
            {
                AnsiConsole.MarkupLine($"[red]Deploy failed:[/] {Markup.Escape(deployment.Error ?? "unknown error")}");
                return false;
            }

            if (loadPlacement != null && loadKind == Modular.Sdk.Modules.LoadOrderKind.External)
            {
                var moved = await loadOrder.MoveAsync(installation, OrderListKind.Load, modId, loadPlacement, deploy: true, ct);
                if (moved.Deployment != null)
                    result.Deployment = moved.Deployment;

                AnsiConsole.MarkupLine($"[grey]Load order: placed {Markup.Escape(loadPlacement.Describe())}[/]");
            }

            return true;
        }
        catch (LoadOrderException ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]Installed, but could not place it:[/] {Markup.Escape(ex.Message)} " +
                "[grey]Use 'modular order move' to place it.[/]");
            return false;
        }
    }

    /// <summary>
    /// Finds out whether the archive offers alternatives and settles each one, from
    /// <c>--option</c> or by asking.
    /// </summary>
    /// <returns>False when an <c>--option</c> matched nothing, so the install should stop.</returns>
    /// <remarks>
    /// Each <c>--option</c> answers whichever choice offers an option of that name, so one
    /// command line can settle several questions. An answer can uncover another question
    /// (a FOMOD page shown only for one pick), so the archive is planned again until no
    /// new choice turns up.
    /// </remarks>
    private static async Task<bool> ResolveChoicesAsync(
        ModInstallationService installService,
        string archivePath,
        string targetDirectory,
        ModInstallationOptions options,
        Settings settings,
        bool interactive,
        CancellationToken ct)
    {
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offered = new List<InstallChoice>();

        while (true)
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
                    DryRun = true,
                    Selections = new Dictionary<string, string>(options.Selections, StringComparer.OrdinalIgnoreCase)
                },
                ct: ct);

            var fresh = preview.Choices.Where(c => asked.Add(c.Id)).ToList();
            if (fresh.Count == 0)
                break;

            foreach (var choice in fresh)
            {
                offered.Add(choice);

                var given = settings.Options.FirstOrDefault(value => choice.Options.Any(o => Names(o, value)));
                var wanted = given == null ? null : choice.Options.First(o => Names(o, given));
                if (given != null)
                    matched.Add(given);

                if (wanted == null && interactive && AnsiConsole.Profile.Capabilities.Interactive)
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
        }

        var unmatched = settings.Options.Where(value => !matched.Contains(value)).ToList();
        if (unmatched.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]No choice offers --option {Markup.Escape(string.Join(", ", unmatched))}.[/]");
            foreach (var choice in offered)
            {
                AnsiConsole.MarkupLine(
                    $"  {Markup.Escape(choice.Title)} " +
                    Markup.Escape(string.Join(", ", choice.Options.Select(o => o.Label))));
            }

            return false;
        }

        return true;

        static bool Names(InstallChoiceOption option, string value) =>
            string.Equals(value, option.Label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, option.Id, StringComparison.OrdinalIgnoreCase);
    }
}
