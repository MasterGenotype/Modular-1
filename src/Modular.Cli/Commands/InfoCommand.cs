using System.ComponentModel;
using System.Globalization;
using Modular.Cli.Infrastructure;
using Modular.Core.Backends.ModIo;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Services;
using Modular.Core.Utilities;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Shows one mod's page details, listed requirements and files, without downloading it.
/// </summary>
public sealed class InfoCommand : AsyncCommand<InfoCommand.Settings>
{
    /// <summary>Options.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The mod.</summary>
        [CommandArgument(0, "<mod>")]
        [Description("A mod id, or a NexusMods / mod.io mod page link")]
        public string Mod { get; init; } = string.Empty;

        /// <summary>The game.</summary>
        [CommandOption("--game|-g")]
        [Description("Game domain (e.g., baldursgate3), or a mod.io game id (e.g., 6715); a NexusMods link names its own")]
        public string? Game { get; init; }

        /// <summary>The hub.</summary>
        [CommandOption("--backend|-b")]
        [Description("Backend to ask (default: taken from the link, else nexusmods)")]
        public string? Backend { get; init; }

        /// <summary>Print the page text.</summary>
        [CommandOption("--description|-d")]
        [Description("Also print the mod page's full description")]
        public bool Description { get; init; }

        /// <summary>Include old versions.</summary>
        [CommandOption("--all-files")]
        [Description("Include old versions in the file list")]
        public bool AllFiles { get; init; }

        /// <summary>Verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        try
        {
            ModReference reference;
            try
            {
                reference = ModTrackingService.Parse(settings.Mod);
            }
            catch (ArgumentException)
            {
                return Fail($"'{settings.Mod}' is not a mod id or a NexusMods or mod.io mod page link.");
            }

            var backendId = settings.Backend ?? reference.BackendId ?? BackendIds.NexusMods;
            if (reference.BackendId != null &&
                !string.Equals(reference.BackendId, backendId, StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"'{settings.Mod}' is a {reference.BackendId} link, not {backendId}.");
            }

            if (reference.GameDomain != null && settings.Game != null &&
                !string.Equals(reference.GameDomain, settings.Game, StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"'{settings.Mod}' is for {reference.GameDomain}, not {settings.Game}.");
            }

            var game = settings.Game ?? reference.GameDomain;

            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var backend = services.CreateBackendRegistry().Get(backendId);
            if (backend == null)
                return Fail($"Unknown or disabled backend: {backendId}");

            if (backend.Capabilities.HasFlag(BackendCapabilities.GameDomains) && string.IsNullOrWhiteSpace(game))
                return Fail($"{backend.DisplayName} needs the game: pass --game (e.g. baldursgate3) or the mod's page link.");

            if (backend is NexusModsBackend && !await services.EnsureNexusLoginAsync(ct))
                return Fail("Not logged in to NexusMods. Run 'modular login'.");

            var modId = reference.ModId;
            if (reference.IsPageName)
            {
                if (backend is not ModIoBackend modIo)
                    return Fail($"'{settings.Mod}' cannot be looked up on {backend.DisplayName}.");

                var found = await modIo.FindModIdAsync(reference.ModId, game, ct);
                if (found == null)
                    return Fail($"No mod.io mod '{reference.ModId}' in the configured games.");
                modId = found;
            }

            var mod = await backend.GetModInfoAsync(modId, game, ct);
            if (mod == null)
            {
                return Fail(
                    $"Could not read mod {modId} on {backend.DisplayName}. It may not exist or may be hidden" +
                    (settings.Verbose ? "." : "; --verbose shows why."));
            }

            game = mod.GameDomain ?? game;

            var category = mod.CategoryName;
            if (category == null && mod.CategoryId is > 0 && backend is NexusModsBackend nexus)
            {
                try
                {
                    var categories = await nexus.GetGameCategoriesAsync(game!, ct);
                    category = categories.GetValueOrDefault(mod.CategoryId.Value);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    category = null;
                }
            }

            IReadOnlyList<ModRequirement>? requirements = null;
            string? requirementsError = null;
            if (backend is IRequirementsBackend requirementsBackend)
            {
                try
                {
                    var all = await requirementsBackend.GetRequirementsAsync([modId], game, ct);
                    requirements = all.TryGetValue(modId, out var listed) ? listed : [];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    requirementsError = ex.Message;
                }
            }

            List<BackendModFile>? files = null;
            string? filesError = null;
            try
            {
                files = await backend.GetModFilesAsync(
                    modId, game, new FileFilter { IncludeArchived = settings.AllFiles }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                filesError = ex.Message;
            }

            PrintDetails(mod, backend, game, category);
            PrintRequirements(requirements, requirementsError, backend is IRequirementsBackend);
            PrintFiles(files, filesError, settings.AllFiles);
            PrintDescription(mod, settings.Description);
            return 0;
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

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        return 1;
    }

    private static void PrintDetails(BackendMod mod, IModBackend backend, string? game, string? category)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(mod.Name)}[/]").LeftJustified());

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        void Row(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                grid.AddRow($"[grey]{label}[/]", Markup.Escape(value));
        }

        Row("Mod", $"{mod.ModId} on {backend.DisplayName}" + (game != null ? $" ({game})" : ""));
        Row("Version", mod.Version);
        Row("Author", mod.Author);
        Row("Category", category ?? mod.CategoryId?.ToString(CultureInfo.InvariantCulture));
        Row("Published", FormatDate(mod.CreatedAt));
        Row("Updated", FormatDate(mod.UpdatedAt));
        Row("Downloads", mod.DownloadCount?.ToString("N0", CultureInfo.InvariantCulture));
        Row("Endorsements", mod.EndorsementCount?.ToString("N0", CultureInfo.InvariantCulture));
        if (mod.IsAdult)
            grid.AddRow("[grey]Adult[/]", "[yellow]yes[/]");
        Row("Page", mod.Url);

        AnsiConsole.Write(grid);

        var summary = ModDescriptionText.ToPlainText(mod.Summary);
        if (summary.Length > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine(summary);
        }
    }

    private static void PrintRequirements(
        IReadOnlyList<ModRequirement>? requirements, string? error, bool supported)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Requirements[/]");

        if (!supported)
        {
            AnsiConsole.MarkupLine("  [grey]this backend does not list requirements[/]");
            return;
        }

        if (error != null)
        {
            AnsiConsole.MarkupLine($"  [yellow]could not read them:[/] {Markup.Escape(error)}");
            return;
        }

        if (requirements is not { Count: > 0 })
        {
            AnsiConsole.MarkupLine("  [grey]none listed (the description may still name some)[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Mod", "Id", "Notes");
        foreach (var requirement in requirements)
        {
            var id = requirement.IsExternal
                ? "[grey]off-site[/]"
                : Markup.Escape(requirement.ModId) + (requirement.SameGame ? "" : " [grey](other game)[/]");
            var name = Markup.Escape(requirement.Name) +
                (requirement.IsExternal && requirement.Url != null ? $"\n[grey]{Markup.Escape(requirement.Url)}[/]" : "");
            table.AddRow(name, id, Markup.Escape(requirement.Notes ?? ""));
        }

        AnsiConsole.Write(table);
    }

    private static void PrintFiles(List<BackendModFile>? files, string? error, bool allFiles)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Files[/]");

        if (error != null)
        {
            AnsiConsole.MarkupLine($"  [yellow]could not read them:[/] {Markup.Escape(error)}");
            return;
        }

        var shown = (files ?? [])
            .Where(f => allFiles || !IsOld(f))
            .OrderByDescending(f => f.UploadedAt ?? DateTime.MinValue)
            .ToList();

        if (shown.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]none[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("File").NoWrap());
            table.AddColumn("Name");
            table.AddColumn(new TableColumn("Version").NoWrap());
            table.AddColumn(new TableColumn("Category").NoWrap());
            table.AddColumn(new TableColumn("Size").NoWrap().RightAligned());
            table.AddColumn(new TableColumn("Uploaded").NoWrap());

            foreach (var file in shown)
            {
                table.AddRow(
                    Markup.Escape(file.FileId),
                    Markup.Escape(file.DisplayName ?? file.FileName),
                    Markup.Escape(file.Version ?? "—"),
                    Markup.Escape(file.Category ?? "—"),
                    FormatSize(file.SizeBytes),
                    FormatDate(file.UploadedAt) ?? "—");
            }

            AnsiConsole.Write(table);

            // Authors repeat a file's notes on every version, so each note is shown once,
            // headed by the files that carry it.
            var notes = shown
                .Select(f => (f.FileId, Text: ModDescriptionText.ToPlainText(f.Description)))
                .Where(n => n.Text.Length > 0)
                .GroupBy(n => n.Text)
                .ToList();

            foreach (var note in notes)
            {
                AnsiConsole.MarkupLine($"[grey]File {Markup.Escape(string.Join(", ", note.Select(n => n.FileId)))}:[/]");
                AnsiConsole.Write(new Padder(new Text(note.Key)).Padding(2, 0, 0, 0));
            }
        }

        if (!allFiles)
            AnsiConsole.MarkupLine("[grey]--all-files lists old and archived versions too.[/]");
    }

    private static bool IsOld(BackendModFile file) =>
        file.Category is "old_version" or "archived" or "deleted";

    private static void PrintDescription(BackendMod mod, bool show)
    {
        if (!show)
        {
            if (!string.IsNullOrWhiteSpace(mod.Description))
                AnsiConsole.MarkupLine("[grey]--description prints the mod page's full text.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Description[/]").LeftJustified());

        var text = ModDescriptionText.ToPlainText(mod.Description);
        if (text.Length == 0)
            AnsiConsole.MarkupLine("[grey]The hub sent no description.[/]");
        else
            AnsiConsole.WriteLine(text);
    }

    private static string? FormatDate(DateTime? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatSize(long? bytes) => bytes switch
    {
        null or <= 0 => "—",
        < 1024 * 1024 => Math.Ceiling(bytes.Value / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes.Value / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes.Value / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
    };
}
