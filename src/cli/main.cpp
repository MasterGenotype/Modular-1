// main.cpp (updated to use LiveUI)
//
// Assumes you added:  LiveUI.h  (as provided earlier)
// and that LiveUI is the only thing repainting during operations.

#include "GameBanana.h"
#include "NexusMods.h"
#include "Rename.h"
#include "LiveUI.h"
#include "HttpClient.h"
#include "RateLimiter.h"
#include "ILogger.h"
#include "Config.h"
#include "Utils.h"
#include "TrackingValidator.h"
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>
#include <map>
#include <limits>

namespace fs = std::filesystem;
using modular::CurlGlobal;

static std::string short_status(std::string s, std::size_t maxLen)
{
    if (s.size() <= maxLen) return s;
    if (maxLen <= 3) return s.substr(0, maxLen);
    return s.substr(0, maxLen - 3) + "...";
}



std::string getDefaultModsDirectory()
{
    const char* homeEnv = std::getenv("HOME");
    std::string homeDir = (homeEnv ? std::string(homeEnv) : "");
    return (fs::path(homeDir) / "Games" / "Mods-Lists").string();
}

void runGameBananaSequence()
{
    const char* envUserId = std::getenv("GB_USER_ID");
    std::string userId = (envUserId ? std::string(envUserId) : "");
    if (userId.empty()) {
        std::cerr << "GB_USER_ID environment variable is not set.\n";
        return;
    }

    // Create HTTP infrastructure (GameBanana doesn't need rate limiting)
    modular::StderrLogger logger(false);  // no debug output
    modular::RateLimiter rateLimiter(logger);
    modular::HttpClient client(rateLimiter, logger);

    auto mods = fetchSubscribedMods(userId, client);
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
        std::string modName = modular::sanitizeFilename(mod.second);
        std::string modId   = extractModId(modUrl);
        if (modId.empty()) {
            ui.setStatus("Skipping (no mod id): " + short_status(modName, 40));
            ui.tick(); // count it as “processed” for the UI
            continue;
        }

        ui.setStatus("Downloading: " + short_status(modName, 50));
        
        // Progress callback for individual file downloads
        auto progress_cb = [&](const std::string& filename, size_t current, size_t total) {
            ui.setStatus("File: " + short_status(filename, 40) + " (" + 
                        std::to_string(current) + "/" + std::to_string(total) + ")");
        };
        
        downloadModFiles(modId, modName, baseDir, client, progress_cb);
        ui.tick();
    }

    ui.finish("Complete");
}

void runNexusModsSequence(const std::vector<std::string>& domains,
                          const modular::Config& config,
                          const std::string& categories = "main,optional",
                          bool dry_run = false,
                          bool force = false)
{
    // Cache validation results per domain to avoid duplicate web scraping
    std::map<std::string, modular::ValidationResult> validationCache;

    LiveUI ui;
    ui.begin();

    // Pass 1: count total files (with validation if enabled)
    ui.setOperation("Scanning Mods", (int)domains.size());
    ui.setStatus("Counting files...");

    int totalFiles = 0;
    for (std::size_t i = 0; i < domains.size(); ++i) {
        const auto& domain = domains[i];

        ui.setStatus("Scan: " + domain);
        auto trackedModIds = get_tracked_mods_for_domain(domain, config);
        
        if (!trackedModIds.empty()) {
            std::vector<int> modsToCount;
            
            // If validation is enabled, perform validation and cache result
            if (config.validate_tracking) {
                int game_id = modular::TrackingValidator::getGameId(domain);
                if (game_id != -1) {
                    auto allTrackedMods = get_tracked_mods_with_domain(config);
                    std::vector<TrackedMod> trackedMods;
                    for (const auto& tm : allTrackedMods) {
                        if (tm.domain_name == domain) {
                            trackedMods.push_back(tm);
                        }
                    }
                    
                    if (!trackedMods.empty()) {
                        auto webMods = modular::TrackingValidator::scrapeTrackingCenter(domain, game_id, config);
                        auto result = modular::TrackingValidator::validateTracking(trackedMods, webMods, domain);
                        validationCache[domain] = result;  // Cache for Pass 2
                        
                        // Count matched + web-only mods
                        for (int mod_id : result.matched_mod_ids) {
                            modsToCount.push_back(mod_id);
                        }
                        for (const auto& mod : result.web_only) {
                            modsToCount.push_back(mod.mod_id);
                        }
                    }
                } else {
                    modsToCount = trackedModIds;
                }
            } else {
                modsToCount = trackedModIds;
            }
            
            if (!modsToCount.empty()) {
                auto fileIdsMap = get_file_ids(modsToCount, domain, config, categories);
                for (const auto& [mod_id, files] : fileIdsMap) {
                    (void)mod_id;
                    totalFiles += (int)files.size();
                }
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

        auto trackedModIds = get_tracked_mods_for_domain(domain, config);
        if (trackedModIds.empty()) continue;
        
        // Get full TrackedMod structs for validation
        auto allTrackedMods = get_tracked_mods_with_domain(config);
        std::vector<TrackedMod> trackedMods;
        for (const auto& tm : allTrackedMods) {
            if (tm.domain_name == domain) {
                trackedMods.push_back(tm);
            }
        }
        
        // Determine which mods to download based on validation
        std::vector<int> modsToDownload;
        
        // Use cached validation if available
        if (config.validate_tracking && validationCache.count(domain)) {
            auto& result = validationCache[domain];
            modular::TrackingValidator::logValidationResult(result, domain);
            
            // Priority 1: Matched mods (both API and web)
            for (int mod_id : result.matched_mod_ids) {
                modsToDownload.push_back(mod_id);
            }
            
            // Priority 2: Web-only mods
            for (const auto& mod : result.web_only) {
                modsToDownload.push_back(mod.mod_id);
            }
            
            // Skip API-only mods (not validated by web scraper)
            if (!result.api_only.empty()) {
                std::cerr << "[INFO] Skipping " << result.api_only.size() 
                          << " API-only mods (not validated by web scraper)" << std::endl;
            }
        } else {
            // Validation disabled or not cached, use API mods
            modsToDownload = trackedModIds;
        }
        
        if (modsToDownload.empty()) continue;

        auto fileIdsMap = get_file_ids(modsToDownload, domain, config, categories);
        if (fileIdsMap.empty()) continue;

        auto downloadLinks = generate_download_links(fileIdsMap, domain, config);
        save_download_links(downloadLinks, domain, config);

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
        download_files(domain, config, nullptr, dry_run, force);
    }

    ui.finish("Done");
    
    // Auto-rename if enabled and not in dry-run mode
    if (!dry_run && config.auto_rename) {
        std::cout << "\n";
        for (const auto& domain : domains) {
            fs::path domainPath = config.mods_directory / domain;
            if (fs::exists(domainPath)) {
                if (config.organize_by_category) {
                    std::cout << "Auto-organizing and renaming mods in " << domain << " by category...\n";
                } else {
                    std::cout << "Auto-renaming mods in " << domain << "...\n";
                }
                reorganizeAndRenameMods(domainPath, config, config.organize_by_category);
            }
        }
    }
}

void runRenameSequence(const modular::Config& config, bool organizeByCategory = true)
{
    fs::path modsDir = config.mods_directory;
    auto gameDomains = getGameDomainNames(modsDir);
    
    if (gameDomains.empty()) {
        std::cout << "No game domains found in " << modsDir << std::endl;
        return;
    }

    std::cout << "\n=== Reorganizing and Renaming Mods ===\n";
    if (organizeByCategory) {
        std::cout << "Mode: Organize by category\n";
    } else {
        std::cout << "Mode: Simple rename\n";
    }
    std::cout << std::endl;

    int totalProcessed = 0;
    for (const auto& gameDomain : gameDomains) {
        fs::path gameDomainPath = modsDir / gameDomain;
        std::cout << "\nProcessing " << gameDomain << "...\n";
        int count = reorganizeAndRenameMods(gameDomainPath, config, organizeByCategory);
        totalProcessed += count;
    }

    std::cout << "\n=== Summary ===\n";
    std::cout << "Total mods processed: " << totalProcessed << "\n";
}

int main(int argc, char* argv[])
{
    // Initialize CURL globally (RAII cleanup on exit)
    CurlGlobal curl_init;
    
    // Load configuration (from file + env vars)
    modular::Config config;
    try {
        config = modular::loadConfig();
    } catch (const std::exception& e) {
        std::cerr << "Warning: Failed to load config: " << e.what() << "\n";
        std::cerr << "Using default configuration.\n";
    }
    
    // CLI execution mode
    if (argc > 1) {
        std::vector<std::string> gameDomains;
        std::string categories = "main,optional";
        bool dry_run = false;
        bool force = false;

        for (int i = 1; i < argc; i++) {
            std::string arg = argv[i];
            if (arg == "--categories" && i + 1 < argc) {
                categories = argv[++i];
            } else if (arg == "--dry-run" || arg == "-n") {
                dry_run = true;
            } else if (arg == "--force" || arg == "-f") {
                force = true;
            } else if (arg == "--organize-by-category") {
                config.organize_by_category = true;
            } else if (arg == "--help" || arg == "-h") {
                std::cout << "Usage: " << argv[0] << " [OPTIONS] <game_domains...>\n"
                          << "\nOptions:\n"
                          << "  --categories <cats>       Comma-separated category list (default: main,optional)\n"
                          << "  --dry-run, -n             Show what would be downloaded without downloading\n"
                          << "  --force, -f               Re-download files even if already downloaded\n"
                          << "  --organize-by-category    Organize renamed mods into category subdirectories\n"
                          << "  --help, -h                Show this help message\n"
                          << "\nExamples:\n"
                          << "  " << argv[0] << " skyrimspecialedition\n"
                          << "  " << argv[0] << " --categories main,optional skyrimspecialedition\n"
                          << "  " << argv[0] << " --dry-run skyrimspecialedition\n"
                          << "  " << argv[0] << " --force --organize-by-category stardewvalley\n";
                return 0;
            } else if (!arg.empty() && arg[0] != '-') {
                gameDomains.push_back(arg);
            }
        }

        if (!gameDomains.empty()) {
            runNexusModsSequence(gameDomains, config, categories, dry_run, force);
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

                if (!gameDomains.empty()) runNexusModsSequence(gameDomains, config, categories);
                break;
            }
            case 3: {
                std::cout << "Skip category organization? (y/N): ";
                std::string response;
                std::getline(std::cin, response);
                bool organizeByCategory = !(response == "y" || response == "Y");
                runRenameSequence(config, organizeByCategory);
                break;
            }
            default: break;
        }
    }
    return 0;
}
