using System.Text.Json;
using SteamKit2;

namespace Modular.Core.Depots;

/// <summary>
/// Edits a Steam <c>appmanifest_*.acf</c> in place with SteamKit2's KeyValue (keeps key order).
/// Callers must check <see cref="SteamProcess.IsRunning"/> first: Steam rewrites the file from
/// memory while it runs.
/// </summary>
public sealed class SteamAcfEditor
{
    private readonly KeyValue _root;

    private SteamAcfEditor(string path, KeyValue root)
    {
        Path = path;
        _root = root;
    }

    public string Path { get; }

    public static SteamAcfEditor Load(string path)
    {
        var kv = new KeyValue();
        if (!kv.ReadFileAsText(path))
            throw new InvalidDataException($"Could not parse {path}.");
        return new SteamAcfEditor(path, kv);
    }

    /// <summary>Value at a path below AppState, e.g. ("UserConfig", "BetaKey"); null when absent.</summary>
    public string? Get(params string[] path)
    {
        var node = _root;
        foreach (var key in path)
        {
            node = node.Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                return null;
        }
        return node.Value;
    }

    /// <summary>Sets a value, creating sections as needed. Null removes the key.</summary>
    public void Set(string? value, params string[] path)
    {
        var node = _root;
        for (var i = 0; i < path.Length; i++)
        {
            var key = path[i];
            var child = node.Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
            var last = i == path.Length - 1;
            if (last && value is null)
            {
                if (child is not null)
                    node.Children.Remove(child);
                return;
            }
            if (child is null)
            {
                child = last ? new KeyValue(key, value) : new KeyValue(key);
                node.Children.Add(child);
            }
            else if (last)
            {
                child.Value = value;
            }
            node = child;
        }
    }

    public void Save()
    {
        var tmp = Path + ".modular-tmp";
        _root.SaveToFile(tmp, asBinary: false);
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>
/// A pinned build: the depot manifests Modular installed and the acf values it set so Steam won't
/// update the game (design D7). Stored per app in <c>~/.config/Modular/pins/&lt;appid&gt;.json</c>,
/// with the values it replaced so <c>unpin</c> restores exactly those.
/// </summary>
public sealed record BuildPin(
    uint AppId,
    DateTimeOffset PinnedAt,
    Dictionary<uint, ulong> Depots,
    string? Branch,
    uint? BuildId,
    Dictionary<string, string?> Replaced)
{
    public const string AutoUpdateOnLaunchOnly = "1";
}

public sealed class BuildPinStore(string? root = null)
{
    public string Root { get; } = root ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Modular", "pins");

    private string PathFor(uint appId) => System.IO.Path.Combine(Root, $"{appId}.json");

    public BuildPin? Load(uint appId)
    {
        var path = PathFor(appId);
        return File.Exists(path) ? JsonSerializer.Deserialize<BuildPin>(File.ReadAllText(path), DepotJson.Options) : null;
    }

    public void Save(BuildPin pin)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(PathFor(pin.AppId), JsonSerializer.Serialize(pin, DepotJson.Options));
    }

    public void Delete(uint appId) => File.Delete(PathFor(appId));
}

/// <summary>Writes and checks the acf side of a pin.</summary>
public static class BuildPinner
{
    // StateFlags bits Steam sets while an update is wanted or in progress.
    private const int UpdateRequired = 2, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024;
    private const int FullyInstalled = 4;

    /// <summary>
    /// Records the true installed manifests, sets "update only on launch", and when the build is an
    /// official branch selects that branch (BetaKey), which Steam then keeps frozen.
    /// </summary>
    public static BuildPin Apply(SteamAcfEditor acf, uint appId, IReadOnlyDictionary<uint, (ulong Manifest, ulong Size)> depots,
        string? branch, uint? buildId, BuildPin? previous, Func<bool>? steamRunning = null)
    {
        if ((steamRunning ?? SteamProcess.IsRunning)())
            throw new InvalidOperationException("Steam is running. Close Steam before Modular edits its files.");

        // Keep the first-seen original values across re-pins, so unpin restores the pre-Modular state.
        var replaced = previous?.Replaced is { } p ? new Dictionary<string, string?>(p) : new Dictionary<string, string?>();
        void SetTracked(string? value, params string[] path)
        {
            var key = string.Join('/', path);
            replaced.TryAdd(key, acf.Get(path));
            acf.Set(value, path);
        }

        foreach (var (depot, (manifest, size)) in depots)
        {
            acf.Set(manifest.ToString(), "InstalledDepots", depot.ToString(), "manifest");
            acf.Set(size.ToString(), "InstalledDepots", depot.ToString(), "size");
        }
        if (buildId is not null)
            acf.Set(buildId.ToString(), "buildid");

        SetTracked(BuildPin.AutoUpdateOnLaunchOnly, "AutoUpdateBehavior");
        if (branch is not null && branch != "public")
        {
            SetTracked(branch, "UserConfig", "BetaKey");
            SetTracked(branch, "MountedConfig", "BetaKey");
        }

        // The files are complete and match the recorded manifests: nothing for Steam to resume.
        acf.Set(FullyInstalled.ToString(), "StateFlags");
        if (acf.Get("TargetBuildID") is not null)
            SetTracked("0", "TargetBuildID"); // tracked, so unpin puts Steam's value back
        acf.Save();

        return new BuildPin(appId, DateTimeOffset.UtcNow, depots.ToDictionary(d => d.Key, d => d.Value.Manifest), branch, buildId, replaced);
    }

    /// <summary>Restores the acf values the pin replaced. Manifest ids stay (they are true).</summary>
    public static void Remove(SteamAcfEditor acf, BuildPin pin, Func<bool>? steamRunning = null)
    {
        if ((steamRunning ?? SteamProcess.IsRunning)())
            throw new InvalidOperationException("Steam is running. Close Steam before Modular edits its files.");
        foreach (var (key, value) in pin.Replaced)
            acf.Set(value, key.Split('/'));
        acf.Save();
    }

    /// <summary>What has moved since the pin was set: the watchdog check run on every status and before launch.</summary>
    public static IReadOnlyList<string> CheckDrift(SteamAppManifest acf, SteamAcfEditor raw, BuildPin pin)
    {
        var drift = new List<string>();
        if ((acf.StateFlags & (UpdateRequired | UpdateRunning | UpdatePaused | UpdateStarted)) != 0)
            drift.Add($"Steam has an update pending or running (StateFlags {acf.StateFlags}).");
        if (raw.Get("TargetBuildID") is { } target && target != "0" && target != acf.BuildId?.ToString())
            drift.Add($"Steam is targeting build {target}.");
        if (long.TryParse(raw.Get("ScheduledAutoUpdate"), out var scheduled) && scheduled > 0)
            drift.Add($"Steam has scheduled an automatic update for {DateTimeOffset.FromUnixTimeSeconds(scheduled):u}.");
        if (raw.Get("AutoUpdateBehavior") != BuildPin.AutoUpdateOnLaunchOnly)
            drift.Add($"AutoUpdateBehavior is {raw.Get("AutoUpdateBehavior") ?? "unset"}, not 1 (update only on launch).");
        if (pin.Branch is not null && pin.Branch != "public" && raw.Get("UserConfig", "BetaKey") != pin.Branch)
            drift.Add($"Branch changed: BetaKey is '{raw.Get("UserConfig", "BetaKey") ?? "public"}', pinned '{pin.Branch}'.");
        foreach (var (depot, manifest) in pin.Depots)
        {
            var recorded = acf.InstalledDepots.FirstOrDefault(d => d.DepotId == depot)?.ManifestId;
            if (recorded != manifest)
                drift.Add($"acf records depot {depot} as {recorded?.ToString() ?? "absent"}, pinned {manifest}.");
        }
        return drift;
    }
}
