using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Configuration;
using Modular.Core.Dependencies;
using Modular.Core.Profiles;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Profile;

/// <summary>
/// Exports a mod profile.
/// </summary>
public sealed class ProfileExportCommand : AsyncCommand<ProfileExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Profile name")]
        public required string Name { get; init; }

        [CommandOption("--output")]
        [Description("Output file path")]
        public string? OutputPath { get; init; }

        [CommandOption("--format")]
        [Description("Export format (json or archive)")]
        [DefaultValue("json")]
        public string Format { get; init; } = "json";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var configService = new ConfigurationService();
            var appSettings = await configService.LoadAsync();

            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(appSettings.Verbose);
            var profileExporter = new ProfileExporter(loggerFactory.CreateLogger<ProfileExporter>());

            // Create a profile from the current mod library
            var profile = new ModProfile
            {
                Name = settings.Name,
                Description = $"Exported on {DateTime.Now:yyyy-MM-dd HH:mm}",
                CreatedAt = DateTime.UtcNow,
                Mods = new List<ProfileMod>()
            };

            // Scan the mods directory for installed mods
            if (Directory.Exists(appSettings.ModsDirectory))
            {
                foreach (var gameDir in Directory.GetDirectories(appSettings.ModsDirectory))
                {
                    var gameDomain = Path.GetFileName(gameDir);
                    foreach (var modDir in Directory.GetDirectories(gameDir))
                    {
                        var modName = Path.GetFileName(modDir);
                        profile.Mods.Add(new ProfileMod
                        {
                            CanonicalId = $"{gameDomain}/{modName}",
                            DisplayName = modName,
                            Enabled = true
                        });
                    }
                }
            }

            var lockfile = new ModLockfile
            {
                GeneratedAt = DateTime.UtcNow,
                Mods = profile.Mods.ToDictionary(
                    m => m.CanonicalId,
                    m => new LockfileMod { Version = "unknown" }
                )
            };

            // Determine output path
            var extension = settings.Format.ToLowerInvariant() == "archive" ? ".zip" : ".json";
            var outputPath = settings.OutputPath ?? Path.Combine(Environment.CurrentDirectory, $"{settings.Name}{extension}");

            var exportFormat = settings.Format.ToLowerInvariant() == "archive" ? ExportFormat.Archive : ExportFormat.Json;
            var options = new ExportOptions { Format = exportFormat };

            var result = await profileExporter.ExportProfileAsync(profile, lockfile, outputPath, options);

            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess($"Exported profile to: {result.OutputPath}");
                Console.WriteLine($"  Mods: {profile.Mods.Count}");
                Console.WriteLine($"  Size: {result.FileSize} bytes");
                return 0;
            }
            else
            {
                LiveProgressDisplay.ShowError($"Export failed: {result.Error}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError($"Export failed: {ex.Message}");
            return 1;
        }
    }
}
