using System.ComponentModel;
using Modular.Core.Configuration;
using Modular.Core.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Deployment;

/// <summary>
/// Sets a value in <c>config.json</c>.
/// </summary>
/// <remarks>
/// Only the settings a user is expected to change from the command line are exposed; the
/// rest of <c>config.json</c> stays hand-edited.
/// </remarks>
public sealed class ConfigSetCommand : AsyncCommand<ConfigSetCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The setting to change.</summary>
        [CommandArgument(0, "<key>")]
        [Description("deployment.method or deployment.fallback")]
        public string Key { get; init; } = string.Empty;

        /// <summary>The new value.</summary>
        [CommandArgument(1, "<value>")]
        [Description("For deployment.method: hardlink, symlink or copy. For deployment.fallback: copy or fail")]
        public string Value { get; init; } = string.Empty;
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var service = new ConfigurationService();
        var config = await service.LoadAsync();

        switch (settings.Key.Trim().ToLowerInvariant())
        {
            case "deployment.method":
            {
                var method = DeploymentMethods.ParseMethod(settings.Value);
                if (method == null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Unknown method:[/] {Markup.Escape(settings.Value)} " +
                        "[grey](hardlink, symlink, copy)[/]");
                    return 1;
                }

                config.DeploymentMethod = method.Value.ToName();
                break;
            }

            case "deployment.fallback":
            {
                var fallback = DeploymentMethods.ParseFallback(settings.Value);
                if (fallback == null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Unknown fallback:[/] {Markup.Escape(settings.Value)} " +
                        "[grey](copy, fail)[/]");
                    return 1;
                }

                config.DeploymentFallback = fallback.Value.ToName();
                break;
            }

            default:
                AnsiConsole.MarkupLine($"[red]Unknown setting:[/] {Markup.Escape(settings.Key)}");
                AnsiConsole.MarkupLine(
                    "[grey]Settable keys: deployment.method, deployment.fallback[/]");
                return 1;
        }

        await service.SaveAsync(config);

        AnsiConsole.MarkupLine(
            $"[green]Set[/] {Markup.Escape(settings.Key)} = {Markup.Escape(settings.Value)}");
        return 0;
    }
}

/// <summary>
/// Shows the settings <c>config set</c> can change.
/// </summary>
public sealed class ConfigShowCommand : AsyncCommand<ConfigShowCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var config = await new ConfigurationService().LoadAsync();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting");
        table.AddColumn("Value");

        table.AddRow("deployment.method", Markup.Escape(config.DeploymentMethod));
        table.AddRow("deployment.fallback", Markup.Escape(config.DeploymentFallback));
        table.AddRow("mods_directory", Markup.Escape(config.ModsDirectory));
        table.AddRow("enabled_backends", Markup.Escape(string.Join(", ", config.EnabledBackends)));

        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ConfigurationService.DefaultConfigPath)}[/]");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            "[grey]A per-game method (deploy --method … --save) overrides deployment.method.[/]");
        return 0;
    }
}
