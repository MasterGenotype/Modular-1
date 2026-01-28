#include "Rename.h"
#include "Database.h"
#include "NexusMods.h"
#include <curl/curl.h>
#include <iostream>
#include <nlohmann/json.hpp>
#include <thread>
#include <chrono>
#include <cctype>
#include <map>

namespace fs = std::filesystem;
using json = nlohmann::json;

// This callback will be used by libcurl to write received data into a std::string.
size_t WriteCallback(void* contents, size_t size, size_t nmemb, void* userp)
{
    std::string* str = static_cast<std::string*>(userp);
    size_t totalSize = size * nmemb;
    str->append(static_cast<char*>(contents), totalSize);
    return totalSize;
}

std::vector<std::string> getGameDomainNames(const fs::path& modsListsDir)
{
    std::vector<std::string> domains;

    if (!fs::exists(modsListsDir)) {
        std::cerr << "Directory does not exist: " << modsListsDir << std::endl;
        return domains;
    }

    for (const auto& entry : fs::directory_iterator(modsListsDir)) {
        if (entry.is_directory()) {
            domains.push_back(entry.path().filename().string());
        }
    }
    return domains;
}

std::vector<std::string> getModIDs(const fs::path& gameDomainPath)
{
    std::vector<std::string> modIDs;

    if (!fs::exists(gameDomainPath)) {
        std::cerr << "Directory does not exist: " << gameDomainPath << std::endl;
        return modIDs;
    }

    for (const auto& entry : fs::directory_iterator(gameDomainPath)) {
        if (entry.is_directory()) {
            modIDs.push_back(entry.path().filename().string());
        }
    }
    return modIDs;
}

std::string fetchModName(const std::string& gameDomain, const std::string& modID,
                        const modular::Config& config)
{
    CURL* curl = curl_easy_init();
    std::string readBuffer;

    if (curl) {
        // Construct the API URL.
        std::string url = "https://api.nexusmods.com/v1/games/" + gameDomain + "/mods/" + modID;
        curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, WriteCallback);
        curl_easy_setopt(curl, CURLOPT_WRITEDATA, &readBuffer);

        // Get API key from config
        if (config.nexus_api_key.empty()) {
            std::cerr << "NexusMods API key is not configured. Please set it in config.json\n";
            curl_easy_cleanup(curl);
            return "";
        }

        // Set the API key as an HTTP header.
        std::string headerStr = "apikey: " + config.nexus_api_key;
        struct curl_slist* headers = nullptr;
        headers = curl_slist_append(headers, headerStr.c_str());
        curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);

        // Perform the API request.
        CURLcode res = curl_easy_perform(curl);
        if (res != CURLE_OK) {
            std::cerr << "curl_easy_perform() failed: " << curl_easy_strerror(res) << "\n";
        }

        // Cleanup.
        curl_easy_cleanup(curl);
        curl_slist_free_all(headers);
    }
    return readBuffer;
}

std::string extractModName(const std::string& jsonResponse)
{
    try {
        auto j = json::parse(jsonResponse);
        if (j.contains("name")) {
            return j["name"].get<std::string>();
        }
    } catch (const std::exception& e) {
        std::cerr << "JSON parse error: " << e.what() << std::endl;
    }
    return "";
}

void combineDirectories(const fs::path& target, const fs::path& source)
{
    if (!fs::exists(target)) {
        fs::create_directories(target);
    }
    // Iterate over every item in the source directory.
    for (const auto& entry : fs::directory_iterator(source)) {
        fs::path dest = target / entry.path().filename();
        if (entry.is_directory()) {
            // Recursively merge subdirectories.
            combineDirectories(dest, entry.path());
        } else {
            // Copy (or overwrite) the file.
            fs::copy(entry.path(), dest, fs::copy_options::overwrite_existing);
        }
    }
}

std::string fetchModInfo(const std::string& gameDomain, 
                         const std::string& modID,
                         const modular::Config& config)
{
    // Same as fetchModName but returns full JSON for category info
    return fetchModName(gameDomain, modID, config);
}

int reorganizeAndRenameMods(const fs::path& gameDomainPath,
                            const modular::Config& config,
                            bool organizeByCategory)
{
    if (!fs::exists(gameDomainPath)) {
        std::cerr << "Game domain path does not exist: " << gameDomainPath << std::endl;
        return 0;
    }

    std::string gameDomain = gameDomainPath.filename().string();
    
    // Collect all mod directories (including those in category subdirectories)
    std::vector<fs::path> modDirs;
    for (const auto& entry : fs::directory_iterator(gameDomainPath)) {
        if (entry.is_directory()) {
            std::string dirName = entry.path().filename().string();
            // Skip special files/dirs
            if (dirName == "downloads.db.json" || dirName == "download_links.txt") {
                continue;
            }
            modDirs.push_back(entry.path());
        }
    }
    
    if (modDirs.empty()) {
        std::cout << "No mods found in " << gameDomain << std::endl;
        return 0;
    }

    // Load database to map already-renamed mods to their mod_ids
    fs::path dbPath = gameDomainPath / "downloads.db.json";
    std::map<int, fs::path> modIdToPath;  // mod_id -> current directory path
    
    if (fs::exists(dbPath)) {
        modular::Database db(dbPath);
        db.load();
        auto records = db.getRecordsByDomain(gameDomain);
        for (const auto& record : records) {
            if (modIdToPath.find(record.mod_id) == modIdToPath.end()) {
                // Extract mod directory path from filepath
                fs::path filepath = record.filepath;
                if (!filepath.empty() && fs::exists(filepath.parent_path())) {
                    modIdToPath[record.mod_id] = filepath.parent_path();
                }
            }
        }
    }

    int successCount = 0;
    
    // Process each mod directory
    for (const auto& modPath : modDirs) {
        std::string modDirName = modPath.filename().string();
        
        // Check if this is a numeric ID (unprocessed mod)
        bool isNumeric = true;
        for (char c : modDirName) {
            if (!std::isdigit(c)) {
                isNumeric = false;
                break;
            }
        }
        
        int actualModId = -1;
        fs::path oldPath = modPath;
        
        if (isNumeric) {
            // Numeric directory - needs renaming and possibly categorization
            actualModId = std::stoi(modDirName);
        } else if (organizeByCategory) {
            // Already renamed directory - check if we need to move it to a category
            // Find the mod_id for this directory path
            for (const auto& [mid, path] : modIdToPath) {
                if (path == modPath) {
                    actualModId = mid;
                    break;
                }
            }
            
            if (actualModId == -1) {
                // Can't find mod_id, skip this directory
                continue;
            }
        } else {
            // Already renamed and not organizing by category, skip
            continue;
        }
        
        // Fetch mod information using actualModId
        std::string jsonResponse = fetchModInfo(gameDomain, std::to_string(actualModId), config);
        if (jsonResponse.empty()) {
            std::cerr << "Failed to fetch info for mod " << actualModId << std::endl;
            continue;
        }
        
        try {
            json modInfo = json::parse(jsonResponse);
            
            // Extract mod name
            std::string modName;
            if (modInfo.contains("name") && modInfo["name"].is_string()) {
                modName = modInfo["name"].get<std::string>();
            } else {
                std::cerr << "No name found for mod " << actualModId << std::endl;
                continue;
            }
            
            // Sanitize filename
            for (char& c : modName) {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|') {
                    c = '_';
                }
            }
            
            // oldPath is already set above
            fs::path newPath;
            
            if (organizeByCategory && modInfo.contains("category_id")) {
                // Organize by category
                int categoryId = modInfo["category_id"].get<int>();
                std::string categoryName = "Category_" + std::to_string(categoryId);
                
                // Try to get category name if available
                if (modInfo.contains("category") && modInfo["category"].is_object()) {
                    if (modInfo["category"].contains("name") && modInfo["category"]["name"].is_string()) {
                        categoryName = modInfo["category"]["name"].get<std::string>();
                        // Sanitize category name
                        for (char& c : categoryName) {
                            if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                                c == '"' || c == '<' || c == '>' || c == '|') {
                                c = '_';
                            }
                        }
                    }
                }
                
                fs::path categoryPath = gameDomainPath / categoryName;
                fs::create_directories(categoryPath);
                newPath = categoryPath / modName;
            } else {
                // No category organization, just rename in place
                newPath = gameDomainPath / modName;
            }
            
            // Check if destination already exists
            if (fs::exists(newPath) && newPath != oldPath) {
                // Merge directories if both are directories
                if (fs::is_directory(oldPath) && fs::is_directory(newPath)) {
                    std::cout << "Merging " << oldPath.filename().string() << " into existing " << modName << std::endl;
                    combineDirectories(newPath, oldPath);
                    fs::remove_all(oldPath);
                } else {
                    std::cerr << "Destination already exists: " << newPath << std::endl;
                    continue;
                }
            } else if (oldPath != newPath) {
                // Rename/move to new location
                try {
                    fs::rename(oldPath, newPath);
                    if (isNumeric) {
                        std::cout << "Renamed: " << modDirName << " -> " << modName << std::endl;
                    } else if (organizeByCategory) {
                        std::cout << "Organized: " << oldPath.filename().string() << " -> " << newPath.parent_path().filename().string() << "/" << modName << std::endl;
                    }
                } catch (const fs::filesystem_error& e) {
                    std::cerr << "Failed to move " << oldPath.filename().string() << ": " << e.what() << std::endl;
                    continue;
                }
            }
            
            successCount++;
            
        } catch (const json::exception& e) {
            std::cerr << "JSON parse error for mod " << actualModId << ": " << e.what() << std::endl;
            continue;
        }
        
        // Rate limiting - sleep between API calls
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }
    
    std::cout << "Successfully processed " << successCount << " mods in " << gameDomain << std::endl;
    
    // Automatically rename Category_# folders to actual names
    if (organizeByCategory) {
        renameCategoryFolders(gameDomainPath, config);
    }
    
    return successCount;
}

std::map<int, std::string> fetchGameCategories(const std::string& gameDomain,
                                                const modular::Config& config)
{
    std::map<int, std::string> categories;
    
    if (config.nexus_api_key.empty()) {
        return categories;
    }
    
    std::string url = "https://api.nexusmods.com/v1/games/" + gameDomain + ".json";
    std::vector<std::string> headers = {
        "accept: application/json",
        "apikey: " + config.nexus_api_key
    };
    
    auto resp = http_get(url, headers);
    
    if (resp.status_code == 200 && !resp.body.empty()) {
        try {
            json gameInfo = json::parse(resp.body);
            if (gameInfo.contains("categories") && gameInfo["categories"].is_array()) {
                for (const auto& cat : gameInfo["categories"]) {
                    if (cat.contains("category_id") && cat.contains("name")) {
                        int id = cat["category_id"].get<int>();
                        if (cat["name"].is_string()) {
                            categories[id] = cat["name"].get<std::string>();
                        }
                    }
                }
            }
        } catch (const json::exception& e) {
            std::cerr << "Failed to parse game categories: " << e.what() << std::endl;
        }
    }
    
    return categories;
}

int renameCategoryFolders(const fs::path& gameDomainPath,
                          const modular::Config& config)
{
    if (!fs::exists(gameDomainPath)) {
        return 0;
    }
    
    std::string gameDomain = gameDomainPath.filename().string();
    
    // Find all Category_# folders
    std::vector<std::pair<fs::path, int>> categoryFolders;
    for (const auto& entry : fs::directory_iterator(gameDomainPath)) {
        if (entry.is_directory()) {
            std::string dirName = entry.path().filename().string();
            if (dirName.rfind("Category_", 0) == 0) {
                // Extract category ID
                std::string idStr = dirName.substr(9); // Length of "Category_"
                try {
                    int categoryId = std::stoi(idStr);
                    categoryFolders.push_back({entry.path(), categoryId});
                } catch (...) {
                    // Not a valid Category_# folder, skip
                }
            }
        }
    }
    
    if (categoryFolders.empty()) {
        return 0;
    }
    
    // Fetch category names from API
    std::cout << "Fetching category names for " << gameDomain << "..." << std::endl;
    auto categoryMap = fetchGameCategories(gameDomain, config);
    
    if (categoryMap.empty()) {
        std::cerr << "No categories found for " << gameDomain << std::endl;
        return 0;
    }
    
    int renamedCount = 0;
    for (const auto& [folderPath, categoryId] : categoryFolders) {
        if (categoryMap.count(categoryId)) {
            std::string newName = categoryMap[categoryId];
            
            // Sanitize the category name
            for (char& c : newName) {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|') {
                    c = '_';
                }
            }
            
            fs::path newPath = gameDomainPath / newName;
            
            if (newPath == folderPath) {
                continue; // Already has correct name
            }
            
            try {
                if (fs::exists(newPath)) {
                    // Merge into existing folder
                    std::cout << "Merging " << folderPath.filename().string() << " into " << newName << std::endl;
                    combineDirectories(newPath, folderPath);
                    fs::remove_all(folderPath);
                } else {
                    fs::rename(folderPath, newPath);
                    std::cout << "Renamed: " << folderPath.filename().string() << " -> " << newName << std::endl;
                }
                renamedCount++;
            } catch (const fs::filesystem_error& e) {
                std::cerr << "Failed to rename " << folderPath.filename().string() << ": " << e.what() << std::endl;
            }
        }
    }
    
    if (renamedCount > 0) {
        std::cout << "Renamed " << renamedCount << " category folders in " << gameDomain << std::endl;
    }
    
    return renamedCount;
}
