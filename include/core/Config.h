#ifndef MODULAR_CONFIG_H
#define MODULAR_CONFIG_H

#include <string>
#include <vector>
#include <filesystem>

namespace modular {

/**
 * @brief Configuration struct for Modular application
 * 
 * Design: Struct (not singleton) for better testability.
 * Usage: Load once in main(), pass const& to functions that need it.
 */
struct Config {
    // NexusMods settings
    std::string nexus_api_key;
    std::vector<std::string> default_categories = {"main", "optional"};
    
    // GameBanana settings
    std::string gamebanana_user_id;
    
    // Storage paths
    std::filesystem::path mods_directory;
    
    // Preferences
    bool auto_rename = true;
    bool organize_by_category = true;   // Organize mods into category subdirectories
    bool verify_downloads = false;
    int max_concurrent_downloads = 1;
    bool verbose = false;
    
    // Tracking validation (web scraping)
    bool validate_tracking = false;  // Validate API tracking against web tracking center
    std::string cookie_file = "~/Documents/cookies.txt";  // Cookie file for web validation
};

/**
 * @brief Get the default config file path
 * @return Path to ~/.config/Modular/config.json
 */
std::filesystem::path defaultConfigPath();

/**
 * @brief Load configuration from file
 * 
 * Loads from JSON file and merges with environment variables.
 * Environment variables take precedence over file values.
 * 
 * Precedence order (highest to lowest):
 * 1. Environment variables (API_KEY, GB_USER_ID)
 * 2. Config file values
 * 3. Default values in Config struct
 * 
 * @param path Path to config file (default: ~/.config/Modular/config.json)
 * @return Loaded and validated config
 * @throws ConfigException if required fields are missing or invalid
 */
Config loadConfig(const std::filesystem::path& path = defaultConfigPath());

/**
 * @brief Save configuration to file
 * 
 * Writes config to JSON file with pretty formatting.
 * Creates parent directories if they don't exist.
 * 
 * @param cfg Config to save
 * @param path Path to config file (default: ~/.config/Modular/config.json)
 * @throws FileSystemException if file cannot be written
 */
void saveConfig(const Config& cfg, 
                const std::filesystem::path& path = defaultConfigPath());

/**
 * @brief Validate configuration
 * 
 * Checks that required fields are set and values are valid.
 * Called automatically by loadConfig().
 * 
 * @param cfg Config to validate
 * @throws ConfigException if validation fails
 */
void validateConfig(const Config& cfg);

} // namespace modular

#endif // MODULAR_CONFIG_H
