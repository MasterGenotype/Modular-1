using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Profiles;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Profile;

/// <summary>
/// Imports a mod profile.
/// </summary>
public sealed class ProfileImportCommand : AsyncCommand<ProfileImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Path to profile file")]
        public required string Path { get; init; }

        [CommandOption("--resolve")]
        [Description("Resolve dependencies after import")]
        public bool Resolve { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(true);
            var profileExporter = new ProfileExporter(loggerFactory.CreateLogger<ProfileExporter>());

            LiveProgressDisplay.ShowInfo($"Importing profile from: {settings.Path}");
            var result = await profileExporter.ImportProfileAsync(settings.Path);

            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess($"Imported profile: {result.Profile?.Name}");
                Console.WriteLine($"  Mods: {result.Profile?.Mods.Count ?? 0}");

                if (result.ValidationWarnings.Count > 0)
                {
                    Console.WriteLine("Warnings:");
                    foreach (var warning in result.ValidationWarnings)
                        Console.WriteLine($"  [WARN] {warning}");
                }

                if (settings.Resolve)
                {
                    LiveProgressDisplay.ShowInfo("Dependency resolution not yet implemented");
                }

                return 0;
            }
            else
            {
                LiveProgressDisplay.ShowError($"Import failed: {result.Error}");
                if (result.ValidationErrors.Count > 0)
                {
                    foreach (var error in result.ValidationErrors)
                        Console.WriteLine($"  [ERROR] {error}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Import failed: {ex.Message}");
            return 1;
        }
    }
}
