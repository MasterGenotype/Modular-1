using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Collections;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

// --- Create ---

public sealed class CollectionCreateCommand : AsyncCommand<CollectionCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--game|-g")]
        [Description("Game domain (e.g., skyrimspecialedition)")]
        public string? Game { get; init; }

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var game = settings.Game;
            if (string.IsNullOrEmpty(game))
            {
                game = LiveProgressDisplay.AskString("Enter game domain (e.g., skyrimspecialedition):");
                if (string.IsNullOrWhiteSpace(game)) { LiveProgressDisplay.ShowError("Game domain is required."); return 1; }
            }

            var collection = await service.CreateAsync(settings.Name, game);
            LiveProgressDisplay.ShowSuccess($"Created collection '{collection.Name}' for {collection.GameId}");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- List ---

public sealed class CollectionListCommand : AsyncCommand<CollectionListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var repo = new ModCollectionRepository();
            var collections = await repo.ListAsync();

            if (collections.Count == 0)
            {
                LiveProgressDisplay.ShowInfo("No collections found.");
                return 0;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("Name");
            table.AddColumn("Game");
            table.AddColumn("Mods");
            table.AddColumn("Updated");

            foreach (var c in collections)
            {
                table.AddRow(
                    Markup.Escape(c.Name),
                    Markup.Escape(c.GameId),
                    c.Entries.Count.ToString(),
                    c.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Show ---

public sealed class CollectionShowCommand : AsyncCommand<CollectionShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var repo = new ModCollectionRepository();
            var (collection, _) = await repo.FindByNameAsync(settings.Name);

            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(collection.Name)}[/]");
            AnsiConsole.MarkupLine($"Game: {Markup.Escape(collection.GameId)}");
            AnsiConsole.MarkupLine($"Backend: {Markup.Escape(collection.BackendId)}");
            if (!string.IsNullOrEmpty(collection.Description))
                AnsiConsole.MarkupLine($"Description: {Markup.Escape(collection.Description)}");
            AnsiConsole.MarkupLine($"Entries: {collection.Entries.Count}");
            AnsiConsole.WriteLine();

            if (collection.Entries.Count > 0)
            {
                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.AddColumn("Mod ID");
                table.AddColumn("Name");
                table.AddColumn("Author");
                table.AddColumn("Version");
                table.AddColumn("Optional");

                foreach (var entry in collection.Entries)
                {
                    table.AddRow(
                        entry.ModId,
                        Markup.Escape(entry.Name),
                        Markup.Escape(entry.Author ?? "—"),
                        Markup.Escape(entry.Version ?? "—"),
                        entry.IsOptional ? "yes" : "no");
                }

                AnsiConsole.Write(table);
            }

            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Add ---

public sealed class CollectionAddCommand : AsyncCommand<CollectionAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandArgument(1, "<modId>")]
        [Description("Mod ID to add")]
        public string ModId { get; init; } = string.Empty;

        [CommandOption("--file-id")]
        [Description("Pin to a specific file ID")]
        public string? FileId { get; init; }

        [CommandOption("--optional")]
        [Description("Mark this mod as optional")]
        public bool Optional { get; init; }

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var (collection, path) = await repo.FindByNameAsync(settings.Name);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            var modInfo = await backend.GetModInfoAsync(settings.ModId, collection.GameId);
            if (modInfo == null)
            {
                LiveProgressDisplay.ShowError($"Could not find mod {settings.ModId} in {collection.GameId}.");
                return 1;
            }

            await service.AddModAsync(collection, modInfo, settings.FileId, settings.Optional);
            LiveProgressDisplay.ShowSuccess($"Added '{modInfo.Name}' to collection '{collection.Name}'");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Remove ---

public sealed class CollectionRemoveCommand : AsyncCommand<CollectionRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandArgument(1, "<modId>")]
        [Description("Mod ID to remove")]
        public string ModId { get; init; } = string.Empty;

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var (collection, _) = await repo.FindByNameAsync(settings.Name);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            await service.RemoveModAsync(collection, settings.ModId);
            LiveProgressDisplay.ShowSuccess($"Removed mod {settings.ModId} from collection '{collection.Name}'");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Download ---

public sealed class CollectionDownloadCommand : AsyncCommand<CollectionDownloadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--output|-o")]
        [Description("Output directory (defaults to configured mods directory)")]
        public string? Output { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be downloaded")]
        public bool DryRun { get; init; }

        [CommandOption("--verify")]
        [Description("Verify downloads with MD5 checksums")]
        public bool Verify { get; init; }

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var (collection, _) = await repo.FindByNameAsync(settings.Name);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            var outputDir = settings.Output ?? services.Settings.ModsDirectory;
            var options = new DownloadOptions
            {
                DryRun = settings.DryRun,
                VerifyDownloads = settings.Verify
            };

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Phase == DownloadPhase.Downloading && p.Total > 0)
                    Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Status}");
                else
                    Console.WriteLine($"[INFO] {p.Status}");
            });

            await service.DownloadCollectionAsync(collection, outputDir, options, progress);
            LiveProgressDisplay.ShowSuccess($"Collection '{collection.Name}' download complete.");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Verify ---

public sealed class CollectionVerifyCommand : AsyncCommand<CollectionVerifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--output|-o")]
        [Description("Directory to verify against")]
        public string? Output { get; init; }

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend, metadataCache: services.MetadataCache);

            var (collection, _) = await repo.FindByNameAsync(settings.Name);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            var outputDir = settings.Output ?? services.Settings.ModsDirectory;
            var results = await service.VerifyCollectionAsync(collection, outputDir);

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("Mod");
            table.AddColumn("Exists");
            table.AddColumn("MD5 OK");

            foreach (var (entry, exists, md5Ok) in results)
            {
                table.AddRow(
                    Markup.Escape(entry.Name),
                    exists ? "[green]yes[/]" : "[red]no[/]",
                    md5Ok ? "[green]yes[/]" : "[red]no[/]");
            }

            AnsiConsole.Write(table);

            var missing = results.Count(r => !r.exists);
            var corrupt = results.Count(r => r.exists && !r.md5Match);
            if (missing > 0) LiveProgressDisplay.ShowWarning($"{missing} file(s) missing");
            if (corrupt > 0) LiveProgressDisplay.ShowWarning($"{corrupt} file(s) failed MD5 verification");
            if (missing == 0 && corrupt == 0) LiveProgressDisplay.ShowSuccess("All files verified successfully.");

            return (missing > 0 || corrupt > 0) ? 1 : 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Export ---

public sealed class CollectionExportCommand : AsyncCommand<CollectionExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--output|-o")]
        [Description("Output file path")]
        public string? Output { get; init; }

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var (collection, _) = await repo.FindByNameAsync(settings.Name);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            var output = settings.Output ?? $"./{collection.Name}.collection.json";
            await service.ExportAsync(collection, output);
            LiveProgressDisplay.ShowSuccess($"Exported to {output}");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Import ---

public sealed class CollectionImportCommand : AsyncCommand<CollectionImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Path to collection JSON file")]
        public string Path { get; init; } = string.Empty;

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var collection = await service.ImportAsync(settings.Path);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Could not import collection from {settings.Path}");
                return 1;
            }

            LiveProgressDisplay.ShowSuccess($"Imported collection '{collection.Name}' ({collection.Entries.Count} mods)");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}

// --- Check Updates ---

public sealed class CollectionCheckUpdatesCommand : AsyncCommand<CollectionCheckUpdatesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Collection name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--verbose")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var registry = services.CreateBackendRegistry();
            var backend = registry.Get("nexusmods")!;
            var repo = new ModCollectionRepository();
            var service = new ModCollectionService(repo, backend);

            var (collection, _) = await repo.FindByNameAsync(settings.Name);
            if (collection == null)
            {
                LiveProgressDisplay.ShowError($"Collection '{settings.Name}' not found.");
                return 1;
            }

            LiveProgressDisplay.ShowInfo($"Checking updates for {collection.Entries.Count} mods...");
            var updates = await service.CheckUpdatesAsync(collection);

            if (updates.Count == 0)
            {
                LiveProgressDisplay.ShowSuccess("All mods are up to date.");
                return 0;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("Mod");
            table.AddColumn("Current File");
            table.AddColumn("Latest File");
            table.AddColumn("Latest Version");

            foreach (var (entry, latestFileId, latestVersion) in updates)
            {
                table.AddRow(
                    Markup.Escape(entry.Name),
                    Markup.Escape(entry.FileId ?? "unpinned"),
                    Markup.Escape(latestFileId ?? "—"),
                    Markup.Escape(latestVersion ?? "—"));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[yellow]{updates.Count} mod(s) have updates available.[/]");
            return 0;
        }
        catch (Exception ex) { LiveProgressDisplay.ShowError(ex.Message); return 1; }
    }
}
