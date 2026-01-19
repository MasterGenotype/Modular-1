#include "NexusMods.h"
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

std::string API_KEY;
namespace fs = std::filesystem;
using json = nlohmann::json;

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

HttpResponse http_get(const std::string& url, const std::vector<std::string>& headers)
{
    HttpResponse response { 0, "" };
    CURL* curl = curl_easy_init();
    if (!curl) {
        return response;
    }

    struct curl_slist* curl_headers = nullptr;
    for (const auto& header : headers) {
        curl_headers = curl_slist_append(curl_headers, header.c_str());
    }

    curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, curl_headers);
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, WriteCallback);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA, &response.body);
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

std::string escape_spaces(const std::string& url)
{
    std::string result;
    result.reserve(url.size());
    for (char c : url) {
        if (c == ' ') {
            result += "%20";
        } else {
            result += c;
        }
    }
    return result;
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

std::vector<int> get_tracked_mods()
{
    std::vector<int> mod_ids;
    std::string url = "https://api.nexusmods.com/v1/user/tracked_mods.json";

    std::vector<std::string> headers = {
        "accept: application/json",
        "apikey: " + API_KEY
    };

    HttpResponse resp = http_get(url, headers);
    if (resp.status_code == 200) {
        try {
            json data = json::parse(resp.body);
            if (data.is_array()) {
                for (auto& mod : data) {
                    if (mod.contains("mod_id")) {
                        int id = mod["mod_id"].get<int>();
                        mod_ids.push_back(id);
                    }
                }
            } else if (data.contains("mods")) {
                auto mods_list = data["mods"];
                for (auto& mod : mods_list) {
                    if (mod.contains("mod_id")) {
                        int id = mod["mod_id"].get<int>();
                        mod_ids.push_back(id);
                    }
                }
            } else {
                // unexpected JSON shape
            }
        } catch (const std::exception&) {
            // parse error
        }
    } else {
        // HTTP error
    }
    return mod_ids;
}

std::vector<int> get_tracked_mods_for_domain(const std::string& game_domain)
{
    std::vector<int> mod_ids;
    std::vector<int> all_mods = get_tracked_mods();

    for (int mod_id : all_mods) {
        std::ostringstream oss;
        oss << "https://api.nexusmods.com/v1/games/" << game_domain
            << "/mods/" << mod_id << ".json";

        std::vector<std::string> headers = {"accept: application/json", "apikey: " + API_KEY};
        HttpResponse resp = http_get(oss.str(), headers);

        if (resp.status_code == 200) {
            try {
                json data = json::parse(resp.body);
                (void)data;
                mod_ids.push_back(mod_id);
            } catch (const std::exception&) {
                // ignore JSON error
            }
        } else if (resp.status_code == 403 || resp.status_code == 404) {
            // skip
        } else {
            // warning
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    return mod_ids;
}

std::map<int, std::vector<int>> get_file_ids(const std::vector<int>& mod_ids,
                                             const std::string& game_domain,
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

    for (auto mod_id : mod_ids) {
        std::ostringstream oss;
        oss << "https://api.nexusmods.com/v1/games/" << game_domain
            << "/mods/" << mod_id << "/files.json";

        if (!filter_categories.empty()) {
            oss << "?filter_file_category=" << filter_categories;
        }

        std::vector<std::string> headers = {
            "accept: application/json",
            "apikey: " + API_KEY
        };

        HttpResponse resp = http_get(oss.str(), headers);

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
            } catch (const std::exception&) {
                mod_file_ids[mod_id] = {};
            }
        } else {
            mod_file_ids[mod_id] = {};
        }

        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
    return mod_file_ids;
}

std::map<std::pair<int, int>, std::string> generate_download_links(
    const std::map<int, std::vector<int>>& mod_file_ids,
    const std::string& game_domain)
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
                "apikey: " + API_KEY
            };

            HttpResponse resp = http_get(oss.str(), headers);

            if (resp.status_code == 200) {
                try {
                    json data = json::parse(resp.body);
                    if (data.is_array() && !data.empty() && data[0].contains("URI")) {
                        std::string uri = data[0]["URI"].get<std::string>();
                        download_links[{mod_id, file_id}] = uri;
                    }
                } catch (const std::exception&) {
                    // ignore JSON error
                }
            } else {
                // ignore HTTP failure
            }
            std::this_thread::sleep_for(std::chrono::seconds(1));
        }
    }
    return download_links;
}

void save_download_links(const std::map<std::pair<int, int>, std::string>& download_links,
                         const std::string& game_domain)
{
    std::string homeDir = std::string(std::getenv("HOME") ? std::getenv("HOME") : "");
    fs::path base_directory = fs::path(homeDir) / "Games" / "Mods-Lists" / game_domain;
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

void download_files(const std::string& game_domain)
{
    std::string homeDir = std::string(std::getenv("HOME") ? std::getenv("HOME") : "");
    fs::path base_directory = fs::path(homeDir) / "Games" / "Mods-Lists" / game_domain;
    fs::path download_links_path = base_directory / "download_links.txt";

    if (!fs::exists(download_links_path)) {
        return;
    }

    std::ifstream ifs(download_links_path.string());
    std::vector<std::string> lines;
    std::string line;
    while (std::getline(ifs, line)) {
        if (!line.empty()) lines.push_back(line);
    }

    auto download_with_retries = [&](const std::string& url,
                                     const fs::path& file_path,
                                     int mod_id,
                                     int file_id) {
        (void)mod_id;     // ← ADD: Silence warning, keep for future use
        (void)file_id;    // ← ADD: Silence warning, keep for future use
        
        const int retries = 5;
        std::string safe_url = escape_spaces(url);

        for (int attempt = 0; attempt < retries; ++attempt) {
            CURL* curl = curl_easy_init();
            if (!curl) {
                continue;
            }

            FILE* fp = std::fopen(file_path.string().c_str(), "wb");
            if (!fp) {
                curl_easy_cleanup(curl);
                return;
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
                return;
            }

            if (attempt < retries - 1) {
                std::this_thread::sleep_for(std::chrono::seconds(5));
            }
        }
        return;
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

            download_with_retries(url, file_path, mod_id, file_id);
            std::this_thread::sleep_for(std::chrono::seconds(1));
        }
    }
}
