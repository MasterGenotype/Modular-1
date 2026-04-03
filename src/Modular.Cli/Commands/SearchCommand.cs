using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Searches for mods on a backend using full-text search.
/// </summary>
public sealed class SearchCommand : AsyncCommand<SearchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[terms]")]
        [Description("Search terms (omit to list all mods for the game)")]
        public string Terms { get; init; } = string.Empty;

        [CommandOption("--game|-g")]
        [Description("Game domain (e.g., skyrimspecialedition)")]
        public string? Game { get; init; }

        [CommandOption("--sort|-s")]
        [Description("Sort order: relevance, endorsements, downloads, updated, added")]
        public string? Sort { get; init; }

        [CommandOption("--page|-p")]
        [Description("Page number (default: 1)")]
        public int Page { get; init; } = 1;

        [CommandOption("--limit|-l")]
        [Description("Results per page (default: 20, max: 20)")]
        public int Limit { get; init; } = 20;

        [CommandOption("--backend|-b")]
        [Description("Backend to search (default: nexusmods)")]
        public string Backend { get; init; } = "nexusmods";

        [CommandOption("--adult")]
        [Description("Include adult content in results")]
        public bool Adult { get; init; }

        [CommandOption("--no-fuzzy")]
        [Description("Disable fuzzy re-ranking of results by mod name")]
        public bool NoFuzzy { get; init; }

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

            var backend = registry.Get(settings.Backend);
            if (backend == null)
            {
                LiveProgressDisplay.ShowError($"Unknown backend: {settings.Backend}");
                return 1;
            }

            if (backend is not ISearchableBackend searchable)
            {
                LiveProgressDisplay.ShowError($"Backend '{settings.Backend}' does not support search.");
                return 1;
            }

            var gameDomain = settings.Game;
            if (string.IsNullOrEmpty(gameDomain) && backend.Capabilities.HasFlag(BackendCapabilities.GameDomains))
            {
                gameDomain = LiveProgressDisplay.AskString("Enter game domain (e.g., skyrimspecialedition):");
                if (string.IsNullOrWhiteSpace(gameDomain))
                {
                    LiveProgressDisplay.ShowError("Game domain is required for this backend.");
                    return 1;
                }
            }

            var sortOrder = ParseSortOrder(settings.Sort);
            var query = new ModSearchQuery
            {
                Terms = settings.Terms,
                GameDomain = gameDomain,
                SortBy = sortOrder,
                Page = settings.Page,
                PageSize = Math.Min(settings.Limit, 20),
                AdultContent = settings.Adult
            };

            var infoMessage = string.IsNullOrWhiteSpace(settings.Terms)
                ? $"Listing mods on {backend.DisplayName} for \"{gameDomain}\"..."
                : $"Searching {backend.DisplayName} for \"{settings.Terms}\"...";
            LiveProgressDisplay.ShowInfo(infoMessage);

            var result = await searchable.SearchModsAsync(query);

            if (result.Mods.Count == 0)
            {
                LiveProgressDisplay.ShowWarning("No results found.");
                return 0;
            }

            // Fuzzy re-rank by mod name is on by default; --no-fuzzy disables it
            // Sort best matches first but keep all API results
            var mods = !settings.NoFuzzy && !string.IsNullOrWhiteSpace(settings.Terms)
                ? result.Mods
                    .OrderByDescending(m => FuzzyMatcher.Score(settings.Terms, m.Name))
                    .ToList()
                : result.Mods;

            var table = new Table();
            table.Border(TableBorder.Rounded);
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
            AnsiConsole.MarkupLine(
                $"[dim]Page {result.Page} — Showing {result.Mods.Count} of {result.TotalCount} results[/]");

            if (result.HasNextPage)
                AnsiConsole.MarkupLine($"[dim]Use --page {result.Page + 1} for next page[/]");

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    private static ModSortOrder ParseSortOrder(string? sort) => sort?.ToLowerInvariant() switch
    {
        "endorsements" => ModSortOrder.Endorsements,
        "downloads" => ModSortOrder.Downloads,
        "updated" => ModSortOrder.Updated,
        "added" => ModSortOrder.Added,
        _ => ModSortOrder.Relevance
    };
}
