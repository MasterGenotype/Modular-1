using System.Text.Json;

namespace Modular.Switch.Scanner;

/// <summary>The two Switch emulator lineages Modular knows; they lay out data differently.</summary>
public enum SwitchEmulatorFamily
{
    /// <summary>yuzu and its forks (Eden, Citron, Sudachi, Suyu, Torzu): mods in <c>load/&lt;TitleID&gt;</c>.</summary>
    Yuzu,
    /// <summary>Ryujinx and its forks: mods in <c>mods/contents/&lt;TitleID&gt;</c>.</summary>
    Ryujinx,
}

/// <summary>An installed Switch emulator.</summary>
public sealed record SwitchEmulator(
    string Name, SwitchEmulatorFamily Family, string DataRoot, string? ConfigDir, string? CacheDir, string? Executable)
{
    /// <summary>The folder the emulator loads a title's mods from.</summary>
    public string ModDir(string titleId) => Family == SwitchEmulatorFamily.Yuzu
        ? Path.Combine(DataRoot, "load", titleId)
        : Path.Combine(DataRoot, "mods", "contents", titleId);

    /// <summary>Folders the emulator scans for games (yuzu's qt-config.ini "Paths\gamedirs"; Ryujinx's Config.json "game_dirs").</summary>
    public IReadOnlyList<string> GameDirs()
    {
        var dirs = new List<string>();
        try
        {
            if (Family == SwitchEmulatorFamily.Yuzu && ConfigDir is not null && File.Exists(Path.Combine(ConfigDir, "qt-config.ini")))
            {
                foreach (var line in File.ReadLines(Path.Combine(ConfigDir, "qt-config.ini")))
                {
                    // Paths\gamedirs\4\path=/home/me/Switch  (SDMC / UserNAND / SysNAND are built in)
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^Paths\\gamedirs\\\d+\\path=(.+)$");
                    if (m.Success && m.Groups[1].Value.StartsWith('/'))
                        dirs.Add(m.Groups[1].Value.Trim());
                }
            }
            else if (Family == SwitchEmulatorFamily.Ryujinx && File.Exists(Path.Combine(DataRoot, "Config.json")))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataRoot, "Config.json")));
                if (doc.RootElement.TryGetProperty("game_dirs", out var list) && list.ValueKind == JsonValueKind.Array)
                    dirs.AddRange(list.EnumerateArray().Select(e => e.GetString()).OfType<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable config just lists no folders.
        }
        return dirs;
    }
}

/// <summary>A game an emulator knows about.</summary>
public sealed record SwitchTitleInfo(string TitleId, string Name, string? Version, string? AddOns, string ModDir, int ModFolderEntries);

/// <summary>Finds installed Switch emulators and the games they know.</summary>
public static class SwitchEmulatorDetector
{
    private static readonly (string Name, string Folder, string[] Flatpaks)[] YuzuFamily =
    [
        ("yuzu", "yuzu", ["org.yuzu_emu.yuzu"]),
        ("Eden", "eden", ["dev.eden_emu.eden"]),
        ("Citron", "citron", ["org.citron_emu.citron"]),
        ("Sudachi", "sudachi", ["org.sudachi_emu.sudachi"]),
        ("Suyu", "suyu", ["org.suyu_emu.suyu"]),
        ("Torzu", "torzu", ["onion.torzu_emu.torzu"]),
    ];

    /// <summary>Every emulator with a data folder on this machine (or under <paramref name="home"/>, for tests).</summary>
    public static IReadOnlyList<SwitchEmulator> Detect(string? home = null)
    {
        var Home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<SwitchEmulator>();
        foreach (var (name, folder, flatpaks) in YuzuFamily)
        {
            var native = Path.Combine(Home, ".local", "share", folder);
            if (Directory.Exists(native))
            {
                found.Add(new SwitchEmulator(name, SwitchEmulatorFamily.Yuzu, native,
                    Path.Combine(Home, ".config", folder), Path.Combine(Home, ".cache", folder), FindExecutable(folder, Home)));
                continue;
            }
            foreach (var flatpak in flatpaks)
            {
                var data = Path.Combine(Home, ".var", "app", flatpak, "data", folder);
                if (Directory.Exists(data))
                {
                    found.Add(new SwitchEmulator(name, SwitchEmulatorFamily.Yuzu, data,
                        Path.Combine(Home, ".var", "app", flatpak, "config", folder),
                        Path.Combine(Home, ".var", "app", flatpak, "cache", folder), $"flatpak run {flatpak}"));
                    break;
                }
            }
        }

        foreach (var (name, data) in new[]
                 {
                     ("Ryujinx", Path.Combine(Home, ".config", "Ryujinx")),
                     ("Ryujinx (Flatpak)", Path.Combine(Home, ".var", "app", "io.github.ryubing.Ryujinx", "config", "Ryujinx")),
                 })
        {
            if (Directory.Exists(data))
                found.Add(new SwitchEmulator(name, SwitchEmulatorFamily.Ryujinx, data, data, null, FindExecutable("ryujinx", Home)));
        }
        return found;
    }

    /// <summary>The emulator's program: on PATH, in ~/.local/bin, or an AppImage in ~/Applications.</summary>
    private static string? FindExecutable(string name, string Home)
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Home, ".local", "bin"));
        foreach (var dir in path.Distinct())
        {
            foreach (var candidate in new[] { name, name.ToLowerInvariant(), char.ToUpperInvariant(name[0]) + name[1..] })
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }
        var apps = Path.Combine(Home, "Applications");
        return Directory.Exists(apps)
            ? Directory.EnumerateFiles(apps, "*.AppImage").FirstOrDefault(f => Path.GetFileName(f).Contains(name, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>
    /// The games an emulator knows: yuzu-family game-list cache entries (with version and
    /// add-ons) and mod folders; Ryujinx's per-game metadata. Names come from Ryujinx's
    /// metadata, else <see cref="SwitchTitleNames"/>.
    /// </summary>
    public static IReadOnlyList<SwitchTitleInfo> Titles(SwitchEmulator emulator)
    {
        var titles = new Dictionary<string, (string? Name, string? Version, string? AddOns)>(StringComparer.OrdinalIgnoreCase);
        static bool IsTitleId(string s) => s.Length == 16 && s.All(Uri.IsHexDigit);

        if (emulator.Family == SwitchEmulatorFamily.Yuzu)
        {
            var cache = emulator.CacheDir is null ? null : Path.Combine(emulator.CacheDir, "game_list");
            if (cache is not null && Directory.Exists(cache))
            {
                foreach (var file in Directory.EnumerateFiles(cache, "*.pv.txt"))
                {
                    var id = Path.GetFileName(file)[..^".pv.txt".Length];
                    if (!IsTitleId(id))
                        continue;
                    // Lines like "Update (1.2.1)" and "SDMC (LayeredExeFS, LayeredFS)".
                    var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToList();
                    var update = lines.FirstOrDefault(l => l.StartsWith("Update", StringComparison.Ordinal));
                    var version = update is null ? null : System.Text.RegularExpressions.Regex.Match(update, @"\(([^)]*)\)").Groups[1].Value;
                    titles[id.ToUpperInvariant()] = (null, string.IsNullOrEmpty(version) ? null : version,
                        string.Join(" · ", lines.Where(l => l != update)));
                }
            }
            var load = Path.Combine(emulator.DataRoot, "load");
            if (Directory.Exists(load))
            {
                foreach (var dir in Directory.EnumerateDirectories(load).Select(Path.GetFileName).OfType<string>().Where(IsTitleId))
                    titles.TryAdd(dir.ToUpperInvariant(), (null, null, null));
            }
        }
        else
        {
            var games = Path.Combine(emulator.DataRoot, "games");
            if (Directory.Exists(games))
            {
                foreach (var dir in Directory.EnumerateDirectories(games))
                {
                    var id = Path.GetFileName(dir);
                    if (!IsTitleId(id))
                        continue;
                    string? name = null;
                    var meta = Path.Combine(dir, "gui", "metadata.json");
                    try
                    {
                        if (File.Exists(meta))
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                            name = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                    titles[id.ToUpperInvariant()] = (name, null, null);
                }
            }
        }

        return titles.Select(t =>
            {
                var modDir = emulator.ModDir(t.Key);
                var entries = Directory.Exists(modDir) ? Directory.EnumerateFileSystemEntries(modDir).Count() : 0;
                return new SwitchTitleInfo(t.Key, t.Value.Name ?? SwitchTitleNames.NameOf(t.Key), t.Value.Version, t.Value.AddOns, modDir, entries);
            })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Names for title ids. Switch game files keep their names in encrypted data, so known titles are
/// listed here; anything else shows its title id until named.
/// </summary>
public static class SwitchTitleNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["01007EF00011E000"] = "The Legend of Zelda: Breath of the Wild",
        ["0100F2C0115B6000"] = "The Legend of Zelda: Tears of the Kingdom",
    };

    public static string NameOf(string titleId) => Known.TryGetValue(titleId, out var name) ? name : $"Switch game {titleId.ToUpperInvariant()}";
}
