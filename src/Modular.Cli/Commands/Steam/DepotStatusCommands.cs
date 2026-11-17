using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.Depots;
using Modular.Core.GameDetection;
using Modular.Core.Security;
using Modular.Core.Steam;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Modular.Cli.Commands.Steam;

/// <summary>
/// <c>modular depot status &lt;appid&gt;</c>: which build is really installed, checked against archived
/// manifests rather than the acf. Read-only: it never writes to the game folder or to Steam's files.
/// </summary>
public sealed class DepotStatusCommand : AsyncCommand<DepotStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }

        [CommandOption("--verify")]
        [Description("Hash every file of the identified manifests (reads the whole install)")]
        public bool Verify { get; init; }

        [CommandOption("--online")]
        [Description("Also compare with the manifests Steam serves now (needs steam login)")]
        public bool Online { get; init; }

        [CommandOption("--branch|-b <BRANCH>")]
        [Description("Branch to compare against with --online")]
        [DefaultValue("public")]
        public string Branch { get; init; } = "public";
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

        // Keep copies of whatever Steam has cached before it deletes them on the next update.
        var store = new DepotManifestStore();
        if (new SteamLocator().FindSteamRoot() is { } steamRoot)
        {
            var imported = store.ImportFromDepotcache(Path.Combine(steamRoot, "depotcache"));
            if (imported.Imported.Count > 0)
                AnsiConsole.MarkupLine($"[grey]Archived {imported.Imported.Count} new manifest(s) from depotcache.[/]");
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(acf.Name)}[/] ({acf.AppId})  ·  {Markup.Escape(acf.InstallPath)}");
        AnsiConsole.MarkupLine($"[grey]acf claims: build {acf.BuildId?.ToString() ?? "?"} · StateFlags {acf.StateFlags} · LastUpdated {acf.LastUpdated?.ToString("u") ?? "?"} (not trusted) · AutoUpdateBehavior {acf.AutoUpdateBehavior?.ToString() ?? "?"}[/]");

        IReadOnlyDictionary<uint, ulong?>? online = null;
        if (settings.Online)
        {
            // DLC depots are listed under the DLC's own app, not the base game.
            var apps = acf.InstalledDepots.Select(d => d.DlcAppId).OfType<uint>().Prepend(acf.AppId).Distinct().ToList();
            online = await TryGetOnlineAsync(apps, settings.Branch, cts.Token);
        }

        var identifier = new BuildIdentifier();
        var rows = new List<(DepotIdentification Id, string Kind, ulong? Claimed)>();

        await AnsiConsole.Status().StartAsync("Identifying depots…", async ctx =>
        {
            var progress = new Progress<HashProgress>(p =>
                ctx.Status($"Hashing {Markup.Escape(Shorten(p.CurrentFile))}  {p.BytesDone / 1e9:F1}/{p.BytesTotal / 1e9:F1} GB"));

            foreach (var depot in acf.InstalledDepots)
            {
                ctx.Status($"Identifying depot {depot.DepotId}…");
                var id = await identifier.IdentifyAsync(acf.InstallPath, depot.DepotId, store.LoadViews(depot.DepotId),
                    settings.Verify, progress, cts.Token);
                rows.Add((id, depot.DlcAppId is { } dlc ? $"dlc {dlc}" : "acf", depot.ManifestId));
            }

            ctx.Status("Looking for depots installed outside Steam's records…");
            var exclude = acf.InstalledDepots.Select(d => d.DepotId).Append(acf.AppId).Concat(acf.SharedDepots).ToHashSet();
            foreach (var oob in OutOfBandDepotFinder.Find(acf.InstallPath, store, exclude, cts.Token))
            {
                ctx.Status($"Identifying out-of-band depot {oob.DepotId}…");
                var id = await identifier.IdentifyAsync(acf.InstallPath, oob.DepotId, store.LoadViews(oob.DepotId),
                    settings.Verify, progress, cts.Token);
                rows.Add((id, "out-of-band", null));
            }
        });

        var table = new Table().AddColumns("Depot", "Source", "On disk", "Match", "acf claims", online is null ? "" : $"Steam {settings.Branch}");
        foreach (var (id, kind, claimed) in rows)
        {
            var onDisk = id.ManifestId?.ToString() ?? "[grey]–[/]";
            if (id.EquivalentManifests.Count > 0)
                onDisk += $"\n[grey]= {string.Join(", ", id.EquivalentManifests)}[/]";

            var claim = kind == "out-of-band" ? "[yellow]not in acf[/]"
                : claimed is null ? "[grey]–[/]"
                : claimed == id.ManifestId || id.EquivalentManifests.Contains(claimed.Value) ? $"[green]{claimed}[/]"
                : $"[red]{claimed}[/]";

            var serve = "";
            if (online is not null)
            {
                var current = online.TryGetValue(id.DepotId, out var c) ? c : null;
                serve = current is null ? "[grey]not listed[/]"
                    : current == id.ManifestId || id.EquivalentManifests.Contains(current.Value) ? $"[green]{current} (current)[/]"
                    : $"[yellow]{current} (differs)[/]";
            }
            table.AddRow(id.DepotId.ToString(), kind, onDisk, MatchMarkup(id), claim, serve);
        }
        AnsiConsole.Write(table);

        foreach (var (id, _, _) in rows)
            ReportDetails(id);

        ReportPin(acf, rows.Select(r => r.Id).ToList());

        if (!settings.Verify)
            AnsiConsole.MarkupLine("[grey]Sizes checked for every file; hashes only where manifests differ. --verify hashes everything.[/]");
        return 0;
    }

    /// <summary>Depot → manifest Steam serves on <paramref name="branch"/>, across the base app and its DLC apps.
    /// The first app that lists a depot with a manifest wins.</summary>
    private static async Task<IReadOnlyDictionary<uint, ulong?>?> TryGetOnlineAsync(
        IReadOnlyList<uint> appIds, string branch, CancellationToken ct)
    {
        try
        {
            await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), ct);
            var client = new SteamDepotClient(session);
            var result = new Dictionary<uint, ulong?>();
            foreach (var appId in appIds)
            {
                var info = await client.GetAppDepotsAsync(appId, branch, ct);
                foreach (var d in info.Depots)
                {
                    if (!result.TryGetValue(d.DepotId, out var existing) || existing is null)
                        result[d.DepotId] = d.ManifestId;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException
                                       or InvalidOperationException or TimeoutException)
        {
            LiveProgressDisplay.ShowWarning($"Online check skipped: {ex.Message}");
            return null;
        }
    }

    private static string MatchMarkup(DepotIdentification id) => id.Match switch
    {
        DepotMatch.Exact => "[green]exact[/] (hashed)",
        DepotMatch.SizesOnly => "[green]sizes match[/]",
        DepotMatch.Modified => $"[yellow]modified[/] ({id.Missing.Count} missing, {id.SizeMismatch.Count + id.HashMismatch.Count} differ)",
        DepotMatch.Ambiguous => "[yellow]ambiguous[/]",
        DepotMatch.Unknown => "[red]unknown build[/]",
        DepotMatch.NoCandidates => "[red]no archived manifest[/]",
        _ => id.Match.ToString(),
    };

    private static void ReportDetails(DepotIdentification id)
    {
        void List(string label, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                return;
            AnsiConsole.MarkupLine($"[yellow]{id.DepotId}: {label} ({paths.Count})[/]");
            foreach (var p in paths.Take(15))
                AnsiConsole.MarkupLine($"  {Markup.Escape(p)}");
            if (paths.Count > 15)
                AnsiConsole.MarkupLine($"  [grey]… {paths.Count - 15} more[/]");
        }
        List("missing", id.Missing);
        List("size differs", id.SizeMismatch);
        List("content differs", id.HashMismatch);

        if (id.Match is DepotMatch.Ambiguous or DepotMatch.Unknown or DepotMatch.Modified && id.Candidates.Count > 1)
        {
            AnsiConsole.MarkupLine($"[grey]{id.DepotId} candidates:[/]");
            foreach (var c in id.Candidates.Take(5))
                AnsiConsole.MarkupLine($"[grey]  {c.ManifestId} ({c.CreationTime:yyyy-MM-dd}): {c.SizeMatches}/{c.Files} sizes, {c.Missing} missing, {c.SizeMismatch} differ{(c.HashMatches is { } h ? $", {h} hash hits" : "")}[/]");
        }
    }

    private static void ReportPin(SteamAppManifest acf, IReadOnlyList<DepotIdentification> ids)
    {
        var pin = new BuildPinStore().Load(acf.AppId);
        if (pin is null)
        {
            AnsiConsole.MarkupLine($"Pin: [grey]not pinned[/] (Steam updates normally; AutoUpdateBehavior {acf.AutoUpdateBehavior?.ToString() ?? "?"})");
            return;
        }
        var drift = BuildPinner.CheckDrift(acf, SteamAcfEditor.Load(acf.AcfPath), pin).ToList();
        foreach (var (depot, manifest) in pin.Depots)
        {
            var onDisk = ids.FirstOrDefault(i => i.DepotId == depot);
            if (onDisk is not null && onDisk.ManifestId != manifest && !onDisk.EquivalentManifests.Contains(manifest))
                drift.Add($"Depot {depot} on disk is {onDisk.ManifestId?.ToString() ?? "unknown"}, pinned {manifest}.");
        }
        var what = $"{string.Join(", ", pin.Depots.Select(d => $"{d.Key}={d.Value}"))}{(pin.Branch is { } b ? $", branch {b}" : "")}";
        if (PinLock.Paths(pin, new DepotManifestStore()) is { } lockPaths)
        {
            var state = BuildLock.GetState(acf.InstallPath, lockPaths);
            if (pin.Locked && !state.FullyLocked)
                drift.Add($"Hard lock incomplete: {state.Unlocked} of {state.Locked + state.Unlocked} build file(s) are not locked.");
            what += pin.Locked ? $", hard-locked ({state.Locked}/{state.Locked + state.Unlocked} files)" : "";
        }
        if (drift.Count == 0)
            AnsiConsole.MarkupLine($"Pin: [green]holding[/] since {pin.PinnedAt:u} ({Markup.Escape(what)})");
        else
        {
            AnsiConsole.MarkupLine($"Pin: [red]drifted[/] (pinned {pin.PinnedAt:u}: {Markup.Escape(what)})");
            foreach (var d in drift)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(d)}");
            AnsiConsole.MarkupLine($"  [grey]Re-pin with 'modular depot pin {acf.AppId}' (Steam closed), or swap back.[/]");
        }
    }

    private static string Shorten(string path) => path.Length <= 48 ? path : "…" + path[^47..];
}

/// <summary><c>modular depot diff &lt;depot&gt; &lt;from&gt; &lt;to&gt;</c>: what changes between two archived manifests.</summary>
public sealed class DepotDiffCommand : Command<DepotDiffCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<DEPOT>")]
        public uint DepotId { get; init; }

        [CommandArgument(1, "<FROM>")]
        public ulong From { get; init; }

        [CommandArgument(2, "<TO>")]
        public ulong To { get; init; }

        [CommandOption("--files")]
        [Description("List every file instead of the first 25 per section")]
        public bool AllFiles { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var store = new DepotManifestStore();
        var from = store.LoadView(settings.DepotId, settings.From);
        var to = store.LoadView(settings.DepotId, settings.To);
        foreach (var (view, id) in new[] { (from, settings.From), (to, settings.To) })
        {
            if (view is null)
            {
                LiveProgressDisplay.ShowError($"{settings.DepotId}_{id} is not archived. Run 'modular depot fetch <appid> {settings.DepotId} {id}'.");
                return 1;
            }
        }

        var diff = DepotDiff.Compare(from!, to!);
        AnsiConsole.MarkupLine($"Depot [bold]{diff.DepotId}[/]: {diff.FromManifest} ({from!.CreationTime:yyyy-MM-dd}) → {diff.ToManifest} ({to!.CreationTime:yyyy-MM-dd})");
        if (diff.IsIdentical)
        {
            LiveProgressDisplay.ShowSuccess("Identical file content.");
            return 0;
        }
        AnsiConsole.MarkupLine($"  {diff.Added.Count} added · {diff.Changed.Count} changed · {diff.Removed.Count} removed");
        AnsiConsole.MarkupLine($"  download (new chunks): [bold]{diff.DownloadBytes / 1e9:F2} GB[/] compressed in {diff.NewChunkCount} chunks ({diff.NewChunkUncompressedBytes / 1e9:F2} GB uncompressed)");
        AnsiConsole.MarkupLine($"  size on disk: {diff.FromTotalSize / 1e9:F2} → {diff.ToTotalSize / 1e9:F2} GB");

        var limit = settings.AllFiles ? int.MaxValue : 25;
        void Section(string label, string prefix, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                return;
            AnsiConsole.MarkupLine($"[bold]{label}[/]");
            foreach (var p in paths.Take(limit))
                AnsiConsole.MarkupLine($"  {prefix} {Markup.Escape(p)}");
            if (paths.Count > limit)
                AnsiConsole.MarkupLine($"  [grey]… {paths.Count - limit} more (--files)[/]");
        }
        Section("Added", "[green]+[/]", diff.Added);
        Section("Changed", "[yellow]~[/]", diff.Changed);
        Section("Removed (a swap must delete these; a Steam verify won't)", "[red]-[/]", diff.Removed);
        return 0;
    }
}
