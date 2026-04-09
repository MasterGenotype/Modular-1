using Spectre.Console;

namespace Modular.Cli.UI;

/// <summary>
/// Terminal progress display using Spectre.Console.
/// </summary>
public class LiveProgressDisplay
{
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
