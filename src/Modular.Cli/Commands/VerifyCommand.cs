using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Database;
using Modular.Core.Installers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Verifies that installed mod files still exist on disk.
/// </summary>
public sealed class VerifyCommand : AsyncCommand<VerifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[mod-id]")]
        [Description("Specific mod ID to verify (verifies all if omitted)")]
        public string? ModId { get; init; }

        [CommandOption("--verbose")]
        [Description("Show detailed file-level results")]
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

            if (!string.IsNullOrEmpty(settings.ModId))
            {
                var result = await tracker.VerifyInstallationAsync(settings.ModId);
                PrintVerification(result, settings.Verbose);
                return result.IsValid ? 0 : 1;
            }

            // Verify all installed mods.
            var mods = await tracker.GetInstalledModsAsync();
            if (mods.Count == 0)
            {
                LiveProgressDisplay.ShowInfo("No mods installed.");
                return 0;
            }

            var allValid = true;
            var table = new Table();
            table.AddColumn("Mod ID");
            table.AddColumn("Status");
            table.AddColumn("Valid");
            table.AddColumn("Missing");

            foreach (var mod in mods)
            {
                var result = await tracker.VerifyInstallationAsync(mod.ModId);
                if (!result.IsValid)
                    allValid = false;

                table.AddRow(
                    Markup.Escape(mod.ModId),
                    result.IsValid ? "[green]OK[/]" : "[red]INVALID[/]",
                    result.ValidFiles.Count.ToString(),
                    result.MissingFiles.Count.ToString());

                if (settings.Verbose && result.MissingFiles.Count > 0)
                {
                    foreach (var missing in result.MissingFiles.Take(5))
                        Console.WriteLine($"    MISSING: {missing}");
                    if (result.MissingFiles.Count > 5)
                        Console.WriteLine($"    ... and {result.MissingFiles.Count - 5} more");
                }
            }

            AnsiConsole.Write(table);

            if (allValid)
                LiveProgressDisplay.ShowSuccess($"All {mods.Count} mod(s) verified successfully.");
            else
                LiveProgressDisplay.ShowWarning("Some mods have missing files.");

            return allValid ? 0 : 1;
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    private static void PrintVerification(VerificationResult result, bool verbose)
    {
        if (!result.Found)
        {
            LiveProgressDisplay.ShowError($"Mod '{result.ModId}' is not installed.");
            return;
        }

        if (result.IsValid)
        {
            LiveProgressDisplay.ShowSuccess($"Mod '{result.ModId}': all {result.ValidFiles.Count} files present.");
        }
        else
        {
            LiveProgressDisplay.ShowWarning(
                $"Mod '{result.ModId}': {result.MissingFiles.Count} file(s) missing out of {result.ValidFiles.Count + result.MissingFiles.Count}.");

            if (verbose)
            {
                foreach (var missing in result.MissingFiles)
                    Console.WriteLine($"  MISSING: {missing}");
            }
        }
    }
}
