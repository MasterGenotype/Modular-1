using System.ComponentModel;
using Modular.Switch.Totk;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Switch.Totk;

/// <summary><c>modular switch totk profile list</c>.</summary>
[Description("List TotK profiles")]
public sealed class TotkProfileListCommand : AsyncCommand
{
    public override Task<int> ExecuteAsync(CommandContext context) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        var current = cli.Service.CurrentProfile;

        foreach (var profile in cli.Service.ModManager.Profiles)
        {
            var marker = profile == current ? "[green]*[/]" : " ";
            var enabled = profile.Mods.Count(m => m.IsEnabled);
            AnsiConsole.MarkupLine($"{marker} {Markup.Escape(profile.Name)} [grey]({enabled}/{profile.Mods.Count} mods enabled)[/]");
        }

        return 0;
    });
}

/// <summary><c>modular switch totk profile create &lt;name&gt;</c>.</summary>
[Description("Create an empty TotK profile")]
public sealed class TotkProfileCreateCommand : AsyncCommand<TotkProfileCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--select")]
        [Description("Make it the current profile")]
        public bool Select { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        var profile = cli.Service.CreateProfile(settings.Name.Trim());
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(profile.Name)}");

        if (settings.Select)
        {
            cli.Service.SelectProfile(profile);
            AnsiConsole.MarkupLine($"[green]Selected[/] {Markup.Escape(profile.Name)} [grey](cheats and exefs merged)[/]");
        }

        return 0;
    });
}

/// <summary><c>modular switch totk profile select &lt;name&gt;</c>.</summary>
[Description("Make a TotK profile current (merges its cheats and exefs, as TKMM does)")]
public sealed class TotkProfileSelectCommand : AsyncCommand<TotkProfileSelectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.Service.FindProfile(settings.Name) is not { } profile)
        {
            AnsiConsole.MarkupLine($"[red]No profile named[/] {Markup.Escape(settings.Name)}");
            return 1;
        }

        cli.Service.SelectProfile(profile);
        AnsiConsole.MarkupLine($"[green]Selected[/] {Markup.Escape(profile.Name)} [grey](cheats and exefs merged; run merge for romfs)[/]");
        return 0;
    });
}

/// <summary><c>modular switch totk profile add &lt;mod&gt;</c>.</summary>
[Description("Add an installed TotK mod to the current profile")]
public sealed class TotkProfileAddCommand : AsyncCommand<TotkProfileModSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, TotkProfileModSettings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.FindModOrReport(settings.Mod) is not { } mod)
            return 1;

        var profile = cli.Service.CurrentProfile;
        profile.AddOrUpdate(mod);
        cli.Service.ModManager.Save();
        AnsiConsole.MarkupLine($"[green]Added[/] {Markup.Escape(mod.Name)} [grey]to {Markup.Escape(profile.Name)}[/]");
        return 0;
    });
}

/// <summary><c>modular switch totk profile remove &lt;mod&gt;</c>.</summary>
[Description("Remove a TotK mod from the current profile (it stays installed)")]
public sealed class TotkProfileRemoveCommand : AsyncCommand<TotkProfileModSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, TotkProfileModSettings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.FindProfileModOrReport(settings.Mod) is not { } profileMod)
            return 1;

        var profile = cli.Service.CurrentProfile;
        profile.Mods.Remove(profileMod);
        AnsiConsole.MarkupLine($"[green]Removed[/] {Markup.Escape(profileMod.Mod.Name)} [grey]from {Markup.Escape(profile.Name)}[/]");
        return 0;
    });
}

/// <summary>Settings naming one mod.</summary>
public sealed class TotkProfileModSettings : CommandSettings
{
    [CommandArgument(0, "<mod>")]
    [Description("Mod name or ID")]
    public string Mod { get; init; } = string.Empty;
}

/// <summary>
/// <c>modular switch totk order &lt;mod&gt; &lt;position&gt;</c>: moves a mod in the current
/// profile (1 = top = highest priority).
/// </summary>
[Description("Move a TotK mod to a position in the current profile (1 = highest priority)")]
public sealed class TotkOrderCommand : AsyncCommand<TotkOrderCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<mod>")]
        [Description("Mod name or ID")]
        public string Mod { get; init; } = string.Empty;

        [CommandArgument(1, "<position>")]
        [Description("New position, as `list` numbers them (1 = top = highest priority)")]
        public int Position { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.FindProfileModOrReport(settings.Mod) is not { } profileMod)
            return 1;

        var profile = cli.Service.CurrentProfile;
        if (settings.Position < 1 || settings.Position > profile.Mods.Count)
        {
            AnsiConsole.MarkupLine($"[red]Position must be 1 to {profile.Mods.Count}.[/]");
            return 1;
        }

        TotkMergeService.Move(profile, profileMod, settings.Position - 1);
        AnsiConsole.MarkupLine($"[green]Moved[/] {Markup.Escape(profileMod.Mod.Name)} [grey]to {settings.Position}[/]");
        return 0;
    });
}

/// <summary><c>modular switch totk enable &lt;mod&gt;</c>.</summary>
[Description("Enable a TotK mod in the current profile")]
public sealed class TotkEnableCommand : AsyncCommand<TotkProfileModSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, TotkProfileModSettings settings) =>
        TotkEnableDisable.SetAsync(settings.Mod, enabled: true);
}

/// <summary><c>modular switch totk disable &lt;mod&gt;</c>.</summary>
[Description("Disable a TotK mod in the current profile")]
public sealed class TotkDisableCommand : AsyncCommand<TotkProfileModSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, TotkProfileModSettings settings) =>
        TotkEnableDisable.SetAsync(settings.Mod, enabled: false);
}

internal static class TotkEnableDisable
{
    public static Task<int> SetAsync(string idOrName, bool enabled) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.FindProfileModOrReport(idOrName) is not { } profileMod)
            return 1;

        profileMod.IsEnabled = enabled;
        AnsiConsole.MarkupLine($"[green]{(enabled ? "Enabled" : "Disabled")}[/] {Markup.Escape(profileMod.Mod.Name)} " +
                               "[grey](merge to apply)[/]");
        return 0;
    });
}

/// <summary>
/// <c>modular switch totk option &lt;mod&gt; [&lt;group&gt; &lt;option&gt; (--on|--off)]</c>:
/// shows or sets a mod's options in the current profile.
/// </summary>
[Description("Show a TotK mod's options, or turn one on or off in the current profile")]
public sealed class TotkOptionCommand : AsyncCommand<TotkOptionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<mod>")]
        [Description("Mod name or ID")]
        public string Mod { get; init; } = string.Empty;

        [CommandArgument(1, "[group]")]
        public string? Group { get; init; }

        [CommandArgument(2, "[option]")]
        public string? Option { get; init; }

        [CommandOption("--on")]
        public bool On { get; init; }

        [CommandOption("--off")]
        public bool Off { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => TotkCli.RunAsync(async () =>
    {
        var cli = await TotkCli.OpenAsync();
        if (cli.FindProfileModOrReport(settings.Mod) is not { } profileMod)
            return 1;

        var profile = cli.Service.CurrentProfile;
        profile.RebaseOptions(profileMod);

        if (settings.Group is null)
        {
            if (profileMod.Mod.OptionGroups.Count == 0)
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(profileMod.Mod.Name)} has no options.[/]");

            foreach (var group in profileMod.Mod.OptionGroups)
            {
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(group.Name)}[/] [grey]({group.Type})[/]");
                foreach (var option in group.Options)
                    AnsiConsole.MarkupLine($"  {(option.IsEnabled ? "[green]✓[/]" : "[grey]✗[/]")} {Markup.Escape(option.Name)}");
            }

            return 0;
        }

        if (settings.Option is null || settings.On == settings.Off)
        {
            AnsiConsole.MarkupLine("[red]Give a group, an option, and --on or --off.[/]");
            return 1;
        }

        var targetGroup = profileMod.Mod.OptionGroups.FirstOrDefault(g => g.Name.Equals(settings.Group, StringComparison.OrdinalIgnoreCase));
        var targetOption = targetGroup?.Options.FirstOrDefault(o => o.Name.Equals(settings.Option, StringComparison.OrdinalIgnoreCase));
        if (targetGroup is null || targetOption is null)
        {
            AnsiConsole.MarkupLine("[red]No such group or option.[/] [grey]List them with: modular switch totk option <mod>[/]");
            return 1;
        }

        TotkMergeService.SetOptionEnabled(profile, profileMod, targetOption, settings.On);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(targetOption.Name)}[/] is now {(targetOption.IsEnabled ? "on" : "off")} [grey](merge to apply)[/]");
        return 0;
    });
}
