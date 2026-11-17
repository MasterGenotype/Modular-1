using Modular.Core.Steam;
using SteamKit2;
using SteamKit2.CDN;

namespace Modular.Core.Depots;

/// <summary>
/// Reads an app's depot layout from PICS and downloads depot manifests from the Steam CDN,
/// using a logged-on <see cref="SteamSession"/>. Downloads nothing but manifests.
/// </summary>
public sealed class SteamDepotClient(SteamSession session)
{
    /// <summary>Depots and the manifest each one has on <paramref name="branch"/>, from PICS.</summary>
    public async Task<AppDepotInfo> GetAppDepotsAsync(uint appId, string branch = "public", CancellationToken ct = default)
    {
        var tokens = await session.Apps.PICSGetAccessTokens(appId, null).ToTask().WaitAsync(ct);
        var token = tokens.AppTokens.TryGetValue(appId, out var t) ? t : 0UL;

        var resultSet = await session.Apps.PICSGetProductInfo(new SteamApps.PICSRequest(appId, token), null)
            .ToTask().WaitAsync(ct);
        var app = resultSet.Results?
            .SelectMany(r => r.Apps)
            .Where(kv => kv.Key == appId)
            .Select(kv => kv.Value)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"PICS returned no product info for app {appId}.");

        var kv = app.KeyValues;
        var name = kv["common"]["name"].AsString() ?? appId.ToString();
        var depotsKv = kv["depots"];
        var branchKv = depotsKv["branches"][branch];

        var depots = new List<DepotInfo>();
        foreach (var child in depotsKv.Children)
        {
            if (!uint.TryParse(child.Name, out var depotId))
                continue; // "branches", "baselanguages", "overridescddb", …

            var manifestKv = child["manifests"][branch];
            // Newer PICS: manifests/<branch>/{gid,size,download}. Older: manifests/<branch> = gid.
            var gidText = manifestKv["gid"].AsString() ?? manifestKv.AsString();
            ulong? gid = ulong.TryParse(gidText, out var g) ? g : null;

            depots.Add(new DepotInfo(
                DepotId: depotId,
                Name: child["name"].AsString(),
                ManifestId: gid,
                Size: ulong.TryParse(manifestKv["size"].AsString(), out var size) ? size : null,
                DownloadSize: ulong.TryParse(manifestKv["download"].AsString(), out var dl) ? dl : null,
                OsList: child["config"]["oslist"].AsString(),
                Language: child["config"]["language"].AsString(),
                DepotFromApp: uint.TryParse(child["depotfromapp"].AsString(), out var from) ? from : null,
                SharedInstall: child["sharedinstall"].AsString() == "1",
                DlcAppId: uint.TryParse(child["dlcappid"].AsString(), out var dlc) ? dlc : null,
                Encrypted: manifestKv == KeyValue.Invalid && child["encryptedmanifests"][branch] != KeyValue.Invalid));
        }

        return new AppDepotInfo(
            AppId: appId,
            Name: name,
            Branch: branch,
            BuildId: uint.TryParse(branchKv["buildid"].AsString(), out var build) ? build : null,
            TimeUpdated: long.TryParse(branchKv["timeupdated"].AsString(), out var tu)
                ? DateTimeOffset.FromUnixTimeSeconds(tu) : null,
            Branches: depotsKv["branches"].Children.Select(b => b.Name ?? "").Where(n => n.Length > 0).ToList(),
            Depots: depots);
    }

    /// <summary>
    /// Downloads one manifest: depot key → manifest request code → CDN, trying servers in
    /// order of load. The returned manifest has its filenames decrypted.
    /// </summary>
    public async Task<DepotManifest> DownloadManifestAsync(
        uint appId, uint depotId, ulong manifestId, string branch = "public", CancellationToken ct = default)
    {
        var key = await session.Apps.GetDepotDecryptionKey(depotId, appId).ToTask().WaitAsync(ct);
        if (key.Result != EResult.OK)
            throw new DepotAccessException($"No depot key for {depotId} ({key.Result}). Does this account own app {appId}?");

        var requestCode = await session.Content.GetManifestRequestCode(depotId, appId, manifestId, branch, null)
            .WaitAsync(ct);
        if (requestCode == 0)
            throw new DepotAccessException(
                $"Steam refused a manifest request code for {depotId}_{manifestId} on branch '{branch}'.");

        var servers = (await session.Content.GetServersForSteamPipe(session.CellId, null).WaitAsync(ct))
            .Where(s => s.Type is "SteamCache" or "CDN")
            .Where(s => s.AllowedAppIds is null || s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(appId))
            .OrderBy(s => s.WeightedLoad)
            .ToList();
        if (servers.Count == 0)
            throw new DepotAccessException("Steam returned no content servers.");

        using var cdn = new SteamKit2.CDN.Client(session.Client);
        var errors = new List<string>();
        foreach (var server in servers.Take(5))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifest = await cdn.DownloadManifestAsync(depotId, manifestId, requestCode, server, key.DepotKey)
                    .WaitAsync(ct);
                if (manifest.FilenamesEncrypted)
                    manifest.DecryptFilenames(key.DepotKey);
                return manifest;
            }
            catch (Exception ex) when (ex is SteamKitWebRequestException or HttpRequestException or TaskCanceledException
                                       && !ct.IsCancellationRequested)
            {
                errors.Add($"{server.Host}: {ex.Message}");
            }
        }
        throw new DepotAccessException(
            $"All content servers failed for {depotId}_{manifestId}:\n  " + string.Join("\n  ", errors));
    }
}

public sealed record AppDepotInfo(
    uint AppId,
    string Name,
    string Branch,
    uint? BuildId,
    DateTimeOffset? TimeUpdated,
    IReadOnlyList<string> Branches,
    IReadOnlyList<DepotInfo> Depots);

public sealed record DepotInfo(
    uint DepotId,
    string? Name,
    ulong? ManifestId,
    ulong? Size,
    ulong? DownloadSize,
    string? OsList,
    string? Language,
    uint? DepotFromApp,
    bool SharedInstall,
    uint? DlcAppId,
    bool Encrypted);

public sealed class DepotAccessException(string message) : Exception(message);
