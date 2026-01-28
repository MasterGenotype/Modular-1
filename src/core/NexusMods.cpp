#include "NexusMods.h"
#include "Utils.h"
#include "Database.h"
#include <cctype>
#include <set>
#include <chrono>
#include <cstdlib>
#include <curl/curl.h>
#include <thread>
#include <sstream>
#include <fstream>
#include <filesystem>
#include <iterator>
#include <nlohmann/json.hpp>

namespace fs = std::filesystem;
using json = nlohmann::json;

// Use types from modular namespace
using modular::HttpResponse;
using modular::Headers;
using modular::Config;

//----------------------------------------------------------------------------------
// Curl utility functions
//----------------------------------------------------------------------------------

static size_t WriteCallback(void* contents, size_t size, size_t nmemb, void* userp)
{
    size_t totalSize = size * nmemb;
    std::string* str = static_cast<std::string*>(userp);
    str->append(static_cast<char*>(contents), totalSize);
    return totalSize;
}

static size_t HeaderCallback(char* buffer, size_t size, size_t nitems, void* userdata)
{
    size_t totalSize = size * nitems;
    auto* headers = static_cast<std::map<std::string, std::string>*>(userdata);
    
    std::string header(buffer, totalSize);
    size_t colon = header.find(':');
    if (colon != std::string::npos) {
        std::string name = header.substr(0, colon);
        std::string value = header.substr(colon + 1);
        
        // Trim leading/trailing whitespace and newlines
        size_t start = value.find_first_not_of(" \t\r\n");
        size_t end = value.find_last_not_of(" \t\r\n");
        if (start != std::string::npos && end != std::string::npos) {
            value = value.substr(start, end - start + 1);
        }
        
        (*headers)[name] = value;
    }
    return totalSize;
}

HttpResponse http_get(const std::string& url, const std::vector<std::string>& headers)
{
    HttpResponse response { 0, "", {} };
    CURL* curl = curl_easy_init();
    if (!curl) {
        return response;
    }

    struct curl_slist* curl_headers = nullptr;
    
    // Add User-Agent header
    curl_headers = curl_slist_append(curl_headers, "User-Agent: Modular/1.0.0");
    
    for (const auto& header : headers) {
        curl_headers = curl_slist_append(curl_headers, header.c_str());
    }

    curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, curl_headers);
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, WriteCallback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &response.body);
    curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, HeaderCallback);
    curl_easy_setopt(curl, CURLOPT_HEADERDATA, &response.headers);
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 1L);
    curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 2L);

    CURLcode res = curl_easy_perform(curl);
    if (res == CURLE_OK) {
        curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &response.status_code);
    }

    curl_slist_free_all(curl_headers);
    curl_easy_cleanup(curl);
    return response;
}

//----------------------------------------------------------------------------------
// Utility functions
//----------------------------------------------------------------------------------

// Helper to log rate limit info from headers
static void logRateLimitInfo(const std::map<std::string, std::string>& headers) {
    if (headers.count("X-RL-Hourly-Remaining") && headers.count("X-RL-Daily-Remaining")) {
        std::string hourly = headers.at("X-RL-Hourly-Remaining");
        std::string daily = headers.at("X-RL-Daily-Remaining");
        std::cout << "[INFO] Rate Limits - Hourly: " << hourly << " remaining, Daily: " << daily << " remaining" << std::endl;
    }
}

// Helper to handle rate limit errors (429)
static bool handleRateLimitError(const HttpResponse& resp) {
    if (resp.status_code == 429) {
        std::cerr << "[ERROR] Rate limit exceeded (HTTP 429)!" << std::endl;
        
        // Try to parse error message
        try {
            json error = json::parse(resp.body);
            if (error.contains("message")) {
                std::cerr << "[ERROR] API: " << error["message"].get<std::string>() << std::endl;
            }
        } catch (...) {}
        
        // Check for Retry-After header
        if (resp.headers.count("Retry-After")) {
            int retry_after = std::stoi(resp.headers.at("Retry-After"));
            std::cerr << "[INFO] Retry after " << retry_after << " seconds" << std::endl;
            std::this_thread::sleep_for(std::chrono::seconds(retry_after));
        } else {
            // Default: wait 1 hour
            std::cerr << "[INFO] Waiting 1 hour for rate limit reset..." << std::endl;
            std::this_thread::sleep_for(std::chrono::hours(1));
        }
        return true;
    }
    return false;
}

// Helper to parse and log API error messages
static void logApiError(const HttpResponse& resp) {
    if (resp.status_code >= 400) {
        std::cerr << "[ERROR] HTTP " << resp.status_code;
        try {
            json error = json::parse(resp.body);
            if (error.contains("message")) {
                std::cerr << ": " << error["message"].get<std::string>();
            }
        } catch (...) {}
        std::cerr << std::endl;
    }
}

int select_best_file(const std::vector<json>& files)
{
    int best_file_id = -1;
    std::time_t best_time = 0;

    for (const auto& file_json : files) {
        if (!file_json.contains("file_id")) continue;

        int file_id = file_json["file_id"].get<int>();

        bool is_primary = false;
        if (file_json.contains("is_primary") && file_json["is_primary"].is_boolean()) {
            is_primary = file_json["is_primary"].get<bool>();
        }

        if (is_primary) {
            return file_id;
        }

        std::time_t file_time = static_cast<std::time_t>(file_id);
        if (file_time > best_time) {
            best_time = file_time;
            best_file_id = file_id;
        }
    }

    return best_file_id;
}

//----------------------------------------------------------------------------------
// NexusMods API workflow functions
//----------------------------------------------------------------------------------

std::vector<TrackedMod> get_tracked_mods_with_domain(const Config& config)
{
    std::vector<TrackedMod> tracked;
    std::string url = "https://api.nexusmods.com/v1/user/tracked_mods.json";

    std::vector<std::string> headers = {
        "accept: application/json",
        "apikey: " + config.nexus_api_key
    };

    HttpResponse resp = http_get(url, headers);
    if (resp.status_code == 200) {
        try {
            json data = json::parse(resp.body);
            auto parse_entry = [&](const json& mod){
                if (!mod.contains("mod_id")) return; 
                TrackedMod tm{};
                tm.mod_id = mod["mod_id"].get<int>();
                if (mod.contains("domain_name") && mod["domain_name"].is_string()) {
                    tm.domain_name = mod["domain_name"].get<std::string>();
                }
                if (mod.contains("name") && mod["name"].is_string()) {
                    tm.name = mod["name"].get<std::string>();
                }
                tracked.push_back(std::move(tm));
            };

            if (data.is_array()) {
                for (const auto& mod : data) parse_entry(mod);
            } else if (data.contains("mods")) {
                for (const auto& mod : data["mods"]) parse_entry(mod);
            }
        } catch (const std::exception&) {
            // parse error
        }
    }
    return tracked;
}

std::vector<int> get_tracked_mods(const Config& config)
{
    std::vector<int> ids;
    for (const auto& tm : get_tracked_mods_with_domain(config)) {
        ids.push_back(tm.mod_id);
    }
    return ids;
}

std::vector<int> get_tracked_mods_for_domain(const std::string& game_domain, const Config& config)
{
    std::vector<int> ids;
    auto all_tracked = get_tracked_mods_with_domain(config);
    ids.reserve(all_tracked.size());
    for (const auto& tm : all_tracked) {
        if (tm.domain_name == game_domain) ids.push_back(tm.mod_id);
    }
    return ids;
}

std::string get_user_info(const Config& config)
{
    std::string url = "https://api.nexusmods.com/v1/users/validate.json";
    
    std::vector<std::string> headers = {
        "accept: application/json",
        "apikey: " + config.nexus_api_key
    };
    
    HttpResponse resp = http_get(url, headers);
    if (resp.status_code == 200) {
        return resp.body;
    }
    return "";
}

bool is_mod_tracked(const std::string& game_domain, int mod_id, const Config& config)
{
    // Get all tracked mods for this domain
    auto all_tracked = get_tracked_mods_with_domain(config);
    
    // Check if the mod is in the tracked list
    for (const auto& tm : all_tracked) {
        if (tm.domain_name == game_domain && tm.mod_id == mod_id) {
            return true;
        }
    }
    
    return false;
}

std::map<int, std::vector<int>> get_file_ids(const std::vector<int>& mod_ids,
                                             const std::string& game_domain,
                                             const Config& config,
                                             const std::string& filter_categories)
{
    std::map<int, std::vector<int>> mod_file_ids;

    std::set<std::string> allowed_categories;
    if (!filter_categories.empty()) {
        std::stringstream ss(filter_categories);
        std::string cat;
        while (std::getline(ss, cat, ',')) {
            std::transform(cat.begin(), cat.end(), cat.begin(),
                           [](unsigned char c) { return std::tolower(c); });
            allowed_categories.insert(cat);
        }
    }

    // Pre-validate: Get tracked mods list to verify each mod
    auto tracked_mods = get_tracked_mods_with_domain(config);
    std::set<int> tracked_ids_for_domain;
    for (const auto& tm : tracked_mods) {
        if (tm.domain_name == game_domain) {
            tracked_ids_for_domain.insert(tm.mod_id);
        }
    }

    for (auto mod_id : mod_ids) {
        // VALIDATION: Ensure this mod is in the user's tracked list
        if (tracked_ids_for_domain.find(mod_id) == tracked_ids_for_domain.end()) {
            std::cerr << "WARNING: Mod " << mod_id << " is NOT in tracked list. Skipping." << std::endl;
            mod_file_ids[mod_id] = {};  // Empty file list
            continue;
        }
        
        std::ostringstream oss;
        oss << "https://api.nexusmods.com/v1/games/" << game_domain
            << "/mods/" << mod_id << "/files.json";

        if (!filter_categories.empty()) {
            oss << "?filter_file_category=" << filter_categories;
        }

        std::vector<std::string> headers = {
            "accept: application/json",
            "apikey: " + config.nexus_api_key
        };

        HttpResponse resp = http_get(oss.str(), headers);
        
        // Handle rate limiting
        if (handleRateLimitError(resp)) {
            // Retry after waiting
            resp = http_get(oss.str(), headers);
        }
        
        // Log rate limit info periodically
        static int call_count = 0;
        if (++call_count % 10 == 0) {
            logRateLimitInfo(resp.headers);
        }

        if (resp.status_code == 200) {
            try {
                json data = json::parse(resp.body);

                if (data.is_null() || !data.is_object()) {
                    mod_file_ids[mod_id] = {};
                    continue;
                }

                if (data.contains("files") && data["files"].empty()) {
                    mod_file_ids[mod_id] = {};
                    continue;
                }

                if (data.contains("files")) {
                    auto file_list = data["files"];
                    std::vector<int> chosen_file_ids;

                    if (allowed_categories.empty()) {
                        std::map<std::string, std::vector<json>> category_files;
                        for (auto& file_json : file_list) {
                            if (file_json.contains("category_name") &&
                                file_json["category_name"].is_string() &&
                                file_json.contains("file_id")) {
                                std::string category =
                                    file_json["category_name"].get<std::string>();
                                category_files[category].push_back(file_json);
                            }
                        }

                        for (const auto& [category, files] : category_files) {
                            int best_file_id = select_best_file(files);
                            if (best_file_id != -1) {
                                chosen_file_ids.push_back(best_file_id);
                            }
                        }
                    } else {
                        std::map<std::string, std::vector<json>> category_files;
                        for (auto& file_json : file_list) {
                            if (file_json.contains("category_name") &&
                                file_json["category_name"].is_string() &&
                                file_json.contains("file_id")) {

                                std::string category_lower =
                                    file_json["category_name"].get<std::string>();
                                std::transform(category_lower.begin(),
                                               category_lower.end(),
                                               category_lower.begin(),
                                               ::tolower);

                                if (allowed_categories.count(category_lower)) {
                                    category_files[category_lower].push_back(file_json);
                                }
                            }
                        }

                        for (const auto& [category, files] : category_files) {
                            int best_file_id = select_best_file(files);
                            if (best_file_id != -1) {
                                chosen_file_ids.push_back(best_file_id);
                            }
                        }
                    }

                    mod_file_ids[mod_id] = chosen_file_ids;
                }
            } catch (const json::exception& e) {
                std::cerr << "[ERROR] JSON parse error for mod " << mod_id << ": " << e.what() << std::endl;
                mod_file_ids[mod_id] = {};
            }
        } else {
            logApiError(resp);
            mod_file_ids[mod_id] = {};
        }

        // Rate limiting: 2 seconds between requests (respects 500/hour limit with margin)
        std::this_thread::sleep_for(std::chrono::seconds(2));
    }
    return mod_file_ids;
}

std::map<std::pair<int, int>, std::string> generate_download_links(
    const std::map<int, std::vector<int>>& mod_file_ids,
    const std::string& game_domain,
    const Config& config)
{
    std::map<std::pair<int, int>, std::string> download_links;

    for (auto& [mod_id, file_ids] : mod_file_ids) {
        for (auto file_id : file_ids) {
            std::ostringstream oss;
            oss << "https://api.nexusmods.com/v1/games/" << game_domain
                << "/mods/" << mod_id << "/files/" << file_id
                << "/download_link.json?expires=999999";

            std::vector<std::string> headers = {
                "accept: application/json",
                "apikey: " + config.nexus_api_key
            };

            HttpResponse resp = http_get(oss.str(), headers);
            
            // Handle rate limiting
            if (handleRateLimitError(resp)) {
                // Retry after waiting
                resp = http_get(oss.str(), headers);
            }

            if (resp.status_code == 200) {
                try {
                    json data = json::parse(resp.body);
                    if (data.is_array() && !data.empty() && data[0].contains("URI")) {
                        std::string uri = data[0]["URI"].get<std::string>();
                        download_links[{mod_id, file_id}] = uri;
                    }
                } catch (const json::exception& e) {
                    std::cerr << "[ERROR] JSON parse error for download link (mod " << mod_id 
                              << ", file " << file_id << "): " << e.what() << std::endl;
                }
            } else {
                logApiError(resp);
            }
            
            // Rate limiting: 2 seconds between requests
            std::this_thread::sleep_for(std::chrono::seconds(2));
        }
    }
    return download_links;
}

void save_download_links(const std::map<std::pair<int, int>, std::string>& download_links,
                         const std::string& game_domain,
                         const Config& config)
{
    fs::path base_directory = config.mods_directory / game_domain;
    fs::create_directories(base_directory);

    fs::path download_links_path = base_directory / "download_links.txt";
    std::ofstream ofs(download_links_path.string());

    if (!ofs.is_open()) {
        return;
    }

    for (const auto& [mod_file_pair, url] : download_links) {
        ofs << mod_file_pair.first << "," << mod_file_pair.second << "," << url << "\n";
    }
    ofs.close();
}

void download_files(const std::string& game_domain,
                   const Config& config,
                   DownloadProgressCallback progress_cb,
                   bool dry_run,
                   bool force)
{
    fs::path base_directory = config.mods_directory / game_domain;
    fs::path download_links_path = base_directory / "download_links.txt";

    if (!fs::exists(download_links_path)) {
        return;
    }

    // Load/create database for tracking downloads
    fs::path db_path = base_directory / "downloads.db.json";
    modular::Database db(db_path);

    std::ifstream ifs(download_links_path.string());
    std::vector<std::string> lines;
    std::string line;
    while (std::getline(ifs, line)) {
        if (!line.empty()) lines.push_back(line);
    }
    
    int total_files = lines.size();
    int completed = 0;

    auto download_with_retries = [&](const std::string& url,
                                     const fs::path& file_path) -> bool {
        const int retries = 5;
        std::string safe_url = modular::escapeSpaces(url);

        for (int attempt = 0; attempt < retries; ++attempt) {
            CURL* curl = curl_easy_init();
            if (!curl) {
                continue;
            }

            FILE* fp = std::fopen(file_path.string().c_str(), "wb");
            if (!fp) {
                curl_easy_cleanup(curl);
                return false;
            }

            curl_easy_setopt(curl, CURLOPT_URL, safe_url.c_str());
            curl_easy_setopt(curl, CURLOPT_WRITEDATA, fp);
            curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, nullptr);
            curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 1L);
            curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 2L);

            CURLcode res = curl_easy_perform(curl);
            long http_code = 0;
            curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);

            std::fclose(fp);
            curl_easy_cleanup(curl);

            if (res == CURLE_OK && http_code == 200) {
                return true;
            }

            if (attempt < retries - 1) {
                std::this_thread::sleep_for(std::chrono::seconds(5));
            }
        }
        return false;
    };

    for (const auto& l : lines) {
        std::stringstream ss(l);
        std::string mod_id_str, file_id_str, url;
        if (std::getline(ss, mod_id_str, ',') &&
            std::getline(ss, file_id_str, ',') &&
            std::getline(ss, url, '\n')) {

            int mod_id = std::stoi(mod_id_str);
            int file_id = std::stoi(file_id_str);

            std::string filename;
            size_t pos = url.rfind('/');
            if (pos != std::string::npos && pos < url.size() - 1) {
                filename = url.substr(pos + 1);
            }
            pos = filename.find('?');
            if (pos != std::string::npos) {
                filename = filename.substr(0, pos);
            }
            if (filename.empty()) {
                filename = "mod_" + std::to_string(mod_id) +
                           "_file_" + std::to_string(file_id) + ".zip";
            }

            fs::path mod_directory = base_directory / std::to_string(mod_id);
            fs::create_directories(mod_directory);
            fs::path file_path = mod_directory / filename;

            // Check if already downloaded successfully (unless --force is used)
            if (!force && db.isDownloaded(game_domain, mod_id, file_id)) {
                if (progress_cb) {
                    progress_cb("Skipped (already downloaded): " + filename, completed + 1, total_files);
                }
                completed++;
                continue;
            }

            if (progress_cb) {
                std::string action = dry_run ? "Would download" : "Downloading";
                progress_cb(action + ": " + filename, completed, total_files);
            }
            
            bool success = false;
            if (!dry_run) {
                success = download_with_retries(url, file_path);
            } else {
                // In dry-run mode, just simulate success
                success = true;
            }
            
            // Create download record
            modular::DownloadRecord record;
            record.game_domain = game_domain;
            record.mod_id = mod_id;
            record.file_id = file_id;
            record.filename = filename;
            record.filepath = file_path.string();
            record.url = url;
            record.download_time = modular::getCurrentTimestamp();
            
            if (dry_run) {
                // In dry-run mode, just show what would be downloaded
                record.status = "dry-run";
                record.file_size = 0;
                
                if (progress_cb) {
                    progress_cb("Would download: " + filename, completed + 1, total_files);
                }
            } else if (success && fs::exists(file_path)) {
                // Get file size
                record.file_size = static_cast<int64_t>(fs::file_size(file_path));
                record.status = "success";
                
                // Calculate MD5 for verification
                try {
                    std::string md5 = modular::calculateMD5(file_path.string());
                    record.md5_actual = md5;
                    record.status = "verified";
                    
                    if (progress_cb) {
                        progress_cb("Verified: " + filename, completed + 1, total_files);
                    }
                } catch (const std::exception& e) {
                    record.error_message = "MD5 calculation failed: " + std::string(e.what());
                    if (progress_cb) {
                        progress_cb("Completed (no MD5): " + filename, completed + 1, total_files);
                    }
                }
            } else {
                record.status = "failed";
                record.error_message = "Download failed after retries";
                record.file_size = 0;
                
                if (progress_cb) {
                    progress_cb("Failed: " + filename, completed + 1, total_files);
                }
            }
            
            // Save record to database (skip in dry-run mode)
            if (!dry_run) {
                db.addRecord(record);
            }
            
            completed++;
            std::this_thread::sleep_for(std::chrono::seconds(1));
        }
    }
}
