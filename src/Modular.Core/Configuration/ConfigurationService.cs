using System.Text.Json;
using Modular.Core.Exceptions;
using Modular.Core.Utilities;

namespace Modular.Core.Configuration;

/// <summary>
/// Service for loading and saving application configuration.
/// </summary>
public class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Gets the default configuration file path.
    /// </summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "config.json");

    /// <summary>
    /// Gets the default database path.
    /// </summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "downloads.json");

    /// <summary>
    /// Gets the default rate limit state path.
    /// </summary>
    public static string DefaultRateLimitStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "rate_limit_state.json");

    /// <summary>
    /// Gets the default metadata cache path.
    /// </summary>
    public static string DefaultMetadataCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Modular", "metadata_cache.json");

    /// <summary>
    /// Loads configuration from file and environment variables.
    /// Environment variables take precedence over file values.
    /// </summary>
    /// <param name="path">Path to config file (defaults to ~/.config/Modular/config.json)</param>
    /// <returns>Loaded configuration</returns>
    public async Task<AppSettings> LoadAsync(string? path = null)
    {
        path ??= DefaultConfigPath;
        var settings = new AppSettings();

        // Load from file if exists
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                throw new ConfigException($"Failed to parse config file: {ex.Message}", ex)
                {
                    Context = path
                };
            }
        }

        // Apply environment variable overrides
        ApplyEnvironmentOverrides(settings);

        // Set default paths if not specified
        if (string.IsNullOrEmpty(settings.DatabasePath))
            settings.DatabasePath = DefaultDatabasePath;
        if (string.IsNullOrEmpty(settings.RateLimitStatePath))
            settings.RateLimitStatePath = DefaultRateLimitStatePath;
        if (string.IsNullOrEmpty(settings.MetadataCachePath))
            settings.MetadataCachePath = DefaultMetadataCachePath;

        // Expand ~ in paths
        settings.ModsDirectory = FileUtils.ExpandPath(settings.ModsDirectory);
        settings.CookieFile = FileUtils.ExpandPath(settings.CookieFile);
        settings.DatabasePath = FileUtils.ExpandPath(settings.DatabasePath);
        settings.RateLimitStatePath = FileUtils.ExpandPath(settings.RateLimitStatePath);
        settings.MetadataCachePath = FileUtils.ExpandPath(settings.MetadataCachePath);

        return settings;
    }

    /// <summary>
    /// Saves configuration to file.
    /// </summary>
    /// <param name="settings">Settings to save</param>
    /// <param name="path">Path to config file (defaults to ~/.config/Modular/config.json)</param>
    public async Task SaveAsync(AppSettings settings, string? path = null)
    {
        path ??= DefaultConfigPath;

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        catch (IOException ex)
        {
            throw new FileSystemException($"Failed to save config file: {ex.Message}", ex)
            {
                FilePath = path
            };
        }
    }

    /// <summary>
    /// Validates that required configuration values are set.
    /// </summary>
    /// <param name="settings">Settings to validate</param>
    /// <param name="requireNexusKey">Whether NexusMods API key is required</param>
    /// <param name="requireGameBananaId">Whether GameBanana user ID is required</param>
    public void Validate(AppSettings settings, bool requireNexusKey = false, bool requireGameBananaId = false)
    {
        if (requireNexusKey && string.IsNullOrWhiteSpace(settings.NexusApiKey))
        {
            throw new ConfigException(
                "NexusMods API key is required. Set NEXUS_API_KEY environment variable or add 'nexus_api_key' to config file.",
                "nexus_api_key");
        }

        if (requireGameBananaId && string.IsNullOrWhiteSpace(settings.GameBananaUserId))
        {
            throw new ConfigException(
                "GameBanana user ID is required. Set GB_USER_ID environment variable or add 'gamebanana_user_id' to config file.",
                "gamebanana_user_id");
        }

        if (settings.MaxConcurrentDownloads < 1)
        {
            throw new ConfigException(
                "max_concurrent_downloads must be at least 1.",
                "max_concurrent_downloads");
        }
    }

    private static void ApplyEnvironmentOverrides(AppSettings settings)
    {
        // NEXUS_API_KEY or API_KEY
        var nexusKey = Environment.GetEnvironmentVariable("NEXUS_API_KEY")
                    ?? Environment.GetEnvironmentVariable("API_KEY");
        if (!string.IsNullOrWhiteSpace(nexusKey))
            settings.NexusApiKey = nexusKey;

        // GB_USER_ID
        var gbUserId = Environment.GetEnvironmentVariable("GB_USER_ID");
        if (!string.IsNullOrWhiteSpace(gbUserId))
            settings.GameBananaUserId = gbUserId;

        // MODULAR_MODS_DIR
        var modsDir = Environment.GetEnvironmentVariable("MODULAR_MODS_DIR");
        if (!string.IsNullOrWhiteSpace(modsDir))
            settings.ModsDirectory = modsDir;

        // MODULAR_VERBOSE
        var verbose = Environment.GetEnvironmentVariable("MODULAR_VERBOSE");
        if (!string.IsNullOrWhiteSpace(verbose))
            settings.Verbose = verbose == "1" || verbose.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
