#ifndef GAMEBANANA_H
#define GAMEBANANA_H

#include <string>
#include <utility>
#include <vector>
#include <functional>
#include "HttpClient.h"
#include "ILogger.h"

namespace modular {
    class HttpClient;
    class ILogger;
}

// Progress callback for download operations
using GameBananaProgressCallback = std::function<void(const std::string& filename, size_t current, size_t total)>;

// Sanitizes a filename by replacing illegal characters with underscores.
std::string sanitizeFilename(const std::string& name);

// Extracts a mod ID from the provided profile URL.
std::string extractModId(const std::string& profileUrl);

// Extracts the file name from the given download URL.
std::string extractFileName(const std::string& downloadUrl);

// Fetches a list of subscribed mods for the given user ID.
// Each mod is represented as a pair where:
//   - first: the mod's profile URL,
//   - second: the mod's name.
// Requires HttpClient for making API requests.
std::vector<std::pair<std::string, std::string>> fetchSubscribedMods(
    const std::string& userId, 
    modular::HttpClient& client);

// Fetches a list of file download URLs for the specified mod ID.
std::vector<std::string> fetchModFileUrls(
    const std::string& modId,
    modular::HttpClient& client);

// Downloads all mod files for the specified mod.
// Files will be stored in a subdirectory (based on a sanitized version of modName) under baseDir.
void downloadModFiles(
    const std::string& modId, 
    const std::string& modName, 
    const std::string& baseDir,
    modular::HttpClient& client,
    GameBananaProgressCallback progress_cb = nullptr);

#endif // GAMEBANANA_H
