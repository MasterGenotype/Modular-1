using System.ComponentModel;
using System.Text.Json;
using Modular.Cli.UI;
using Modular.Core.Depots;
using Modular.Core.Security;
using Modular.Core.Steam;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Steam;

public class DepotTargetSettings : CommandSettings
{
    [CommandArgument(0, "<APPID>")]
    public uint AppId { get; init; }

    [CommandOption("--to-branch <BRANCH>")]
    [Description("Target the manifests of an official branch (e.g. 2_31_old, or public to go back to the latest)")]
    public string? ToBranch { get; init; }

    [CommandOption("--manifest|-m <DEPOT=MANIFEST>")]
    [Description("Target a specific manifest for a depot (repeatable), e.g. from SteamDB")]
    public string[]? Manifests { get; init; }

    [CommandOption("--allow-modified")]
    [Description("Proceed even if installed depot files differ from their manifest (modded game files)")]
    public bool AllowModified { get; init; }

    public override ValidationResult Validate() =>
        (ToBranch is null) == (Manifests is null or { Length: 0 })
            ? ValidationResult.Error("Give exactly one of --to-branch or --manifest.")
            : ValidationResult.Success();
}

/// <summary><c>modular depot stage</c>: download a build into a staging folder. Never touches the game.</summary>
public sealed class DepotStageCommand : AsyncCommand<DepotStageCommand.Settings>
{
    public sealed class Settings : DepotTargetSettings
    {
        [CommandOption("--dry-run")]
        [Description("Show the plan and download size, download nothing")]
        public bool DryRun { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        var acf = SteamAppManifest.Find(settings.AppId);
        if (acf is null)
        {
            LiveProgressDisplay.ShowError($"App {settings.AppId} is not installed in any Steam library.");
            return 1;
        }

        try
        {
            await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), cts.Token);
            var client = new SteamDepotClient(session);
            var service = new DepotVersionService();

            var plan = await service.PlanStageAsync(acf.AppId, ParseTarget(settings), client, settings.AllowModified, cts.Token);
            if (plan.Branch is not null)
                AnsiConsole.MarkupLine($"Target: branch [bold]{Markup.Escape(plan.Branch)}[/] (build {plan.BuildId?.ToString() ?? "?"})");
            foreach (var w in plan.Warnings)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(w)}[/]");

            if (plan.Items.Count == 0)
            {
                LiveProgressDisplay.ShowSuccess("Nothing to stage: the install already is the target build.");
                return 0;
            }

            var table = new Table().AddColumns("Depot", "From → To", "Files to stage", "Removed", "Chunks local / download", "Download");
            foreach (var i in plan.Items)
                table.AddRow(i.Depot.DepotId.ToString(), $"{i.Depot.From} ({i.From.CreationTime:yyyy-MM-dd})\n→ {i.Depot.To} ({i.To.CreationTime:yyyy-MM-dd})",
                    $"{i.Plan.Files.Count} ({i.Plan.StageBytes / 1e9:F2} GB){(i.Plan.AlreadyStaged > 0 ? $", {i.Plan.AlreadyStaged} done" : "")}",
                    i.Plan.Diff.Removed.Count.ToString(), $"{i.Plan.LocalChunks} / {i.Plan.DownloadChunks}", $"{i.Plan.DownloadBytes / 1e9:F2} GB");
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]Staging in {Markup.Escape(StagingPaths.AppRoot(acf))} (same filesystem as the game).[/]");

            if (settings.DryRun)
                return 0;

            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn())
                .StartAsync(async ctx =>
                {
                    var tasks = plan.Items.ToDictionary(i => i.Depot.DepotId,
                        i => ctx.AddTask($"Depot {i.Depot.DepotId}", maxValue: Math.Max(1, i.Plan.StageBytes)));
                    await service.StageAsync(plan, session, new Progress<(uint DepotId, long Done, long Total)>(p => tasks[p.DepotId].Value = p.Done), cts.Token);
                    foreach (var t in tasks.Values)
                        t.Value = t.MaxValue;
                });

            LiveProgressDisplay.ShowSuccess($"Staged and verified. Close Steam, then run: modular depot swap {acf.AppId}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            LiveProgressDisplay.ShowWarning("Cancelled. Re-run the same command to resume; verified files are kept.");
            return 1;
        }
        catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException
                                       or DepotAccessException or StagingException or InvalidOperationException or IOException
                                       or DepotOperationException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    private static VersionTarget ParseTarget(DepotTargetSettings settings)
    {
        if (settings.ToBranch is { } branch)
            return VersionTarget.ForBranch(branch);
        var manifests = new Dictionary<uint, ulong>();
        foreach (var spec in settings.Manifests!)
        {
            var parts = spec.Split('=', 2);
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var depot) || !ulong.TryParse(parts[1], out var manifest))
                throw new DepotOperationException($"--manifest wants DEPOT=MANIFEST, got '{spec}'.");
            manifests[depot] = manifest;
        }
        return VersionTarget.ForManifests(manifests);
    }
}

/// <summary><c>modular depot swap</c>: apply a staged build to the game and pin it. Steam must be closed.</summary>
public sealed class DepotSwapCommand : AsyncCommand<DepotSwapCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }

        [CommandOption("--yes|-y")]
        [Description("Don't ask for confirmation")]
        public bool Yes { get; init; }

        [CommandOption("--allow-modified")]
        public bool AllowModified { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        var service = new DepotVersionService();
        SwapPlan plan;
        try
        {
            plan = await service.PrepareSwapAsync(settings.AppId, settings.AllowModified, cts.Token);
        }
        catch (DepotOperationException ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }

        foreach (var w in plan.Items)
            AnsiConsole.MarkupLine($"Depot [bold]{w.Depot.DepotId}[/]: {w.Depot.From} → {w.Depot.To} · replace {w.Plan.Files.Count}, delete {w.Plan.Diff.Removed.Count}");
        if (plan.IsDowngrade)
            LiveProgressDisplay.ShowWarning("This is a downgrade. Saves made on the newer build may not load on the older one; keep a copy if they matter.");
        AnsiConsole.MarkupLine("[grey]Afterwards Modular pins the build: update only on launch" + (plan.Staged.Branch is { } b && b != "public" ? $", branch {Markup.Escape(b)}" : "") + ".[/]");
        if (!settings.Yes && !AnsiConsole.Confirm("Apply to the game folder now?", defaultValue: false))
            return 1;

        try
        {
            await service.ApplySwapAsync(plan, new Progress<string>(m => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(m)}[/]")), cts.Token);
            LiveProgressDisplay.ShowSuccess($"Swapped and pinned. {(plan.Staged.Branch is { } br ? $"Branch {br}. " : "")}Check any time with: modular depot status {plan.Acf.AppId}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StagingException or InvalidOperationException
                                       or InvalidDataException or DepotOperationException)
        {
            LiveProgressDisplay.ShowError($"{ex.Message}\nThe swap journal is kept; run 'depot swap {settings.AppId}' again to resume.");
            return 1;
        }
    }
}

/// <summary><c>modular depot pin</c> / <c>unpin</c>: hold the installed build without swapping, or release it.</summary>
public sealed class DepotPinCommand : AsyncCommand<DepotPinCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }

        [CommandOption("--branch <BRANCH>")]
        [Description("Also select this official branch; its manifests must match what is installed (needs steam login)")]
        public string? Branch { get; init; }

        [CommandOption("--as-current")]
        [Description("Tell Steam the game is on the newest public build, so it neither flags an update nor blocks launching. Modular keeps the true build; unpin restores it (needs steam login). Pair with 'depot lock' so a Steam verify can't overwrite the files")]
        public bool AsCurrent { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        try
        {
            SteamSession? session = null;
            if (settings.Branch is not null || settings.AsCurrent)
                session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), cts.Token);
            await using var _ = session;
            var pin = await new DepotVersionService().PinAsync(settings.AppId, settings.Branch, settings.AsCurrent,
                session is null ? null : new SteamDepotClient(session), cts.Token);
            var name = SteamAppManifest.Find(settings.AppId)?.Name ?? settings.AppId.ToString();
            LiveProgressDisplay.ShowSuccess($"Pinned {name}: {string.Join(", ", pin.Depots.Select(d => $"{d.Key}={d.Value}"))}; update only on launch{(pin.Branch is { } b ? $", branch {b}" : "")}"
                + (pin.ReportsCurrent ? $"; Steam is told it's on current build {pin.ReportedBuildId}." : "."));
            if (pin.ReportsCurrent && !pin.Locked)
                AnsiConsole.MarkupLine($"[grey]Lock it too, so a Steam 'verify files' can't overwrite the build: modular depot lock {settings.AppId}[/]");
            return 0;
        }
        catch (Exception ex) when (ex is DepotOperationException or SteamNotLoggedInException or SteamLogOnException
                                       or SteamDisconnectedException or InvalidOperationException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}

public sealed class DepotUnpinCommand : AsyncCommand<DepotUnpinCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        try
        {
            var kept = await new DepotVersionService().UnpinAsync(settings.AppId, cts.Token);
            var name = SteamAppManifest.Find(settings.AppId)?.Name ?? settings.AppId.ToString();
            LiveProgressDisplay.ShowSuccess($"Unpinned {name}. Steam's own update settings are restored; it will update the game to the latest build.");
            if (kept.Count > 0)
                LiveProgressDisplay.ShowWarning($"Left as they are, because they changed after the pin: {string.Join(", ", kept)}.");
            return 0;
        }
        catch (DepotOperationException ex)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }
}
