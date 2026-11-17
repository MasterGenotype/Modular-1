using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.Depots;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Steam;

/// <summary>Shared lock plumbing for lock/unlock/swap/unpin/status.</summary>
internal static class PinLock
{
    /// <summary>Every file of the pinned build, relative to the install folder.</summary>
    public static IReadOnlyList<string>? Paths(BuildPin pin, DepotManifestStore store)
    {
        var paths = new List<string>();
        foreach (var (depot, manifest) in pin.Depots)
        {
            var view = store.LoadView(depot, manifest);
            if (view is null)
                return null;
            paths.AddRange(view.Files.Select(f => f.Path));
        }
        return paths;
    }

    public static async Task<bool> SetAsync(SteamAppManifest acf, BuildPin pin, DepotManifestStore store, bool locked, CancellationToken ct)
    {
        var paths = Paths(pin, store);
        if (paths is null)
        {
            LiveProgressDisplay.ShowError("A pinned manifest is missing from Modular's archive; can't list the build's files.");
            return false;
        }
        AnsiConsole.MarkupLine($"[grey]{(locked ? "Locking" : "Unlocking")} {paths.Count} file(s); your system will ask for administrator rights…[/]");
        try
        {
            var r = await BuildLock.SetAsync(acf.InstallPath, paths, locked, ct);
            AnsiConsole.MarkupLine($"[grey]{(locked ? "Locked" : "Unlocked")} {r.Changed}, already {(locked ? "locked" : "unlocked")} {r.Unchanged}, skipped {r.Skipped}.[/]");
            foreach (var s in r.SkipReasons.Take(10))
                AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(s)}[/]");
            return true;
        }
        catch (BuildLockUnavailableException ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return false;
        }
    }
}

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
        var acf = SteamAppManifest.Find(settings.AppId);
        var pins = new BuildPinStore();
        var pin = pins.Load(settings.AppId);
        if (acf is null || pin is null)
        {
            LiveProgressDisplay.ShowError($"App {settings.AppId} has no pinned build. Pin it first ('depot pin' or 'depot swap').");
            return 1;
        }

        AnsiConsole.MarkupLine("The hard lock makes this build's files unchangeable until you unlock them:");
        AnsiConsole.MarkupLine("  • Steam can't update, repair or overwrite them, whether it is open or closed.");
        AnsiConsole.MarkupLine("  • Steam will show download or disk-write errors for this game while it is locked.");
        AnsiConsole.MarkupLine("  • New files (logs, configs, mods) can still be created; Modular unlocks for its own swaps.");
        if (!await PinLock.SetAsync(acf, pin, new DepotManifestStore(), locked: true, cts.Token))
            return 1;
        pins.Save(pin with { Locked = true });
        LiveProgressDisplay.ShowSuccess($"{acf.Name} is locked. Undo with: modular depot unlock {acf.AppId}");
        return 0;
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
        var acf = SteamAppManifest.Find(settings.AppId);
        var pins = new BuildPinStore();
        var pin = pins.Load(settings.AppId);
        if (acf is null || pin is null)
        {
            LiveProgressDisplay.ShowError($"App {settings.AppId} has no pinned build.");
            return 1;
        }
        if (!await PinLock.SetAsync(acf, pin, new DepotManifestStore(), locked: false, cts.Token))
            return 1;
        pins.Save(pin with { Locked = false });
        LiveProgressDisplay.ShowSuccess($"{acf.Name} is unlocked (still pinned).");
        return 0;
    }
}
