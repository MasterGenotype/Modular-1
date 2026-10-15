using System.Text.Json.Serialization;

namespace Modular.Core.Configuration;

/// <summary>
/// Settings for the Tears of the Kingdom merge installer, the <c>switch_totk</c> section
/// of <c>config.json</c>. Mirrors TKMM v2.0.0-rc1's <c>TkConfig</c> (game dump) plus the
/// merge, emulator and 7-Zip settings from its <c>Config</c>.
/// </summary>
public class TotkSettings
{
    /// <summary>The <see cref="PreferredGameVersion"/> that lets the ROM provider choose.</summary>
    public const string AutoGameVersion = "Auto";

    /// <summary>Choices for <see cref="PreferredGameVersion"/>, as rc1 lists them.</summary>
    public static readonly IReadOnlyList<string> GameVersions =
        [AutoGameVersion, "1.4.2", "1.4.1", "1.4.0", "1.2.1", "1.2.0", "1.1.2", "1.1.1", "1.1.0"];

    /// <summary>Choices for <see cref="GameLanguage"/> with display names, as rc1 lists them.</summary>
    public static readonly IReadOnlyList<(string Value, string DisplayName)> GameLanguages =
    [
        ("USen", "American English"),
        ("EUen", "British English"),
        ("JPja", "日本語"),
        ("EUfr", "Français (France)"),
        ("USfr", "Français Canadien"),
        ("USes", "Español (América)"),
        ("EUes", "Español (Europa)"),
        ("USpt", "Português Brasileiro (1.4.0+)"),
        ("EUde", "Deutsch"),
        ("EUnl", "Nederlands"),
        ("EUit", "Italiano"),
        ("EUru", "Русский"),
        ("KRko", "한국어"),
        ("CNzh", "简体中文"),
        ("TWzh", "繁體中文 (台灣)")
    ];

    /// <summary>
    /// Game version to look for when the configured SD card holds more than one;
    /// <see cref="AutoGameVersion"/> to let the ROM provider choose.
    /// </summary>
    [JsonPropertyName("preferred_game_version")]
    public string PreferredGameVersion { get; set; } = AutoGameVersion;

    /// <summary>Folder containing the dumped Switch prod/title keys.</summary>
    [JsonPropertyName("keys_folder_path")]
    public string? KeysFolderPath { get; set; }

    /// <summary>Dumped base game files (.xci or .nsp) or split folders.</summary>
    [JsonPropertyName("packaged_base_game_paths")]
    public List<string> PackagedBaseGamePaths { get; set; } = [];

    /// <summary>Dumped game update files (.nsp).</summary>
    [JsonPropertyName("packaged_update_paths")]
    public List<string> PackagedUpdatePaths { get; set; } = [];

    /// <summary>
    /// Root of an SD card or emuMMC with the base game or update installed under
    /// <c>Nintendo/Contents</c>.
    /// </summary>
    [JsonPropertyName("sd_card_root_path")]
    public string? SdCardRootPath { get; set; }

    /// <summary>Extracted RomFS dump folders.</summary>
    [JsonPropertyName("game_dump_folder_paths")]
    public List<string> GameDumpFolderPaths { get; set; } = [];

    /// <summary>Virtual NAND folders.</summary>
    [JsonPropertyName("nand_folder_paths")]
    public List<string> NandFolderPaths { get; set; } = [];

    /// <summary>Language the MALS merger builds its archive for, e.g. <c>USen</c>.</summary>
    [JsonPropertyName("game_language")]
    public string GameLanguage { get; set; } = "USen";

    /// <summary>
    /// Folder the merged mod is written to. Empty for the default,
    /// <c>~/.config/Modular/switch/totk/merged</c>.
    /// </summary>
    [JsonPropertyName("merge_output")]
    public string? MergeOutput { get; set; }

    /// <summary>
    /// Rename the merged <c>romfs</c> to <c>romfslite</c> after merging (rc1's Atmosphère
    /// 20.0 support for the TotK Optimizer).
    /// </summary>
    [JsonPropertyName("use_romfslite")]
    public bool UseRomfslite { get; set; }

    /// <summary>
    /// The emulator's executable. Selects Ryujinx or a Yuzu-family emulator when reading
    /// the game from the emulator and deploying to it; empty to use Yuzu's default paths.
    /// </summary>
    [JsonPropertyName("emulator_path")]
    public string? EmulatorPath { get; set; }

    /// <summary>
    /// The 7-Zip executable used for faster .7z extraction. Found on <c>PATH</c> when
    /// empty.
    /// </summary>
    [JsonPropertyName("seven_zip_path")]
    public string? SevenZipPath { get; set; }
}
