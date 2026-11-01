using System.ComponentModel;
using Modular.Cli.Commands.Deployment;
using Modular.Cli.Infrastructure;
using Modular.Core.Backends.ModIo;
using Modular.Core.Database;
using Modular.Core.InGame;
using Modular.Core.Installers;
using Modular.Core.Services;
using Modular.Sdk.Backends;
using Modular.Sdk.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>Options shared by track and untrack.</summary>
public class TrackingSettings : CommandSettings
{
    /// <summary>Mod ids or page links.</summary>
    [CommandArgument(0, "<mods>")]
    [Description("Mod ids or NexusMods / mod.io mod page links")]
    public string[] Mods { get; init; } = [];

    /// <summary>The hub.</summary>
    [CommandOption("--backend|-b")]
    [Description("nexusmods or modio (default: taken from the links, else nexusmods)")]
    public string? Backend { get; init; }

    /// <summary>The game.</summary>
    [CommandOption("--game|-g")]
    [Description("The game: a Steam app id (1086940), a NexusMods domain (baldursgate3) or a mod.io game id (6715)")]
    public string? Game { get; init; }

    /// <summary>Only say what would happen.</summary>
    [CommandOption("--dry-run")]
    [Description("Show what would change without changing anything")]
    public bool DryRun { get; init; }

    /// <summary>Skip confirmations.</summary>
    [CommandOption("--yes|-y")]
    [Description("Don't ask for confirmation")]
    public bool Yes { get; init; }

    /// <summary>Verbose output.</summary>
    [CommandOption("--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; init; }
}

/// <summary>
/// Adds mods to the user's NexusMods tracked list or mod.io subscriptions.
/// </summary>
/// <remarks>
/// For Baldur's Gate 3 on mod.io the game downloads every subscription itself, so the mod
/// is handed to the game: Modular records it as waiting and installs nothing.
/// <c>modular sync</c> picks up the file after the game has run.
/// </remarks>
public sealed class TrackCommand : AsyncCommand<TrackCommand.Settings>
{
    /// <summary>Options.</summary>
    public sealed class Settings : TrackingSettings
    {
        /// <summary>Which copy wins when Modular already deploys the mod.</summary>
        [CommandOption("--prefer")]
        [Description("When Modular already deploys a subscribed mod's module: 'modio' disables Modular's copy so the game's download is the only one; 'nexus' skips the subscription")]
        public string? Prefer { get; init; }
    }

    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) =>
        TrackingRun.RunAsync(settings, track: true, settings.Prefer);
}

/// <summary>
/// Removes mods from the user's NexusMods tracked list or mod.io subscriptions.
/// </summary>
/// <remarks>
/// For Baldur's Gate 3 on mod.io the game's copy of the mod and its load-order entry are
/// removed at once, so nothing is left half-installed for the next launch.
/// </remarks>
public sealed class UntrackCommand : AsyncCommand<TrackingSettings>
{
    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext context, TrackingSettings settings) =>
        TrackingRun.RunAsync(settings, track: false, prefer: null);
}

/// <summary>The work behind track and untrack.</summary>
internal static class TrackingRun
{
    public static async Task<int> RunAsync(TrackingSettings settings, bool track, string? prefer)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (prefer != null && prefer is not ("modio" or "nexus"))
        {
            AnsiConsole.MarkupLine("[red]--prefer takes 'modio' or 'nexus'.[/]");
            return 1;
        }

        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            await using var db = await DeployCommand.OpenDatabaseAsync();
            var context = await TrackingContext.CreateAsync(services, db, settings.Backend, settings.Mods, settings.Game, cts.Token);
            if (context == null)
                return 1;

            var terms = ((ITrackingBackend)context.Backend).Terms;
            var verb = track ? terms.Verb : terms.UndoVerb;
            var inGame = context.InGame;

            AnsiConsole.MarkupLine(
                $"[bold]{verb}[/] {settings.Mods.Length} mod(s) on {context.Backend.DisplayName}" +
                (context.GameDomain != null ? $" [grey](game {Markup.Escape(context.GameDomain)})[/]" : ""));

            if (inGame != null)
            {
                if (inGame.Manager.IsGameRunning())
                {
                    AnsiConsole.MarkupLine("[red]Baldur's Gate 3 is running.[/] Close it first: it writes its mods folder and load order while it runs.");
                    return 1;
                }

                AnsiConsole.MarkupLine(track
                    ? "[grey]Baldur's Gate 3 downloads its mod.io subscriptions itself on its next launch. Modular records them and installs nothing, so there is exactly one copy. Run 'modular sync --game 1086940' after the game has run.[/]"
                    : "[grey]The game's copy of each mod and its load-order entry are removed now.[/]");
            }

            if (settings.DryRun)
            {
                foreach (var input in settings.Mods)
                    AnsiConsole.MarkupLine($"  would {verb.ToLowerInvariant()} {Markup.Escape(input)}");
                return 0;
            }

            if (track && inGame != null && !settings.Yes &&
                !AnsiConsole.Confirm($"Subscribe and let the game download {settings.Mods.Length} mod(s) on its next launch?"))
            {
                return 1;
            }

            var copiesToDisable = new Dictionary<string, IReadOnlyList<ModularCopy>>();

            async Task<bool> BeforeAsync(string modId, CancellationToken ct)
            {
                if (!track || inGame == null)
                    return true;

                var copies = (await inGame.FindModularCopiesAsync(context.Installation!, modId, ct))
                    .Where(c => c.Enabled).ToList();
                if (copies.Count == 0)
                    return true;

                var names = string.Join(", ", copies.Select(c => $"{c.Name} ({c.ModId})"));
                switch (prefer)
                {
                    case "nexus":
                        AnsiConsole.MarkupLine($"  [yellow]skipped[/] mod {modId}: Modular already deploys it as {Markup.Escape(names)} (--prefer nexus)");
                        return false;
                    case "modio":
                        copiesToDisable[modId] = copies;
                        return true;
                    default:
                        AnsiConsole.MarkupLine(
                            $"  [yellow]skipped[/] mod {modId}: Modular already deploys it as {Markup.Escape(names)}. " +
                            "Subscribing would give the game a second copy. Re-run with --prefer modio (disable Modular's copy) or --prefer nexus (keep it, don't subscribe).");
                        return false;
                }
            }

            async Task AfterAsync(TrackingItemResult item, CancellationToken ct)
            {
                Print(item, terms);

                if (inGame == null || item.Result == null || item.ModId == null)
                    return;

                if (track)
                {
                    var record = await inGame.RecordSubscriptionAsync(
                        context.Installation!, item.ModId, item.Result.Mod?.Name, null, ct);
                    AnsiConsole.MarkupLine(record.State == GameOwnedModState.Installed
                        ? $"    [grey]already downloaded by the game: {Markup.Escape(record.FileName ?? "")}[/]"
                        : "    [grey]waiting for the game to download it[/]");

                    if (copiesToDisable.TryGetValue(item.ModId, out var copies))
                    {
                        var result = await context.InstallService.Deployment.SetEnabledAsync(
                            context.Installation!, copies.Select(c => c.ModId).ToList(), enabled: false, ct);
                        AnsiConsole.MarkupLine(result.Success
                            ? $"    [grey]disabled Modular's copy ({Markup.Escape(string.Join(", ", copies.Select(c => c.Name)))}); its staging is kept[/]"
                            : $"    [red]could not disable Modular's copy:[/] {Markup.Escape(result.Error ?? "")}");
                    }
                }
                else
                {
                    foreach (var removed in await inGame.RemoveAsync(context.Installation!, item.ModId, ct))
                        AnsiConsole.MarkupLine($"    [grey]removed {Markup.Escape(removed)}[/]");
                }
            }

            var results = await new ModTrackingService().ApplyAsync(
                context.Backend, settings.Mods, context.GameDomain, track, BeforeAsync, AfterAsync, cts.Token);

            var failed = results.Count(r => !r.Succeeded && r.Error != "skipped");
            AnsiConsole.MarkupLine(
                $"[bold]{results.Count(r => r.Result?.Changed == true)}[/] changed, " +
                $"{results.Count(r => r.Succeeded && r.Result?.Changed != true)} unchanged, " +
                $"{results.Count(r => r.Error == "skipped")} skipped, {failed} failed");
            return failed > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
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

    private static void Print(TrackingItemResult item, TrackingTerms terms)
    {
        var name = item.Result?.Mod?.Name is { } n ? $" {Markup.Escape(n)}" : "";
        var id = Markup.Escape(item.ModId ?? item.Input);

        if (item.Result is { } result)
        {
            var text = result.Outcome switch
            {
                TrackOutcome.Tracked => $"[green]{terms.ListName.ToLowerInvariant()}[/]",
                TrackOutcome.AlreadyTracked => $"[grey]already {terms.ListName.ToLowerInvariant()}[/]",
                TrackOutcome.Untracked => $"[green]un{terms.ListName.ToLowerInvariant()}[/]",
                _ => $"[grey]was not {terms.ListName.ToLowerInvariant()}[/]",
            };
            AnsiConsole.MarkupLine($"  {text} {id}{name}");
        }
        else if (item.Error != "skipped")
        {
            AnsiConsole.MarkupLine($"  [red]failed[/] {id}: {Markup.Escape(item.Error ?? "")}");
        }
    }
}

/// <summary>
/// The backend, game and in-game manager a tracking command works with.
/// </summary>
internal sealed class TrackingContext
{
    public required IModBackend Backend { get; init; }
    public required string? GameDomain { get; init; }
    public required GameInstallation? Installation { get; init; }
    public required InGameModService? InGame { get; init; }
    public required ModInstallationService InstallService { get; init; }

    public static async Task<TrackingContext?> CreateAsync(
        RuntimeServices services,
        ModularDatabase db,
        string? backendId,
        IReadOnlyList<string> mods,
        string? game,
        CancellationToken ct)
    {
        backendId ??= mods
            .Select(m => { try { return ModTrackingService.Parse(m).BackendId; } catch (ArgumentException) { return null; } })
            .FirstOrDefault(b => b != null) ?? BackendIds.NexusMods;

        var registry = services.CreateBackendRegistry();
        var backend = registry.Get(backendId);
        if (backend == null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown or disabled backend:[/] {Markup.Escape(backendId)}");
            return null;
        }

        if (backend is not ITrackingBackend)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(backend.DisplayName)} does not support tracking.[/]");
            return null;
        }

        var installService = services.CreateInstallationService(db);
        var isModIo = string.Equals(backend.Id, BackendIds.ModIo, StringComparison.OrdinalIgnoreCase);

        // mod.io's only configured game is Baldur's Gate 3 by default; its subscriptions
        // land in the game either way, so they are handed to it even without --game.
        var gameRef = game;
        if (gameRef == null && isModIo && services.Settings.ModIoGameIds is [6715])
            gameRef = InGameModManagers.BaldursGate3SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (gameRef == BG3InGameModManager.ModIoGameId)
            gameRef = InGameModManagers.BaldursGate3SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        GameInstallation? installation = null;
        if (gameRef != null)
            installation = await installService.Deployment.ResolveInstallationAsync(gameRef, ct);

        var manager = installation == null ? null : InGameModManagers.For(installation);
        var inGame = isModIo && manager != null
            ? new InGameModService(new GameOwnedModRepository(db), installService.Deployment.Mods, manager)
            : null;

        string? domain;
        if (isModIo)
            domain = manager?.RemoteGameId ?? game;
        else if (installation != null && !installation.GameId.All(char.IsAsciiDigit) && installation.GameId.Length > 0)
            domain = installation.GameId;
        else
            domain = game;

        return new TrackingContext
        {
            Backend = backend,
            GameDomain = domain,
            Installation = installation,
            InGame = inGame,
            InstallService = installService,
        };
    }
}

/// <summary>Lists the user's tracked mods or subscriptions.</summary>
public sealed class TrackedCommand : AsyncCommand<TrackedCommand.Settings>
{
    /// <summary>Options.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The hub.</summary>
        [CommandOption("--backend|-b")]
        [Description("nexusmods or modio (default: nexusmods)")]
        public string Backend { get; init; } = BackendIds.NexusMods;

        /// <summary>The game.</summary>
        [CommandOption("--game|-g")]
        [Description("Only this game's mods")]
        public string? Game { get; init; }

        /// <summary>Verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            await using var db = await DeployCommand.OpenDatabaseAsync();
            var tracking = await TrackingContext.CreateAsync(services, db, settings.Backend, [], settings.Game, CancellationToken.None);
            if (tracking == null)
                return 1;

            var mods = await tracking.Backend.GetUserModsAsync(tracking.GameDomain);
            var owned = tracking.InGame == null
                ? new Dictionary<string, GameOwnedModRecord>()
                : (await new GameOwnedModRepository(db).ListAsync(tracking.Installation!.GameKey))
                    .ToDictionary(r => r.RemoteModId);

            var table = new Table().AddColumns("Id", "Name", "Game");
            if (tracking.InGame != null)
                table.AddColumn("In game");

            foreach (var mod in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = new List<string> { mod.ModId, Markup.Escape(mod.Name), Markup.Escape(mod.GameDomain ?? "") };
                if (tracking.InGame != null)
                {
                    row.Add(owned.TryGetValue(mod.ModId, out var r)
                        ? r.State == GameOwnedModState.Installed ? $"[green]{Markup.Escape(r.FileName ?? "installed")}[/]" : "[yellow]awaiting download[/]"
                        : "[grey]not synced[/]");
                }

                table.AddRow(row.ToArray());
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"{mods.Count} {((ITrackingBackend)tracking.Backend).Terms.ListName.ToLowerInvariant()} mod(s)");
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
}

/// <summary>
/// Brings Modular's records of a game's own mod manager in line with what the game has
/// downloaded and the user's subscriptions. Changes only Modular's records.
/// </summary>
public sealed class SyncCommand : AsyncCommand<SyncCommand.Settings>
{
    /// <summary>Options.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game.</summary>
        [CommandOption("--game|-g")]
        [Description("The game (default: Baldur's Gate 3, 1086940)")]
        public string Game { get; init; } = "1086940";

        /// <summary>Verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            await using var db = await DeployCommand.OpenDatabaseAsync();
            var tracking = await TrackingContext.CreateAsync(services, db, BackendIds.ModIo, [], settings.Game, CancellationToken.None);
            if (tracking == null)
                return 1;

            if (tracking.InGame == null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(settings.Game)} has no in-game mod manager Modular works with.[/]");
                return 1;
            }

            var subscribed = await tracking.Backend.GetUserModsAsync(tracking.GameDomain);
            var report = await tracking.InGame.SyncAsync(
                tracking.Installation!,
                subscribed.Select(m => m.ModId).ToHashSet(StringComparer.Ordinal),
                subscribed.ToDictionary(m => m.ModId, m => m.Name));

            if (report.Items.Count == 0)
            {
                AnsiConsole.MarkupLine($"[green]In step:[/] {subscribed.Count} subscription(s), nothing to report.");
                return 0;
            }

            var table = new Table().AddColumns("", "Mod", "File", "Detail");
            foreach (var item in report.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var kind = item.Kind switch
                {
                    InGameSyncKind.Adopted => "[green]installed[/]",
                    InGameSyncKind.UpdatedByGame => "[yellow]updated[/]",
                    InGameSyncKind.AwaitingDownload => "[blue]waiting[/]",
                    InGameSyncKind.Forgotten => "[grey]forgotten[/]",
                    InGameSyncKind.Unsubscribed => "[yellow]unsubscribed[/]",
                    InGameSyncKind.ModularCopy => "[red]second copy[/]",
                    InGameSyncKind.Duplicate => "[red]duplicate[/]",
                    _ => item.Kind.ToString(),
                };
                table.AddRow(kind, Markup.Escape($"{item.Name} ({item.RemoteModId})"), Markup.Escape(item.FileName ?? ""), Markup.Escape(item.Detail));
            }

            AnsiConsole.Write(table);
            return report.Items.Any(i => i.Kind is InGameSyncKind.Duplicate or InGameSyncKind.ModularCopy) ? 2 : 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            if (settings.Verbose)
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
