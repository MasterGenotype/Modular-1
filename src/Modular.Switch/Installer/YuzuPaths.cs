using Modular.Switch.Models;

namespace Modular.Switch.Installer;

/// <summary>
/// Resolves Yuzu filesystem paths on Linux.
/// Supports both the legacy XDG path and the newer Flatpak layout.
/// </summary>
public static class YuzuPaths
{
    // ── Known base directories ────────────────────────────────────────────

    /// <summary>
    /// Candidate root directories for Yuzu data, in priority order.
    /// The first directory that actually exists on disk is used.
    /// </summary>
    private static readonly string[] Candidates =
    [
        // Native / AppImage install
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "yuzu"),
        // Flatpak
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".var", "app", "org.yuzu_emu.yuzu", "data", "yuzu"),
        // Snap (less common)
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "snap", "yuzu", "common", ".local", "share", "yuzu"),
    ];

    // ── Configurable override ────────────────────────────────────────────

    private static string? _customDataRoot;

    /// <summary>
    /// Sets a custom data root, overriding auto-detection.
    /// Pass <c>null</c> to revert to auto-detection.
    /// </summary>
    public static void SetCustomDataRoot(string? path) => _customDataRoot = path;

    // ── API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the Yuzu data root. Uses the custom override if set,
    /// otherwise returns the first existing candidate or the default native path.
    /// </summary>
    public static string DataRoot
    {
        get
        {
            if (!string.IsNullOrEmpty(_customDataRoot))
                return _customDataRoot;
            foreach (var c in Candidates)
                if (Directory.Exists(c)) return c;
            return Candidates[0]; // fall back — will be created on first install
        }
    }

    /// <summary>
    /// Full path to the load directory for a given TitleID.
    /// e.g. ~/.local/share/yuzu/load/0100F2C0115B6000/
    /// </summary>
    public static string LoadDir(SwitchTitleId titleId) =>
        Path.Combine(DataRoot, "load", titleId.YuzuLoadComponent);

    /// <summary>
    /// Full path to the mod slot directory for a specific mod inside the load folder.
    /// e.g. ~/.local/share/yuzu/load/0100F2C0115B6000/MyMod/
    /// </summary>
    public static string ModSlotDir(SwitchTitleId titleId, string modName) =>
        Path.Combine(LoadDir(titleId), SanitiseSlot(modName));

    /// <summary>
    /// Validates that a destination path is inside the Yuzu load directory.
    /// Throws <see cref="InvalidOperationException"/> if path traversal is detected.
    /// </summary>
    public static void AssertInsideLoadDir(SwitchTitleId titleId, string destPath)
    {
        var loadDir = Path.GetFullPath(LoadDir(titleId));
        var full    = Path.GetFullPath(destPath);

        if (!full.StartsWith(loadDir, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Safety violation: path '{destPath}' escapes Yuzu load directory '{loadDir}'");
    }

    /// <summary>
    /// Sanitises a mod name for use as a directory name (removes characters illegal on Linux).
    /// </summary>
    private static string SanitiseSlot(string name) =>
        string.Concat(name.Select(c => c is '/' or '\0' ? '_' : c)).Trim();
}
