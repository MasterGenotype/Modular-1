using Modular.Core.Deployment;
using Modular.Sdk.Modules;
using Spectre.Console;

namespace Modular.Cli.Infrastructure;

/// <summary>
/// Renders a <see cref="DeploymentResult"/> for the terminal.
/// </summary>
/// <remarks>
/// Shared by <c>install</c> and <c>deploy</c> so both report a deployment the same way.
/// </remarks>
public static class DeploymentReport
{
    /// <summary>
    /// Prints what a deployment did.
    /// </summary>
    /// <param name="result">The deployment result.</param>
    /// <param name="verbose">Whether to list every file rather than counts.</param>
    public static void Render(DeploymentResult result, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("Deployment");
        summary.AddColumn("Count");

        AddRow(summary, "Placed", result.Placed.Count);
        AddRow(summary, "Unchanged", result.Unchanged.Count);
        AddRow(summary, "Removed", result.Removed.Count);
        AddRow(summary, "Originals restored", result.Restored.Count);
        AddRow(summary, "Originals backed up", result.VanillaBackedUp.Count);
        AddRow(summary, "Replaced unmanaged files", result.ReplacedUnmanaged.Count);
        AddRow(summary, "Preserved your edits", result.Displaced.Count);
        AddRow(summary, "Copied (method unavailable)", result.FellBackToCopy.Count);
        AddRow(summary, "Module constraints applied", result.ConstraintApplied.Count);

        summary.AddRow(
            "Method",
            $"{result.RequestedMethod.ToName()} [grey]({Markup.Escape(result.MethodSource)})[/]");

        AnsiConsole.Write(summary);

        if (result.Plan is { } plan && plan.Overlaps.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]File overrides[/] [grey](later in the order wins)[/]");

            foreach (var overlap in plan.Overlaps.Take(verbose ? int.MaxValue : 10))
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]{Markup.Escape(overlap.Root)}/{Markup.Escape(overlap.RelativePath)}:[/] " +
                    $"{Markup.Escape(overlap.WinnerModId)} [grey]over[/] {Markup.Escape(overlap.LoserModId)}");
            }

            if (!verbose && plan.Overlaps.Count > 10)
                AnsiConsole.MarkupLine($"  [grey]… and {plan.Overlaps.Count - 10} more[/]");
        }

        if (result.Plan is { } p2 && p2.EngineOrderNotes.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[bold]Engine load order[/] [grey](these all load; the engine picks one)[/]");

            foreach (var note in p2.EngineOrderNotes)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]{Markup.Escape(note.Folder)}/{Markup.Escape(note.BaseName)}*[/] " +
                    $"[grey]from {Markup.Escape(string.Join(", ", note.ModIds))}[/]");
            }
        }

        if (verbose && result.Plan is { } p3 && p3.Skipped.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Skipped[/]");
            foreach (var skipped in p3.Skipped)
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]{Markup.Escape(skipped.Root)}/{Markup.Escape(skipped.RelativePath)}: " +
                    $"{Markup.Escape(skipped.Reason)}[/]");
            }
        }

        foreach (var warning in result.Warnings)
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");

        RenderLaunchRequirements(result);
    }

    /// <summary>
    /// Prints the launch arguments, Wine overrides and tool actions a deployed set needs.
    /// </summary>
    /// <param name="result">The deployment result.</param>
    /// <remarks>
    /// Reported, never applied: Modular does not edit Steam's launch options. A missing
    /// native-first DLL override is the most common cause of "the mod installed but
    /// nothing happened" under Proton, so it is worth showing prominently.
    /// </remarks>
    public static void RenderLaunchRequirements(DeploymentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var requirements = result.LaunchRequirements;
        var overrideOption = result.WineDllOverridesLaunchOption;

        if (requirements.IsEmpty && overrideOption == null)
            return;

        AnsiConsole.WriteLine();

        var lines = new List<string>();

        if (requirements.LaunchArguments.Count > 0)
        {
            lines.Add("[bold]Launch arguments[/]");
            lines.Add($"  {Markup.Escape(string.Join(" ", requirements.LaunchArguments))}");
        }

        if (overrideOption != null)
        {
            lines.Add("[bold]Steam launch options[/] [grey](Proton needs this for proxy DLLs)[/]");
            lines.Add($"  {Markup.Escape(overrideOption)}");
        }

        foreach (var action in requirements.PostDeployActions)
        {
            lines.Add($"[bold]{Markup.Escape(action.Description)}[/]");
            lines.Add(
                $"  {Markup.Escape(action.Executable)} " +
                Markup.Escape(string.Join(" ", action.Arguments)));

            if (!action.AutoRunnable)
                lines.Add("  [grey]run this yourself; Modular will not[/]");
        }

        AnsiConsole.Write(new Panel(string.Join("\n", lines))
            .Header("Launch requirements")
            .BorderColor(Color.Yellow));
    }

    private static void AddRow(Table table, string label, int count)
    {
        if (count == 0)
            return;

        table.AddRow(label, count.ToString());
    }
}
