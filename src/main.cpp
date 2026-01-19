// main.cpp (updated to use LiveUI)
//
// Assumes you added:  LiveUI.h  (as provided earlier)
// and that LiveUI is the only thing repainting during operations.

#include "GameBanana.h"
#include "NexusMods.h"
#include "Rename.h"
#include "LiveUI.h"
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>
#include <limits>

namespace fs = std::filesystem;

extern std::string API_KEY;

static std::string short_status(std::string s, std::size_t maxLen)
{
    if (s.size() <= maxLen) return s;
    if (maxLen <= 3) return s.substr(0, maxLen);
    return s.substr(0, maxLen - 3) + "...";
}

std::string sanitizeFileName(const std::string& name)
{
    std::string sanitized = name;
    for (char& c : sanitized) {
        if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
            c == '\"' || c == '<' || c == '>' || c == '|') {
            c = '_';
        }
    }
    return sanitized;
}

std::string getApiKey()
{
    const char* envApiKey = std::getenv("API_KEY");
    if (envApiKey && std::strlen(envApiKey) > 0) return std::string(envApiKey);

    const char* home = std::getenv("HOME");
    if (home) {
        fs::path configDir = fs::path(home) / ".config" / "Modular";
        fs::path apiKeyFile = configDir / "api_key.txt";
        if (fs::exists(apiKeyFile)) {
            std::ifstream file(apiKeyFile);
            std::string key((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
            key.erase(std::remove_if(key.begin(), key.end(), ::isspace), key.end());
            if (!key.empty()) return key;
        }
    }

    std::cout << "Enter NexusMods API Key: ";
    std::string key;
    std::cin >> key;

    if (home) {
        fs::path configDir = fs::path(home) / ".config" / "Modular";
        fs::create_directories(configDir);
        fs::path apiKeyFile = configDir / "api_key.txt";
        std::ofstream file(apiKeyFile);
        file << key;
    }
    return key;
}

std::string getDefaultModsDirectory()
{
    const char* homeEnv = std::getenv("HOME");
    std::string homeDir = (homeEnv ? std::string(homeEnv) : "");
    return (fs::path(homeDir) / "Games" / "Mods-Lists").string();
}

void runGameBananaSequence()
{
    initialize();

    const char* envUserId = std::getenv("GB_USER_ID");
    std::string userId = (envUserId ? std::string(envUserId) : "");
    if (userId.empty()) {
        // keep errors minimal for now
        std::cerr << "GB_USER_ID environment variable is not set.\n";
        return;
    }

    auto mods = fetchSubscribedMods(userId);
    if (mods.empty()) {
        std::cout << "No subscribed mods found.\n";
        return;
    }

    std::string defaultModsDir = getDefaultModsDirectory();
    std::cout << "Enter base directory (ENTER for " << defaultModsDir << "): ";
    std::string baseDir;
    std::getline(std::cin, baseDir);
    if (baseDir.empty()) baseDir = defaultModsDir;

    LiveUI ui;
    ui.begin();
    ui.setOperation("GameBanana Downloads", (int)mods.size());

    for (std::size_t i = 0; i < mods.size(); ++i) {
        const auto& mod = mods[i];

        std::string modUrl  = mod.first;
        std::string modName = sanitizeFileName(mod.second);
        std::string modId   = extractModId(modUrl);
        if (modId.empty()) {
            ui.setStatus("Skipping (no mod id): " + short_status(modName, 40));
            ui.tick(); // count it as “processed” for the UI
            continue;
        }

        ui.setStatus("Downloading: " + short_status(modName, 50));
        downloadModFiles(modId, modName, baseDir);
        ui.tick();
    }

    ui.finish("Complete");
    cleanup();
}

void runNexusModsSequence(const std::vector<std::string>& domains,
                          const std::string& categories = "main,optional")
{
    API_KEY = getApiKey();

    LiveUI ui;
    ui.begin();

    // Pass 1: count total files
    ui.setOperation("Scanning Mods", (int)domains.size());
    ui.setStatus("Counting files...");

    int totalFiles = 0;
    for (std::size_t i = 0; i < domains.size(); ++i) {
        const auto& domain = domains[i];

        ui.setStatus("Scan: " + domain);
        auto trackedMods = get_tracked_mods_for_domain(domain);
        if (!trackedMods.empty()) {
            auto fileIdsMap = get_file_ids(trackedMods, domain, categories);
            for (const auto& [mod_id, files] : fileIdsMap) {
                (void)mod_id;
                totalFiles += (int)files.size();
            }
        }
        ui.tick();
    }

    if (totalFiles == 0) {
        ui.setOperation("NexusMods Downloads", 1);
        ui.setProgress(1);
        ui.setStatus("No files to download.");
        ui.finish();
        return;
    }

    // Pass 2: download
    ui.setOperation("NexusMods Downloads", totalFiles);
    ui.setStatus("Starting downloads...");

    int processed = 0;

    for (const auto& domain : domains) {
        ui.setStatus("Domain: " + domain);

        auto trackedMods = get_tracked_mods_for_domain(domain);
        if (trackedMods.empty()) continue;

        auto fileIdsMap = get_file_ids(trackedMods, domain, categories);
        if (fileIdsMap.empty()) continue;

        auto downloadLinks = generate_download_links(fileIdsMap, domain);
        save_download_links(downloadLinks, domain);

        // NOTE: This is still “per link” progress, not per-byte or per-successful-file.
        // It gives you UI behavior now; later you’ll move these ticks into the real
        // download callback.
        ui.setStatus("Downloading (" + domain + "): " + std::to_string(downloadLinks.size()) + " files");

        // Advance progress for the number of links we intend to download.
        // (If download_files fails mid-way, the bar will still reach the target;
        // fix later by emitting real events from download_files.)
        for (std::size_t j = 0; j < downloadLinks.size(); ++j) {
            (void)j;
            ++processed;
            ui.setProgress(processed);
        }

        // Do actual downloads (should be silent; you already stripped NexusMods.cpp output)
        download_files(domain);
    }

    ui.finish("Done");
}

void runRenameSequence()
{
    fs::path modsDir = getDefaultModsDirectory();
    auto gameDomains = getGameDomainNames(modsDir);
    if (gameDomains.empty()) return;

    // Count total renames
    int totalMods = 0;
    for (const auto& gameDomain : gameDomains) {
        fs::path gameDomainPath = modsDir / gameDomain;
        auto modIDs = getModIDs(gameDomainPath);
        totalMods += (int)modIDs.size();
    }

    LiveUI ui;
    ui.begin();
    ui.setOperation("Renaming Mods", std::max(1, totalMods));
    ui.setStatus("Starting...");

    int renamedCount = 0;

    for (const auto& gameDomain : gameDomains) {
        fs::path gameDomainPath = modsDir / gameDomain;
        auto modIDs = getModIDs(gameDomainPath);

        for (const auto& modID : modIDs) {
            std::string jsonResponse = fetchModName(gameDomain, modID);
            std::string rawModName   = extractModName(jsonResponse);
            if (rawModName.empty()) {
                ui.setStatus("Skip: " + gameDomain + " " + modID);
                ui.tick(); // count as processed
                continue;
            }

            std::string modName = sanitizeFileName(rawModName);
            fs::path oldPath = gameDomainPath / modID;
            fs::path newPath = gameDomainPath / modName;

            try {
                fs::rename(oldPath, newPath);
                ++renamedCount;
                ui.setStatus("Renamed: " + gameDomain + " " + short_status(modName, 45));
                ui.setProgress(renamedCount);
            } catch (...) {
                // keep silent for now
                ui.setStatus("Rename failed: " + gameDomain + " " + modID);
                ui.tick();
            }
        }
    }

    ui.finish("Done");
}

int main(int argc, char* argv[])
{
    // CLI execution mode
    if (argc > 1) {
        std::vector<std::string> gameDomains;
        std::string categories = "main,optional";

        for (int i = 1; i < argc; i++) {
            std::string arg = argv[i];
            if (arg == "--categories" && i + 1 < argc) {
                categories = argv[++i];
            } else if (!arg.empty() && arg[0] != '-') {
                gameDomains.push_back(arg);
            }
        }

        if (!gameDomains.empty()) {
            runNexusModsSequence(gameDomains, categories);
            return 0;
        }
    }

    // Menu mode (left mostly intact; UI is inside operations)
    bool running = true;
    while (running) {
        std::cout << "\n=== Main Menu ===\n"
                  << "1. GameBanana\n"
                  << "2. NexusMods\n"
                  << "3. Rename\n"
                  << "0. Exit\n"
                  << "Choice: ";

        int choice;
        std::cin >> choice;
        std::cin.ignore(10000, '\n');

        switch (choice) {
            case 0: running = false; break;
            case 1: runGameBananaSequence(); break;
            case 2: {
                std::vector<std::string> gameDomains;
                std::string categories = "main,optional";

                // domain input
                std::cout << "Game domains: ";
                std::string line;
                std::getline(std::cin, line);
                std::istringstream iss(line);
                std::string domain;
                while (iss >> domain) gameDomains.push_back(domain);

                if (!gameDomains.empty()) runNexusModsSequence(gameDomains, categories);
                break;
            }
            case 3: runRenameSequence(); break;
            default: break;
        }
    }
    return 0;
}
