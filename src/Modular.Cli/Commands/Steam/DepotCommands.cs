using System.ComponentModel;
using Modular.Cli.UI;
using Modular.Core.Depots;
using Modular.Core.GameDetection;
using Modular.Core.Security;
using Modular.Core.Steam;
using Spectre.Console;
using Spectre.Console.Cli;
using SteamKit2;

namespace Modular.Cli.Commands.Steam;

/// <summary><c>modular depot list &lt;appid&gt;</c>: the app's depots and their manifests on a branch (PICS).</summary>
public sealed class DepotListCommand : AsyncCommand<DepotListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }

        [CommandOption("--branch|-b <BRANCH>")]
        [DefaultValue("public")]
        public string Branch { get; init; } = "public";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        try
        {
            await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), cts.Token);
            var info = await new SteamDepotClient(session).GetAppDepotsAsync(settings.AppId, settings.Branch, cts.Token);
            var store = new DepotManifestStore();

            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(info.Name)}[/] ({info.AppId}) · branch [bold]{Markup.Escape(info.Branch)}[/] · build {info.BuildId?.ToString() ?? "?"} · updated {info.TimeUpdated?.ToString("u") ?? "?"}");
            AnsiConsole.MarkupLine($"[grey]Branches: {Markup.Escape(string.Join(", ", info.Branches))}[/]");

            var table = new Table().AddColumns("Depot", "Name", "Manifest", "Size", "Download", "OS / lang", "Notes", "Archived");
            foreach (var d in info.Depots.OrderBy(d => d.DepotId))
            {
                var notes = string.Join(" ", new[]
                {
                    d.DepotFromApp is { } from ? $"from {from}" : null,
                    d.SharedInstall ? "shared" : null,
                    d.DlcAppId is { } dlc ? $"dlc {dlc}" : null,
                    d.Encrypted ? "encrypted-branch" : null,
                }.Where(n => n is not null));
                table.AddRow(
                    d.DepotId.ToString(),
                    Markup.Escape(d.Name ?? ""),
                    d.ManifestId?.ToString() ?? "[grey]–[/]",
                    Gb(d.Size),
                    // PICS reports nonsense "download" values for some old manifests (Bastion legacy: ~1.3 TB for a 0.9 GB depot).
                    d.DownloadSize is { } dl && d.Size is { } sz && dl > sz * 2 ? "[grey]?[/]" : Gb(d.DownloadSize),
                    Markup.Escape(string.Join(" ", new[] { d.OsList, d.Language }.Where(s => !string.IsNullOrEmpty(s)))),
                    Markup.Escape(notes),
                    d.ManifestId is { } m && store.Contains(d.DepotId, m) ? "[green]yes[/]" : "");
            }
            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException
                                       or DepotAccessException or InvalidOperationException or TimeoutException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    internal static string Gb(ulong? bytes) => bytes is { } b ? $"{b / 1e9:F2} GB" : "";
}

/// <summary><c>modular depot fetch &lt;appid&gt; &lt;depot&gt; [manifest]</c>: download one manifest into Modular's archive.</summary>
public sealed class DepotFetchCommand : AsyncCommand<DepotFetchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<APPID>")]
        public uint AppId { get; init; }

        [CommandArgument(1, "<DEPOT>")]
        public uint DepotId { get; init; }

        [CommandArgument(2, "[MANIFEST]")]
        [Description("Manifest id (e.g. from SteamDB). Omit for the branch's current manifest.")]
        public ulong? ManifestId { get; init; }

        [CommandOption("--branch|-b <BRANCH>")]
        [DefaultValue("public")]
        public string Branch { get; init; } = "public";

        [CommandOption("--force")]
        [Description("Download even if the manifest is already archived")]
        public bool Force { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var cts = ConsoleCancellation.Create();
        var store = new DepotManifestStore();
        try
        {
            await using var session = await SteamSession.OpenStoredAsync(new SteamAccountStore(new DefaultSecretStore()), cts.Token);
            var client = new SteamDepotClient(session);

            var manifestId = settings.ManifestId;
            if (manifestId is null)
            {
                var info = await client.GetAppDepotsAsync(settings.AppId, settings.Branch, cts.Token);
                manifestId = info.Depots.FirstOrDefault(d => d.DepotId == settings.DepotId)?.ManifestId
                    ?? throw new DepotAccessException($"Depot {settings.DepotId} has no manifest on branch '{settings.Branch}'.");
            }

            DepotManifest manifest;
            string path;
            if (!settings.Force && store.Load(settings.DepotId, manifestId.Value) is { } archived)
            {
                manifest = archived;
                path = store.PathFor(settings.DepotId, manifestId.Value);
                AnsiConsole.MarkupLine("[grey]Already archived; not downloading (use --force to re-fetch).[/]");
            }
            else
            {
                manifest = await client.DownloadManifestAsync(settings.AppId, settings.DepotId, manifestId.Value, settings.Branch, cts.Token);
                path = store.Save(manifest);
            }

            Summarize(manifest, path);
            return 0;
        }
        catch (Exception ex) when (ex is SteamNotLoggedInException or SteamLogOnException or SteamDisconnectedException
                                       or DepotAccessException or InvalidOperationException or TimeoutException)
        {
            LiveProgressDisplay.ShowError(ex.Message);
            return 1;
        }
    }

    internal static void Summarize(DepotManifest m, string path)
    {
        var files = m.Files ?? new();
        var chunks = files.SelectMany(f => f.Chunks).Select(c => c.ChunkID is null ? "" : Convert.ToHexString(c.ChunkID)).Distinct().Count();
        LiveProgressDisplay.ShowSuccess($"Depot {m.DepotID} manifest {m.ManifestGID}");
        AnsiConsole.MarkupLine($"  created {m.CreationTime:u} · {files.Count} entries · {chunks} unique chunks");
        AnsiConsole.MarkupLine($"  {m.TotalUncompressedSize / 1e9:F2} GB on disk · {m.TotalCompressedSize / 1e9:F2} GB compressed");
        AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}[/]");
    }
}

/// <summary><c>modular depot archive</c>: copy manifests from Steam's depotcache into Modular's archive before Steam deletes them.</summary>
public sealed class DepotArchiveCommand : Command<DepotArchiveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--depot|-d <DEPOT>")]
        [Description("Only these depots (repeatable). Default: everything in depotcache.")]
        public uint[]? Depots { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var steamRoot = new SteamLocator().FindSteamRoot();
        if (steamRoot is null)
        {
            LiveProgressDisplay.ShowError("Steam installation not found.");
            return 1;
        }
        var store = new DepotManifestStore();
        var filter = settings.Depots is { Length: > 0 } ds ? new HashSet<uint>(ds) : null;
        var result = store.ImportFromDepotcache(Path.Combine(steamRoot, "depotcache"),
            filter is null ? null : filter.Contains);

        LiveProgressDisplay.ShowSuccess($"Archived {result.Imported.Count} new manifest(s) into {store.Root}; {result.AlreadyArchived} already there.");
        foreach (var bad in result.Unreadable)
            LiveProgressDisplay.ShowWarning($"Not archived (unreadable or encrypted): {bad}");
        return 0;
    }
}

/// <summary><c>modular depot show &lt;depot&gt; &lt;manifest&gt;</c>: summarize an archived manifest without logging in.</summary>
public sealed class DepotShowCommand : Command<DepotShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<DEPOT>")]
        public uint DepotId { get; init; }

        [CommandArgument(1, "<MANIFEST>")]
        public ulong ManifestId { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var store = new DepotManifestStore();
        var m = store.Load(settings.DepotId, settings.ManifestId);
        if (m is null)
        {
            LiveProgressDisplay.ShowError($"{settings.DepotId}_{settings.ManifestId} is not archived. Try 'modular depot archive' or 'modular depot fetch'.");
            return 1;
        }
        DepotFetchCommand.Summarize(m, store.PathFor(settings.DepotId, settings.ManifestId));
        return 0;
    }
}
