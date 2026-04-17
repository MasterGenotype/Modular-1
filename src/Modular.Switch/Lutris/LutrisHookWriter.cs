using Modular.Switch.Models;

namespace Modular.Switch.Lutris;

/// <summary>
/// Generates and manages Lutris pre-launch hook scripts for Switch games running via Yuzu.
///
/// The generated shell script is placed at:
///   ~/.config/Modular/switch_hooks/<TitleID>_prelaunchi.sh
///
/// Lutris integration:
///   In the Lutris game config (System → Pre-launch script) point to the hook path.
///   If "--runner lutris" is passed to <c>modular install</c>, the hook is wired
///   automatically into the detected Lutris game config YAML.
///
/// The hook script:
///   1. Verifies that Yuzu's load directory exists for the TitleID.
///   2. Optionally re-runs "modular switch install --game <TitleID>" to ensure
///      the latest mod set is applied before the game starts.
///   3. Logs activity to ~/.local/share/Modular/switch_hook.log.
/// </summary>
public static class LutrisHookWriter
{
    private static string HookDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "switch_hooks");

    public static string HookPath(SwitchTitleId titleId) =>
        Path.Combine(HookDir, $"{titleId.Value}_prelaunch.sh");

    // ── Write hook ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes (or overwrites) the pre-launch hook script for <paramref name="titleId"/>.
    /// Passes the ordered list of mod names so the script can print a summary.
    /// </summary>
    public static async Task WriteAsync(
        SwitchTitleId titleId,
        IReadOnlyList<string> installedModNames,
        bool autoApplyOnLaunch = false,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(HookDir);

        var loadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "yuzu", "load", titleId.Value);

        var modList = installedModNames.Count > 0
            ? string.Join("\n", installedModNames.Select(n => $"  # - {n}"))
            : "  # (no mods installed)";

        var autoApplyBlock = autoApplyOnLaunch
            ? """
              echo "[modular] Re-applying mod set before launch..."
              modular switch install --game "$TITLE_ID" 2>>"$LOG" || {
                  echo "[modular] WARNING: auto-apply failed; launching anyway" | tee -a "$LOG"
              }
              """
            : "# auto-apply disabled; run: modular switch install --game \"$TITLE_ID\" to update mods";

        var script = $"""
#!/usr/bin/env bash
# Modular Switch pre-launch hook — auto-generated; do not edit manually
# TitleID : {titleId.Value}
# Managed mods:
{modList}

set -euo pipefail

TITLE_ID="{titleId.Value}"
YUZU_LOAD_DIR="{loadDir}"
LOG="$HOME/.local/share/Modular/switch_hook.log"

mkdir -p "$(dirname "$LOG")"
echo "[modular] Pre-launch hook triggered for $TITLE_ID at $(date -u)" >> "$LOG"

# Verify Yuzu load directory
if [ ! -d "$YUZU_LOAD_DIR" ]; then
    echo "[modular] WARNING: Yuzu load directory missing: $YUZU_LOAD_DIR" | tee -a "$LOG"
fi

{autoApplyBlock}

echo "[modular] Hook complete — launching game" >> "$LOG"
""";

        await File.WriteAllTextAsync(HookPath(titleId), script, ct);

        // Make executable (Linux only)
        var fi = new System.IO.FileInfo(HookPath(titleId));
        fi.UnixFileMode =
            UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    }

    // ── Lutris YAML integration ───────────────────────────────────────────

    /// <summary>
    /// Attempts to locate the Lutris YAML config for a game whose slug or name
    /// contains <paramref name="titleId"/> and injects the pre-launch script path.
    ///
    /// Lutris game configs live at ~/.config/lutris/games/*.yml.
    /// We look for a <c>game</c> block whose <c>exe</c> or <c>name</c> references
    /// Yuzu and whose configured game path or title matches the TitleID.
    /// </summary>
    public static async Task<bool> TryInjectLutrisConfigAsync(
        SwitchTitleId titleId,
        CancellationToken ct = default)
    {
        var lutrisGamesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "lutris", "games");

        if (!Directory.Exists(lutrisGamesDir)) return false;

        foreach (var ymlPath in Directory.EnumerateFiles(lutrisGamesDir, "*.yml"))
        {
            ct.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(ymlPath, ct);

            // Heuristic: YAML must mention yuzu and the TitleID (or game name near Yuzu entry)
            if (!content.Contains("yuzu", StringComparison.OrdinalIgnoreCase)) continue;
            if (!content.Contains(titleId.Value, StringComparison.OrdinalIgnoreCase)) continue;

            var hookPath = HookPath(titleId);

            // If already injected, skip
            if (content.Contains(hookPath, StringComparison.Ordinal)) continue;

            // Inject prelaunch_command into the system block (simple line-based approach)
            var lines = (await File.ReadAllLinesAsync(ymlPath, ct)).ToList();
            var systemIdx = lines.FindIndex(l => l.TrimStart().StartsWith("system:", StringComparison.Ordinal));

            if (systemIdx < 0)
            {
                // Append a system block
                lines.Add("system:");
                lines.Add($"  prelaunch_command: {hookPath}");
            }
            else
            {
                // Insert prelaunch_command right after "system:"
                var indent = new string(' ', lines[systemIdx].Length - lines[systemIdx].TrimStart().Length + 2);
                lines.Insert(systemIdx + 1, $"{indent}prelaunch_command: {hookPath}");
            }

            await File.WriteAllLinesAsync(ymlPath, lines, ct);
            return true;
        }

        return false;
    }
}
