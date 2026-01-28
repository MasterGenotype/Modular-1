#ifndef NEXUSMODS_H
#define NEXUSMODS_H

#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <nlohmann/json.hpp>
#include <sstream>
#include <string>
#include <thread>
#include <vector>
#include <functional>
#include "HttpClient.h"
#include "Config.h"

namespace fs = std::filesystem;
using json = nlohmann::json;

// Progress callback for downloads (replaces LiveUI dependency)
using DownloadProgressCallback = std::function<void(const std::string& status, int completed, int total)>;

/**
 * @brief Represents a tracked mod with domain information
 */
struct TrackedMod {
    int mod_id;
    std::string domain_name;
    std::string name;
};

// Function declarations

/**
 * @brief Perform HTTP GET request
 * @param url URL to request
 * @param headers HTTP headers to include
 * @return HttpResponse with status, body, and headers
 */
modular::HttpResponse http_get(const std::string& url, const std::vector<std::string>& headers);

/**
 * @brief Get all tracked mods for the user with domain information
 * @param config Configuration containing API key
 * @return Vector of tracked mods with domain info
 */
std::vector<TrackedMod> get_tracked_mods_with_domain(const modular::Config& config);

/**
 * @brief Get tracked mods (legacy - returns only IDs)
 * @param config Configuration containing API key
 * @return Vector of mod IDs
 */
std::vector<int> get_tracked_mods(const modular::Config& config);

/**
 * @brief Get tracked mods for a specific game domain (optimized)
 * @param game_domain Game domain name (e.g., "stardewvalley")
 * @param config Configuration containing API key
 * @return Vector of mod IDs for the specified domain
 */
std::vector<int> get_tracked_mods_for_domain(const std::string& game_domain, const modular::Config& config);

/**
 * @brief Verify that a mod is in the user's tracked list
 * @param game_domain Game domain name
 * @param mod_id Mod ID to verify
 * @param config Configuration containing API key
 * @return true if mod is tracked by the user, false otherwise
 */
bool is_mod_tracked(const std::string& game_domain, int mod_id, const modular::Config& config);

/**
 * @brief Get user info including user ID from API key
 * @param config Configuration containing API key
 * @return JSON response with user information
 */
std::string get_user_info(const modular::Config& config);
std::map<int, std::vector<int>> get_file_ids(const std::vector<int>& mod_ids, 
                                            const std::string& game_domain,
                                            const modular::Config& config,
                                            const std::string& filter_categories = "");
std::map<std::pair<int, int>, std::string> generate_download_links(const std::map<int, std::vector<int>>& mod_file_ids, 
                                                                    const std::string& game_domain,
                                                                    const modular::Config& config);
void save_download_links(const std::map<std::pair<int, int>, std::string>& download_links, 
                        const std::string& game_domain,
                        const modular::Config& config);

// Download files with optional progress callback (no UI dependency)
void download_files(const std::string& game_domain, 
                   const modular::Config& config,
                   DownloadProgressCallback progress_cb = nullptr,
                   bool dry_run = false,
                   bool force = false);


#endif // NEXUSMODS_H
