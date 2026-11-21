using System.Text.Json;
using Modular.Core.GameDetection;
using Modular.Core.Steam;

namespace Modular.Core.Depots;

/// <summary>A user-facing failure of a depot operation (message is shown as is).</summary>
public sealed class DepotOperationException(string message) : Exception(message);

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

/// <summary>A build to move to: an official branch, or specific manifests (e.g. an archived build).</summary>
public sealed record VersionTarget(string? Branch, IReadOnlyDictionary<uint, ulong>? Manifests)
{
    public static VersionTarget ForBranch(string branch) => new(branch, null);
    public static VersionTarget ForManifests(IReadOnlyDictionary<uint, ulong> manifests) => new(null, manifests);
}

/// <summary>One depot's share of a staging plan.</summary>
public sealed record StageItem(StagedDepot Depot, StagePlan Plan, ManifestView From, ManifestView To);

/// <summary>What staging a target will do; <see cref="Items"/> is empty when the install already is the target.</summary>
public sealed record StagePlanResult(uint AppId, string? Branch, uint? BuildId, IReadOnlyList<StageItem> Items, IReadOnlyList<string> Warnings)
{
    public ulong DownloadBytes => Items.Aggregate(0UL, (sum, i) => sum + (ulong)i.Plan.DownloadBytes);
}

/// <summary>One depot's share of a swap.</summary>
public sealed record SwapItem(StagedDepot Depot, StagePlan Plan, ManifestView From, ManifestView To, string Dir);

/// <summary>A staged swap, checked and ready to apply.</summary>
public sealed record SwapPlan(SteamAppManifest Acf, StagedSwap Staged, IReadOnlyList<SwapItem> Items)
{
    public bool IsDowngrade => Items.Any(w => w.To.CreationTime < w.From.CreationTime);
}

/// <summary>A depot as found on disk.</summary>
public sealed record DepotState(uint DepotId, ulong? ManifestId, DepotMatch Match, DateTime? BuildDate, uint? DlcAppId);

/// <summary>A Steam game's installed build, pin and lock, as the version browser shows them.</summary>
public sealed record GameBuildStatus(
    SteamAppManifest Acf, IReadOnlyList<DepotState> Depots, BuildPin? Pin, IReadOnlyList<string> Drift,
    LockState? Lock, bool Superseded, bool StagedSwapWaiting);

public enum GameVersionKind { Branch, ArchivedBuild }

/// <summary>A version the version browser lists.</summary>
public sealed record GameVersion(
    GameVersionKind Kind, string Label, string? Branch, uint? BuildId, DateTimeOffset? Date,
    IReadOnlyDictionary<uint, ulong> Manifests, bool IsInstalled, bool PasswordRequired, string? Description)
{
    public VersionTarget Target => Kind == GameVersionKind.Branch ? VersionTarget.ForBranch(Branch!) : VersionTarget.ForManifests(Manifests);
}

/// <summary>
/// Everything the depot tools do, for the CLI and the GUI: identify the installed build, list
/// the versions Steam serves and Modular has archived, download (stage) a version, swap it in,
/// pin it against Steam updates, and hard-lock its files. Writes to Steam's files need Steam
/// closed (design D3); Modular never closes Steam.
/// </summary>
public sealed class DepotVersionService
{
    private readonly Func<bool> _steamRunning;

    private readonly string _stagingRoot;

    public DepotVersionService(DepotManifestStore? store = null, BuildPinStore? pins = null, Func<bool>? steamRunning = null,
        string? modStagingRoot = null)
    {
        Store = store ?? new DepotManifestStore();
        Pins = pins ?? new BuildPinStore();
        _steamRunning = steamRunning ?? SteamProcess.IsRunning;
        _stagingRoot = modStagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "staging");
    }

    /// <summary>
    /// Game-folder paths Modular deployed for a mod (from the game's deployment record). They belong
    /// to the mod: identifying, switching and locking a build leave them alone.
    /// </summary>
    public IReadOnlySet<string> ModOwnedPaths(uint appId)
    {
        var dir = Path.Combine(_stagingRoot, appId.ToString());
        if (!File.Exists(Path.Combine(dir, "deployment.json")))
            return new HashSet<string>();
        try
        {
            return Modular.Core.Deployment.DeploymentManifest.Load(dir, appId.ToString()).Files
                .Where(f => f.Root == "game")
                .Select(f => f.Path)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new HashSet<string>();
        }
    }

    /// <summary>Where a switch keeps the build's own copies of files a mod deployed over.</summary>
    public string BuildFilesDirectory(uint appId, ulong manifest) =>
        Path.Combine(_stagingRoot, appId.ToString(), "_build-files", manifest.ToString());

    public DepotManifestStore Store { get; }
    public BuildPinStore Pins { get; }
    public bool SteamRunning => _steamRunning();

    private static SteamAppManifest FindAcf(uint appId) =>
        SteamAppManifest.Find(appId) ?? throw new DepotOperationException($"App {appId} is not installed in any Steam library.");

    private void RequireSteamClosed()
    {
        if (_steamRunning())
            throw new DepotOperationException("Steam is running. Close Steam completely (Steam → Exit) first; Modular won't close it for you.");
    }

    /// <summary>Copies whatever manifests Steam has cached before it deletes them on the next update.</summary>
    public int ArchiveDepotcache()
    {
        if (new SteamLocator().FindSteamRoot() is not { } root)
            return 0;
        return Store.ImportFromDepotcache(Path.Combine(root, "depotcache")).Imported.Count;
    }

    // ---- status ---------------------------------------------------------------------------

    public async Task<GameBuildStatus> GetStatusAsync(uint appId, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        ArchiveDepotcache();
        var identifier = new BuildIdentifier();
        var modOwned = ModOwnedPaths(appId);
        var depots = new List<DepotState>();
        foreach (var d in acf.InstalledDepots)
        {
            var id = await identifier.IdentifyAsync(acf.InstallPath, d.DepotId, Store.LoadViews(d.DepotId), ct: ct, ignore: modOwned);
            var date = id.ManifestId is { } m ? Store.LoadView(d.DepotId, m)?.CreationTime : null;
            depots.Add(new DepotState(d.DepotId, id.ManifestId, id.Match, date, d.DlcAppId));
        }

        var pin = Pins.Load(appId);
        var drift = new List<string>();
        LockState? lockState = null;
        var superseded = false;
        if (pin is not null)
        {
            drift.AddRange(BuildPinner.CheckDrift(acf, SteamAcfEditor.Load(acf.AcfPath), pin));
            foreach (var (depot, manifest) in pin.Depots)
            {
                var onDisk = depots.FirstOrDefault(x => x.DepotId == depot);
                if (onDisk?.ManifestId is { } m && m != manifest)
                    drift.Add($"Depot {depot} on disk is {m}, pinned {manifest}.");
            }
            superseded = BuildPinner.IsSuperseded(acf, pin);
            if (LockPaths(pin) is { } paths)
            {
                lockState = BuildLock.GetState(acf.InstallPath, paths);
                if (pin.Locked && !lockState.FullyLocked)
                    drift.Add($"Hard lock incomplete: {lockState.Unlocked} of {lockState.Locked + lockState.Unlocked} file(s) not locked.");
            }
        }
        return new GameBuildStatus(acf, depots, pin, drift, lockState, superseded, StagedSwap.Load(acf) is not null);
    }

    /// <summary>
    /// The versions to offer: Steam's branches (needs a logged-in client) and the builds Modular has
    /// archived manifests for (older builds of the game's largest depot, for non-branch downgrades).
    /// </summary>
    public async Task<IReadOnlyList<GameVersion>> ListVersionsAsync(
        GameBuildStatus status, SteamDepotClient? client, CancellationToken ct = default)
    {
        var acf = status.Acf;
        var installed = status.Depots.Where(d => d.ManifestId is not null).ToDictionary(d => d.DepotId, d => d.ManifestId!.Value);
        bool Matches(IReadOnlyDictionary<uint, ulong> manifests) =>
            installed.Count > 0 && installed.All(i => !manifests.TryGetValue(i.Key, out var m) || m == i.Value)
            && manifests.Keys.Any(installed.ContainsKey);

        var versions = new List<GameVersion>();
        var branchManifests = new HashSet<(uint, ulong)>();
        if (client is not null)
        {
            foreach (var b in (await client.GetBranchesAsync(acf.AppId, ct))
                         .OrderBy(b => b.Name == "public" ? 0 : 1).ThenByDescending(b => b.TimeUpdated))
            {
                var relevant = b.Manifests.Where(m => installed.ContainsKey(m.Key)).ToDictionary(m => m.Key, m => m.Value);
                foreach (var m in relevant)
                    branchManifests.Add((m.Key, m.Value));
                versions.Add(new GameVersion(GameVersionKind.Branch, b.Name == "public" ? "Latest (public)" : b.Name,
                    b.Name, b.BuildId, b.TimeUpdated, relevant, Matches(relevant), b.PasswordRequired, b.Description));
            }
        }

        // Archived builds of the largest depot: what a non-branch downgrade targets.
        var main = acf.InstalledDepots
            .Select(d => (d.DepotId, Size: Store.LoadViews(d.DepotId).Select(v => v.TotalSize).DefaultIfEmpty(0UL).Max()))
            .OrderByDescending(x => x.Size).FirstOrDefault();
        if (main.DepotId != 0)
        {
            foreach (var view in Store.LoadViews(main.DepotId).OrderByDescending(v => v.CreationTime))
            {
                if (branchManifests.Contains((main.DepotId, view.ManifestId)))
                    continue;
                var manifests = new Dictionary<uint, ulong> { [main.DepotId] = view.ManifestId };
                versions.Add(new GameVersion(GameVersionKind.ArchivedBuild, $"Build of {view.CreationTime:yyyy-MM-dd}", null, null,
                    new DateTimeOffset(DateTime.SpecifyKind(view.CreationTime, DateTimeKind.Utc)), manifests,
                    installed.TryGetValue(main.DepotId, out var cur) && cur == view.ManifestId, false,
                    $"depot {main.DepotId} manifest {view.ManifestId}"));
            }
        }
        return versions;
    }

    // ---- stage ----------------------------------------------------------------------------

    /// <summary>Works out what to download for a target; fetches and archives manifests as needed.</summary>
    public async Task<StagePlanResult> PlanStageAsync(
        uint appId, VersionTarget target, SteamDepotClient client, bool allowModified = false, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        var owners = acf.InstalledDepots.ToDictionary(d => d.DepotId, d => d.DlcAppId ?? acf.AppId);
        var warnings = new List<string>();
        var targets = new List<(uint Depot, uint Owner, ulong To)>();
        string? branch = target.Branch;
        uint? buildId = null;

        if (branch is not null)
        {
            foreach (var app in owners.Values.Distinct())
            {
                var info = await client.GetAppDepotsAsync(app, branch, ct);
                if (app == acf.AppId)
                {
                    if (!info.Branches.Contains(branch))
                        throw new DepotOperationException($"App {app} has no branch '{branch}'. Branches: {string.Join(", ", info.Branches)}");
                    buildId = info.BuildId;
                }
                foreach (var (depot, owner) in owners.Where(o => o.Value == app))
                {
                    if (info.Depots.FirstOrDefault(d => d.DepotId == depot)?.ManifestId is { } m)
                        targets.Add((depot, owner, m));
                    else
                        warnings.Add($"Depot {depot} has no manifest on '{branch}'; leaving it as is.");
                }
            }
        }
        else
        {
            foreach (var (depot, manifest) in target.Manifests ?? new Dictionary<uint, ulong>())
            {
                if (!owners.TryGetValue(depot, out var owner))
                    throw new DepotOperationException($"Depot {depot} is not installed for app {acf.AppId}.");
                targets.Add((depot, owner, manifest));
            }
        }

        var items = new List<StageItem>();
        foreach (var (depot, owner, to) in targets)
        {
            var targetView = Store.LoadView(depot, to);
            if (targetView is null)
            {
                var m = await client.DownloadManifestAsync(owner, depot, to, branch ?? "public", ct);
                Store.Save(m);
                targetView = ManifestView.From(m);
            }
            var installed = await IdentifyInstalledAsync(acf, depot, allowModified, ct);
            if (installed.ManifestId == to)
                continue;
            var sd = new StagedDepot(depot, owner, installed.ManifestId, to);
            items.Add(new StageItem(sd, DepotStager.Plan(acf.InstallPath, StagingPaths.For(acf, depot, to), installed, targetView), installed, targetView));
        }
        return new StagePlanResult(appId, branch, buildId, items, warnings);
    }

    /// <summary>Downloads a plan into staging (resumable; never touches the game).</summary>
    /// <param name="progress">Per depot: bytes staged so far and the total.</param>
    public async Task StageAsync(
        StagePlanResult plan, SteamSession session, IProgress<(uint DepotId, long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        if (plan.Items.Count == 0)
            return;
        var acf = FindAcf(plan.AppId);
        new StagedSwap(acf.AppId, plan.Branch, plan.BuildId, plan.Items.Select(i => i.Depot).ToList(), DateTimeOffset.UtcNow).Save(acf);
        foreach (var item in plan.Items)
        {
            using var source = await SteamCdnChunkSource.CreateAsync(session, item.Depot.OwnerAppId, item.Depot.DepotId, ct);
            var total = (long)Math.Max(1UL, (ulong)item.Plan.StageBytes);
            await new DepotStager(source).StageAsync(acf.InstallPath, StagingPaths.For(acf, item.Depot.DepotId, item.Depot.To), item.From, item.To,
                new Progress<StageProgress>(sp => progress?.Report((item.Depot.DepotId, sp.BytesDone, total))), ct: ct);
            progress?.Report((item.Depot.DepotId, total, total));
        }
    }

    /// <summary>The installed build of one depot, or a clear error when it can't be told.</summary>
    public async Task<ManifestView> IdentifyInstalledAsync(SteamAppManifest acf, uint depot, bool allowModified, CancellationToken ct)
    {
        var candidates = Store.LoadViews(depot);
        var id = await new BuildIdentifier().IdentifyAsync(acf.InstallPath, depot, candidates, ct: ct, ignore: ModOwnedPaths(acf.AppId));
        var ok = id.Match is DepotMatch.Exact or DepotMatch.SizesOnly || (allowModified && id.Match == DepotMatch.Modified);
        if (!ok || id.ManifestId is null)
        {
            throw new DepotOperationException(id.Match == DepotMatch.Modified
                ? $"Depot {depot}: installed files differ from manifest {id.ManifestId} ({id.Missing.Count} missing, {id.SizeMismatch.Count} differ). Check 'depot status', or allow modified files."
                : $"Depot {depot}: can't tell which build is installed ({id.Match}). Run 'depot status {acf.AppId}'.");
        }
        return candidates.First(c => c.ManifestId == id.ManifestId);
    }

    // ---- swap -----------------------------------------------------------------------------

    /// <summary>Checks a staged swap can be applied (Steam closed, install unchanged, all files staged).</summary>
    public async Task<SwapPlan> PrepareSwapAsync(uint appId, bool allowModified = false, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        var staged = StagedSwap.Load(acf) ?? throw new DepotOperationException($"Nothing staged for app {appId}. Stage a version first.");
        RequireSteamClosed();
        var items = new List<SwapItem>();
        foreach (var d in staged.Depots)
        {
            var dir = StagingPaths.For(acf, d.DepotId, d.To);
            var from = Store.LoadView(d.DepotId, d.From);
            var to = Store.LoadView(d.DepotId, d.To);
            if (from is null || to is null)
                throw new DepotOperationException($"Manifest for depot {d.DepotId} is missing from the archive. Stage again.");
            var plan = DepotStager.Plan(acf.InstallPath, dir, from, to);
            // Resuming an interrupted swap is fine; starting one needs the install to still be the staged "from".
            if (!DepotSwapper.IsComplete(dir) && !File.Exists(Path.Combine(dir, DepotSwapper.JournalFile)))
            {
                var current = await IdentifyInstalledAsync(acf, d.DepotId, allowModified, ct);
                if (current.ManifestId != d.From)
                    throw new DepotOperationException($"Depot {d.DepotId} is now {current.ManifestId}, not {d.From} as when staged (did Steam update it?). Stage again.");
                if (plan.AlreadyStaged != plan.Files.Count)
                    throw new DepotOperationException($"Depot {d.DepotId}: {plan.Files.Count - plan.AlreadyStaged} file(s) not staged yet. Stage again.");
            }
            items.Add(new SwapItem(d, plan, from, to, dir));
        }
        return new SwapPlan(acf, staged, items);
    }

    /// <summary>
    /// Applies a swap: unlocks a locked build, moves the staged files in, verifies them, pins the
    /// new build (true manifests, update on launch, branch when there is one), re-locks if it was
    /// locked, and removes the staging folder. Returns the new pin.
    /// </summary>
    public async Task<BuildPin> ApplySwapAsync(SwapPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireSteamClosed();
        var acf = plan.Acf;
        var previous = Pins.Load(acf.AppId);
        var wasLocked = previous is { Locked: true };
        if (wasLocked)
            await SetFilesLockedAsync(acf, previous!, locked: false, ct);

        var modOwned = ModOwnedPaths(acf.AppId);
        foreach (var w in plan.Items)
        {
            var keep = BuildFilesDirectory(acf.AppId, w.Depot.To);
            var r = DepotSwapper.Apply(acf.InstallPath, w.Dir, w.Plan, w.From, w.To, modOwned, keep);
            progress?.Report($"Depot {w.Depot.DepotId}: moved {r.FilesMoved}, deleted {r.FilesDeleted}, pruned {r.DirectoriesPruned} empty dir(s)."
                + (r.ModFilesKept > 0 ? $" Kept {r.ModFilesKept} file(s) a mod replaces; the build's own copies are in {keep}." : ""));
        }

        // Post-check: the files now on disk must identify as the target.
        foreach (var w in plan.Items)
        {
            var id = await new BuildIdentifier().IdentifyAsync(acf.InstallPath, w.Depot.DepotId, new[] { w.From, w.To }, ct: ct, ignore: modOwned);
            if (id.ManifestId != w.Depot.To || id.Match is not (DepotMatch.Exact or DepotMatch.SizesOnly))
                throw new DepotOperationException($"Post-check failed for depot {w.Depot.DepotId}: {id.Match}, on disk {id.ManifestId}. Staging is kept; Steam's files were not changed.");
        }

        var depots = new Dictionary<uint, (ulong, ulong)>();
        foreach (var d in acf.InstalledDepots)
        {
            var w = plan.Items.FirstOrDefault(x => x.Depot.DepotId == d.DepotId);
            if (w is not null)
                depots[d.DepotId] = (w.Depot.To, w.To.TotalSize);
            else if (d.ManifestId is { } m)
                depots[d.DepotId] = (m, d.Size ?? 0);
        }
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(acf.AcfPath), acf.AppId, depots, plan.Staged.Branch, plan.Staged.BuildId, previous, _steamRunning);
        Pins.Save(pin);
        if (wasLocked)
        {
            await SetFilesLockedAsync(acf, pin, locked: true, ct);
            pin = pin with { Locked = true };
            Pins.Save(pin);
        }

        Directory.Delete(StagingPaths.AppRoot(acf), recursive: true);
        var stagingRoot = Path.GetDirectoryName(StagingPaths.AppRoot(acf))!;
        if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            Directory.Delete(stagingRoot);
        return pin;
    }

    /// <summary>Drops a staged download (frees its disk space).</summary>
    public void DiscardStaged(uint appId)
    {
        var acf = FindAcf(appId);
        if (Directory.Exists(StagingPaths.AppRoot(acf)))
            Directory.Delete(StagingPaths.AppRoot(acf), recursive: true);
    }

    // ---- pin / unpin ----------------------------------------------------------------------

    /// <summary>
    /// Pins the installed build. <paramref name="branch"/> also selects that official branch (its
    /// manifests must match); <paramref name="asCurrent"/> tells Steam the newest public build is
    /// installed so it neither flags an update nor blocks launching. Both need a client.
    /// </summary>
    public async Task<BuildPin> PinAsync(uint appId, string? branch, bool asCurrent, SteamDepotClient? client, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        RequireSteamClosed();
        if (asCurrent && branch is not null)
            throw new DepotOperationException("Pinning to a branch already stops Steam updating; 'tell Steam it's current' is for non-branch builds.");
        if ((asCurrent || branch is not null) && client is null)
            throw new DepotOperationException("This needs Steam login ('modular steam login').");

        var depots = new Dictionary<uint, (ulong, ulong)>();
        foreach (var d in acf.InstalledDepots)
        {
            var view = await IdentifyInstalledAsync(acf, d.DepotId, allowModified: true, ct);
            depots[d.DepotId] = (view.ManifestId, view.TotalSize);
        }

        uint? buildId = null;
        Dictionary<uint, (ulong, ulong)>? reportAs = null;
        uint? reportBuildId = null;
        if (asCurrent)
        {
            reportAs = new();
            foreach (var app in acf.InstalledDepots.Select(d => d.DlcAppId ?? acf.AppId).Distinct())
            {
                var info = await client!.GetAppDepotsAsync(app, "public", ct);
                if (app == acf.AppId)
                    reportBuildId = info.BuildId;
                foreach (var d in info.Depots.Where(x => depots.ContainsKey(x.DepotId) && x.ManifestId is not null))
                    reportAs[d.DepotId] = (d.ManifestId!.Value, d.Size ?? depots[d.DepotId].Item2);
            }
            if (reportBuildId is null || reportAs.Count == 0)
                throw new DepotOperationException("Steam didn't return the public build's manifests for this game.");
        }
        if (branch is not null)
        {
            foreach (var app in acf.InstalledDepots.Select(d => d.DlcAppId ?? acf.AppId).Distinct())
            {
                var info = await client!.GetAppDepotsAsync(app, branch, ct);
                if (app == acf.AppId)
                    buildId = info.BuildId;
                foreach (var d in info.Depots.Where(x => depots.ContainsKey(x.DepotId)))
                {
                    if (d.ManifestId != depots[d.DepotId].Item1)
                        throw new DepotOperationException($"Branch '{branch}' has {d.ManifestId} for depot {d.DepotId}, but {depots[d.DepotId].Item1} is installed. Switch to the branch instead.");
                }
            }
        }

        var previous = Pins.Load(acf.AppId);
        var pin = BuildPinner.Apply(SteamAcfEditor.Load(acf.AcfPath), acf.AppId, depots, branch, buildId, previous, _steamRunning,
            reportAs: reportAs, reportBuildId: reportBuildId);
        if (previous is { Locked: true })
            pin = pin with { Locked = true };
        Pins.Save(pin);
        return pin;
    }

    /// <summary>What Steam serves as the latest public build of an app, compared with what it has installed.</summary>
    public sealed record LatestInfo(uint AppId, uint? LatestBuildId, DateTimeOffset? LatestDate, bool NewerThanInstalled, bool ClaimIsStale);

    /// <summary>
    /// Asks Steam for an app's latest public build. <c>NewerThanInstalled</c>: the files on disk are
    /// an older build than Steam's latest. <c>ClaimIsStale</c>: a "let Steam launch it" pin claims a
    /// build that is no longer the latest, so Steam will flag an update and refuse to launch.
    /// </summary>
    public async Task<LatestInfo> CheckLatestAsync(uint appId, SteamDepotClient client, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        var latest = new Dictionary<uint, ulong>();
        uint? latestBuild = null;
        DateTimeOffset? latestDate = null;
        foreach (var app in acf.InstalledDepots.Select(d => d.DlcAppId ?? acf.AppId).Distinct())
        {
            var info = await client.GetAppDepotsAsync(app, "public", ct);
            if (app == acf.AppId)
            {
                latestBuild = info.BuildId;
                latestDate = info.TimeUpdated;
            }
            foreach (var d in info.Depots.Where(x => x.ManifestId is not null))
                latest.TryAdd(d.DepotId, d.ManifestId!.Value);
        }
        var installed = Pins.Load(appId)?.Depots
            ?? acf.InstalledDepots.Where(d => d.ManifestId is not null).ToDictionary(d => d.DepotId, d => d.ManifestId!.Value);
        var newer = installed.Any(i => latest.TryGetValue(i.Key, out var m) && m != i.Value);
        var pin = Pins.Load(appId);
        var stale = pin is { ReportsCurrent: true } && pin.ReportedDepots!.Any(r => latest.TryGetValue(r.Key, out var m) && m != r.Value);
        return new LatestInfo(appId, latestBuild, latestDate, newer, stale);
    }

    /// <summary>
    /// Re-claims the latest build for every "let Steam launch it" pin whose claim went stale (a new
    /// patch shipped), so Play keeps working. Needs Steam closed; does nothing while it runs.
    /// Returns the apps renewed.
    /// </summary>
    public async Task<IReadOnlyList<uint>> RenewAsync(SteamDepotClient client, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var renewed = new List<uint>();
        if (_steamRunning())
            return renewed;
        foreach (var pin in Pins.List().Where(p => p.ReportsCurrent))
        {
            try
            {
                if (!(await CheckLatestAsync(pin.AppId, client, ct)).ClaimIsStale)
                    continue;
                await PinAsync(pin.AppId, null, asCurrent: true, client, ct);
                renewed.Add(pin.AppId);
                progress?.Report($"Renewed {SteamAppManifest.Find(pin.AppId)?.Name ?? pin.AppId.ToString()}: Steam sees it as up to date again.");
            }
            catch (Exception ex) when (ex is DepotOperationException or InvalidOperationException or IOException)
            {
                progress?.Report($"Couldn't renew app {pin.AppId}: {ex.Message}");
            }
        }
        return renewed;
    }

    /// <summary>Releases a pin (unlocking first), restoring Steam's own update settings. Returns the acf keys left as Steam set them.</summary>
    public async Task<IReadOnlyList<string>> UnpinAsync(uint appId, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        var pin = Pins.Load(appId) ?? throw new DepotOperationException($"App {appId} is not pinned.");
        RequireSteamClosed();
        if (pin.Locked)
            await SetFilesLockedAsync(acf, pin, locked: false, ct);
        var kept = BuildPinner.Remove(SteamAcfEditor.Load(acf.AcfPath), pin, _steamRunning);
        Pins.Delete(appId);
        return kept;
    }

    // ---- lock -----------------------------------------------------------------------------

    /// <summary>Every file of the pinned build, relative to the install folder; null if a manifest is missing.</summary>
    /// <remarks>Files a mod deployed over are left out: locking them would also lock the mod's stored copy
    /// (a hardlink shares the file), and Modular could no longer update or remove that mod.</remarks>
    public IReadOnlyList<string>? LockPaths(BuildPin pin)
    {
        var modOwned = ModOwnedPaths(pin.AppId);
        var paths = new List<string>();
        foreach (var (depot, manifest) in pin.Depots)
        {
            var view = Store.LoadView(depot, manifest);
            if (view is null)
                return null;
            paths.AddRange(view.Files.Select(f => f.Path).Where(p => !modOwned.Contains(p)));
        }
        return paths;
    }

    /// <summary>Hard-locks (or unlocks) a pinned build's files. Steam may be running.</summary>
    public async Task<LockResult> SetLockAsync(uint appId, bool locked, CancellationToken ct = default)
    {
        var acf = FindAcf(appId);
        var pin = Pins.Load(appId) ?? throw new DepotOperationException("Pin the build first: only a pinned build can be locked.");
        var result = await SetFilesLockedAsync(acf, pin, locked, ct);
        Pins.Save(pin with { Locked = locked });
        return result;
    }

    private async Task<LockResult> SetFilesLockedAsync(SteamAppManifest acf, BuildPin pin, bool locked, CancellationToken ct)
    {
        var paths = LockPaths(pin) ?? throw new DepotOperationException("A pinned manifest is missing from Modular's archive; can't list the build's files.");
        try
        {
            return await BuildLock.SetAsync(acf.InstallPath, paths, locked, ct);
        }
        catch (BuildLockUnavailableException ex)
        {
            throw new DepotOperationException(ex.Message);
        }
    }
}
