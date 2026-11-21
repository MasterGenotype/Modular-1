using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary><c>modular fix-names</c>: name installed mods that are listed under their bare mod number.</summary>
public sealed class FixNamesCommand : AsyncCommand<FixNamesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <GAME>")]
        [Description("Only this game key (e.g. 1091500); default all games")]
        public string? Game { get; init; }

        [CommandOption("--online")]
        [Description("Ask NexusMods for names the cache and archive names don't have")]
        public bool Online { get; init; }

        [CommandOption("--apply")]
        [Description("Write the names (default: only show them)")]
        public bool Apply { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var services = await RuntimeServices.InitializeMinimalAsync();
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "modular.db");
        await using var db = new ModularDatabase(dbPath);
        await db.InitializeAsync();

        Func<string, int, CancellationToken, Task<string?>>? online = null;
        if (settings.Online)
        {
            if (services.CreateBackendRegistry().Get("nexusmods") is not NexusModsBackend nexus || !await services.EnsureNexusLoginAsync(cts.Token))
            {
                AnsiConsole.MarkupLine("[red]Not logged in to NexusMods. Run 'modular login', or drop --online.[/]");
                return 1;
            }
            online = async (domain, id, ct) =>
            {
                try
                {
                    return (await nexus.GetModInfoAsync(id.ToString(), domain, ct))?.Name;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return null;
                }
            };
        }

        var fixes = await new ModNameRepair(db, services.MetadataCache).RepairAsync(settings.Game, online, settings.Apply, cts.Token);
        foreach (var f in fixes)
        {
            AnsiConsole.MarkupLine(f.NewName is null
                ? $"[yellow]{Markup.Escape(f.GameKey)}  {Markup.Escape(f.ModId)}  (no name found)[/]"
                : $"{Markup.Escape(f.GameKey)}  {Markup.Escape(f.ModId)} → {Markup.Escape(f.NewName)} [grey]({Markup.Escape(f.Source)})[/]");
        }
        var named = fixes.Count(f => f.NewName is not null);
        AnsiConsole.MarkupLine($"[bold]{named}[/] of {fixes.Count} numbered mod(s) {(settings.Apply ? "renamed" : "can be renamed; add --apply to write")}.");
        return 0;
    }
}
