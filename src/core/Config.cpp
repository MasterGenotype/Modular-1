#include "Config.h"
#include "Exceptions.h"
#include <nlohmann/json.hpp>
#include <fstream>
#include <cstdlib>

using json = nlohmann::json;
namespace fs = std::filesystem;

namespace modular {

fs::path defaultConfigPath() {
    const char* home = std::getenv("HOME");
    if (!home) {
        throw ConfigException("HOME environment variable not set");
    }
    
    return fs::path(home) / ".config" / "Modular" / "config.json";
}

Config loadConfig(const fs::path& path) {
    Config cfg;
    
    // Set default mods directory
    const char* home = std::getenv("HOME");
    if (home) {
        cfg.mods_directory = fs::path(home) / "Games" / "Mods-Lists";
    }
    
    // Try to load from file
    if (fs::exists(path)) {
        try {
            std::ifstream file(path);
            if (!file) {
                throw FileSystemException("Failed to open config file", path.string());
            }
            
            json config_json = json::parse(file);
            
            // Load NexusMods settings
            if (config_json.contains("nexus_api_key")) {
                cfg.nexus_api_key = config_json["nexus_api_key"].get<std::string>();
            }
            
            if (config_json.contains("default_categories")) {
                cfg.default_categories = config_json["default_categories"]
                    .get<std::vector<std::string>>();
            }
            
            // Load GameBanana settings
            if (config_json.contains("gamebanana_user_id")) {
                cfg.gamebanana_user_id = config_json["gamebanana_user_id"]
                    .get<std::string>();
            }
            
            // Load storage paths
            if (config_json.contains("mods_directory")) {
                cfg.mods_directory = config_json["mods_directory"]
                    .get<std::string>();
            }
            
            // Load preferences
            if (config_json.contains("auto_rename")) {
                cfg.auto_rename = config_json["auto_rename"].get<bool>();
            }
            
            if (config_json.contains("organize_by_category")) {
                cfg.organize_by_category = config_json["organize_by_category"].get<bool>();
            }
            
            if (config_json.contains("verify_downloads")) {
                cfg.verify_downloads = config_json["verify_downloads"].get<bool>();
            }
            
            if (config_json.contains("max_concurrent_downloads")) {
                cfg.max_concurrent_downloads = config_json["max_concurrent_downloads"]
                    .get<int>();
            }
            
            if (config_json.contains("verbose")) {
                cfg.verbose = config_json["verbose"].get<bool>();
            }
            
            // Load tracking validation settings
            if (config_json.contains("validate_tracking")) {
                cfg.validate_tracking = config_json["validate_tracking"].get<bool>();
            }
            
            if (config_json.contains("cookie_file")) {
                cfg.cookie_file = config_json["cookie_file"].get<std::string>();
            }
            
        } catch (const json::exception& e) {
            throw ParseException("Failed to parse config file: " + std::string(e.what()),
                                path.string());
        }
    }
    
    // Override with environment variables (highest precedence)
    const char* api_key_env = std::getenv("API_KEY");
    if (api_key_env && std::strlen(api_key_env) > 0) {
        cfg.nexus_api_key = api_key_env;
    }
    
    const char* gb_user_id_env = std::getenv("GB_USER_ID");
    if (gb_user_id_env && std::strlen(gb_user_id_env) > 0) {
        cfg.gamebanana_user_id = gb_user_id_env;
    }
    
    // If API key is still empty, try legacy location
    if (cfg.nexus_api_key.empty() && home) {
        fs::path legacy_api_key_file = fs::path(home) / ".config" / "Modular" / "api_key.txt";
        if (fs::exists(legacy_api_key_file)) {
            std::ifstream file(legacy_api_key_file);
            std::string key((std::istreambuf_iterator<char>(file)),
                           std::istreambuf_iterator<char>());
            
            // Trim whitespace
            key.erase(0, key.find_first_not_of(" \t\r\n"));
            key.erase(key.find_last_not_of(" \t\r\n") + 1);
            
            if (!key.empty()) {
                cfg.nexus_api_key = key;
            }
        }
    }
    
    // Validate before returning
    // Note: We don't validate here to allow partial configs
    // Validation happens when features are used
    
    return cfg;
}

void saveConfig(const Config& cfg, const fs::path& path) {
    try {
        // Create parent directories
        fs::create_directories(path.parent_path());
        
        // Build JSON
        json config_json;
        
        config_json["nexus_api_key"] = cfg.nexus_api_key;
        config_json["default_categories"] = cfg.default_categories;
        config_json["gamebanana_user_id"] = cfg.gamebanana_user_id;
        config_json["mods_directory"] = cfg.mods_directory.string();
        config_json["auto_rename"] = cfg.auto_rename;
        config_json["organize_by_category"] = cfg.organize_by_category;
        config_json["verify_downloads"] = cfg.verify_downloads;
        config_json["max_concurrent_downloads"] = cfg.max_concurrent_downloads;
        config_json["verbose"] = cfg.verbose;
        config_json["validate_tracking"] = cfg.validate_tracking;
        config_json["cookie_file"] = cfg.cookie_file;
        
        // Write atomically (write to temp, then rename)
        fs::path temp_path = path.string() + ".tmp";
        
        std::ofstream file(temp_path);
        if (!file) {
            throw FileSystemException("Failed to create temp config file",
                                     temp_path.string());
        }
        
        file << config_json.dump(2);  // Pretty print with 2-space indent
        file.close();
        
        if (!file) {
            throw FileSystemException("Failed to write config file",
                                     temp_path.string());
        }
        
        // Atomic rename
        fs::rename(temp_path, path);
        
    } catch (const fs::filesystem_error& e) {
        throw FileSystemException("Filesystem error while saving config: " +
                                 std::string(e.what()), path.string());
    }
}

void validateConfig(const Config& cfg) {
    // Validate max_concurrent_downloads
    if (cfg.max_concurrent_downloads < 1 || cfg.max_concurrent_downloads > 10) {
        throw ConfigException("max_concurrent_downloads must be between 1 and 10");
    }
    
    // Note: We don't require API keys here because:
    // - User might only use GameBanana (doesn't need NexusMods key)
    // - User might only use NexusMods (doesn't need GameBanana ID)
    // Validation happens when features are actually used
}

} // namespace modular
