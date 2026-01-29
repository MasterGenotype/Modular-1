using Spectre.Console;

namespace Modular.Cli.UI;

/// <summary>
/// Terminal progress display using Spectre.Console.
/// </summary>
public class LiveProgressDisplay
{
    /// <summary>
    /// Runs a task with a progress bar.
    /// </summary>
    public static async Task RunWithProgressAsync(string description, Func<IProgress<(string status, int completed, int total)>, Task> task)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var progressTask = ctx.AddTask($"[green]{Markup.Escape(description)}[/]");
                progressTask.MaxValue = 100;

                // Create a thread-safe progress reporter that updates Spectre directly
                var progress = new SpectreProgress(progressTask);

                await task(progress);
                progressTask.Value = progressTask.MaxValue;
            });
    }

    /// <summary>
    /// Thread-safe progress reporter for Spectre.Console.
    /// </summary>
    private class SpectreProgress : IProgress<(string status, int completed, int total)>
    {
        private readonly ProgressTask _task;

        public SpectreProgress(ProgressTask task) => _task = task;

        public void Report((string status, int completed, int total) value)
        {
            _task.Description = $"[green]{Markup.Escape(value.status)}[/]";
            if (value.total > 0)
            {
                _task.MaxValue = value.total;
                _task.Value = value.completed;
            }
        }
    }

    /// <summary>
    /// Displays an interactive menu and returns the selected option.
    /// </summary>
    public static string ShowMenu(string title, string[] options)
    {
        AnsiConsole.Write(new Rule($"[yellow]{title}[/]").RuleStyle("grey"));

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Choose an option:")
                .PageSize(10)
                .AddChoices(options));

        return selection;
    }

    /// <summary>
    /// Displays a simple menu with numbered options.
    /// </summary>
    public static int ShowNumberedMenu(string title, string[] options)
    {
        AnsiConsole.MarkupLine($"[bold yellow]=== {title} ===[/]");
        for (int i = 0; i < options.Length; i++)
        {
            AnsiConsole.MarkupLine($"[grey]{i + 1}.[/] {options[i]}");
        }
        AnsiConsole.MarkupLine("[grey]0.[/] Exit");

        while (true)
        {
            AnsiConsole.Markup("[green]Choose: [/]");
            var input = Console.ReadLine();
            if (int.TryParse(input, out var choice) && choice >= 0 && choice <= options.Length)
            {
                return choice;
            }
            AnsiConsole.MarkupLine("[red]Invalid choice. Try again.[/]");
        }
    }

    /// <summary>
    /// Prompts for a string input.
    /// </summary>
    public static string AskString(string prompt, string? defaultValue = null)
    {
        var textPrompt = new TextPrompt<string>($"[green]{prompt}[/]");
        if (defaultValue != null)
        {
            textPrompt.DefaultValue(defaultValue);
        }
        return AnsiConsole.Prompt(textPrompt);
    }

    /// <summary>
    /// Prompts for a yes/no confirmation.
    /// </summary>
    public static bool Confirm(string prompt, bool defaultValue = true)
    {
        return AnsiConsole.Confirm(prompt, defaultValue);
    }

    /// <summary>
    /// Displays an error message.
    /// </summary>
    public static void ShowError(string message)
    {
        AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays a warning message.
    /// </summary>
    public static void ShowWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]WARNING:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays an info message.
    /// </summary>
    public static void ShowInfo(string message)
    {
        AnsiConsole.MarkupLine($"[blue]INFO:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Displays a success message.
    /// </summary>
    public static void ShowSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    }
}
