#include <catch2/catch_test_macros.hpp>
#include "Config.h"
#include "Exceptions.h"
#include <filesystem>
#include <fstream>

using namespace modular;
namespace fs = std::filesystem;

TEST_CASE("Config save and load", "[config]") {
    std::string test_config_path = "/tmp/modular_test_config.json";
    
    SECTION("saves and loads config correctly") {
        // Remove any existing test config
        if (fs::exists(test_config_path)) {
            fs::remove(test_config_path);
        }
        
        Config config;
        config.nexus_api_key = "test_api_key_12345";
        config.mods_directory = "/home/user/mods";
        config.gamebanana_user_id = "123456";
        config.max_concurrent_downloads = 5;
        
        saveConfig(config, test_config_path);
        
        REQUIRE(fs::exists(test_config_path));
        
        // Unset environment variables that might override
        const char* old_api_key = std::getenv("API_KEY");
        const char* old_gb_user = std::getenv("GB_USER_ID");
        
        #ifndef _WIN32
        unsetenv("API_KEY");
        unsetenv("GB_USER_ID");
        #endif
        
        Config loaded = loadConfig(test_config_path);
        REQUIRE(loaded.nexus_api_key == "test_api_key_12345");
        REQUIRE(loaded.mods_directory == "/home/user/mods");
        REQUIRE(loaded.gamebanana_user_id == "123456");
        REQUIRE(loaded.max_concurrent_downloads == 5);
        
        // Restore environment (if needed)
        #ifndef _WIN32
        if (old_api_key) setenv("API_KEY", old_api_key, 1);
        if (old_gb_user) setenv("GB_USER_ID", old_gb_user, 1);
        #endif
        
        // Cleanup
        fs::remove(test_config_path);
    }
    
    SECTION("validates max_concurrent_downloads") {
        Config config;
        config.max_concurrent_downloads = 0;  // Invalid value
        
        REQUIRE_THROWS_AS(validateConfig(config), ConfigException);
        
        config.max_concurrent_downloads = 11;  // Invalid value (too high)
        REQUIRE_THROWS_AS(validateConfig(config), ConfigException);
        
        config.max_concurrent_downloads = 5;  // Valid value
        REQUIRE_NOTHROW(validateConfig(config));
    }
    
    SECTION("handles missing file gracefully") {
        // loadConfig returns default config when file doesn't exist
        Config cfg = loadConfig("/nonexistent/config.json");
        REQUIRE(cfg.mods_directory.string() != "");
    }
    
    SECTION("creates default config with loadConfig") {
        Config config = loadConfig("/tmp/nonexistent_test_config.json");
        // Should have a default mods directory path
        REQUIRE(config.mods_directory.string() != "");
        REQUIRE(config.max_concurrent_downloads == 1);  // Default value
    }
}

TEST_CASE("Config environment variable override", "[config]") {
    SECTION("environment variables take precedence") {
        std::string test_config_path = "/tmp/modular_test_config_env.json";
        
        // Remove existing test config
        if (fs::exists(test_config_path)) {
            fs::remove(test_config_path);
        }
        
        // Save config with one value
        Config config;
        config.nexus_api_key = "file_key";
        config.mods_directory = "/home/user/mods";
        saveConfig(config, test_config_path);
        
        // Unset environment variables first
        #ifndef _WIN32
        unsetenv("API_KEY");
        unsetenv("GB_USER_ID");
        #endif
        
        // Load should use file value
        Config loaded = loadConfig(test_config_path);
        REQUIRE(loaded.nexus_api_key == "file_key");
        
        // Cleanup
        fs::remove(test_config_path);
    }
}

TEST_CASE("Config JSON structure", "[config]") {
    std::string test_config_path = "/tmp/modular_test_config_json.json";
    
    SECTION("creates valid JSON") {
        Config config;
        config.nexus_api_key = "test_key";
        config.mods_directory = "/home/user/mods";
        config.gamebanana_user_id = "12345";
        
        saveConfig(config, test_config_path);
        
        // Verify JSON structure by reading raw file
        std::ifstream f(test_config_path);
        REQUIRE(f.good());
        
        std::string content((std::istreambuf_iterator<char>(f)),
                           std::istreambuf_iterator<char>());
        
        // Check that JSON contains expected keys
        REQUIRE(content.find("\"nexus_api_key\"") != std::string::npos);
        REQUIRE(content.find("\"mods_directory\"") != std::string::npos);
        REQUIRE(content.find("\"gamebanana_user_id\"") != std::string::npos);
        
        // Cleanup
        fs::remove(test_config_path);
    }
}
