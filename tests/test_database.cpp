#include <catch2/catch_test_macros.hpp>
#include "Database.h"
#include <filesystem>

using namespace modular;
namespace fs = std::filesystem;

TEST_CASE("Database basic operations", "[database]") {
    std::string test_db_path = "/tmp/modular_test.db.json";
    
    // Clean up before test
    if (fs::exists(test_db_path)) {
        fs::remove(test_db_path);
    }
    
    SECTION("creates new database") {
        Database db(test_db_path);
        REQUIRE(db.getRecordCount() == 0);
    }
    
    SECTION("adds and retrieves records") {
        Database db(test_db_path);
        
        DownloadRecord record;
        record.game_domain = "skyrimspecialedition";
        record.mod_id = 12345;
        record.file_id = 67890;
        record.filename = "test_mod.zip";
        record.filepath = "/path/to/test_mod.zip";
        record.url = "http://example.com/mod.zip";
        record.status = "success";
        record.file_size = 1024000;
        record.download_time = "2026-01-25T00:00:00Z";
        
        db.addRecord(record);
        REQUIRE(db.getRecordCount() == 1);
        
        auto found = db.findRecord("skyrimspecialedition", 12345, 67890);
        REQUIRE(found.has_value());
        REQUIRE(found->filename == "test_mod.zip");
        REQUIRE(found->status == "success");
        REQUIRE(found->file_size == 1024000);
    }
    
    SECTION("updates existing record") {
        Database db(test_db_path);
        
        DownloadRecord record;
        record.game_domain = "skyrimspecialedition";
        record.mod_id = 123;
        record.file_id = 456;
        record.filename = "mod.zip";
        record.status = "success";
        
        db.addRecord(record);
        REQUIRE(db.getRecordCount() == 1);
        
        // Update the record
        record.status = "verified";
        record.md5_actual = "abc123def456";
        db.addRecord(record);
        
        // Should still have only 1 record
        REQUIRE(db.getRecordCount() == 1);
        
        auto found = db.findRecord("skyrimspecialedition", 123, 456);
        REQUIRE(found.has_value());
        REQUIRE(found->status == "verified");
        REQUIRE(found->md5_actual == "abc123def456");
    }
    
    SECTION("persists to disk and reloads") {
        {
            Database db(test_db_path);
            
            DownloadRecord record;
            record.game_domain = "fallout4";
            record.mod_id = 999;
            record.file_id = 111;
            record.filename = "persistent.zip";
            record.status = "success";
            
            db.addRecord(record);
        }
        
        // Create new database instance - should load from disk
        Database db2(test_db_path);
        REQUIRE(db2.getRecordCount() == 1);
        
        auto found = db2.findRecord("fallout4", 999, 111);
        REQUIRE(found.has_value());
        REQUIRE(found->filename == "persistent.zip");
    }
    
    // Cleanup
    if (fs::exists(test_db_path)) {
        fs::remove(test_db_path);
    }
}

TEST_CASE("Database query operations", "[database]") {
    std::string test_db_path = "/tmp/modular_test_query.db.json";
    
    if (fs::exists(test_db_path)) {
        fs::remove(test_db_path);
    }
    
    Database db(test_db_path);
    
    // Add multiple records
    for (int i = 0; i < 5; i++) {
        DownloadRecord record;
        record.game_domain = "skyrimspecialedition";
        record.mod_id = 100 + i;
        record.file_id = 200 + i;
        record.filename = "mod_" + std::to_string(i) + ".zip";
        record.status = "success";
        db.addRecord(record);
    }
    
    // Add records for different game
    for (int i = 0; i < 3; i++) {
        DownloadRecord record;
        record.game_domain = "fallout4";
        record.mod_id = 300 + i;
        record.file_id = 400 + i;
        record.filename = "fallout_mod_" + std::to_string(i) + ".zip";
        record.status = "success";
        db.addRecord(record);
    }
    
    SECTION("retrieves records by domain") {
        auto skyrim_records = db.getRecordsByDomain("skyrimspecialedition");
        REQUIRE(skyrim_records.size() == 5);
        
        auto fallout_records = db.getRecordsByDomain("fallout4");
        REQUIRE(fallout_records.size() == 3);
    }
    
    SECTION("retrieves records by mod") {
        auto mod_records = db.getRecordsByMod("skyrimspecialedition", 102);
        REQUIRE(mod_records.size() == 1);
        REQUIRE(mod_records[0].file_id == 202);
    }
    
    SECTION("checks if downloaded") {
        REQUIRE(db.isDownloaded("skyrimspecialedition", 100, 200) == true);
        REQUIRE(db.isDownloaded("skyrimspecialedition", 999, 999) == false);
    }
    
    SECTION("removes record") {
        REQUIRE(db.removeRecord("skyrimspecialedition", 100, 200) == true);
        REQUIRE(db.getRecordCount() == 7);  // 8 - 1 = 7
        REQUIRE(db.isDownloaded("skyrimspecialedition", 100, 200) == false);
        
        // Try to remove non-existent record
        REQUIRE(db.removeRecord("skyrimspecialedition", 999, 999) == false);
    }
    
    // Cleanup
    if (fs::exists(test_db_path)) {
        fs::remove(test_db_path);
    }
}

TEST_CASE("Database verification operations", "[database]") {
    std::string test_db_path = "/tmp/modular_test_verify.db.json";
    
    if (fs::exists(test_db_path)) {
        fs::remove(test_db_path);
    }
    
    Database db(test_db_path);
    
    DownloadRecord record;
    record.game_domain = "skyrimspecialedition";
    record.mod_id = 123;
    record.file_id = 456;
    record.filename = "test.zip";
    record.status = "success";
    
    db.addRecord(record);
    
    SECTION("updates verification status") {
        db.updateVerification("skyrimspecialedition", 123, 456, "abc123", true);
        
        auto found = db.findRecord("skyrimspecialedition", 123, 456);
        REQUIRE(found.has_value());
        REQUIRE(found->md5_actual == "abc123");
        REQUIRE(found->status == "verified");
    }
    
    SECTION("updates verification failure") {
        db.updateVerification("skyrimspecialedition", 123, 456, "wrong_hash", false);
        
        auto found = db.findRecord("skyrimspecialedition", 123, 456);
        REQUIRE(found.has_value());
        REQUIRE(found->md5_actual == "wrong_hash");
        REQUIRE(found->status == "md5_mismatch");
    }
    
    // Cleanup
    if (fs::exists(test_db_path)) {
        fs::remove(test_db_path);
    }
}

TEST_CASE("getCurrentTimestamp returns valid ISO 8601 format", "[database]") {
    std::string timestamp = getCurrentTimestamp();
    
    // Basic format check: YYYY-MM-DDTHH:MM:SSZ
    REQUIRE(timestamp.length() == 20);
    REQUIRE(timestamp[4] == '-');
    REQUIRE(timestamp[7] == '-');
    REQUIRE(timestamp[10] == 'T');
    REQUIRE(timestamp[13] == ':');
    REQUIRE(timestamp[16] == ':');
    REQUIRE(timestamp[19] == 'Z');
}
