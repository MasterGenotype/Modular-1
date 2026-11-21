using Modular.Cli.UI;
using Modular.Core.Depots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Steam;

/// <summary><c>modular depot lock &lt;appid&gt;</c>: opt-in hard lock of a pinned build (pin layer 6).</summary>
public sealed class DepotLockCommand : AsyncCommand<DepotLockCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        AnsiConsole.MarkupLine("The hard lock makes this build's files unchangeable until you unlock them:");
        AnsiConsole.MarkupLine("  • Steam can't update, repair or overwrite them, whether it is open or closed.");
        AnsiConsole.MarkupLine("  • Steam will show download or disk-write errors for this game while it is locked.");
        AnsiConsole.MarkupLine("  • New files (logs, configs, mods) can still be created; Modular unlocks for its own swaps.");
        return await DepotCli.LockAsync(settings.AppId, locked: true, cts.Token);
    }
}

public sealed class DepotUnlockCommand : AsyncCommand<DepotUnlockCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        return await DepotCli.LockAsync(settings.AppId, locked: false, cts.Token);
    }
}

internal static class DepotCli
{
    public static async Task<int> LockAsync(uint appId, bool locked, CancellationToken ct)
    {
        try
        {
            AnsiConsole.MarkupLine($"[grey]{(locked ? "Locking" : "Unlocking")} the pinned build's files…[/]");
            var r = await new DepotVersionService().SetLockAsync(appId, locked, ct);
            AnsiConsole.MarkupLine($"[grey]{(locked ? "Locked" : "Unlocked")} {r.Changed}, already {(locked ? "locked" : "unlocked")} {r.Unchanged}, skipped {r.Skipped}.[/]");
            foreach (var s in r.SkipReasons.Take(10))
                AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(s)}[/]");
            var name = SteamAppManifest.Find(appId)?.Name ?? appId.ToString();
            LiveProgressDisplay.ShowSuccess(locked ? $"{name} is locked. Undo with: modular depot unlock {appId}" : $"{name} is unlocked (still pinned).");
            return 0;
        }
        catch (DepotOperationException ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
