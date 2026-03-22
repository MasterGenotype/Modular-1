using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Lists all installed mods.
/// </summary>
public sealed class ListInstalledCommand : AsyncCommand<ListInstalledCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game-domain")]
        [Description("Filter by game domain")]
        public string? GameDomain { get; init; }

        [CommandOption("--verbose")]
        [Description("Show detailed information including file lists")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var modularDb = new ModularDatabase(services.Settings.DatabasePath);
            await modularDb.InitializeAsync();

            var tracker = new InstallationTracker(modularDb);
            var mods = await tracker.GetInstalledModsAsync(settings.GameDomain);

            if (mods.Count == 0)
            {
                LiveProgressDisplay.ShowInfo("No mods installed.");
                return 0;
            }

            var table = new Table();
            table.AddColumn("Mod ID");
            table.AddColumn("Name");
            table.AddColumn("Version");
            table.AddColumn("Game");
            table.AddColumn("Files");
            table.AddColumn("Installer");
            table.AddColumn("Installed");

            foreach (var mod in mods)
            {
                table.AddRow(
                    Markup.Escape(mod.ModId),
                    Markup.Escape(mod.ModName ?? "-"),
                    Markup.Escape(mod.Version ?? "-"),
                    Markup.Escape(mod.GameDomain ?? "-"),
                    mod.InstalledFiles.Count.ToString(),
                    Markup.Escape(mod.InstallerId ?? "-"),
                    mod.InstalledAtUtc.Length >= 10 ? mod.InstalledAtUtc[..10] : mod.InstalledAtUtc);
            }

            AnsiConsole.Write(table);
            LiveProgressDisplay.ShowInfo($"Total: {mods.Count} mod(s) installed");

            if (settings.Verbose)
            {
                foreach (var mod in mods)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  {mod.ModId} ({mod.InstalledFiles.Count} files):");
                    foreach (var file in mod.InstalledFiles.Take(20))
                        Console.WriteLine($"    {file}");
                    if (mod.InstalledFiles.Count > 20)
                        Console.WriteLine($"    ... and {mod.InstalledFiles.Count - 20} more");
                }
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
