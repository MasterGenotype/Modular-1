using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modular.Switch.Models;

/// <summary>
/// Persisted state file for all Switch mods known to Modular.
/// Stored at <c>~/.config/Modular/switch_state.json</c>.
/// </summary>
public sealed class SwitchInstallState
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Map of ModKey → SwitchMod for every mod Modular knows about.</summary>
    [JsonPropertyName("mods")]
    public Dictionary<string, SwitchMod> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path this state was loaded from (not serialised).</summary>
    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    // ── Factory / persistence ────────────────────────────────────────────

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "Modular", "switch_state.json");

    public static async Task<SwitchInstallState> LoadAsync(
        string? path = null, CancellationToken ct = default)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new SwitchInstallState { FilePath = path };

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<SwitchInstallState>(stream, _json, ct)
                    ?? new SwitchInstallState();
        state.FilePath = path;
        return state;
    }

    public async Task SaveAsync(string? path = null, CancellationToken ct = default)
    {
        path ??= (!string.IsNullOrEmpty(FilePath) ? FilePath : DefaultPath);
        FilePath = path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, this, _json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    public void Upsert(SwitchMod mod) => Mods[mod.ModKey] = mod;

    public bool TryGet(string modKey, out SwitchMod mod) => Mods.TryGetValue(modKey, out mod!);

    public IEnumerable<SwitchMod> ForTitle(SwitchTitleId titleId) =>
        Mods.Values.Where(m =>
            m.TitleId.Equals(titleId.Value, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<SwitchMod> Installed =>
        Mods.Values.Where(m => m.IsInstalled);
}
