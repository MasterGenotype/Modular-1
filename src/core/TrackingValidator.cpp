#include "TrackingValidator.h"
#include "HtmlParser.h"
#include <curl/curl.h>
#include <iostream>
#include <fstream>
#include <sstream>
#include <thread>
#include <chrono>
#include <algorithm>
#include <map>

namespace modular {

// Write callback for libcurl
static size_t WriteCallback(void* contents, size_t size, size_t nmemb, void* userp) {
    size_t totalSize = size * nmemb;
    std::string* str = static_cast<std::string*>(userp);
    str->append(static_cast<char*>(contents), totalSize);
    return totalSize;
}

int TrackingValidator::getGameId(const std::string& game_domain) {
    // Map of common game domains to their IDs
    static const std::map<std::string, int> game_ids = {
        {"skyrim", 110},
        {"skyrimspecialedition", 1704},
        {"fallout4", 1151},
        {"fallout3", 120},
        {"falloutnv", 130},
        {"oblivion", 101},
        {"morrowind", 100},
        {"witcher3", 952},
        {"stardewvalley", 1303},
        {"cyberpunk2077", 3333},
        {"baldursgate3", 3474},
        {"starfield", 4187},
        {"finalfantasy7remake", 3606},
        {"finalfantasy7rebirth", 5049},
        {"horizonzerodawn", 3481},
        {"finalfantasyxx2hdremaster", 3285}
    };
    
    auto it = game_ids.find(game_domain);
    if (it != game_ids.end()) {
        return it->second;
    }
    
    return -1; // Unknown game
}

std::string TrackingValidator::buildWidgetUrl(int game_id, int page) {
    std::ostringstream oss;
    oss << "https://www.nexusmods.com/Core/Libs/Common/Widgets/TrackedModsTab"
        << "?RH_TrackedModsTab=game_id:" << game_id
        << ",id:0"
        << ",sort_by:lastupload"
        << ",order:DESC"
        << ",page_size:60"
        << ",page:" << page;
    return oss.str();
}

std::string TrackingValidator::fetchWidgetPage(
    const std::string& url,
    const std::string& game_domain,
    const std::string& cookie_file
) {
    CURL* curl = curl_easy_init();
    std::string response;
    
    if (!curl) {
        return "";
    }
    
    // Set URL
    curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
    
    // Set write callback
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, WriteCallback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &response);
    
    // Load cookies from file
    curl_easy_setopt(curl, CURLOPT_COOKIEFILE, cookie_file.c_str());
    
    // Set headers
    struct curl_slist* headers = nullptr;
    headers = curl_slist_append(headers, "User-Agent: Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    headers = curl_slist_append(headers, "X-Requested-With: XMLHttpRequest");
    
    std::string referer = "Referer: https://www.nexusmods.com/" + game_domain + "/mods/trackingcentre";
    headers = curl_slist_append(headers, referer.c_str());
    
    headers = curl_slist_append(headers, "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    headers = curl_slist_append(headers, "Accept-Language: en-US,en;q=0.5");
    
    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
    
    // Follow redirects
    curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);
    
    // SSL options
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 1L);
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 2L);
    
    // Perform request
    CURLcode res = curl_easy_perform(curl);
    
    if (res != CURLE_OK) {
        std::cerr << "[ERROR] Curl failed: " << curl_easy_strerror(res) << std::endl;
        response.clear();
    }
    
    // Cleanup
    curl_slist_free_all(headers);
    curl_easy_cleanup(curl);
    
    return response;
}

std::vector<WebTrackedMod> TrackingValidator::scrapeTrackingCenter(
    const std::string& game_domain,
    int game_id,
    const Config& config
) {
    std::vector<WebTrackedMod> all_mods;
    std::set<int> seen_ids;
    
    if (game_id == -1) {
        std::cerr << "[ERROR] Unknown game domain: " << game_domain << std::endl;
        return all_mods;
    }
    
    // Check cookie file exists
    std::string cookie_file = config.cookie_file;
    if (cookie_file.empty()) {
        cookie_file = std::string(getenv("HOME")) + "/Documents/cookies.txt";
    }
    
    // Expand ~ if present
    if (cookie_file[0] == '~') {
        cookie_file = std::string(getenv("HOME")) + cookie_file.substr(1);
    }
    
    std::ifstream test_file(cookie_file);
    if (!test_file.good()) {
        std::cerr << "[WARNING] Cookie file not found: " << cookie_file << std::endl;
        std::cerr << "[WARNING] Skipping web validation" << std::endl;
        return all_mods;
    }
    
    int page = 1;
    int max_pages = 100;  // Safety limit
    int consecutive_empty_pages = 0;
    
    while (page <= max_pages && consecutive_empty_pages < 2) {
        std::string url = buildWidgetUrl(game_id, page);
        
        // Rate limiting
        if (page > 1) {
            std::this_thread::sleep_for(std::chrono::milliseconds(800));
        }
        
        std::string html = fetchWidgetPage(url, game_domain, cookie_file);
        
        if (html.empty()) {
            std::cerr << "[ERROR] Failed to fetch page " << page << std::endl;
            break;
        }
        
        // Check for Cloudflare challenge
        if (HtmlParser::isCloudflareChallenge(html)) {
            std::cerr << "[ERROR] Cloudflare challenge detected. Cannot proceed with web validation." << std::endl;
            break;
        }
        
        // Check for login redirect
        if (HtmlParser::isLoginPage(html)) {
            std::cerr << "[ERROR] Login required. Cookie may be expired." << std::endl;
            break;
        }
        
        // Extract mod IDs
        std::vector<int> page_ids = HtmlParser::extractModIds(html);
        
        if (page_ids.empty()) {
            consecutive_empty_pages++;
            page++;
            continue;
        }
        
        // Reset empty page counter if we found mods
        consecutive_empty_pages = 0;
        
        // Add new mods
        int new_mods = 0;
        for (int mod_id : page_ids) {
            if (seen_ids.find(mod_id) == seen_ids.end()) {
                seen_ids.insert(mod_id);
                
                WebTrackedMod mod;
                mod.mod_id = mod_id;
                mod.mod_url = "https://www.nexusmods.com/" + game_domain + "/mods/" + std::to_string(mod_id);
                mod.page_found = page;
                
                all_mods.push_back(mod);
                new_mods++;
            }
        }
        
        // Stop if no new mods found on this page
        if (new_mods == 0) {
            break;
        }
        
        page++;
    }
    
    return all_mods;
}

ValidationResult TrackingValidator::validateTracking(
    const std::vector<TrackedMod>& api_mods,
    const std::vector<WebTrackedMod>& web_mods,
    const std::string& game_domain
) {
    ValidationResult result;
    
    // Build sets of mod IDs
    std::set<int> api_ids;
    std::map<int, TrackedMod> api_map;
    for (const auto& mod : api_mods) {
        api_ids.insert(mod.mod_id);
        api_map[mod.mod_id] = mod;
    }
    
    std::set<int> web_ids;
    std::map<int, WebTrackedMod> web_map;
    for (const auto& mod : web_mods) {
        web_ids.insert(mod.mod_id);
        web_map[mod.mod_id] = mod;
    }
    
    // Set counts
    result.api_count = api_ids.size();
    result.web_count = web_ids.size();
    
    // Find intersection (matched mods)
    std::set<int> matched;
    std::set_intersection(
        api_ids.begin(), api_ids.end(),
        web_ids.begin(), web_ids.end(),
        std::inserter(matched, matched.begin())
    );
    result.matched_count = matched.size();
    result.matched_mod_ids = matched;
    
    // Find mods only in API
    std::set<int> api_only_ids;
    std::set_difference(
        api_ids.begin(), api_ids.end(),
        web_ids.begin(), web_ids.end(),
        std::inserter(api_only_ids, api_only_ids.begin())
    );
    
    for (int mod_id : api_only_ids) {
        MismatchedMod mm;
        mm.mod_id = mod_id;
        mm.game_domain = game_domain;
        mm.source = "API";
        mm.mod_url = "https://www.nexusmods.com/" + game_domain + "/mods/" + std::to_string(mod_id);
        result.api_only.push_back(mm);
    }
    
    // Find mods only in Web
    std::set<int> web_only_ids;
    std::set_difference(
        web_ids.begin(), web_ids.end(),
        api_ids.begin(), api_ids.end(),
        std::inserter(web_only_ids, web_only_ids.begin())
    );
    
    for (int mod_id : web_only_ids) {
        MismatchedMod mm;
        mm.mod_id = mod_id;
        mm.game_domain = game_domain;
        mm.source = "Web";
        mm.mod_url = web_map[mod_id].mod_url;
        result.web_only.push_back(mm);
    }
    
    result.has_mismatches = (!result.api_only.empty() || !result.web_only.empty());
    
    return result;
}

void TrackingValidator::logValidationResult(
    const ValidationResult& result,
    const std::string& game_domain
) {
    if (!result.error_message.empty()) {
        std::cerr << "[ERROR] Tracking validation failed for " << game_domain 
                  << ": " << result.error_message << std::endl;
        return;
    }
    
    if (!result.has_mismatches) {
        std::cout << "[INFO] Tracking validation: " << result.matched_count 
                  << " mods (API: " << result.api_count 
                  << ", Web: " << result.web_count 
                  << ", Matched: " << result.matched_count << ")" << std::endl;
        return;
    }
    
    // Log mismatches
    std::cerr << "[WARNING] Tracking validation mismatch detected for " << game_domain << "!" << std::endl;
    std::cerr << "[WARNING] API mods: " << result.api_count 
              << ", Web mods: " << result.web_count 
              << ", Matched: " << result.matched_count << std::endl;
    
    if (!result.api_only.empty()) {
        std::cerr << "[WARNING] Mods only in API (" << result.api_only.size() << "):" << std::endl;
        for (const auto& mod : result.api_only) {
            std::cerr << "[WARNING]   - Mod ID: " << mod.mod_id 
                      << ", Domain: " << mod.game_domain 
                      << ", URL: " << mod.mod_url 
                      << ", Source: " << mod.source << std::endl;
        }
    }
    
    if (!result.web_only.empty()) {
        std::cerr << "[WARNING] Mods only in Web (" << result.web_only.size() << "):" << std::endl;
        for (const auto& mod : result.web_only) {
            std::cerr << "[WARNING]   - Mod ID: " << mod.mod_id 
                      << ", Domain: " << mod.game_domain 
                      << ", URL: " << mod.mod_url 
                      << ", Source: " << mod.source << std::endl;
        }
    }
}

} // namespace modular
