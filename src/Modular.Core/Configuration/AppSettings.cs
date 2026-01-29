using System.Text.Json.Serialization;

namespace Modular.Core.Configuration;

/// <summary>
/// Application settings for Modular.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// NexusMods API key.
    /// </summary>
    [JsonPropertyName("nexus_api_key")]
    public string NexusApiKey { get; set; } = string.Empty;

    /// <summary>
    /// GameBanana user ID.
    /// </summary>
    [JsonPropertyName("gamebanana_user_id")]
    public string GameBananaUserId { get; set; } = string.Empty;

    /// <summary>
    /// Base directory for storing downloaded mods.
    /// </summary>
    [JsonPropertyName("mods_directory")]
    public string ModsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Games", "Mods-Lists");

    /// <summary>
    /// Default file categories to download (e.g., "main", "optional").
    /// </summary>
    [JsonPropertyName("default_categories")]
    public List<string> DefaultCategories { get; set; } = ["main", "optional"];

    /// <summary>
    /// Whether to automatically rename mod folders to human-readable names.
    /// </summary>
    [JsonPropertyName("auto_rename")]
    public bool AutoRename { get; set; } = true;

    /// <summary>
    /// Whether to organize mods into category subdirectories.
    /// </summary>
    [JsonPropertyName("organize_by_category")]
    public bool OrganizeByCategory { get; set; } = true;

    /// <summary>
    /// Whether to verify downloads using MD5 checksums.
    /// </summary>
    [JsonPropertyName("verify_downloads")]
    public bool VerifyDownloads { get; set; } = false;

    /// <summary>
    /// Whether to validate API tracking against web tracking center.
    /// </summary>
    [JsonPropertyName("validate_tracking")]
    public bool ValidateTracking { get; set; } = false;

    /// <summary>
    /// Maximum number of concurrent downloads.
    /// </summary>
    [JsonPropertyName("max_concurrent_downloads")]
    public int MaxConcurrentDownloads { get; set; } = 1;

    /// <summary>
    /// Enable verbose logging output.
    /// </summary>
    [JsonPropertyName("verbose")]
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Path to cookie file for web scraping authentication.
    /// </summary>
    [JsonPropertyName("cookie_file")]
    public string CookieFile { get; set; } = "~/Documents/cookies.txt";

    /// <summary>
    /// Path to the download database file.
    /// </summary>
    [JsonPropertyName("database_path")]
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the rate limiter state file.
    /// </summary>
    [JsonPropertyName("rate_limit_state_path")]
    public string RateLimitStatePath { get; set; } = string.Empty;
}
