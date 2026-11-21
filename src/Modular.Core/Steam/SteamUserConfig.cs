using Modular.Core.Depots;
using SteamKit2;

namespace Modular.Core.Steam;

/// <summary>A compatibility tool (Proton build) Steam can run a game with.</summary>
public sealed record SteamCompatTool(string InternalName, string DisplayName);

/// <summary>
/// The Steam client's per-user and global config Modular reads for shortcuts: which user is
/// signed in, a game's launch options, compat tool (Proton) choices and cached library art.
/// </summary>
public sealed class SteamUserConfig
{
    /// <summary>SteamID64 of account id 0; account id = SteamID64 − this.</summary>
    private const ulong SteamId64Base = 76561197960265728UL;

    public SteamUserConfig(string steamRoot, string accountId)
    {
        SteamRoot = steamRoot;
        AccountId = accountId;
    }

    public string SteamRoot { get; }
    public string AccountId { get; }

    public string UserConfigDirectory => Path.Combine(SteamRoot, "userdata", AccountId, "config");
    public string ShortcutsPath => Path.Combine(UserConfigDirectory, "shortcuts.vdf");
    public string LocalConfigPath => Path.Combine(UserConfigDirectory, "localconfig.vdf");
    public string GlobalConfigPath => Path.Combine(SteamRoot, "config", "config.vdf");

    /// <summary>
    /// The Steam user to edit: the most recent login in <c>config/loginusers.vdf</c>, else the only
    /// <c>userdata</c> folder (ignoring Steam's placeholder <c>0</c>).
    /// </summary>
    public static SteamUserConfig? Find(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
            return null;
        var accounts = Directory.GetDirectories(userdata)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n != "0" && n.All(char.IsDigit))
            .Select(n => n!)
            .ToList();

        var loginUsers = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            var kv = new KeyValue();
            if (kv.ReadFileAsText(loginUsers))
            {
                var recent = kv.Children.FirstOrDefault(u => u["MostRecent"].Value == "1")
                    ?? kv.Children.OrderByDescending(u => long.TryParse(u["Timestamp"].Value, out var t) ? t : 0).FirstOrDefault();
                if (recent is not null && ulong.TryParse(recent.Name, out var id64) && id64 > SteamId64Base)
                {
                    var account = (id64 - SteamId64Base).ToString();
                    if (accounts.Contains(account))
                        return new SteamUserConfig(steamRoot, account);
                }
            }
        }
        return accounts.Count == 1 ? new SteamUserConfig(steamRoot, accounts[0]) : null;
    }

    /// <summary>The launch options set on a Steam game (Properties → General), or empty.</summary>
    public string GetLaunchOptions(uint appId)
    {
        if (!File.Exists(LocalConfigPath))
            return "";
        var kv = new KeyValue();
        if (!kv.ReadFileAsText(LocalConfigPath))
            return "";
        var apps = Child(kv, "Software", "Valve", "Steam", "apps");
        return apps is null ? "" : Child(apps, appId.ToString(), "LaunchOptions")?.Value ?? "";
    }

    /// <summary>The compat tool Steam runs an app with: its own mapping, else the global default (id 0).</summary>
    public string? GetCompatTool(uint appId, bool fallBackToDefault = true)
    {
        if (!File.Exists(GlobalConfigPath))
            return null;
        var cfg = SteamAcfEditor.Load(GlobalConfigPath);
        var own = cfg.Get("Software", "Valve", "Steam", "CompatToolMapping", appId.ToString(), "name");
        if (!string.IsNullOrEmpty(own))
            return own;
        return fallBackToDefault ? NullIfEmpty(cfg.Get("Software", "Valve", "Steam", "CompatToolMapping", "0", "name")) : null;
    }

    /// <summary>Sets (or with null clears) the compat tool for an app id. Steam must be closed.</summary>
    public void SetCompatTool(uint appId, string? toolName, Func<bool>? steamRunning = null)
    {
        if ((steamRunning ?? SteamProcess.IsRunning)())
            throw new InvalidOperationException("Steam is running. Close Steam before Modular edits its config.");
        var text = File.ReadAllText(GlobalConfigPath);
        var patched = TextVdfPatcher.SetBlock(text,
            ["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping"], appId.ToString(),
            string.IsNullOrEmpty(toolName) ? null : [("name", toolName), ("config", ""), ("priority", "250")]);
        if (patched == text)
            return;
        var tmp = GlobalConfigPath + ".modular-tmp";
        File.WriteAllText(tmp, patched);
        File.Move(tmp, GlobalConfigPath, overwrite: true);
    }

    /// <summary>
    /// Proton builds Steam can use: custom tools in <c>compatibilitytools.d</c>, Valve's Proton
    /// installs, and any tool already mapped to a game.
    /// </summary>
    public IReadOnlyList<SteamCompatTool> ListCompatTools()
    {
        var tools = new Dictionary<string, SteamCompatTool>(StringComparer.Ordinal);
        foreach (var dir in new[] { Path.Combine(SteamRoot, "compatibilitytools.d"), "/usr/share/steam/compatibilitytools.d" })
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var vdf in Directory.GetDirectories(dir).Select(d => Path.Combine(d, "compatibilitytool.vdf")).Where(File.Exists))
            {
                var kv = new KeyValue();
                if (!kv.ReadFileAsText(vdf))
                    continue;
                foreach (var tool in kv["compat_tools"].Children)
                    tools.TryAdd(tool.Name!, new SteamCompatTool(tool.Name!, tool["display_name"].Value ?? tool.Name!));
            }
        }

        var common = Path.Combine(SteamRoot, "steamapps", "common");
        if (Directory.Exists(Path.Combine(common, "Proton - Experimental")))
            tools.TryAdd("proton_experimental", new SteamCompatTool("proton_experimental", "Proton Experimental"));
        if (Directory.Exists(common))
        {
            foreach (var dir in Directory.GetDirectories(common, "Proton *"))
            {
                var version = Path.GetFileName(dir)["Proton ".Length..];
                if (version.Split('.') is [var major, ..] && int.TryParse(major, out var m))
                    tools.TryAdd($"proton_{m}", new SteamCompatTool($"proton_{m}", $"Proton {version}"));
            }
        }

        if (File.Exists(GlobalConfigPath))
        {
            var kv = new KeyValue();
            if (kv.ReadFileAsText(GlobalConfigPath) && Child(kv, "Software", "Valve", "Steam", "CompatToolMapping") is { } mapping)
            {
                foreach (var name in mapping.Children.Select(c => c["name"].Value).Where(n => !string.IsNullOrEmpty(n)))
                    tools.TryAdd(name!, new SteamCompatTool(name!, name!));
            }
        }
        return tools.Values.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The art Steam already downloaded for a game it sells, from <c>appcache/librarycache</c>.
    /// Steam has used three layouts: flat <c>&lt;appid&gt;_library_600x900.jpg</c> files, a per-app
    /// folder, and (2025+) per-app folders with each image in a content-hash subfolder and newer
    /// names (<c>library_capsule.jpg</c>, <c>library_header.jpg</c>).
    /// </summary>
    public IReadOnlyDictionary<SteamArtKind, string> FindCachedArt(uint appId)
    {
        var found = new Dictionary<SteamArtKind, string>();
        var cache = Path.Combine(SteamRoot, "appcache", "librarycache");
        var dir = Path.Combine(cache, appId.ToString());

        // Every file in the app folder and its hash subfolders, newest first per name.
        var files = Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList()
            : [];
        string? Named(params string[] names) => names
            .Select(n => files.FirstOrDefault(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.FullName)
            .FirstOrDefault(p => p is not null);
        string? Flat(string suffix) => File.Exists(Path.Combine(cache, $"{appId}_{suffix}")) ? Path.Combine(cache, $"{appId}_{suffix}") : null;

        void Set(SteamArtKind kind, string? path)
        {
            if (path is not null)
                found.TryAdd(kind, path);
        }
        Set(SteamArtKind.Grid, Named("library_600x900_2x.jpg", "library_600x900.jpg", "library_capsule_2x.jpg", "library_capsule.jpg") ?? Flat("library_600x900.jpg"));
        Set(SteamArtKind.Wide, Named("header.jpg", "library_header_2x.jpg", "library_header.jpg") ?? Flat("header.jpg"));
        Set(SteamArtKind.Hero, Named("library_hero_2x.jpg", "library_hero.jpg") ?? Flat("library_hero.jpg"));
        Set(SteamArtKind.Logo, Named("logo_2x.png", "logo.png") ?? Flat("logo.png"));
        // The icon is stored under its content hash (40 hex chars) at the app folder's top level.
        Set(SteamArtKind.Icon, files.FirstOrDefault(f => f.DirectoryName == dir && f.Extension == ".jpg"
                && Path.GetFileNameWithoutExtension(f.Name) is { Length: 40 } n && n.All(Uri.IsHexDigit))?.FullName
            ?? Flat("icon.jpg"));
        return found;
    }

    /// <summary>
    /// Best guess at the executable a Steam game's Windows build starts: an .exe in the install root
    /// named like the folder, else the largest one that isn't an installer or crash reporter.
    /// </summary>
    public static string? GuessExecutable(string installPath)
    {
        if (!Directory.Exists(installPath))
            return null;
        string[] skip = ["unins", "setup", "crash", "redist", "vcredist", "dxsetup", "ue4prereq", "launcherpatcher", "easyanticheat_setup"];
        var folder = Path.GetFileName(installPath.TrimEnd('/')).Replace(" ", "");
        IEnumerable<FileInfo> Candidates(SearchOption depth) => new DirectoryInfo(installPath)
            .EnumerateFiles("*.exe", depth)
            .Where(f => !skip.Any(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase)));
        var root = Candidates(SearchOption.TopDirectoryOnly).ToList();
        var pool = root.Count > 0 ? root : Candidates(SearchOption.AllDirectories).Take(200).ToList();
        return (pool.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Replace(" ", "")
                    .Equals(folder, StringComparison.OrdinalIgnoreCase))
                ?? pool.OrderByDescending(f => f.Length).FirstOrDefault())?.FullName;
    }

    /// <summary>
    /// Launch options for a shortcut that starts a Steam game outside its own entry: the game keeps
    /// its Proton prefix (saves) and registers with Steam as itself; its own options are kept.
    /// </summary>
    public static string ShortcutLaunchOptionsForSteamGame(uint appId, string libraryRoot, string ownOptions)
    {
        var prefix = Path.Combine(libraryRoot, "steamapps", "compatdata", appId.ToString());
        var env = $"SteamAppId={appId} STEAM_COMPAT_DATA_PATH=\"{prefix}\"";
        ownOptions = ownOptions.Trim();
        if (ownOptions.Length == 0)
            return $"{env} %command%";
        return ownOptions.Contains("%command%", StringComparison.Ordinal)
            ? $"{env} {ownOptions}"
            : $"{env} %command% {ownOptions}";
    }

    private static KeyValue? Child(KeyValue node, params string[] path)
    {
        var current = node;
        foreach (var key in path)
        {
            current = current.Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }
        return current;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
