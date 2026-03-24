using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Backends.NexusMods;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Browses discovery feeds: trending, latest added, recently updated mods.
/// </summary>
public sealed class BrowseCommand : AsyncCommand<BrowseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<feed>")]
        [Description("Feed type: trending, latest, updated")]
        public string Feed { get; init; } = string.Empty;

        [CommandOption("--game|-g")]
        [Description("Game domain (e.g., skyrimspecialedition)")]
        public string? Game { get; init; }

        [CommandOption("--limit|-l")]
        [Description("Number of results (default: 20)")]
        public int Limit { get; init; } = 20;

        [CommandOption("--period")]
        [Description("Time period for 'updated' feed: 1d, 1w, 1m (default: 1w)")]
        public string Period { get; init; } = "1w";

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();

            var backend = registry.Get("nexusmods") as NexusModsBackend;
            if (backend == null)
            {
                LiveProgressDisplay.ShowError("NexusMods backend is not configured.");
                return 1;
            }

            var gameDomain = settings.Game;
            if (string.IsNullOrEmpty(gameDomain))
            {
                gameDomain = LiveProgressDisplay.AskString("Enter game domain (e.g., skyrimspecialedition):");
                if (string.IsNullOrWhiteSpace(gameDomain))
                {
                    LiveProgressDisplay.ShowError("Game domain is required.");
                    return 1;
                }
            }

            var feedType = settings.Feed.ToLowerInvariant();
            LiveProgressDisplay.ShowInfo($"Fetching {feedType} mods for {gameDomain}...");

            var mods = feedType switch
            {
                "trending" => await backend.GetTrendingModsAsync(gameDomain, settings.Limit),
                "latest" => await backend.GetLatestAddedModsAsync(gameDomain, settings.Limit),
                "updated" => await backend.GetRecentlyUpdatedModsAsync(gameDomain, settings.Period),
                _ => throw new ArgumentException($"Unknown feed type: {settings.Feed}. Use: trending, latest, updated")
            };

            if (mods.Count == 0)
            {
                LiveProgressDisplay.ShowWarning("No mods found.");
                return 0;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.Title($"[bold]{feedType.ToUpperInvariant()}[/] — {gameDomain}");
            table.AddColumn("Mod ID");
            table.AddColumn("Name");
            table.AddColumn("Author");
            table.AddColumn("Downloads");
            table.AddColumn("Endorsements");
            table.AddColumn("Updated");

            foreach (var mod in mods)
            {
                table.AddRow(
                    mod.ModId,
                    Markup.Escape(mod.Name),
                    Markup.Escape(mod.Author ?? "—"),
                    mod.DownloadCount?.ToString("N0") ?? "—",
                    mod.EndorsementCount?.ToString("N0") ?? "—",
                    mod.UpdatedAt?.ToString("yyyy-MM-dd") ?? "—");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]{mods.Count} results[/]");

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
