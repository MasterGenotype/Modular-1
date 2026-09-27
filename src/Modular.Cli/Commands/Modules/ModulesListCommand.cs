using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.Modules;
using Modular.Core.Plugins;
using Modular.Sdk.Modules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Modules;

/// <summary>
/// Lists the registered game modules, built-in and plugin-supplied.
/// </summary>
public sealed class ModulesListCommand : AsyncCommand<ModulesListCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Whether to also show each module's installers and anchors.</summary>
        [CommandOption("--verbose")]
        [Description("Show each module's installers")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var registry = new GameModuleRegistry();

            // Plugin modules are only visible once their plugins have been loaded.
            var pluginModuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var loader = new PluginLoader();
                loader.LoadAllPlugins();

                foreach (var module in loader.GetAllModules())
                {
                    pluginModuleIds.Add(module.ModuleId);
                    registry.Register(module);
                }
            }
            catch (Exception ex)
            {
                LiveProgressDisplay.ShowWarning($"Could not load plugin modules: {ex.Message}");
            }

            var modules = registry.Modules
                .Concat([registry.Fallback])
                .ToList();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Module");
            table.AddColumn("Name");
            table.AddColumn("Source");
            table.AddColumn("Game ids");
            table.AddColumn("Steam AppIDs");

            foreach (var module in modules)
            {
                var source = module.ModuleId == GenericGameModule.Id
                    ? "[grey]fallback[/]"
                    : pluginModuleIds.Contains(module.ModuleId)
                        ? "[blue]plugin[/]"
                        : "built-in";

                table.AddRow(
                    Markup.Escape(module.ModuleId),
                    Markup.Escape(module.DisplayName),
                    source,
                    module.GameIds.Count == 0
                        ? "[grey]any[/]"
                        : Markup.Escape(string.Join(", ", module.GameIds)),
                    module.SteamAppIds.Count == 0
                        ? "[grey]—[/]"
                        : Markup.Escape(string.Join(", ", module.SteamAppIds)));
            }

            AnsiConsole.Write(table);

            if (settings.Verbose)
            {
                foreach (var module in modules)
                {
                    var installers = SafeGetInstallers(module);

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[bold]{Markup.Escape(module.DisplayName)}[/]");

                    if (installers.Count == 0)
                    {
                        AnsiConsole.MarkupLine("  [grey]universal installers only[/]");
                        continue;
                    }

                    foreach (var installer in installers)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [grey]installer:[/] {Markup.Escape(installer.InstallerId)} " +
                            $"[grey](priority {installer.Priority})[/]");
                    }
                }
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return Task.FromResult(1);
        }
    }

    private static IReadOnlyList<Sdk.Installers.IModInstaller> SafeGetInstallers(IGameModule module)
    {
        try
        {
            return module.GetInstallers();
        }
        catch (Exception ex)
        {
            LiveProgressDisplay.ShowWarning(
                $"Module {module.ModuleId} threw while listing installers: {ex.Message}");
            return Array.Empty<Sdk.Installers.IModInstaller>();
        }
    }
}
