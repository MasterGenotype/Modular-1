using System.ComponentModel;
using System.Text.Json;
using Modular.Cli.Infrastructure;
using Modular.Core.Database;
using Modular.Core.Dependencies;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Core.Plugins;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Deployment;

/// <summary>
/// Deploys a game's enabled mods, in order, into the game's resolved roots.
/// </summary>
public sealed class DeployCommand : AsyncCommand<DeployCommand.Settings>
{
    /// <summary>Command settings.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>The game to deploy.</summary>
        [CommandOption("--game <GAME>")]
        [Description("Steam AppID, NexusMods slug, display name or install path")]
        public string Game { get; init; } = string.Empty;

        /// <summary>A profile whose load order to apply.</summary>
        [CommandOption("--profile <FILE>")]
        [Description("Profile JSON whose load order should be applied")]
        public string? Profile { get; init; }

        /// <summary>The deployment method for this run.</summary>
        [CommandOption("--method <METHOD>")]
        [Description("hardlink, symlink or copy (default: the game or config setting)")]
        public string? Method { get; init; }

        /// <summary>Whether to store the method for this game.</summary>
        [CommandOption("--save")]
        [Description("Store --method as this game's deployment method")]
        public bool Save { get; init; }

        /// <summary>Whether to remove everything Modular deployed instead of deploying.</summary>
        [CommandOption("--purge")]
        [Description("Remove everything Modular deployed and restore the original files")]
        public bool Purge { get; init; }

        /// <summary>Whether to lay every file down again, even those already in place.</summary>
        [CommandOption("--force")]
        [Description("Remove and redeploy every mod file, not only missing or changed ones")]
        public bool Force { get; init; }

        /// <summary>Whether to list every affected file.</summary>
        [CommandOption("--verbose")]
        [Description("List every file rather than counts")]
        public bool Verbose { get; init; }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            AnsiConsole.MarkupLine("[red]--game is required.[/]");
            return 1;
        }

        DeploymentMethod? method = null;
        if (settings.Method != null)
        {
            method = DeploymentMethods.ParseMethod(settings.Method);
            if (method == null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Unknown deployment method:[/] {Markup.Escape(settings.Method)}");
                AnsiConsole.MarkupLine("[grey]Valid methods: hardlink, symlink, copy[/]");
                return 1;
            }
        }

        var services = await RuntimeServices.InitializeMinimalAsync(settings.Verbose);
        var db = await OpenDatabaseAsync();

        try
        {
            var pluginLoader = new PluginLoader();
            pluginLoader.LoadAllPlugins();

            var installService = services.CreateInstallationService(db, pluginLoader);

            var installation = await installService.Deployment
                .ResolveInstallationAsync(settings.Game, cts.Token);

            if (installation == null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not resolve game:[/] {Markup.Escape(settings.Game)}");
                return 1;
            }

            AnsiConsole.MarkupLine(
                $"[bold]Game:[/] {Markup.Escape(installation.DisplayName ?? installation.GameId)} " +
                $"[grey]({Markup.Escape(installation.InstallPath)})[/]");

            if (settings.Purge)
            {
                var purged = installService.Deployment.Purge(installation);
                AnsiConsole.MarkupLine(
                    $"[green]Purged[/] {purged.Removed.Count} file(s), " +
                    $"restored {purged.Restored.Count} original(s)");

                foreach (var warning in purged.Warnings)
                    AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");

                return 0;
            }

            if (settings.Save && method != null)
            {
                var changed = await installService.Deployment.SetDeploymentMethodAsync(
                    installation, method.Value, ct: cts.Token);

                AnsiConsole.MarkupLine(
                    $"[green]Stored[/] deployment method '{method.Value.ToName()}' for this game");

                if (changed != null)
                {
                    // A method change is not comparable across identity records, so the
                    // deployer purged and redeployed rather than updating in place.
                    AnsiConsole.MarkupLine(
                        "[grey]The method changed, so the deployment was rebuilt.[/]");
                    DeploymentReport.Render(changed, settings.Verbose);
                    return changed.Success ? 0 : 1;
                }
            }

            var loadOrder = await ReadProfileLoadOrderAsync(settings.Profile);

            DeploymentResult? result = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Deploying...", async _ =>
                {
                    result = await installService.Deployment.DeployAsync(
                        installation, method, loadOrder, cts.Token, settings.Force);
                });

            if (result == null || !result.Success)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Deploy failed:[/] {Markup.Escape(result?.Error ?? "unknown error")}");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]Deploy complete[/]");
            DeploymentReport.Render(result, settings.Verbose);
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            await db.DisposeAsync();
            services.Dispose();
        }
    }

    internal static async Task<ModularDatabase> OpenDatabaseAsync()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "modular.db");
        var db = new ModularDatabase(dbPath);
        await db.InitializeAsync();
        return db;
    }

    /// <summary>
    /// Reads a profile's load order.
    /// </summary>
    /// <param name="profilePath">Path to the profile JSON, or <c>null</c>.</param>
    /// <returns>The load order, or <c>null</c> when no profile was given.</returns>
    private static async Task<IReadOnlyList<string>?> ReadProfileLoadOrderAsync(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
            return null;

        if (!File.Exists(profilePath))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Profile not found, using the stored order:[/] {Markup.Escape(profilePath)}");
            return null;
        }

        try
        {
            var profile = JsonSerializer.Deserialize<ModProfile>(
                await File.ReadAllTextAsync(profilePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return profile?.LoadOrder is { Count: > 0 } order ? order : null;
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Could not read the profile ({Markup.Escape(ex.Message)}); " +
                "using the stored order.[/]");
            return null;
        }
    }
}
