#include "Database.h"
#include "Exceptions.h"
#include <nlohmann/json.hpp>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <ctime>
#include <algorithm>

using json = nlohmann::json;
namespace fs = std::filesystem;

namespace modular {

std::string getCurrentTimestamp()
{
    auto now = std::chrono::system_clock::now();
    auto time_t_now = std::chrono::system_clock::to_time_t(now);
    
    std::tm tm_utc;
#ifdef _WIN32
    gmtime_s(&tm_utc, &time_t_now);
#else
    gmtime_r(&time_t_now, &tm_utc);
#endif
    
    std::ostringstream oss;
    oss << std::put_time(&tm_utc, "%Y-%m-%dT%H:%M:%SZ");
    return oss.str();
}

Database::Database(const std::filesystem::path& db_path)
    : db_path_(db_path)
{
    // Create parent directories if needed
    if (!db_path_.parent_path().empty()) {
        fs::create_directories(db_path_.parent_path());
    }
    
    // Load existing database if it exists
    if (fs::exists(db_path_)) {
        try {
            load();
        } catch (const std::exception&) {
            // If load fails, start with empty database
            records_.clear();
        }
    }
}

void Database::addRecord(const DownloadRecord& record)
{
    // Check if record already exists and update it
    auto it = std::find_if(records_.begin(), records_.end(),
        [&](const DownloadRecord& r) {
            return r.game_domain == record.game_domain &&
                   r.mod_id == record.mod_id &&
                   r.file_id == record.file_id;
        });
    
    if (it != records_.end()) {
        *it = record;
    } else {
        records_.push_back(record);
    }
    
    save();
}

std::optional<DownloadRecord> Database::findRecord(const std::string& game_domain, 
                                                    int mod_id, 
                                                    int file_id) const
{
    auto it = std::find_if(records_.begin(), records_.end(),
        [&](const DownloadRecord& r) {
            return r.game_domain == game_domain &&
                   r.mod_id == mod_id &&
                   r.file_id == file_id;
        });
    
    if (it != records_.end()) {
        return *it;
    }
    return std::nullopt;
}

std::vector<DownloadRecord> Database::getRecordsByDomain(const std::string& game_domain) const
{
    std::vector<DownloadRecord> result;
    std::copy_if(records_.begin(), records_.end(), std::back_inserter(result),
        [&](const DownloadRecord& r) {
            return r.game_domain == game_domain;
        });
    return result;
}

std::vector<DownloadRecord> Database::getRecordsByMod(const std::string& game_domain, 
                                                       int mod_id) const
{
    std::vector<DownloadRecord> result;
    std::copy_if(records_.begin(), records_.end(), std::back_inserter(result),
        [&](const DownloadRecord& r) {
            return r.game_domain == game_domain && r.mod_id == mod_id;
        });
    return result;
}

bool Database::isDownloaded(const std::string& game_domain, int mod_id, int file_id) const
{
    auto record = findRecord(game_domain, mod_id, file_id);
    if (!record) {
        return false;
    }
    
    // Consider it downloaded if status is success or verified
    return record->status == "success" || record->status == "verified";
}

void Database::updateVerification(const std::string& game_domain, 
                                  int mod_id, 
                                  int file_id, 
                                  const std::string& md5_actual,
                                  bool verified)
{
    auto it = std::find_if(records_.begin(), records_.end(),
        [&](const DownloadRecord& r) {
            return r.game_domain == game_domain &&
                   r.mod_id == mod_id &&
                   r.file_id == file_id;
        });
    
    if (it != records_.end()) {
        it->md5_actual = md5_actual;
        it->status = verified ? "verified" : "md5_mismatch";
        save();
    }
}

bool Database::removeRecord(const std::string& game_domain, int mod_id, int file_id)
{
    auto it = std::find_if(records_.begin(), records_.end(),
        [&](const DownloadRecord& r) {
            return r.game_domain == game_domain &&
                   r.mod_id == mod_id &&
                   r.file_id == file_id;
        });
    
    if (it != records_.end()) {
        records_.erase(it);
        save();
        return true;
    }
    return false;
}

size_t Database::getRecordCount() const
{
    return records_.size();
}

void Database::save()
{
    json j = json::array();
    
    for (const auto& record : records_) {
        json rec_json;
        rec_json["game_domain"] = record.game_domain;
        rec_json["mod_id"] = record.mod_id;
        rec_json["file_id"] = record.file_id;
        rec_json["filename"] = record.filename;
        rec_json["filepath"] = record.filepath;
        rec_json["url"] = record.url;
        rec_json["md5_expected"] = record.md5_expected;
        rec_json["md5_actual"] = record.md5_actual;
        rec_json["file_size"] = record.file_size;
        rec_json["download_time"] = record.download_time;
        rec_json["status"] = record.status;
        rec_json["error_message"] = record.error_message;
        j.push_back(rec_json);
    }
    
    std::ofstream ofs(db_path_);
    if (!ofs) {
        throw FileSystemException("Failed to open database for writing", db_path_.string());
    }
    
    ofs << j.dump(2);  // Pretty print with 2-space indent
}

void Database::load()
{
    std::ifstream ifs(db_path_);
    if (!ifs) {
        throw FileSystemException("Failed to open database for reading", db_path_.string());
    }
    
    json j;
    try {
        ifs >> j;
    } catch (const json::exception& e) {
        throw ParseException("Failed to parse database JSON: " + std::string(e.what()), 
                           db_path_.string());
    }
    
    records_.clear();
    
    if (!j.is_array()) {
        throw ParseException("Database JSON must be an array", db_path_.string());
    }
    
    for (const auto& rec_json : j) {
        DownloadRecord record;
        
        if (rec_json.contains("game_domain") && rec_json["game_domain"].is_string()) {
            record.game_domain = rec_json["game_domain"].get<std::string>();
        }
        if (rec_json.contains("mod_id") && rec_json["mod_id"].is_number_integer()) {
            record.mod_id = rec_json["mod_id"].get<int>();
        }
        if (rec_json.contains("file_id") && rec_json["file_id"].is_number_integer()) {
            record.file_id = rec_json["file_id"].get<int>();
        }
        if (rec_json.contains("filename") && rec_json["filename"].is_string()) {
            record.filename = rec_json["filename"].get<std::string>();
        }
        if (rec_json.contains("filepath") && rec_json["filepath"].is_string()) {
            record.filepath = rec_json["filepath"].get<std::string>();
        }
        if (rec_json.contains("url") && rec_json["url"].is_string()) {
            record.url = rec_json["url"].get<std::string>();
        }
        if (rec_json.contains("md5_expected") && rec_json["md5_expected"].is_string()) {
            record.md5_expected = rec_json["md5_expected"].get<std::string>();
        }
        if (rec_json.contains("md5_actual") && rec_json["md5_actual"].is_string()) {
            record.md5_actual = rec_json["md5_actual"].get<std::string>();
        }
        if (rec_json.contains("file_size") && rec_json["file_size"].is_number_integer()) {
            record.file_size = rec_json["file_size"].get<int64_t>();
        }
        if (rec_json.contains("download_time") && rec_json["download_time"].is_string()) {
            record.download_time = rec_json["download_time"].get<std::string>();
        }
        if (rec_json.contains("status") && rec_json["status"].is_string()) {
            record.status = rec_json["status"].get<std::string>();
        }
        if (rec_json.contains("error_message") && rec_json["error_message"].is_string()) {
            record.error_message = rec_json["error_message"].get<std::string>();
        }
        
        records_.push_back(record);
    }
}

} // namespace modular
