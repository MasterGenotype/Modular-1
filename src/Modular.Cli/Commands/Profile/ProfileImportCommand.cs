using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Dependencies;
using Modular.Core.Profiles;
using Modular.Core.Versioning;
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

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var loggerFactory = ServiceConfiguration.CreateLoggerFactory(settings.Verbose);
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

                if (settings.Resolve && result.Profile != null)
                {
                    await ResolveProfileDependenciesAsync(result.Profile, settings.Verbose);
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

    private static async Task ResolveProfileDependenciesAsync(ModProfile profile, bool verbose)
    {
        LiveProgressDisplay.ShowInfo("Resolving dependencies...");

        using var services = await RuntimeServices.InitializeMinimalAsync(verbose);
        var gameDomain = profile.Game ?? "";
        var versionProvider = services.CreateVersionProvider(gameDomain);

        var loggerFactory = verbose ? ServiceConfiguration.CreateLoggerFactory(true) : null;
        var resolver = new GreedyDependencyResolver(
            versionProvider,
            loggerFactory?.CreateLogger<GreedyDependencyResolver>());

        // Build root requirements from profile mods
        var requirements = new List<(string canonicalId, VersionRange? constraint)>();
        foreach (var mod in profile.Mods.Where(m => m.Enabled))
        {
            VersionRange? constraint = null;
            if (!string.IsNullOrEmpty(mod.Version))
            {
                VersionRange.TryParse(mod.Version, out constraint);
            }
            requirements.Add((mod.CanonicalId, constraint));
        }

        if (requirements.Count == 0)
        {
            LiveProgressDisplay.ShowWarning("No enabled mods to resolve");
            return;
        }

        Console.WriteLine($"  Resolving {requirements.Count} mod(s)...");

        var result = await resolver.ResolveAsync(requirements);

        if (result.Success)
        {
            LiveProgressDisplay.ShowSuccess($"Resolution successful: {result.ResolvedVersions.Count} mod(s) resolved");

            foreach (var (modId, version) in result.ResolvedVersions.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"  {modId} @ {version}");
            }

            if (result.InstallOrder.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Install order:");
                for (var i = 0; i < result.InstallOrder.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {result.InstallOrder[i].CanonicalId}");
                }
            }
        }
        else
        {
            LiveProgressDisplay.ShowWarning($"Resolution failed: {result.FailureReason}");

            if (result.Conflicts.Count > 0)
            {
                Console.WriteLine("Conflicts:");
                foreach (var conflict in result.Conflicts)
                {
                    Console.WriteLine($"  [{conflict.Type}] {conflict.Explanation}");
                }
            }
        }

        loggerFactory?.Dispose();
    }
}
