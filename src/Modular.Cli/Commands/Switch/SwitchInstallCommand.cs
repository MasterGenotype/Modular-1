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

        [CommandOption("--no-options")]
        [Description("Skip BNP option selection (install base content only)")]
        public bool NoOptions { get; init; }

        [CommandOption("--allow-conflicts")]
        [Description("Treat declared mod conflicts as warnings instead of errors")]
        public bool AllowConflicts { get; init; }
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
        var resolution = graph.Resolve(requestedKeys, settings.AllowConflicts);

        if (!resolution.Success)
        {
            AnsiConsole.MarkupLine($"[red]Dependency resolution failed:[/] {resolution.Error}");
            if (resolution.Conflicts.Count > 0)
                AnsiConsole.MarkupLine("[grey]Hint: use --allow-conflicts to override declared conflicts[/]");
            return 1;
        }

        // Show any warnings from conflict override
        foreach (var warning in resolution.Warnings)
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {warning}");

        // File overlap detection
        var overlapReport = await SwitchFileOverlapDetector.DetectAsync(resolution.InstallOrder, cts.Token);
        if (overlapReport.HasOverlaps)
        {
            AnsiConsole.MarkupLine($"\n[yellow]File overlaps detected ({overlapReport.Overlaps.Count} file(s)):[/]");
            var overlapTable = new Table().Border(TableBorder.Simple)
                .AddColumn("File").AddColumn("Provided by (last wins)");
            foreach (var overlap in overlapReport.Overlaps.Take(20))
                overlapTable.AddRow(
                    Markup.Escape(overlap.NormalizedPath),
                    Markup.Escape(string.Join(" → ", overlap.ModKeys)));
            if (overlapReport.Overlaps.Count > 20)
                overlapTable.AddRow($"[grey]... and {overlapReport.Overlaps.Count - 20} more[/]", "");
            AnsiConsole.Write(overlapTable);
        }

        // Interactive reorder when overlaps or warnings exist
        if ((overlapReport.HasOverlaps || resolution.Warnings.Count > 0)
            && !settings.DryRun
            && resolution.InstallOrder.Count > 1)
        {
            AnsiConsole.MarkupLine("\n[bold]Current install order (last mod wins for overlapping files):[/]");
            for (int i = 0; i < resolution.InstallOrder.Count; i++)
                AnsiConsole.MarkupLine($"  {i + 1}. {Markup.Escape(resolution.InstallOrder[i].Name)}");

            if (AnsiConsole.Confirm("\nReorder mods before installing?", defaultValue: false))
            {
                var modByName = resolution.InstallOrder.ToDictionary(m => m.Name);
                var names = resolution.InstallOrder.Select(m => m.Name).ToList();

                var reordered = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("Select mods in desired install order (select in order, last selected wins overlaps)")
                        .Required()
                        .PageSize(15)
                        .InstructionsText("[grey](Space to toggle, Enter to confirm)[/]")
                        .AddChoices(names));

                // Add any unselected mods at the beginning (keep them in original order)
                var unselected = names.Where(n => !reordered.Contains(n)).ToList();
                var fullOrder = unselected.Concat(reordered).ToList();

                // Validate dependency constraints
                var orderValid = true;
                var positionMap = fullOrder.Select((n, i) => (n, i))
                    .ToDictionary(x => modByName[x.n].ModKey, x => x.i, StringComparer.OrdinalIgnoreCase);

                foreach (var mod in resolution.InstallOrder)
                {
                    foreach (var dep in mod.Dependencies)
                    {
                        if (positionMap.TryGetValue(dep, out var depPos)
                            && positionMap.TryGetValue(mod.ModKey, out var modPos)
                            && depPos >= modPos)
                        {
                            AnsiConsole.MarkupLine(
                                $"[red]Invalid order:[/] '{Markup.Escape(mod.Name)}' depends on " +
                                $"'{Markup.Escape(dep)}' which is placed after it.");
                            orderValid = false;
                        }
                    }
                }

                if (!orderValid)
                {
                    AnsiConsole.MarkupLine("[yellow]Keeping original order due to dependency constraint violations.[/]");
                }
                else
                {
                    resolution.InstallOrder = fullOrder.Select(n => modByName[n]).ToList();
                    for (int i = 0; i < resolution.InstallOrder.Count; i++)
                        resolution.InstallOrder[i].LoadOrder = i + 1;
                }
            }
        }

        AnsiConsole.MarkupLine($"\n[bold]Installing {resolution.InstallOrder.Count} mod(s) → TitleID {titleId}[/]");
        if (settings.DryRun)
            AnsiConsole.MarkupLine("[yellow]DRY RUN — no files will be written[/]");
        AnsiConsole.WriteLine();

        // BNP option selection
        if (!settings.NoOptions)
        {
            foreach (var mod in resolution.InstallOrder.Where(m => m.HasBnpOptions))
            {
                AnsiConsole.MarkupLine($"\n[bold]Options for '{Markup.Escape(mod.Name)}':[/]");
                mod.SelectedBnpOptions.Clear();
                var options = mod.BnpOptions!;

                // Single-select groups (pick exactly one)
                foreach (var group in options.Single.Where(g => g.Options.Count > 0))
                {
                    var prompt = new SelectionPrompt<string>()
                        .Title($"[yellow]{Markup.Escape(group.Description)}[/]")
                        .PageSize(10)
                        .AddChoices(group.Options.Select(o => o.Name));

                    var chosen = AnsiConsole.Prompt(prompt);
                    var folder = group.Options.First(o => o.Name == chosen).Folder;
                    mod.SelectedBnpOptions.Add(folder);
                }

                // Multi-select groups (pick any number)
                foreach (var group in options.Multi.Where(g => g.Options.Count > 0))
                {
                    var prompt = new MultiSelectionPrompt<string>()
                        .Title($"[yellow]{Markup.Escape(group.Description)}[/]")
                        .PageSize(10)
                        .InstructionsText("[grey](Space to toggle, Enter to confirm)[/]")
                        .AddChoices(group.Options.Select(o => o.Name));

                    var chosen = AnsiConsole.Prompt(prompt);
                    foreach (var name in chosen)
                    {
                        var folder = group.Options.First(o => o.Name == name).Folder;
                        mod.SelectedBnpOptions.Add(folder);
                    }
                }

                AnsiConsole.MarkupLine($"[grey]Selected {mod.SelectedBnpOptions.Count} option(s)[/]");
            }
        }

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
