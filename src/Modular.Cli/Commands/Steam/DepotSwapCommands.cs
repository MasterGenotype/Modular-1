using System.ComponentModel;
using System.Text.Json;
using Modular.Cli.UI;
using Modular.Core.Depots;
using Modular.Core.Security;
using Modular.Core.Steam;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Steam;

/// <summary>What <c>depot stage</c> prepared, read back by <c>depot swap</c> (no login needed to apply).</summary>
public sealed record StagedSwap(uint AppId, string? Branch, uint? BuildId, List<StagedDepot> Depots, DateTimeOffset StagedAt)
{
    public const string FileName = "stage.json";

    public static string PathFor(SteamAppManifest acf) => Path.Combine(StagingPaths.AppRoot(acf), FileName);

    public static StagedSwap? Load(SteamAppManifest acf) =>
        File.Exists(PathFor(acf)) ? JsonSerializer.Deserialize<StagedSwap>(File.ReadAllText(PathFor(acf))) : null;

    public void Save(SteamAppManifest acf)
    {
        Directory.CreateDirectory(StagingPaths.AppRoot(acf));
        File.WriteAllText(PathFor(acf), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record StagedDepot(uint DepotId, uint OwnerAppId, ulong From, ulong To);

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
            var store = new DepotManifestStore();

            var (targets, branch, buildId) = await ResolveTargetsAsync(client, acf, settings, cts.Token);
            if (branch is not null)
                AnsiConsole.MarkupLine($"Target: branch [bold]{Markup.Escape(branch)}[/] (build {buildId?.ToString() ?? "?"})");

            var staged = new List<StagedDepot>();
            var plans = new List<(StagedDepot Depot, StagePlan Plan, ManifestView From, ManifestView To)>();
            foreach (var (depot, owner, to) in targets)
            {
                var targetView = store.LoadView(depot, to)
                    ?? ManifestView.From(await FetchAndArchive(client, store, owner, depot, to, branch ?? "public", cts.Token));
                var installed = await IdentifyInstalledAsync(acf, store, depot, settings.AllowModified, cts.Token);
                if (installed is null)
                    return 1;
                if (installed.ManifestId == to)
                {
                    AnsiConsole.MarkupLine($"[grey]Depot {depot} is already on {to}.[/]");
                    continue;
                }
                var sd = new StagedDepot(depot, owner, installed.ManifestId, to);
                var plan = DepotStager.Plan(acf.InstallPath, StagingPaths.For(acf, depot, to), installed, targetView);
                staged.Add(sd);
                plans.Add((sd, plan, installed, targetView));
            }

            if (plans.Count == 0)
            {
                LiveProgressDisplay.ShowSuccess("Nothing to stage: the install already is the target build.");
                return 0;
            }

            var table = new Table().AddColumns("Depot", "From → To", "Files to stage", "Removed", "Chunks local / download", "Download");
            foreach (var (d, p, from, to) in plans)
                table.AddRow(d.DepotId.ToString(), $"{d.From} ({from.CreationTime:yyyy-MM-dd})\n→ {d.To} ({to.CreationTime:yyyy-MM-dd})",
                    $"{p.Files.Count} ({p.StageBytes / 1e9:F2} GB){(p.AlreadyStaged > 0 ? $", {p.AlreadyStaged} done" : "")}",
                    p.Diff.Removed.Count.ToString(), $"{p.LocalChunks} / {p.DownloadChunks}", $"{p.DownloadBytes / 1e9:F2} GB");
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]Staging in {Markup.Escape(StagingPaths.AppRoot(acf))} (same filesystem as the game).[/]");

            if (settings.DryRun)
                return 0;

            new StagedSwap(acf.AppId, branch, buildId, staged, DateTimeOffset.UtcNow).Save(acf);
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn())
                .StartAsync(async ctx =>
                {
                    foreach (var (d, p, from, to) in plans)
                    {
                        var task = ctx.AddTask($"Depot {d.DepotId}", maxValue: Math.Max(1, p.StageBytes));
                        using var source = await SteamCdnChunkSource.CreateAsync(session, d.OwnerAppId, d.DepotId, cts.Token);
                        await new DepotStager(source).StageAsync(acf.InstallPath, StagingPaths.For(acf, d.DepotId, d.To), from, to,
                            new Progress<StageProgress>(sp => task.Value = sp.BytesDone), ct: cts.Token);
                        task.Value = task.MaxValue;
                    }
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
                                       or DepotAccessException or StagingException or InvalidOperationException or IOException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    private static async Task<(List<(uint Depot, uint Owner, ulong To)>, string?, uint?)> ResolveTargetsAsync(
        SteamDepotClient client, SteamAppManifest acf, DepotTargetSettings settings, CancellationToken ct)
    {
        var owners = acf.InstalledDepots.ToDictionary(d => d.DepotId, d => d.DlcAppId ?? acf.AppId);
        var targets = new List<(uint, uint, ulong)>();

        if (settings.ToBranch is { } branch)
        {
            uint? buildId = null;
            foreach (var app in owners.Values.Distinct())
            {
                var info = await client.GetAppDepotsAsync(app, branch, ct);
                if (app == acf.AppId)
                {
                    if (!info.Branches.Contains(branch))
                        throw new InvalidOperationException($"App {app} has no branch '{branch}'. Branches: {string.Join(", ", info.Branches)}");
                    buildId = info.BuildId;
                }
                foreach (var (depot, owner) in owners.Where(o => o.Value == app))
                {
                    if (info.Depots.FirstOrDefault(d => d.DepotId == depot)?.ManifestId is { } m)
                        targets.Add((depot, owner, m));
                    else
                        AnsiConsole.MarkupLine($"[yellow]Depot {depot} has no manifest on '{Markup.Escape(branch)}'; leaving it as is.[/]");
                }
            }
            return (targets, branch, buildId);
        }

        foreach (var spec in settings.Manifests!)
        {
            var parts = spec.Split('=', 2);
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var depot) || !ulong.TryParse(parts[1], out var manifest))
                throw new InvalidOperationException($"--manifest wants DEPOT=MANIFEST, got '{spec}'.");
            if (!owners.TryGetValue(depot, out var owner))
                throw new InvalidOperationException($"Depot {depot} is not installed for app {acf.AppId}.");
            targets.Add((depot, owner, manifest));
        }
        return (targets, null, null);
    }

    private static async Task<SteamKit2.DepotManifest> FetchAndArchive(
        SteamDepotClient client, DepotManifestStore store, uint app, uint depot, ulong manifest, string branch, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[grey]Fetching manifest {depot}_{manifest}…[/]");
        var m = await client.DownloadManifestAsync(app, depot, manifest, branch, ct);
        store.Save(m);
        return m;
    }

    internal static async Task<ManifestView?> IdentifyInstalledAsync(
        SteamAppManifest acf, DepotManifestStore store, uint depot, bool allowModified, CancellationToken ct)
    {
        var candidates = store.LoadViews(depot);
        var id = await new BuildIdentifier().IdentifyAsync(acf.InstallPath, depot, candidates, ct: ct);
        var ok = id.Match is DepotMatch.Exact or DepotMatch.SizesOnly || (allowModified && id.Match == DepotMatch.Modified);
        if (!ok || id.ManifestId is null)
        {
            LiveProgressDisplay.ShowError(id.Match == DepotMatch.Modified
                ? $"Depot {depot}: installed files differ from manifest {id.ManifestId} ({id.Missing.Count} missing, {id.SizeMismatch.Count} differ). Check 'depot status', or pass --allow-modified."
                : $"Depot {depot}: can't tell which build is installed ({id.Match}). Run 'depot status {acf.AppId}'.");
            return null;
        }
        return candidates.First(c => c.ManifestId == id.ManifestId);
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
        var acf = SteamAppManifest.Find(settings.AppId);
        var staged = acf is null ? null : StagedSwap.Load(acf);
        if (acf is null || staged is null)
        {
            LiveProgressDisplay.ShowError($"Nothing staged for app {settings.AppId}. Run 'modular depot stage' first.");
            return 1;
        }
        if (SteamProcess.IsRunning())
        {
            LiveProgressDisplay.ShowError("Steam is running. Close Steam completely (Steam → Exit), then run this again. Modular won't close it for you.");
            return 1;
        }

        var store = new DepotManifestStore();
        var work = new List<(StagedDepot Depot, StagePlan Plan, ManifestView From, ManifestView To, string Dir)>();
        foreach (var d in staged.Depots)
        {
            var dir = StagingPaths.For(acf, d.DepotId, d.To);
            var from = store.LoadView(d.DepotId, d.From);
            var to = store.LoadView(d.DepotId, d.To);
            if (from is null || to is null)
            {
                LiveProgressDisplay.ShowError($"Manifest for depot {d.DepotId} is missing from the archive. Run 'depot stage' again.");
                return 1;
            }
            var plan = DepotStager.Plan(acf.InstallPath, dir, from, to);
            if (!DepotSwapper.IsComplete(dir))
            {
                // Resuming an interrupted swap is fine; starting one needs the install to still be the staged "from".
                var started = File.Exists(Path.Combine(dir, DepotSwapper.JournalFile));
                if (!started)
                {
                    var current = await DepotStageCommand.IdentifyInstalledAsync(acf, store, d.DepotId, settings.AllowModified, cts.Token);
                    if (current is null)
                        return 1;
                    if (current.ManifestId != d.From)
                    {
                        LiveProgressDisplay.ShowError($"Depot {d.DepotId} is now {current.ManifestId}, not {d.From} as when staged (did Steam update it?). Run 'depot stage' again.");
                        return 1;
                    }
                    if (plan.AlreadyStaged != plan.Files.Count)
                    {
                        LiveProgressDisplay.ShowError($"Depot {d.DepotId}: {plan.Files.Count - plan.AlreadyStaged} file(s) not staged yet. Run 'depot stage' again.");
                        return 1;
                    }
                }
            }
            work.Add((d, plan, from, to, dir));
        }

        var downgrade = work.Any(w => w.To.CreationTime < w.From.CreationTime);
        foreach (var w in work)
            AnsiConsole.MarkupLine($"Depot [bold]{w.Depot.DepotId}[/]: {w.Depot.From} → {w.Depot.To} · replace {w.Plan.Files.Count}, delete {w.Plan.Diff.Removed.Count}");
        if (downgrade)
            LiveProgressDisplay.ShowWarning("This is a downgrade. Saves made on the newer build may not load on the older one; keep a copy if they matter.");
        AnsiConsole.MarkupLine("[grey]Afterwards Modular pins the build: update only on launch" + (staged.Branch is { } b && b != "public" ? $", branch {Markup.Escape(b)}" : "") + ".[/]");
        if (!settings.Yes && !AnsiConsole.Confirm("Apply to the game folder now?", defaultValue: false))
            return 1;

        // A hard-locked build has to be unlocked for the swap and is locked again afterwards.
        var previousPin = new BuildPinStore().Load(acf.AppId);
        var wasLocked = previousPin is { Locked: true };
        if (wasLocked && !await PinLock.SetAsync(acf, previousPin!, store, locked: false, cts.Token))
            return 1;

        try
        {
            foreach (var w in work)
            {
                var r = DepotSwapper.Apply(acf.InstallPath, w.Dir, w.Plan, w.From, w.To);
                AnsiConsole.MarkupLine($"[grey]Depot {w.Depot.DepotId}: moved {r.FilesMoved}, deleted {r.FilesDeleted}, pruned {r.DirectoriesPruned} empty dir(s).[/]");
            }

            // Post-check: the files now on disk must identify as the target (hashes taken where builds differ).
            var failed = false;
            foreach (var w in work)
            {
                var id = await new BuildIdentifier().IdentifyAsync(acf.InstallPath, w.Depot.DepotId, new[] { w.From, w.To }, ct: cts.Token);
                if (id.ManifestId != w.Depot.To || id.Match is not (DepotMatch.Exact or DepotMatch.SizesOnly))
                {
                    LiveProgressDisplay.ShowError($"Post-check failed for depot {w.Depot.DepotId}: {id.Match}, on disk {id.ManifestId}. Staging is kept; the acf was not changed.");
                    failed = true;
                }
            }
            if (failed)
                return 1;

            // Pin: true manifests into the acf, update-on-launch, branch when there is one.
            var pins = new BuildPinStore();
            var depots = new Dictionary<uint, (ulong, ulong)>();
            foreach (var d in acf.InstalledDepots)
            {
                var w = work.FirstOrDefault(x => x.Depot.DepotId == d.DepotId);
                if (w.To is not null)
                    depots[d.DepotId] = (w.Depot.To, w.To.TotalSize);
                else if (d.ManifestId is { } m)
                    depots[d.DepotId] = (m, d.Size ?? 0);
            }
            var pin = BuildPinner.Apply(SteamAcfEditor.Load(acf.AcfPath), acf.AppId, depots, staged.Branch, staged.BuildId, pins.Load(acf.AppId));
            pins.Save(pin);
            if (wasLocked)
            {
                if (await PinLock.SetAsync(acf, pin, store, locked: true, cts.Token))
                    pins.Save(pin with { Locked = true });
                else
                    LiveProgressDisplay.ShowWarning("The new build is pinned but not locked. Run 'depot lock' to lock it.");
            }

            Directory.Delete(StagingPaths.AppRoot(acf), recursive: true);
            var stagingRoot = Path.GetDirectoryName(StagingPaths.AppRoot(acf))!;
            if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                Directory.Delete(stagingRoot);
            LiveProgressDisplay.ShowSuccess($"Swapped and pinned. {(staged.Branch is { } br ? $"Branch {br}. " : "")}Check any time with: modular depot status {acf.AppId}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StagingException or InvalidOperationException or InvalidDataException)
        {
            LiveProgressDisplay.ShowError($"{ex.Message}\nThe swap journal is kept; run 'depot swap {acf.AppId}' again to resume.");
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
        var acf = SteamAppManifest.Find(settings.AppId);
        if (acf is null)
        {
            LiveProgressDisplay.ShowError($"App {settings.AppId} is not installed in any Steam library.");
            return 1;
        }
        if (SteamProcess.IsRunning())
        {
            LiveProgressDisplay.ShowError("Steam is running. Close Steam completely, then run this again.");
            return 1;
        }

        var store = new DepotManifestStore();
        var depots = new Dictionary<uint, (ulong, ulong)>();
        foreach (var d in acf.InstalledDepots)
        {
            var view = await DepotStageCommand.IdentifyInstalledAsync(acf, store, d.DepotId, allowModified: true, cts.Token);
            if (view is null)
                return 1;
            depots[d.DepotId] = (view.ManifestId, view.TotalSize);
        }

        if (settings.AsCurrent && settings.Branch is not null)
        {
            LiveProgressDisplay.ShowError("--as-current and --branch don't mix: a branch pin already stops Steam updating.");
            return 1;
        }

        uint? buildId = null;
        Dictionary<uint, (ulong, ulong)>? reportAs = null;
        uint? reportBuildId = null;
        if (settings.AsCurrent)
        {
            try
            {
                await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), cts.Token);
                var client = new SteamDepotClient(session);
                reportAs = new();
                foreach (var app in acf.InstalledDepots.Select(d => d.DlcAppId ?? acf.AppId).Distinct())
                {
                    var info = await client.GetAppDepotsAsync(app, "public", cts.Token);
                    if (app == acf.AppId)
                        reportBuildId = info.BuildId;
                    foreach (var d in info.Depots.Where(x => depots.ContainsKey(x.DepotId) && x.ManifestId is not null))
                        reportAs[d.DepotId] = (d.ManifestId!.Value, d.Size ?? depots[d.DepotId].Item2);
                }
            }
            catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException or InvalidOperationException)
            {
                LiveProgressDisplay.ShowError(ex.Message);
                return 1;
            }
            if (reportBuildId is null || reportAs.Count == 0)
            {
                LiveProgressDisplay.ShowError("Steam didn't return the public build's manifests for this game.");
                return 1;
            }
        }
        if (settings.Branch is { } branch)
        {
            try
            {
                await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), cts.Token);
                var client = new SteamDepotClient(session);
                foreach (var app in acf.InstalledDepots.Select(d => d.DlcAppId ?? acf.AppId).Distinct())
                {
                    var info = await client.GetAppDepotsAsync(app, branch, cts.Token);
                    if (app == acf.AppId)
                        buildId = info.BuildId;
                    foreach (var d in info.Depots.Where(x => depots.ContainsKey(x.DepotId)))
                    {
                        if (d.ManifestId != depots[d.DepotId].Item1)
                        {
                            LiveProgressDisplay.ShowError($"Branch '{branch}' has {d.ManifestId} for depot {d.DepotId}, but {depots[d.DepotId].Item1} is installed. Swap to the branch instead: depot stage {acf.AppId} --to-branch {branch}");
                            return 1;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException or InvalidOperationException)
            {
                LiveProgressDisplay.ShowError(ex.Message);
                return 1;
            }
        }

        var pins = new BuildPinStore();
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(acf.AcfPath), acf.AppId, depots, settings.Branch, buildId, pins.Load(acf.AppId),
            reportAs: reportAs, reportBuildId: reportBuildId);
        pins.Save(pin);
        LiveProgressDisplay.ShowSuccess($"Pinned {acf.Name}: {string.Join(", ", depots.Select(d => $"{d.Key}={d.Value.Item1}"))}; update only on launch{(settings.Branch is { } b ? $", branch {b}" : "")}"
            + (pin.ReportsCurrent ? $"; Steam is told it's on current build {pin.ReportedBuildId}." : "."));
        if (pin.ReportsCurrent && !pin.Locked)
            AnsiConsole.MarkupLine($"[grey]Lock it too, so a Steam 'verify files' can't overwrite the build: modular depot lock {acf.AppId}[/]");
        return 0;
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
        var acf = SteamAppManifest.Find(settings.AppId);
        var pins = new BuildPinStore();
        var pin = pins.Load(settings.AppId);
        if (acf is null || pin is null)
        {
            LiveProgressDisplay.ShowWarning($"App {settings.AppId} is not pinned.");
            return 1;
        }
        if (SteamProcess.IsRunning())
        {
            LiveProgressDisplay.ShowError("Steam is running. Close Steam completely, then run this again.");
            return 1;
        }
        if (pin.Locked && !await PinLock.SetAsync(acf, pin, new DepotManifestStore(), locked: false, cts.Token))
            return 1;
        var kept = BuildPinner.Remove(SteamAcfEditor.Load(acf.AcfPath), pin);
        pins.Delete(settings.AppId);
        LiveProgressDisplay.ShowSuccess($"Unpinned {acf.Name}. Steam's own update settings are restored; it will update the game to the latest build.");
        if (kept.Count > 0)
            LiveProgressDisplay.ShowWarning($"Left as they are, because they changed after the pin: {string.Join(", ", kept)}.");
        return 0;
    }
}
