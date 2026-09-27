using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.GameDetection;
using Modular.Core.Modules;
using Modular.Sdk.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.GameDetection;

/// <summary>
/// Resolves a game reference to a full installation and reports what Modular found:
/// the matched game module, the engine, the named deployment roots and whether a
/// Proton prefix backs the installation.
/// </summary>
/// <remarks>
/// This is the user-facing view of <see cref="GameInstallationResolver"/>. It accepts
/// whatever <c>--game</c> accepts elsewhere: a Steam AppID, a NexusMods slug, a
/// GameBanana game id, a display name or a path.
/// </remarks>
public sealed class DetectCommand : AsyncCommand<DetectCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game to resolve.</summary>
        [CommandArgument(0, "<game>")]
        [Description("Steam AppID, NexusMods slug, GameBanana game id, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>Whether to skip writing the detection tables.</summary>
        [CommandOption("--no-persist")]
        [Description("Do not record the result in the detection tables")]
        public bool NoPersist { get; init; }

        /// <summary>Whether to enable verbose output.</summary>
        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "Modular", "modular.db");
            var db = new ModularDatabase(dbPath);
            await db.InitializeAsync();

            var registry = new GameModuleRegistry();
            var resolver = new GameInstallationResolver(registry: registry, database: db);

            var installation = await resolver.ResolveAsync(
                settings.Game, persist: !settings.NoPersist);

            if (installation == null)
            {
                LiveProgressDisplay.ShowError($"Could not resolve game: {settings.Game}");
                AnsiConsole.MarkupLine(
                    "[grey]Try a Steam AppID, a NexusMods slug, a display name, or a full install path.[/]");
                return 1;
            }

            var module = resolver.GetModule(installation);
            var modPaths = module.ResolveModPaths(installation);

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Property");
            table.AddColumn("Value");

            table.AddRow("Game", Markup.Escape(installation.DisplayName ?? installation.GameId));
            table.AddRow("Game id", Markup.Escape(installation.GameId));
            table.AddRow("Game key", Markup.Escape(installation.GameKey));
            table.AddRow("Steam AppID", installation.SteamAppId?.ToString() ?? "[grey]n/a[/]");
            table.AddRow("Build id", Markup.Escape(installation.BuildId ?? "unknown"));
            table.AddRow("Store", installation.Store.ToString());
            table.AddRow("Install path", Markup.Escape(installation.InstallPath));
            table.AddRow(
                "Module",
                module.ModuleId == GenericGameModule.Id
                    ? $"[yellow]{Markup.Escape(module.DisplayName)}[/] [grey](no dedicated module)[/]"
                    : $"[green]{Markup.Escape(module.DisplayName)}[/] [grey]({Markup.Escape(module.ModuleId)})[/]");
            table.AddRow(
                "Engine",
                installation.EngineFamily == null
                    ? "[grey]not detected[/]"
                    : $"{Markup.Escape(installation.EngineFamily)} ({installation.EngineConfidence ?? 0:P0})");
            table.AddRow(
                "Proton prefix",
                installation.ProtonPrefixPath == null
                    ? "[grey]none — game runs natively or has never been launched[/]"
                    : $"[green]{Markup.Escape(installation.ProtonPrefixPath)}[/]");

            AnsiConsole.Write(table);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Deployment roots[/]");

            var rootsTable = new Table().Border(TableBorder.Rounded);
            rootsTable.AddColumn("Root");
            rootsTable.AddColumn("Path");
            rootsTable.AddColumn("Exists");

            foreach (var (name, path) in modPaths.Roots.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                var marker = name == modPaths.DefaultRoot ? $"{name} [grey](default)[/]" : name;
                rootsTable.AddRow(
                    marker,
                    Markup.Escape(path),
                    Directory.Exists(path) ? "[green]yes[/]" : "[yellow]no[/]");
            }

            AnsiConsole.Write(rootsTable);

            if (settings.Verbose && registry.ResolveByPath(installation.InstallPath) is { } resolution)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[bold]Detection evidence[/] [grey]({resolution.Match.Confidence:P0})[/]");
                foreach (var evidence in resolution.Match.Evidence)
                    AnsiConsole.MarkupLine($"  [grey]- {Markup.Escape(evidence)}[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
