#pragma once

#include <string>
#include <vector>
#include <optional>
#include <chrono>
#include <filesystem>

namespace modular {

/**
 * @brief Download record for tracking mod file downloads
 */
struct DownloadRecord {
    std::string game_domain;      // Game domain (e.g., "skyrimspecialedition")
    int mod_id;                   // Mod ID
    int file_id;                  // File ID
    std::string filename;         // Downloaded filename
    std::string filepath;         // Full path to downloaded file
    std::string url;              // Download URL
    std::string md5_expected;     // Expected MD5 (if available from API)
    std::string md5_actual;       // Actual MD5 of downloaded file
    int64_t file_size;            // File size in bytes
    std::string download_time;    // ISO 8601 timestamp
    std::string status;           // "success", "failed", "verified", etc.
    std::string error_message;    // Error message if download failed
    
    DownloadRecord() : mod_id(0), file_id(0), file_size(0) {}
};

/**
 * @brief Simple JSON-based database for tracking download history
 * 
 * Uses a JSON file to store download records. Not thread-safe.
 */
class Database {
public:
    /**
     * @brief Creates/opens a database at the specified path
     * @param db_path Path to database file (will be created if doesn't exist)
     */
    explicit Database(const std::filesystem::path& db_path);
    
    /**
     * @brief Adds a download record to the database
     * @param record The download record to add
     */
    void addRecord(const DownloadRecord& record);
    
    /**
     * @brief Finds a download record by game domain, mod ID, and file ID
     * @param game_domain Game domain
     * @param mod_id Mod ID
     * @param file_id File ID
     * @return Optional record if found
     */
    std::optional<DownloadRecord> findRecord(const std::string& game_domain, 
                                             int mod_id, 
                                             int file_id) const;
    
    /**
     * @brief Gets all download records for a specific game domain
     * @param game_domain Game domain
     * @return Vector of all records for the domain
     */
    std::vector<DownloadRecord> getRecordsByDomain(const std::string& game_domain) const;
    
    /**
     * @brief Gets all download records for a specific mod
     * @param game_domain Game domain
     * @param mod_id Mod ID
     * @return Vector of all records for the mod
     */
    std::vector<DownloadRecord> getRecordsByMod(const std::string& game_domain, 
                                                 int mod_id) const;
    
    /**
     * @brief Checks if a file has already been downloaded successfully
     * @param game_domain Game domain
     * @param mod_id Mod ID
     * @param file_id File ID
     * @return true if file was downloaded and verified successfully
     */
    bool isDownloaded(const std::string& game_domain, int mod_id, int file_id) const;
    
    /**
     * @brief Updates the MD5 hash and verification status of a record
     * @param game_domain Game domain
     * @param mod_id Mod ID
     * @param file_id File ID
     * @param md5_actual Actual MD5 hash
     * @param verified Whether MD5 matches expected value
     */
    void updateVerification(const std::string& game_domain, 
                           int mod_id, 
                           int file_id, 
                           const std::string& md5_actual,
                           bool verified);
    
    /**
     * @brief Removes a record from the database
     * @param game_domain Game domain
     * @param mod_id Mod ID
     * @param file_id File ID
     * @return true if record was found and removed
     */
    bool removeRecord(const std::string& game_domain, int mod_id, int file_id);
    
    /**
     * @brief Gets total number of records in the database
     */
    size_t getRecordCount() const;
    
    /**
     * @brief Saves the database to disk
     */
    void save();
    
    /**
     * @brief Loads the database from disk
     */
    void load();

private:
    std::filesystem::path db_path_;
    std::vector<DownloadRecord> records_;
    
    // Helper to convert record to/from JSON
    static std::string recordToJson(const DownloadRecord& record);
    static DownloadRecord jsonToRecord(const std::string& json_str);
};

/**
 * @brief Gets current timestamp in ISO 8601 format
 */
std::string getCurrentTimestamp();

} // namespace modular
