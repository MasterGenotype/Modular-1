using System.ComponentModel;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary>
/// <c>modular switch totk config [&lt;key&gt; [&lt;value&gt;]] [--add] [--clear] [--check]</c>:
/// shows or changes where the game dump and keys are, and how merges are written.
/// </summary>
[Description("Show or set the TotK game dump, keys and merge settings")]
public sealed class TotkConfigCommand : AsyncCommand<TotkConfigCommand.Settings>
{
    private static readonly string[] Keys =
    [
        "keys", "base-game", "update", "sd-card", "dump", "nand",
        "version", "language", "merge-output", "romfslite", "emulator", "7z"
    ];

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[key]")]
        [Description("keys, base-game, update, sd-card, dump, nand, version, language, merge-output, romfslite, emulator or 7z")]
        public string? Key { get; init; }

        [CommandArgument(1, "[value]")]
        [Description("The new value. For base-game, update, dump and nand it replaces the list (see --add)")]
        public string? Value { get; init; }

        [CommandOption("--add")]
        [Description("Add the value to a list setting (base-game, update, dump, nand) instead of replacing it")]
        public bool Add { get; init; }

        [CommandOption("--clear")]
        [Description("Unset the setting (empty the list, or restore the default)")]
        public bool Clear { get; init; }

        [CommandOption("--check")]
        [Description("Check that the game can be read with these settings")]
        public bool Check { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();

        if (settings.Key is null)
        {
            Show(cli);
            if (settings.Check)
                return cli.ReportRom() ? 0 : 1;
            return 0;
        }

        var key = settings.Key.Trim().ToLowerInvariant();
        if (!Keys.Contains(key))
        {
            AnsiConsole.MarkupLine($"[red]Unknown key:[/] {Markup.Escape(settings.Key)} [grey]({string.Join(", ", Keys)})[/]");
            return 1;
        }

        if (!settings.Clear && settings.Value is null)
        {
            AnsiConsole.MarkupLine("[red]Give a value, or --clear to unset.[/]");
            return 1;
        }

        if (!Apply(cli, key, settings.Clear ? null : settings.Value, settings.Add))
            return 1;

        await cli.SaveSettingsAsync();
        AnsiConsole.MarkupLine($"[green]Saved[/] [grey]{Markup.Escape(ConfigurationService.DefaultConfigPath)}[/]");

        if (settings.Check)
            return cli.ReportRom() ? 0 : 1;
        return 0;
    });

    private static bool Apply(TotkCli cli, string key, string? value, bool add)
    {
        var s = cli.Settings;
        var path = value is null ? null : Path.GetFullPath(FileUtils.ExpandPath(value));

        switch (key)
        {
            case "keys": s.KeysFolderPath = path; break;
            case "sd-card": s.SdCardRootPath = path; break;
            case "emulator": s.EmulatorPath = path; break;
            case "7z": s.SevenZipPath = path; break;
            case "base-game": SetList(s.PackagedBaseGamePaths, path, add); break;
            case "update": SetList(s.PackagedUpdatePaths, path, add); break;
            case "dump": SetList(s.GameDumpFolderPaths, path, add); break;
            case "nand": SetList(s.NandFolderPaths, path, add); break;
            case "merge-output": cli.Service.SetMergeOutput(path); break;

            case "version":
            {
                var version = value ?? TotkSettings.AutoGameVersion;
                var match = TotkSettings.GameVersions.FirstOrDefault(v => v.Equals(version, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    AnsiConsole.MarkupLine($"[red]Unknown version:[/] {Markup.Escape(version)} " +
                                           $"[grey]({string.Join(", ", TotkSettings.GameVersions)})[/]");
                    return false;
                }

                s.PreferredGameVersion = match;
                break;
            }

            case "language":
            {
                var language = value ?? "USen";
                var match = TotkSettings.GameLanguages.FirstOrDefault(l => l.Value.Equals(language, StringComparison.OrdinalIgnoreCase));
                if (match.Value is null)
                {
                    AnsiConsole.MarkupLine($"[red]Unknown language:[/] {Markup.Escape(language)} " +
                                           $"[grey]({string.Join(", ", TotkSettings.GameLanguages.Select(l => l.Value))})[/]");
                    return false;
                }

                s.GameLanguage = match.Value;
                break;
            }

            case "romfslite":
            {
                if (value is null)
                {
                    s.UseRomfslite = false;
                }
                else if (bool.TryParse(value, out var on))
                {
                    s.UseRomfslite = on;
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]romfslite is true or false.[/]");
                    return false;
                }

                break;
            }
        }

        return true;
    }

    private static void SetList(List<string> list, string? path, bool add)
    {
        if (!add)
            list.Clear();
        if (path != null && !list.Contains(path))
            list.Add(path);
    }

    private static void Show(TotkCli cli)
    {
        var s = cli.Settings;
        var table = new Table().Border(TableBorder.Rounded).AddColumn("[bold]Key[/]").AddColumn("[bold]Value[/]");

        table.AddRow("keys", Value(s.KeysFolderPath));
        table.AddRow("base-game", List(s.PackagedBaseGamePaths));
        table.AddRow("update", List(s.PackagedUpdatePaths));
        table.AddRow("sd-card", Value(s.SdCardRootPath));
        table.AddRow("dump", List(s.GameDumpFolderPaths));
        table.AddRow("nand", List(s.NandFolderPaths));
        table.AddRow("version", Markup.Escape(s.PreferredGameVersion));
        table.AddRow("language", Markup.Escape(s.GameLanguage));
        table.AddRow("merge-output", Markup.Escape(cli.Service.MergedOutputFolder) +
                                     (string.IsNullOrWhiteSpace(s.MergeOutput) ? " [grey](default)[/]" : ""));
        table.AddRow("romfslite", s.UseRomfslite ? "true" : "false");
        table.AddRow("emulator", Value(s.EmulatorPath, "(not set: Yuzu's default folders)"));
        table.AddRow("7z", Value(s.SevenZipPath, "(not set: found on PATH)"));

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Set with: modular switch totk config <key> <value>   Check the game with: --check[/]");
    }

    private static string Value(string? value, string unset = "(not set)") =>
        string.IsNullOrWhiteSpace(value) ? $"[grey]{Markup.Escape(unset)}[/]" : Markup.Escape(value);

    private static string List(List<string> values) =>
        values.Count == 0 ? "[grey](none)[/]" : Markup.Escape(string.Join("\n", values));
}
