using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Database;
using Modular.Core.ModLists;
using Modular.Core.Plugins;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary><c>modular modlist export</c>: write a game's installed mods to a shareable list.</summary>
public sealed class ModListExportCommand : AsyncCommand<ModListExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <GAME>")]
        [Description("Game key (Steam app id such as 1091500, or e.g. enshrouded)")]
        public string Game { get; init; } = "";

        [CommandOption("-o|--output <FILE>")]
        [Description("Where to write the list (default: <game>.modular-modlist.json here)")]
        public string? Output { get; init; }

        [CommandOption("--offline")]
        [Description("Don't ask NexusMods for exact file ids")]
        public bool Offline { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            AnsiConsole.MarkupLine("[red]Give the game with --game.[/]");
            return 1;
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var services = await RuntimeServices.InitializeMinimalAsync();
        await using var db = await ModListCli.OpenDatabaseAsync();

        var lookups = settings.Offline ? null : await ModListCli.LookupsAsync(services, cts.Token);

        var installation = await new Modular.Core.GameDetection.GameInstallationResolver(database: db).ResolveAsync(settings.Game, persist: false, cts.Token);
        var list = await AnsiConsole.Status().StartAsync("Reading installed mods…", async status =>
            await new ModListExporter(db).ExportAsync(settings.Game, installation?.DisplayName, installation?.BuildId, lookups,
                new Progress<string>(m => status.Status($"Reading {Markup.Escape(m)}…")), cts.Token));

        var output = settings.Output ?? $"{ModListCli.FileStem(installation?.DisplayName ?? settings.Game)}{ModList.Extension}";
        await File.WriteAllTextAsync(output, list.ToJson(), cts.Token);
        var exact = list.Mods.Count(m => m.Source?.FileId is not null);
        AnsiConsole.MarkupLine($"Wrote [bold]{list.Mods.Count}[/] mod(s) to {Markup.Escape(Path.GetFullPath(output))}: " +
            $"{exact} with an exact download, {list.Mods.Count(m => m.Source is { FileId: null })} with a mod page only, {list.Mods.Count(m => m.Source is null)} local only.");
        foreach (var note in list.Notes)
            AnsiConsole.MarkupLine($"[grey]• {Markup.Escape(note)}[/]");
        return 0;
    }
}

/// <summary><c>modular modlist import</c>: install the mods of a list into its game.</summary>
public sealed class ModListImportCommand : AsyncCommand<ModListImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<FILE>")]
        public string File { get; init; } = "";

        [CommandOption("--game <GAME>")]
        [Description("Install into this game instead of the one the list names")]
        public string? Game { get; init; }

        [CommandOption("--apply")]
        [Description("Download and install (default: only show the plan)")]
        public bool Apply { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        ModList list;
        try
        {
            list = ModList.Load(settings.File);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        var gameKey = settings.Game ?? list.Game.Key;
        using var services = await RuntimeServices.InitializeMinimalAsync();
        await using var db = await ModListCli.OpenDatabaseAsync();
        var plugins = new PluginLoader();
        plugins.LoadAllPlugins();
        var install = services.CreateInstallationService(db, plugins);
        var importer = new ModListImporter(install);

        var plan = await importer.PlanAsync(list, gameKey, cts.Token);
        foreach (var group in plan.GroupBy(p => p.Action))
            AnsiConsole.MarkupLine($"[bold]{group.Count()}[/] {Describe(group.Key)}");
        foreach (var p in plan.Where(p => p.Action is ModListAction.PickFile or ModListAction.Manual))
            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(p.Entry.Name)}[/] [grey]{Markup.Escape(p.Detail)}[/]");
        foreach (var note in list.Notes)
            AnsiConsole.MarkupLine($"[grey]• {Markup.Escape(note)}[/]");
        if (ModListImporter.BuildMismatch(list, gameKey) is { } mismatch)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(mismatch)}[/]");
        if (!settings.Apply)
        {
            AnsiConsole.MarkupLine("[grey]Nothing changed. Add --apply to download and install.[/]");
            return 0;
        }

        var installation = await install.Deployment.ResolveInstallationAsync(gameKey, cts.Token);
        if (installation is null)
        {
            AnsiConsole.MarkupLine($"[red]Game {Markup.Escape(gameKey)} is not installed here.[/]");
            return 1;
        }
        var lookups = await ModListCli.LookupsAsync(services, cts.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Modular/1.0");
        var result = await AnsiConsole.Status().StartAsync("Installing…", async status =>
            await importer.ApplyAsync(list, installation, gameKey,
                lookups.ResolveUrlAsync, http, services.Settings.ModsDirectory,
                new Progress<string>(m => status.Status(Markup.Escape(m))), cts.Token));

        AnsiConsole.MarkupLine($"Installed [bold]{result.Installed}[/], already there {result.Skipped}, failed {result.Failed.Count}, need you {result.NeedsUser.Count}.");
        foreach (var (entry, error) in result.Failed)
            AnsiConsole.MarkupLine($"  [red]{Markup.Escape(entry.Name)}[/]: {Markup.Escape(error)}");
        return result.Failed.Count == 0 ? 0 : 1;
    }

    private static string Describe(ModListAction a) => a switch
    {
        ModListAction.AlreadyInstalled => "already installed",
        ModListAction.Download => "to download and install",
        ModListAction.PickFile => "with a mod page but no exact file (pick it there)",
        _ => "local only (supply the archive)",
    };
}

internal static class ModListCli
{
    public static async Task<ModularDatabase> OpenDatabaseAsync()
    {
        var db = new ModularDatabase(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "modular.db"));
        await db.InitializeAsync();
        return db;
    }

    public static async Task<ModListLookups> LookupsAsync(RuntimeServices services, CancellationToken ct)
    {
        var registry = services.CreateBackendRegistry();
        var nexus = registry.Get("nexusmods") is NexusModsBackend n && await services.EnsureNexusLoginAsync(ct) ? n : null;
        return new ModListLookups(nexus, registry.Get("modio") as Modular.Core.Backends.ModIo.ModIoBackend);
    }

    public static string FileStem(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : c is ' ' or '-' or '_' ? '-' : '\0').Where(c => c != '\0')).Trim('-');
}
