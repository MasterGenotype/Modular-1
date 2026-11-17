using Modular.Core.GameDetection;
using Modular.Core.Utilities;

namespace Modular.Core.Depots;

/// <summary>
/// What Steam's <c>appmanifest_&lt;appid&gt;.acf</c> claims about an install. These are claims, not
/// facts: after a manual depot swap the build id and manifest ids can be wrong, so
/// <see cref="BuildIdentifier"/> checks them against the files on disk.
/// </summary>
public sealed record SteamAppManifest(
    uint AppId,
    string Name,
    string AcfPath,
    string LibraryRoot,
    string InstallPath,
    uint? BuildId,
    int StateFlags,
    DateTimeOffset? LastUpdated,
    int? AutoUpdateBehavior,
    IReadOnlyList<AcfDepot> InstalledDepots,
    IReadOnlyList<uint> SharedDepots)
{
    public static SteamAppManifest Load(string acfPath)
    {
        var root = KeyValuesParser.ParseFile(acfPath);
        var app = root.GetChild("AppState") ?? root;
        var libraryRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(acfPath)))!;
        var installDir = app.GetValue("installdir") ?? "";

        var depots = new List<AcfDepot>();
        foreach (var d in app.GetChild("InstalledDepots")?.Children ?? new List<KeyValuesNode>())
        {
            if (!uint.TryParse(d.Key, out var depotId))
                continue;
            depots.Add(new AcfDepot(
                depotId,
                ulong.TryParse(d.GetValue("manifest"), out var m) ? m : null,
                ulong.TryParse(d.GetValue("size"), out var s) ? s : null,
                uint.TryParse(d.GetValue("dlcappid"), out var dlc) ? dlc : null));
        }

        var shared = (app.GetChild("SharedDepots")?.Values.Keys ?? Enumerable.Empty<string>())
            .Select(k => uint.TryParse(k, out var id) ? id : 0u)
            .Where(id => id != 0)
            .ToList();

        return new SteamAppManifest(
            AppId: uint.TryParse(app.GetValue("appid"), out var appId) ? appId : 0,
            Name: app.GetValue("name") ?? installDir,
            AcfPath: acfPath,
            LibraryRoot: libraryRoot,
            InstallPath: Path.Combine(libraryRoot, "steamapps", "common", installDir),
            BuildId: uint.TryParse(app.GetValue("buildid"), out var build) ? build : null,
            StateFlags: int.TryParse(app.GetValue("StateFlags"), out var flags) ? flags : 0,
            LastUpdated: long.TryParse(app.GetValue("LastUpdated"), out var lu) ? DateTimeOffset.FromUnixTimeSeconds(lu) : null,
            AutoUpdateBehavior: int.TryParse(app.GetValue("AutoUpdateBehavior"), out var aub) ? aub : null,
            InstalledDepots: depots,
            SharedDepots: shared);
    }

    /// <summary>Finds the acf for <paramref name="appId"/> across all Steam libraries.</summary>
    public static SteamAppManifest? Find(uint appId, SteamLibraryScanner? libraries = null)
    {
        foreach (var root in (libraries ?? new SteamLibraryScanner()).GetLibraryRoots())
        {
            var acf = Path.Combine(root, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(acf))
                return Load(acf);
        }
        return null;
    }
}

public sealed record AcfDepot(uint DepotId, ulong? ManifestId, ulong? Size, uint? DlcAppId);
