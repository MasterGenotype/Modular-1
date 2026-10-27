using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modular.Cli.Commands.Deployment;
using Modular.Cli.Infrastructure;
using Modular.Core.Compatibility;
using Modular.Core.Installers;
using Modular.Sdk.Compatibility;
using Modular.Sdk.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Lists the mods other mods, or the game, rely on, and with <c>--since</c>, whether each
/// has a release for a game update.
/// </summary>
public sealed class CoreModsCommand : AsyncCommand<CoreModsCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game to check.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>An inventory of mods Modular did not install.</summary>
        [CommandOption("--inventory <FILE>")]
        [Description("JSON inventory of the game's mods, for a game modded outside Modular")]
        public string? Inventory { get; init; }

        /// <summary>When the game update came out.</summary>
        [CommandOption("--since <DATE>")]
        [Description("Release date of the game update (yyyy-MM-dd); checks each core mod for a release since")]
        public string? Since { get; init; }

        /// <summary>The game update's version.</summary>
        [CommandOption("--game-version <VERSION>")]
        [Description("The update's version, such as 2.5; flags new releases that do not name it")]
        public string? GameVersion { get; init; }

        /// <summary>A list of core mods kept by hand.</summary>
        [CommandOption("--core-list <FILE>")]
        [Description("JSON list of mods to treat as core (default ~/.config/Modular/core-mods.json)")]
        public string? CoreList { get; init; }

        /// <summary>Where to write the report as JSON.</summary>
        [CommandOption("--json <FILE>")]
        [Description("Also write the report to this JSON file")]
        public string? Json { get; init; }

        /// <summary>Whether to enable verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Show every reason for each mod")]
        public bool Verbose { get; init; }

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Game))
                return ValidationResult.Error("--game is required");

            if (Since != null && !TryParseDate(Since, out _))
                return ValidationResult.Error("--since must be a date such as 2026-10-22");

            return ValidationResult.Success();
        }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var services = await RuntimeServices.InitializeAsync(settings.Verbose);
        var db = await DeployCommand.OpenDatabaseAsync();

        try
        {
            var installService = services.CreateInstallationService(db);
            var installation = await installService.Deployment.ResolveInstallationAsync(settings.Game, cts.Token);
            if (installation == null)
            {
                AnsiConsole.MarkupLine($"[red]Could not resolve game:[/] {Markup.Escape(settings.Game)}");
                return 1;
            }

            var module = installation.ModuleId == null
                ? null
                : installService.ModuleRegistry.GetByModuleId(installation.ModuleId);
            var domain = DomainOf(module, installation);

            IInstalledModSource source;
            if (settings.Inventory != null)
            {
                source = new InventoryInstalledModSource(settings.Inventory);
            }
            else
            {
                var records = await installService.ListInstalledAsync(installation.InstallPath, cts.Token);
                source = new ChangesetInstalledModSource(records, domain);
            }

            var mods = await source.ListAsync(cts.Token);
            if (mods.Count == 0)
            {
                AnsiConsole.MarkupLine(settings.Inventory == null
                    ? "[grey]Modular has no installed mods for this game. Use --inventory for mods installed another way.[/]"
                    : "[grey]The inventory lists no mods.[/]");
                return 0;
            }

            var backends = services.CreateBackendRegistry().GetAll().ToList();
            var coreList = settings.CoreList ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "core-mods.json");

            var classifier = new CoreModClassifier(
            [
                new RequiredByInstalledSignal(backends),
                new GameFileRuleSignal(),
                new DeclaredCoreModsSignal(coreList)
            ]);

            var coreContext = new CoreModContext(installation.GameKey, mods, module);

            if (settings.Since == null)
            {
                CoreModReport report = null!;
                await AnsiConsole.Status().StartAsync($"Classifying {mods.Count} mod(s)...", async _ =>
                    report = await classifier.ClassifyAsync(coreContext, cts.Token));

                RenderCore(report, settings.Verbose);
                await WriteJsonAsync(settings.Json, report, cts.Token);
                return 0;
            }

            TryParseDate(settings.Since, out var since);
            var target = new GameUpdateTarget(since, settings.GameVersion);
            var service = new GameUpdateReadinessService(classifier, [new BackendReleaseCheck(backends)]);

            GameUpdateReadinessReport readiness = null!;
            await AnsiConsole.Status().StartAsync($"Checking {mods.Count} mod(s)...", async ctx =>
                readiness = await service.CheckAsync(
                    coreContext, target, new Progress<string>(name => ctx.Status($"Checking {Markup.Escape(name)}...")),
                    cts.Token));

            RenderReadiness(readiness, settings.Verbose);
            await WriteJsonAsync(settings.Json, readiness, cts.Token);
            await services.SaveStateAsync();

            // Non-zero when a core mod is not on a release for the update, so a script can act on it.
            return readiness.Ready ? 0 : 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            if (settings.Verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
        finally
        {
            await db.DisposeAsync();
            services.Dispose();
        }
    }

    private static void RenderCore(CoreModReport report, bool verbose)
    {
        var table = new Table();
        table.AddColumn("Core mod");
        table.AddColumn("Role");
        table.AddColumn(new TableColumn("Dependents").RightAligned());
        table.AddColumn("Why");

        foreach (var entry in report.Entries)
        {
            table.AddRow(
                Markup.Escape(entry.Mod.Name),
                Markup.Escape(string.Join(", ", entry.Roles)),
                entry.Dependents.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(Why(entry, verbose)));
        }

        AnsiConsole.MarkupLine($"[bold]{report.Entries.Count} core mod(s)[/] [grey]among {report.ModsChecked}[/]");
        AnsiConsole.Write(table);
        RenderErrors(report.Errors);
    }

    private static void RenderReadiness(GameUpdateReadinessReport report, bool verbose)
    {
        var table = new Table();
        table.AddColumn("Core mod");
        table.AddColumn("Role");
        table.AddColumn(new TableColumn("Dependents").RightAligned());
        table.AddColumn("Installed");
        table.AddColumn("Latest");
        table.AddColumn("Status");
        if (verbose)
            table.AddColumn("Why");

        // What needs doing first: available updates, then mods with no release yet.
        var ordered = report.Items
            .OrderBy(i => i.Status.Verdict switch
            {
                ReleaseVerdict.UpdateAvailable => 0,
                ReleaseVerdict.NoReleaseSinceTarget => 1,
                ReleaseVerdict.Unknown => 2,
                ReleaseVerdict.OnTargetRelease => 3,
                _ => 4
            })
            .ToList();

        foreach (var item in ordered)
        {
            var status = item.Status;
            var label = status.Verdict switch
            {
                ReleaseVerdict.OnTargetRelease => "[green]on an update release[/]",
                ReleaseVerdict.UpdateAvailable => "[yellow]update available[/]",
                ReleaseVerdict.NoReleaseSinceTarget => "[red]no release since the update[/]",
                ReleaseVerdict.NotAffected => "[grey]not affected[/]",
                _ => "[grey]unknown[/]"
            };

            // Most hubs' file names never mention the game version, so this is a hint
            // for a closer look rather than a warning.
            if (verbose && status.Verdict is ReleaseVerdict.UpdateAvailable or ReleaseVerdict.OnTargetRelease &&
                report.Target.Version != null && !status.MentionsTargetVersion)
            {
                label += $" [grey](does not say {Markup.Escape(report.Target.Version)})[/]";
            }

            if (status.Note != null)
                label += $" [grey]{Markup.Escape(status.Note)}[/]";

            var cells = new List<string>
            {
                Markup.Escape(item.Entry.Mod.Name),
                Markup.Escape(string.Join(", ", item.Entry.Roles)),
                item.Entry.Dependents.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(item.Entry.Mod.Versions.Count == 0 ? "?" : string.Join(", ", item.Entry.Mod.Versions)),
                Markup.Escape(status.LatestVersion == null
                    ? "?"
                    : $"{status.LatestVersion} ({status.LatestUploadedAt:yyyy-MM-dd})"),
                label
            };
            if (verbose)
                cells.Add(Markup.Escape(Why(item.Entry, verbose: true)));

            table.AddRow(cells.ToArray());
        }

        var behind = report.Items.Count(i =>
            i.Status.Verdict is not (ReleaseVerdict.OnTargetRelease or ReleaseVerdict.NotAffected));
        AnsiConsole.MarkupLine(
            $"[bold]{report.Items.Count} core mod(s)[/] [grey]among {report.ModsChecked}; " +
            $"{behind} not on a release since {report.Target.ReleasedAt:yyyy-MM-dd}[/]");
        AnsiConsole.Write(table);
        RenderErrors(report.Errors);
    }

    private static string Why(CoreModEntry entry, bool verbose) =>
        verbose
            ? string.Join("\n", entry.Evidence.Select(e => $"{e.Signal}: {e.Reason}"))
            : entry.Evidence[0].Reason + (entry.Evidence.Count > 1 ? $" (+{entry.Evidence.Count - 1})" : string.Empty);

    private static void RenderErrors(IReadOnlyList<string> errors)
    {
        foreach (var error in errors)
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(error)}");
    }

    private static async Task WriteJsonAsync<T>(string? path, T report, CancellationToken ct)
    {
        if (path == null)
            return;

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        }, ct);
        AnsiConsole.MarkupLine($"[grey]Wrote {Markup.Escape(path)}[/]");
    }

    private static bool TryParseDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);

    private static string? DomainOf(IGameModule? module, GameInstallation installation) =>
        module?.GameIds.FirstOrDefault(id => id.Length > 0 && !id.All(char.IsDigit))
        ?? (!string.IsNullOrEmpty(installation.GameId) && !installation.GameId.All(char.IsDigit)
            ? installation.GameId
            : null);
}
