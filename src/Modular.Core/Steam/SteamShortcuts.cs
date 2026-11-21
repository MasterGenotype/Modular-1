using System.IO.Hashing;
using System.Text;
using Modular.Core.Depots;

namespace Modular.Core.Steam;

/// <summary>The artwork slots Steam's library shows for a game.</summary>
public enum SteamArtKind
{
    /// <summary>Portrait capsule, 600×900 (<c>&lt;id&gt;p.png</c>).</summary>
    Grid,
    /// <summary>Wide capsule, 920×430 (<c>&lt;id&gt;.png</c>).</summary>
    Wide,
    /// <summary>Banner behind the game page header, 1920×620 (<c>&lt;id&gt;_hero.png</c>).</summary>
    Hero,
    /// <summary>Transparent logo drawn over the hero (<c>&lt;id&gt;_logo.png</c>).</summary>
    Logo,
    /// <summary>Square icon (<c>&lt;id&gt;_icon.png</c>, also the shortcut's <c>icon</c>).</summary>
    Icon,
}

/// <summary>One non-Steam game entry in <c>shortcuts.vdf</c>.</summary>
public sealed record SteamShortcut(
    uint AppId,
    string AppName,
    string Exe,
    string StartDir,
    string LaunchOptions,
    string Icon = "")
{
    /// <summary>The quoted form Steam stores (<c>"/path/game.exe"</c>).</summary>
    public static string Quote(string path) => path.StartsWith('"') ? path : $"\"{path}\"";

    public static string Unquote(string path) => path.Trim().Trim('"');

    /// <summary>
    /// Steam's id for a shortcut: CRC32 of exe + name with the top bit set. Steam keeps whatever
    /// <c>appid</c> the file holds, so this only matters for new entries.
    /// </summary>
    public static uint ComputeAppId(string exe, string appName)
    {
        var crc = Crc32.HashToUInt32(Encoding.UTF8.GetBytes(Quote(exe) + appName));
        return crc | 0x80000000;
    }
}

/// <summary>
/// Reads and writes one Steam user's <c>userdata/&lt;id&gt;/config/shortcuts.vdf</c> and the
/// <c>grid/</c> artwork beside it. Writes need Steam closed: it rewrites the file from memory.
/// </summary>
public sealed class SteamShortcutsFile
{
    private readonly BinaryVdfNode _root;

    private SteamShortcutsFile(string path, BinaryVdfNode root)
    {
        Path = path;
        _root = root;
    }

    public string Path { get; }

    public string GridDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "grid");

    public static SteamShortcutsFile Load(string path)
    {
        var root = File.Exists(path) ? BinaryVdfNode.Read(File.ReadAllBytes(path)) : new BinaryVdfNode("shortcuts");
        return new SteamShortcutsFile(path, root);
    }

    public IReadOnlyList<SteamShortcut> Shortcuts => _root.Children
        .Select(c => c.Value as BinaryVdfNode)
        .Where(n => n is not null)
        .Select(n => ToShortcut(n!))
        .ToList();

    private static SteamShortcut ToShortcut(BinaryVdfNode n)
    {
        var exe = n.GetString("Exe") ?? n.GetString("exe") ?? "";
        var name = n.GetString("AppName") ?? n.GetString("appname") ?? "";
        var id = n.GetInt("appid") is { } i ? unchecked((uint)i) : SteamShortcut.ComputeAppId(SteamShortcut.Unquote(exe), name);
        return new SteamShortcut(id, name, SteamShortcut.Unquote(exe), SteamShortcut.Unquote(n.GetString("StartDir") ?? ""),
            n.GetString("LaunchOptions") ?? "", n.GetString("icon") ?? "");
    }

    /// <summary>The entry with this exe (case-sensitive path), or this id.</summary>
    public SteamShortcut? Find(string exe, uint? appId = null) => Shortcuts.FirstOrDefault(s =>
        (appId is { } id && s.AppId == id) || s.Exe == SteamShortcut.Unquote(exe));

    /// <summary>
    /// Adds a shortcut, or updates the one with the same exe (or id) in place, keeping Steam's own
    /// fields (play time, tags, hidden flag). Returns the entry as stored.
    /// </summary>
    public SteamShortcut Upsert(SteamShortcut shortcut)
    {
        var existing = _root.Children
            .Select(c => c.Value as BinaryVdfNode)
            .FirstOrDefault(n => n is not null && (ToShortcut(n).AppId == shortcut.AppId || ToShortcut(n).Exe == shortcut.Exe));

        var node = existing;
        var appId = shortcut.AppId;
        if (node is null)
        {
            node = new BinaryVdfNode(NextIndex());
            node["appid"] = unchecked((int)appId);
            node["AppName"] = "";
            node["Exe"] = "";
            node["StartDir"] = "";
            node["icon"] = "";
            node["ShortcutPath"] = "";
            node["LaunchOptions"] = "";
            node["IsHidden"] = 0;
            node["AllowDesktopConfig"] = 1;
            node["AllowOverlay"] = 1;
            node["OpenVR"] = 0;
            node["Devkit"] = 0;
            node["DevkitGameID"] = "";
            node["DevkitOverrideAppID"] = 0;
            node["LastPlayTime"] = 0;
            node["FlatpakAppID"] = "";
            node["sortas"] = "";
            node["tags"] = new BinaryVdfNode("tags");
            _root.Children.Add(new(node.Name, node));
        }
        else
        {
            appId = ToShortcut(node).AppId; // Steam already knows the game by this id
            if (node.GetInt("appid") is null)
                node["appid"] = unchecked((int)appId);
        }

        node["AppName"] = shortcut.AppName;
        node["Exe"] = SteamShortcut.Quote(shortcut.Exe);
        node["StartDir"] = SteamShortcut.Quote(shortcut.StartDir);
        node["LaunchOptions"] = shortcut.LaunchOptions;
        if (shortcut.Icon.Length > 0)
            node["icon"] = shortcut.Icon;
        return ToShortcut(node);
    }

    private string NextIndex()
    {
        var used = _root.Children.Select(c => int.TryParse(c.Key, out var i) ? i : -1).DefaultIfEmpty(-1).Max();
        return (used + 1).ToString();
    }

    public void Save(Func<bool>? steamRunning = null)
    {
        if ((steamRunning ?? SteamProcess.IsRunning)())
            throw new InvalidOperationException("Steam is running. Close Steam before Modular edits its shortcuts.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".modular-tmp";
        File.WriteAllBytes(tmp, _root.ToBytes());
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>The file name Steam looks for, without extension.</summary>
    public static string ArtBaseName(uint appId, SteamArtKind kind) => kind switch
    {
        SteamArtKind.Grid => $"{appId}p",
        SteamArtKind.Wide => $"{appId}",
        SteamArtKind.Hero => $"{appId}_hero",
        SteamArtKind.Logo => $"{appId}_logo",
        SteamArtKind.Icon => $"{appId}_icon",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The artwork currently set for an id and slot, if any.</summary>
    public string? FindArt(uint appId, SteamArtKind kind)
    {
        var baseName = ArtBaseName(appId, kind);
        return new[] { ".png", ".jpg", ".jpeg", ".webp" }
            .Select(ext => System.IO.Path.Combine(GridDirectory, baseName + ext))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Places artwork for a slot, replacing any earlier image for it (in any format). Returns the
    /// path written. Works for Steam games too (custom artwork by their own app id).
    /// </summary>
    public string SetArt(uint appId, SteamArtKind kind, byte[] image, string extension)
    {
        extension = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        if (extension == ".jpeg")
            extension = ".jpg";
        Directory.CreateDirectory(GridDirectory);
        var baseName = ArtBaseName(appId, kind);
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var old = System.IO.Path.Combine(GridDirectory, baseName + ext);
            if (ext != extension && File.Exists(old))
                File.Delete(old);
        }
        var path = System.IO.Path.Combine(GridDirectory, baseName + extension);
        var tmp = path + ".modular-tmp";
        File.WriteAllBytes(tmp, image);
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
