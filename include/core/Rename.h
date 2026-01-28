#ifndef RENAME_H
#define RENAME_H

#include <filesystem>
#include <string>
#include <vector>
#include <map>
#include "Config.h"

// Given a base folder (e.g. "~/Games/Mods-Lists"), returns a list of game domains.
std::vector<std::string> getGameDomainNames(const std::filesystem::path& modsListsDir);

// Given a game domain folder, returns a list of mod IDs (subdirectory names).
std::vector<std::string> getModIDs(const std::filesystem::path& gameDomainPath);

// Using the game domain and mod ID, performs a GET request to the Nexus Mods API.
// (For example: https://api.nexusmods.com/v1/games/<game_domain>/mods/<mod_id>)
// Returns the JSON response as a string.
std::string fetchModName(const std::string& gameDomain, const std::string& modID, 
                        const modular::Config& config);

// Given the JSON response from the API, extracts the mod name.
// (This function assumes that the JSON object has a "name" field.)
std::string extractModName(const std::string& jsonResponse);

// Given a target directory and a source directory, recursively copies (merges) the files.
void combineDirectories(const std::filesystem::path& target, const std::filesystem::path& source);

/**
 * @brief Reorganizes, sorts, and renames mods in a game domain
 * 
 * This function:
 * 1. Fetches mod information from NexusMods API
 * 2. Optionally organizes mods into category subdirectories
 * 3. Renames folders from numeric IDs to human-readable names
 * 4. Ensures alphabetical sorting
 * 
 * @param gameDomainPath Path to game domain directory (e.g., ~/Games/Mods-Lists/skyrimspecialedition)
 * @param config Configuration containing API key
 * @param organizeByCategory If true, creates category subdirectories
 * @return Number of mods successfully processed
 */
int reorganizeAndRenameMods(const std::filesystem::path& gameDomainPath,
                            const modular::Config& config,
                            bool organizeByCategory = true);

/**
 * @brief Fetches full mod information including categories from NexusMods API
 * @param gameDomain Game domain name
 * @param modID Mod ID
 * @param config Configuration containing API key
 * @return JSON response with mod details
 */
std::string fetchModInfo(const std::string& gameDomain, 
                         const std::string& modID,
                         const modular::Config& config);

/**
 * @brief Fetches game categories from NexusMods API
 * @param gameDomain Game domain name
 * @param config Configuration containing API key
 * @return Map of category_id -> category_name
 */
std::map<int, std::string> fetchGameCategories(const std::string& gameDomain,
                                                const modular::Config& config);

/**
 * @brief Renames Category_# folders to their actual category names
 * @param gameDomainPath Path to game domain directory
 * @param config Configuration containing API key
 * @return Number of categories renamed
 */
int renameCategoryFolders(const std::filesystem::path& gameDomainPath,
                          const modular::Config& config);

#endif // RENAME_H
