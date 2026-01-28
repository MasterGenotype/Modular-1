#pragma once

#include "Config.h"
#include "NexusMods.h"
#include <string>
#include <vector>
#include <set>

namespace modular {

/**
 * @brief Mod information scraped from web tracking center
 */
struct WebTrackedMod {
    int mod_id;
    std::string mod_url;
    int page_found;
    
    WebTrackedMod() : mod_id(0), page_found(0) {}
};

/**
 * @brief Information about a mismatched mod between API and web
 */
struct MismatchedMod {
    int mod_id;
    std::string game_domain;
    std::string mod_url;
    std::string source;  // "API" or "Web"
    
    MismatchedMod() : mod_id(0) {}
};

/**
 * @brief Result of validation between API and web tracking
 */
struct ValidationResult {
    int api_count;
    int web_count;
    int matched_count;
    std::set<int> matched_mod_ids;        // Mod IDs in both API and web
    std::vector<MismatchedMod> api_only;  // In API but not web
    std::vector<MismatchedMod> web_only;  // In web but not API
    bool has_mismatches;
    std::string error_message;  // Set if validation failed
    
    ValidationResult() : api_count(0), web_count(0), matched_count(0), has_mismatches(false) {}
};

/**
 * @brief Validates API-based tracking against web tracking center
 */
class TrackingValidator {
public:
    /**
     * @brief Scrape tracking center for a specific game domain
     * 
     * @param game_domain Game domain (e.g., "stardewvalley")
     * @param game_id Game ID for widget request
     * @param config Configuration (for cookie file path, etc.)
     * @return Vector of web-tracked mods
     */
    static std::vector<WebTrackedMod> scrapeTrackingCenter(
        const std::string& game_domain,
        int game_id,
        const Config& config
    );
    
    /**
     * @brief Validate API mods against web mods
     * 
     * @param api_mods Mods from API
     * @param web_mods Mods from web scraping
     * @param game_domain Game domain for logging
     * @return Validation result with mismatches
     */
    static ValidationResult validateTracking(
        const std::vector<TrackedMod>& api_mods,
        const std::vector<WebTrackedMod>& web_mods,
        const std::string& game_domain
    );
    
    /**
     * @brief Log validation results to stderr
     * 
     * @param result Validation result
     * @param game_domain Game domain for context
     */
    static void logValidationResult(
        const ValidationResult& result,
        const std::string& game_domain
    );
    
    /**
     * @brief Get game ID for a game domain
     * 
     * Maps domain names to numeric game IDs. Returns -1 if unknown.
     * 
     * @param game_domain Game domain
     * @return Game ID or -1
     */
    static int getGameId(const std::string& game_domain);

private:
    /**
     * @brief Load cookies from Netscape format file
     * 
     * @param cookie_path Path to cookie file
     * @return Cookie string for curl, or empty if failed
     */
    static std::string loadCookies(const std::string& cookie_path);
    
    /**
     * @brief Build widget URL for tracking center
     * 
     * @param game_id Game ID
     * @param page Page number (1-indexed)
     * @return Widget URL
     */
    static std::string buildWidgetUrl(int game_id, int page);
    
    /**
     * @brief Fetch widget page with proper headers and cookies
     * 
     * @param url Widget URL
     * @param game_domain Game domain for referer header
     * @param cookie_file Path to cookie file
     * @return HTML response body
     */
    static std::string fetchWidgetPage(
        const std::string& url,
        const std::string& game_domain,
        const std::string& cookie_file
    );
};

} // namespace modular
