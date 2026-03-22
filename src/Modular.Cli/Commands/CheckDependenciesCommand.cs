using System.ComponentModel;
using Modular.Cli.Infrastructure;
using Modular.Cli.UI;
using Modular.Core.Dependencies;
using Modular.Core.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands;

/// <summary>
/// Checks and validates mod dependencies using the backtracking constraint solver.
/// </summary>
public sealed class CheckDependenciesCommand : AsyncCommand<CheckDependenciesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<game-domain>")]
        [Description("Game domain (e.g., skyrimspecialedition)")]
        public string GameDomain { get; init; } = string.Empty;

        [CommandOption("--mod")]
        [Description("Specific mod IDs to check (can specify multiple)")]
        public string[]? ModIds { get; init; }

        [CommandOption("--include-optional")]
        [Description("Include optional dependencies in resolution")]
        public bool IncludeOptional { get; init; }

        [CommandOption("--verbose")]
        [Description("Enable verbose output")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
            var versionProvider = services.CreateVersionProvider(settings.GameDomain);

            var resolver = new BacktrackingDependencyResolver(
                versionProvider,
                services.LoggerFactory?.CreateLogger<BacktrackingDependencyResolver>());

            var requirements = new List<(string canonicalId, VersionRange? constraint)>();

            if (settings.ModIds != null && settings.ModIds.Length > 0)
            {
                foreach (var modId in settings.ModIds)
                {
                    requirements.Add((modId, null));
                }
            }
            else
            {
                LiveProgressDisplay.ShowError("Specify at least one --mod to check.");
                return 1;
            }

            LiveProgressDisplay.ShowInfo($"Resolving dependencies for {requirements.Count} mod(s)...");

            var result = await resolver.ResolveAsync(
                requirements,
                includeOptional: settings.IncludeOptional);

            if (result.Success)
            {
                LiveProgressDisplay.ShowSuccess("All dependencies satisfied!");

                var table = new Table();
                table.AddColumn("Mod");
                table.AddColumn("Version");

                foreach (var (modId, version) in result.ResolvedVersions.OrderBy(kvp => kvp.Key))
                {
                    table.AddRow(Markup.Escape(modId), version.ToString());
                }

                AnsiConsole.Write(table);

                if (result.InstallOrder != null && result.InstallOrder.Count > 0)
                {
                    LiveProgressDisplay.ShowInfo("Install order:");
                    for (int i = 0; i < result.InstallOrder.Count; i++)
                    {
                        var node = result.InstallOrder[i];
                        Console.WriteLine($"  {i + 1}. {node.CanonicalId} @ {node.Version}");
                    }
                }

                if (result.OptionalFailures.Count > 0)
                {
                    LiveProgressDisplay.ShowWarning($"{result.OptionalFailures.Count} optional dependency/dependencies could not be satisfied:");
                    foreach (var opt in result.OptionalFailures)
                        Console.WriteLine($"  - {opt.CanonicalId}: {opt.Reason}");
                }

                return 0;
            }
            else
            {
                LiveProgressDisplay.ShowError("Dependency resolution failed!");
                Console.WriteLine($"  Reason: {result.FailureReason}");

                if (result.Conflicts.Count > 0)
                {
                    LiveProgressDisplay.ShowError("Conflicts:");
                    foreach (var conflict in result.Conflicts)
                        Console.WriteLine($"  - [{conflict.Type}] {conflict.Explanation}");
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
