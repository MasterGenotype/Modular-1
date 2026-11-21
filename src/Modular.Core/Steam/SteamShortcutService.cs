using Modular.Core.Depots;

namespace Modular.Core.Steam;

/// <summary>An image chosen for one artwork slot.</summary>
public sealed record SteamArtChoice(byte[] Image, string Extension);

/// <summary>Everything a "Create Steam shortcut" needs; prefilled by <see cref="SteamShortcutService"/>.</summary>
public sealed record SteamShortcutRequest(
    string AppName,
    string Exe,
    string StartDir,
    string LaunchOptions,
    string? CompatTool,
    // The Steam game this shortcut starts, if any (for its art and the SteamGridDB lookup).
    uint? SourceSteamAppId = null,
    // The shortcut being edited, so an update keeps its id.
    uint? ExistingAppId = null);

/// <summary>What <see cref="SteamShortcutService.Apply"/> wrote.</summary>
public sealed record SteamShortcutResult(SteamShortcut Shortcut, bool Created, IReadOnlyDictionary<SteamArtKind, string> ArtWritten);

/// <summary>
/// Creates or updates a non-Steam game shortcut: the <c>shortcuts.vdf</c> entry, its Proton
/// choice (<c>CompatToolMapping</c>) and its library artwork. Steam must be closed (design D3).
/// </summary>
public sealed class SteamShortcutService
{
    private readonly Func<bool> _steamRunning;

    public SteamShortcutService(SteamUserConfig user, Func<bool>? steamRunning = null)
    {
        User = user;
        _steamRunning = steamRunning ?? SteamProcess.IsRunning;
    }

    public SteamUserConfig User { get; }

    public static SteamShortcutService? ForCurrentUser(string? steamRoot = null)
    {
        steamRoot ??= new Modular.Core.GameDetection.SteamLocator().FindSteamRoot();
        return steamRoot is null || SteamUserConfig.Find(steamRoot) is not { } user ? null : new SteamShortcutService(user);
    }

    public IReadOnlyList<SteamShortcut> ListShortcuts() => SteamShortcutsFile.Load(User.ShortcutsPath).Shortcuts;

    /// <summary>
    /// Prefill for a Steam game: its Windows exe, its own launch options (plus what keeps its Proton
    /// prefix and Steam identity), its Proton choice, and an existing shortcut for that exe if any.
    /// </summary>
    public SteamShortcutRequest PrefillForSteamGame(uint appId, string name, string installPath, string libraryRoot)
    {
        var exe = SteamUserConfig.GuessExecutable(installPath) ?? "";
        var existing = exe.Length > 0 ? SteamShortcutsFile.Load(User.ShortcutsPath).Find(exe) : null;
        if (existing is not null)
            return FromShortcut(existing) with { SourceSteamAppId = appId };
        return new SteamShortcutRequest(
            name,
            exe,
            exe.Length > 0 ? Path.GetDirectoryName(exe)! : installPath,
            SteamUserConfig.ShortcutLaunchOptionsForSteamGame(appId, libraryRoot, User.GetLaunchOptions(appId)),
            User.GetCompatTool(appId),
            SourceSteamAppId: appId);
    }

    /// <summary>Prefill for an existing shortcut (edit) or a new exe the user picked.</summary>
    public SteamShortcutRequest PrefillForExe(string exe)
    {
        var existing = SteamShortcutsFile.Load(User.ShortcutsPath).Find(exe);
        if (existing is not null)
            return FromShortcut(existing);
        var windows = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        return new SteamShortcutRequest(
            Path.GetFileNameWithoutExtension(exe),
            exe,
            Path.GetDirectoryName(exe) ?? "",
            windows ? "%command%" : "",
            windows ? User.GetCompatTool(0) : null);
    }

    public SteamShortcutRequest FromShortcut(SteamShortcut s) => new(
        s.AppName, s.Exe, s.StartDir, s.LaunchOptions, User.GetCompatTool(s.AppId, fallBackToDefault: false), ExistingAppId: s.AppId);

    /// <summary>The artwork a shortcut id currently has in Steam's grid folder.</summary>
    public IReadOnlyDictionary<SteamArtKind, string> CurrentArt(uint shortcutAppId)
    {
        var file = SteamShortcutsFile.Load(User.ShortcutsPath);
        return Enum.GetValues<SteamArtKind>()
            .Select(k => (k, path: file.FindArt(shortcutAppId, k)))
            .Where(x => x.path is not null)
            .ToDictionary(x => x.k, x => x.path!);
    }

    public SteamShortcutResult Apply(SteamShortcutRequest request, IReadOnlyDictionary<SteamArtKind, SteamArtChoice> art)
    {
        if (string.IsNullOrWhiteSpace(request.AppName))
            throw new ArgumentException("The shortcut needs a name.");
        if (string.IsNullOrWhiteSpace(request.Exe))
            throw new ArgumentException("The shortcut needs a program to start.");
        if (_steamRunning())
            throw new InvalidOperationException("Steam is running. Close Steam (Steam → Exit), then create the shortcut; Modular won't close it for you.");

        var file = SteamShortcutsFile.Load(User.ShortcutsPath);
        var before = file.Find(request.Exe, request.ExistingAppId);
        var appId = before?.AppId ?? request.ExistingAppId ?? SteamShortcut.ComputeAppId(request.Exe, request.AppName);

        var written = new Dictionary<SteamArtKind, string>();
        foreach (var (kind, choice) in art)
            written[kind] = file.SetArt(appId, kind, choice.Image, choice.Extension);

        var stored = file.Upsert(new SteamShortcut(appId, request.AppName.Trim(), request.Exe.Trim(), request.StartDir.Trim(),
            request.LaunchOptions.Trim(), written.TryGetValue(SteamArtKind.Icon, out var icon) ? icon : ""));
        file.Save(_steamRunning);

        if (request.CompatTool is { Length: > 0 } tool)
            User.SetCompatTool(stored.AppId, tool, _steamRunning);
        else if (before is not null)
            User.SetCompatTool(stored.AppId, null, _steamRunning);

        return new SteamShortcutResult(stored, before is null, written);
    }
}
