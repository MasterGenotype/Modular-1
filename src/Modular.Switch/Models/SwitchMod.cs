using System.Text.Json.Serialization;

namespace Modular.Switch.Models;

/// <summary>
/// Canonical representation of a discovered Switch mod, populated by
/// <c>SwitchModScanner</c> and enriched by <c>SwitchModNormalizer</c>.
/// </summary>
public sealed class SwitchMod
{
    // ── Identity ────────────────────────────────────────────────────────

    /// <summary>Unique key within Modular: "<TitleID>/<Category>/<Name>".</summary>
    [JsonPropertyName("mod_key")]
    public string ModKey { get; set; } = string.Empty;

    /// <summary>Human-readable mod name, derived from folder/archive name or manifest.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Mod version string (semver-ish or raw).</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    // ── Target ──────────────────────────────────────────────────────────

    /// <summary>The Switch TitleID this mod targets.</summary>
    [JsonPropertyName("title_id")]
    public string TitleId { get; set; } = string.Empty;

    /// <summary>Detected LayeredFS category.</summary>
    [JsonPropertyName("category")]
    public SwitchModCategory Category { get; set; } = SwitchModCategory.Unknown;

    // ── Source ──────────────────────────────────────────────────────────

    /// <summary>Absolute path to the archive file or extracted folder on disk.</summary>
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Whether <see cref="SourcePath"/> points to an already-extracted folder.</summary>
    [JsonPropertyName("is_extracted")]
    public bool IsExtracted { get; set; }

    // ── Integrity ────────────────────────────────────────────────────────

    /// <summary>SHA-256 of the source archive (or directory tree hash for extracted mods).</summary>
    [JsonPropertyName("source_hash")]
    public string SourceHash { get; set; } = string.Empty;

    // ── Dependency metadata ──────────────────────────────────────────────

    /// <summary>Mod keys this mod requires to be installed first.</summary>
    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    /// <summary>Mod keys that must NOT be installed alongside this one.</summary>
    [JsonPropertyName("conflicts")]
    public List<string> Conflicts { get; set; } = [];

    /// <summary>Explicit load priority — higher values load later (override earlier).</summary>
    [JsonPropertyName("load_order")]
    public int LoadOrder { get; set; }

    // ── Normalised internal path ─────────────────────────────────────────

    /// <summary>
    /// Modular-internal staging path:
    /// Domain/Switch/&lt;TitleID&gt;/&lt;Category&gt;/&lt;Name&gt;
    /// </summary>
    [JsonPropertyName("internal_path")]
    public string InternalPath { get; set; } = string.Empty;

    // ── Installation state ───────────────────────────────────────────────

    /// <summary>True when this mod is currently installed in Yuzu's load directory.</summary>
    [JsonPropertyName("is_installed")]
    public bool IsInstalled { get; set; }

    /// <summary>UTC timestamp of last successful install.</summary>
    [JsonPropertyName("installed_at")]
    public DateTime? InstalledAt { get; set; }

    /// <summary>Hash recorded at install time — compared on re-install for idempotency.</summary>
    [JsonPropertyName("installed_hash")]
    public string InstalledHash { get; set; } = string.Empty;

    // ── BNP options ─────────────────────────────────────────────────────

    /// <summary>
    /// Available BNP option groups, populated by the scanner when the source
    /// is a BNP archive with an options/ directory. Null for non-BNP mods.
    /// </summary>
    [JsonPropertyName("bnp_options")]
    public BnpOptionsContainer? BnpOptions { get; set; }

    /// <summary>
    /// User-selected option folder names (from <see cref="BnpOption.Folder"/>).
    /// Empty list means "no options selected" (only base content is installed).
    /// </summary>
    [JsonPropertyName("selected_bnp_options")]
    public List<string> SelectedBnpOptions { get; set; } = [];

    /// <summary>True when this mod is a BNP with options that need user selection.</summary>
    [JsonIgnore]
    public bool HasBnpOptions => BnpOptions?.HasOptions == true;

    // ── Snapshot ─────────────────────────────────────────────────────────

    /// <summary>Pre-install snapshot directory (for rollback).</summary>
    [JsonPropertyName("snapshot_path")]
    public string SnapshotPath { get; set; } = string.Empty;
}
