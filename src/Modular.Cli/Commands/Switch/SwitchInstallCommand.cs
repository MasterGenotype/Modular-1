using System.ComponentModel;
using Modular.Switch.DependencyResolver;
using Modular.Switch.Installer;
using Modular.Switch.Lutris;
using Modular.Switch.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch;

/// <summary>
/// modular switch install --game &lt;TITLE_ID&gt; [--mods &lt;ModA,...&gt;] [--runner lutris] [--dry-run]
///
/// Resolves the dependency graph, then installs mods into Yuzu's load directory
/// in the correct topological order.  Transactional: any failure triggers
/// snapshot-based rollback of already-installed mods in the batch.
///
/// When --runner lutris is supplied, generates and wires a Lutris pre-launch hook.
/// </summary>
[Description("Install Switch mods into Yuzu's load directory")]
public sealed class SwitchInstallCommand : AsyncCommand<SwitchInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--game <TITLE_ID>")]
        [Description("16-digit hex TitleID of the target Switch game")]
        public string TitleId { get; init; } = string.Empty;

        [CommandOption("--mods <MODS>")]
        [Description("Comma-separated mod names/keys; omit to install all known mods for the game")]
        public string? Mods { get; init; }

        [CommandOption("--runner <RUNNER>")]
        [Description("Game runner type (e.g. 'lutris'). Enables runner-specific hooks")]
        public string? Runner { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be installed without making changes")]
        public bool DryRun { get; init; }

        [CommandOption("--force")]
        [Description("Reinstall even if the mod hash is unchanged")]
        public bool Force { get; init; }

        [CommandOption("--auto-apply-hook")]
        [Description("When --runner lutris: re-run install on every game launch")]
        public bool AutoApplyHook { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (!SwitchTitleId.TryParse(settings.TitleId, out var titleId))
        {
            AnsiConsole.MarkupLine($"[red]Invalid TitleID:[/] {settings.TitleId}");
            return 1;
        }

        // Load state
        var state = await SwitchInstallState.LoadAsync(ct: cts.Token);
        var gameMods = state.ForTitle(titleId).ToList();

        if (gameMods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No mods found for TitleID {titleId}.[/]");
            AnsiConsole.MarkupLine("[grey]Run: modular switch scan <path> --game <TITLE_ID>[/]");
            return 1;
        }

        // Determine which mods to install
        List<string> requestedKeys;
        if (!string.IsNullOrEmpty(settings.Mods))
        {
            requestedKeys = settings.Mods
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name =>
                {
                    var exact = gameMods.FirstOrDefault(m =>
                        m.ModKey.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (exact != null) return exact.ModKey;
                    var partial = gameMods.FirstOrDefault(m =>
                        m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                    return partial?.ModKey ?? name;
                })
                .ToList();
        }
        else
        {
            requestedKeys = gameMods.Select(m => m.ModKey).ToList();
        }

        // Resolve dependency graph
        var graph = new SwitchDependencyGraph();
        graph.AddRange(gameMods);
        var resolution = graph.Resolve(requestedKeys);

        if (!resolution.Success)
        {
            AnsiConsole.MarkupLine($"[red]Dependency resolution failed:[/] {resolution.Error}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Installing {resolution.InstallOrder.Count} mod(s) → TitleID {titleId}[/]");
        if (settings.DryRun)
            AnsiConsole.MarkupLine("[yellow]DRY RUN — no files will be written[/]");
        AnsiConsole.WriteLine();

        // Force flag: clear installed state so hash check is bypassed
        if (settings.Force)
            foreach (var mod in resolution.InstallOrder)
                mod.InstalledHash = string.Empty;

        var installer = new SwitchModInstaller();
        var installedNames = new List<string>();
        int exitCode = 0;

        await AnsiConsole.Progress()
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async progressCtx =>
            {
                foreach (var mod in resolution.InstallOrder)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var task = progressCtx.AddTask($"[cyan]{Markup.Escape(mod.Name)}[/]",
                        maxValue: Math.Max(1, 100));

                    var progress = new Progress<SwitchInstallProgress>(p =>
                    {
                        if (p.TotalFiles > 0)
                            task.Value = (double)p.FilesProcessed / p.TotalFiles * 100;
                        task.Description =
                            $"[cyan]{Markup.Escape(mod.Name)}[/] [grey]{p.Phase}[/]";
                    });

                    var result = await installer.InstallAsync(mod, settings.DryRun, progress, cts.Token);
                    task.Value = 100;

                    if (result.Skipped)
                    {
                        task.Description =
                            $"[grey]{Markup.Escape(mod.Name)} — {result.SkipReason}[/]";
                        continue;
                    }

                    if (!result.Success)
                    {
                        task.Description =
                            $"[red]{Markup.Escape(mod.Name)} FAILED: {Markup.Escape(result.Error ?? "unknown error")}[/]";
                        exitCode = 1;
                        break;
                    }

                    // Update state
                    if (!settings.DryRun)
                    {
                        mod.IsInstalled  = true;
                        mod.InstalledAt  = DateTime.UtcNow;
                        mod.InstalledHash = mod.SourceHash;
                        state.Upsert(mod);
                        installedNames.Add(mod.Name);
                    }

                    task.Description = $"[green]{Markup.Escape(mod.Name)}[/]";
                }
            });

        if (exitCode != 0)
        {
            AnsiConsole.MarkupLine("\n[red]Installation failed. Any completed mods have been rolled back.[/]");
            return exitCode;
        }

        // Persist state
        if (!settings.DryRun)
            await state.SaveAsync(ct: cts.Token);

        // Lutris hook
        if (string.Equals(settings.Runner, "lutris", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Writing Lutris pre-launch hook...[/]");
            await LutrisHookWriter.WriteAsync(titleId, installedNames, settings.AutoApplyHook, cts.Token);
            var hookPath = LutrisHookWriter.HookPath(titleId);
            AnsiConsole.MarkupLine($"[green]Hook written:[/] {hookPath}");

            var injected = await LutrisHookWriter.TryInjectLutrisConfigAsync(titleId, cts.Token);
            if (injected)
                AnsiConsole.MarkupLine("[green]Lutris game config updated automatically.[/]");
            else
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not auto-detect Lutris config.[/]\n" +
                    $"[grey]Set Pre-launch script to:[/] {hookPath}");
        }

        if (settings.DryRun)
            AnsiConsole.MarkupLine($"\n[yellow]DRY RUN complete — {resolution.InstallOrder.Count} mod(s) would be installed.[/]");
        else
            AnsiConsole.MarkupLine($"\n[green]{installedNames.Count} mod(s) installed successfully.[/]");

        return 0;
    }
}
