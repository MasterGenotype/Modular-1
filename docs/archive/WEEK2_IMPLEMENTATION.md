# Week 2 Implementation Plan

**Date:** January 25, 2026  
**Status:** Planning phase  
**Prerequisites:** Week 1 complete (core infrastructure implemented)

## Overview

Week 2 focuses on configuration management, download tracking, and code quality improvements. These changes build on the solid foundation from Week 1 and prepare the codebase for future features like concurrent downloads and GUI.

## Goals

1. **Configuration System** - Move from environment variables to structured config
2. **Download History** - Track what's been downloaded to enable smart sync
3. **File Verification** - Add MD5 checking for download integrity
4. **Code Consolidation** - Remove remaining duplicate code
5. **Testing Foundation** - Set up test framework and write critical tests

## Tasks

### Task 2.1: Config as Struct (Not Singleton)

**Priority:** High  
**Estimated Time:** 2-3 hours  
**Status:** Not started

**Problem:**
- API keys stored in env vars or separate files
- No central configuration
- Hard to override settings
- Testing requires changing global state

**Solution:**
Create a `Config` struct that can be loaded, modified, and passed to services:

```cpp
// include/core/Config.h
struct Config {
    // NexusMods
    std::string nexus_api_key;
    std::vector<std::string> default_categories = {"main", "optional"};
    
    // GameBanana
    std::string gamebanana_user_id;
    
    // Storage
    fs::path mods_directory;
    
    // Preferences
    bool auto_rename = true;
    bool verify_downloads = false;
    int max_concurrent_downloads = 1;
    bool verbose = false;
};

// Free functions (not methods)
Config loadConfig(const fs::path& path = defaultConfigPath());
void saveConfig(const Config& cfg, const fs::path& path = defaultConfigPath());
fs::path defaultConfigPath();  // ~/.config/Modular/config.json
```

**Benefits:**
- Easy to test (just create a Config struct)
- Easy to override (CLI args -> modify struct)
- No global state
- Can have multiple configs in tests

**Files to Create:**
- `include/core/Config.h`
- `src/core/Config.cpp`

**Files to Modify:**
- `src/cli/main.cpp` - Use `loadConfig()` instead of `getApiKey()`
- `src/core/NexusMods.cpp` - Remove `extern std::string API_KEY`

**Implementation Steps:**
1. Create Config struct with all settings
2. Implement loadConfig() - read from JSON, merge with env vars
3. Implement saveConfig() - write to JSON
4. Add validation (throw ConfigException on missing required fields)
5. Update main.cpp to load config early
6. Pass config (or parts of it) to functions that need it

**ChatGPT Feedback Applied:**
> "Config as struct, not singleton - better for testing"

### Task 2.2: Download History Tracking

**Priority:** High  
**Estimated Time:** 2-3 hours  
**Status:** Not started

**Problem:**
- Re-downloads files that already exist
- No way to know what was downloaded when
- Can't implement "download only updates"

**Solution:**
Create a database that tracks every download:

```cpp
// include/core/Database.h
struct DownloadRecord {
    std::string game_domain;
    int mod_id;
    int file_id;
    std::string filename;
    size_t size;
    std::string md5;
    std::chrono::system_clock::time_point downloaded_at;
    std::string source;  // "nexus" or "gamebanana"
    std::optional<int> category_id;
};

class Database {
public:
    explicit Database(const fs::path& db_path);
    
    // Record operations
    void recordDownload(const DownloadRecord& record);
    std::optional<DownloadRecord> getDownload(const std::string& game, 
                                              int mod_id, int file_id);
    std::vector<DownloadRecord> getModDownloads(const std::string& game, 
                                                int mod_id);
    bool hasDownloaded(const std::string& game, int mod_id, int file_id);
    
    // Persistence
    void save();
    void load();
};
```

**Storage Format:** JSON file at `~/.config/Modular/downloads.json`

```json
{
  "skyrimspecialedition": {
    "12345": [
      {
        "file_id": 67890,
        "filename": "example-1.0.zip",
        "size": 1048576,
        "md5": "d41d8cd98f00b204e9800998ecf8427e",
        "downloaded_at": "2026-01-25T07:23:50Z",
        "source": "nexus",
        "category_id": 1
      }
    ]
  }
}
```

**Usage:**
```cpp
Database db(Config::defaultConfigPath().parent_path() / "downloads.json");
db.load();

// Before downloading
if (db.hasDownloaded(game_domain, mod_id, file_id)) {
    logger.info("Already downloaded, skipping");
    continue;
}

// After successful download
DownloadRecord record;
record.game_domain = game_domain;
record.mod_id = mod_id;
record.file_id = file_id;
record.filename = filename;
record.size = file_size;
record.md5 = calculated_md5;
record.downloaded_at = std::chrono::system_clock::now();
record.source = "nexus";

db.recordDownload(record);
db.save();
```

**Files to Create:**
- `include/core/Database.h`
- `src/core/Database.cpp`

**Files to Modify:**
- `src/core/NexusMods.cpp` - Check database before downloading
- `src/core/GameBanana.cpp` - Record downloads

**ChatGPT Feedback Applied:**
> "Single DownloadRecord for both history and verification"

### Task 2.3: File Verification (MD5)

**Priority:** Medium  
**Estimated Time:** 1-2 hours  
**Status:** Not started  
**Depends on:** Task 2.2 (uses DownloadRecord.md5)

**Problem:**
- Downloads can be corrupted
- No way to verify file integrity
- Can't detect incomplete downloads

**Solution:**
Add MD5 verification after downloads:

```cpp
// include/core/Utils.h (new file)
namespace modular::utils {
    std::string calculateMD5(const fs::path& file);
    std::string formatBytes(size_t bytes);
    std::string formatDuration(std::chrono::seconds duration);
}
```

**For NexusMods:**
API provides MD5 in file info. Fetch it before download, verify after.

**For GameBanana:**
API sometimes provides `_sMd5Checksum`. Use if available.

**Implementation:**
1. Add OpenSSL or use a header-only MD5 library
2. Calculate MD5 after download
3. Compare with expected (from API or previous download)
4. If mismatch: delete file, throw exception, optionally retry

**Config Integration:**
```cpp
struct Config {
    bool verify_downloads = false;  // Off by default (performance)
};

// In download code
if (config.verify_downloads) {
    std::string actual_md5 = utils::calculateMD5(file_path);
    if (actual_md5 != expected_md5) {
        fs::remove(file_path);
        throw FileSystemException("MD5 mismatch: " + file_path.string());
    }
}
```

**Files to Create:**
- `include/core/Utils.h`
- `src/core/Utils.cpp`

**Files to Modify:**
- `src/core/NexusMods.cpp` - Add verification
- `src/core/GameBanana.cpp` - Add verification
- `CMakeLists.txt` - Link OpenSSL or MD5 library

### Task 2.4: Consolidate Duplicate Code

**Priority:** Medium  
**Estimated Time:** 1 hour  
**Status:** Not started

**Problem:**
- `sanitizeFilename()` exists in multiple places
- Similar code patterns repeated

**Solution:**
Move shared utilities to `Utils.h/cpp`:

```cpp
// include/core/Utils.h
namespace modular::utils {
    std::string sanitizeFilename(const std::string& name);
    std::string escapeSpaces(const std::string& url);
    std::string shortStatus(const std::string& s, size_t maxLen);
    std::string calculateMD5(const fs::path& file);
    std::string formatBytes(size_t bytes);
    std::string formatDuration(std::chrono::seconds duration);
}
```

**Files to Update:**
- `src/cli/main.cpp` - Remove `sanitizeFileName()`, use `utils::`
- `src/core/NexusMods.cpp` - Remove `escape_spaces()`, use `utils::`
- `src/core/GameBanana.cpp` - Remove `sanitizeFilename()`, use `utils::`

**Benefit:** Single source of truth, easier to test

### Task 2.5: Testing Foundation (Catch2)

**Priority:** Medium  
**Estimated Time:** 2-3 hours  
**Status:** Not started

**Goal:** Set up test framework and write critical tests

**Setup:**
```cmake
# CMakeLists.txt
include(FetchContent)
FetchContent_Declare(
    Catch2
    GIT_REPOSITORY https://github.com/catchorg/Catch2.git
    GIT_TAG v3.5.0
)
FetchContent_MakeAvailable(Catch2)

add_executable(modular-tests
    tests/test_main.cpp
    tests/test_rate_limiter.cpp
    tests/test_config.cpp
    tests/test_utils.cpp
)
target_link_libraries(modular-tests PRIVATE 
    modular-core 
    Catch2::Catch2WithMain
)
```

**Tests to Write:**

**test_rate_limiter.cpp:**
```cpp
TEST_CASE("RateLimiter parses headers case-insensitively") {
    NullLogger logger;
    RateLimiter limiter(logger);
    
    std::map<std::string, std::string> headers;
    headers["x-rl-daily-remaining"] = "100";  // lowercase
    headers["X-RL-Hourly-Remaining"] = "50";  // uppercase
    
    limiter.updateFromHeaders(headers);
    
    REQUIRE(limiter.getDailyRemaining() == 100);
    REQUIRE(limiter.getHourlyRemaining() == 50);
}

TEST_CASE("RateLimiter handles missing headers gracefully") {
    NullLogger logger;
    RateLimiter limiter(logger);
    
    std::map<std::string, std::string> headers;  // Empty
    
    REQUIRE_NOTHROW(limiter.updateFromHeaders(headers));
    REQUIRE(limiter.canMakeRequest());
}
```

**test_utils.cpp:**
```cpp
TEST_CASE("sanitizeFilename removes invalid characters") {
    REQUIRE(utils::sanitizeFilename("test/file") == "test_file");
    REQUIRE(utils::sanitizeFilename("a:b*c?d") == "a_b_c_d");
    REQUIRE(utils::sanitizeFilename("normal.zip") == "normal.zip");
}

TEST_CASE("formatBytes displays human-readable sizes") {
    REQUIRE(utils::formatBytes(1024) == "1.0 KB");
    REQUIRE(utils::formatBytes(1048576) == "1.0 MB");
    REQUIRE(utils::formatBytes(1073741824) == "1.0 GB");
}
```

**test_config.cpp:**
```cpp
TEST_CASE("Config loads from JSON") {
    // Create temp config
    json config_json = {
        {"nexus_api_key", "test_key"},
        {"mods_directory", "/tmp/mods"}
    };
    
    fs::path temp = fs::temp_directory_path() / "test_config.json";
    std::ofstream(temp) << config_json.dump();
    
    Config cfg = loadConfig(temp);
    
    REQUIRE(cfg.nexus_api_key == "test_key");
    REQUIRE(cfg.mods_directory == "/tmp/mods");
    
    fs::remove(temp);
}
```

**Files to Create:**
- `tests/test_main.cpp`
- `tests/test_rate_limiter.cpp`
- `tests/test_config.cpp`
- `tests/test_utils.cpp`

**Run Tests:**
```bash
cmake --build build --target modular-tests
./build/modular-tests
```

### Task 2.6: Update NexusMods to Use Config

**Priority:** High  
**Estimated Time:** 1 hour  
**Status:** Not started  
**Depends on:** Task 2.1

**Changes:**
```cpp
// OLD
extern std::string API_KEY;

std::vector<int> get_tracked_mods() {
    std::vector<std::string> headers = {
        "apikey: " + API_KEY
    };
}

// NEW
std::vector<int> get_tracked_mods(const Config& config) {
    std::vector<std::string> headers = {
        "apikey: " + config.nexus_api_key
    };
}
```

**Files to Update:**
- `include/core/NexusMods.h` - Add `const Config&` params
- `src/core/NexusMods.cpp` - Remove global `API_KEY`
- `src/cli/main.cpp` - Pass config to functions

### Task 2.7: Add --dry-run and --force Flags

**Priority:** Low  
**Estimated Time:** 1 hour  
**Status:** Not started  
**Depends on:** Task 2.2 (needs Database)

**Implementation:**
```cpp
struct CLIOptions {
    bool dry_run = false;
    bool force = false;
    bool verbose = false;
};

// In download logic
if (options.dry_run) {
    if (db.hasDownloaded(game, mod_id, file_id)) {
        std::cout << "SKIP (already downloaded): " << filename << "\n";
    } else {
        std::cout << "WOULD DOWNLOAD: " << filename << "\n";
    }
    continue;
}

if (!options.force && db.hasDownloaded(game, mod_id, file_id)) {
    logger.info("Already downloaded, skipping (use --force to re-download)");
    continue;
}
```

## Implementation Order

**Week 2, Day 1-2:**
1. Task 2.1: Config as struct
2. Task 2.6: Update NexusMods to use Config
3. Task 2.4: Consolidate duplicate code (Utils)

**Week 2, Day 3-4:**
4. Task 2.2: Download history tracking
5. Task 2.3: File verification (MD5)

**Week 2, Day 5:**
6. Task 2.5: Testing foundation (Catch2)
7. Task 2.7: Add --dry-run and --force flags

## Success Criteria

- ✅ Config loaded from JSON file
- ✅ API key no longer in global variable
- ✅ Downloads tracked in database
- ✅ Can skip already-downloaded files
- ✅ MD5 verification optional and working
- ✅ No duplicate utility functions
- ✅ Test suite runs and passes
- ✅ `--dry-run` shows what would be downloaded
- ✅ `--force` re-downloads existing files

## What Changed from Week 1

Week 1 focused on **architectural correctness** (HttpClient, RateLimiter, exceptions, core/UI separation).

Week 2 focuses on **operational improvements** (config, history, verification, testing).

These are complementary:
- Week 1 made the code **testable**
- Week 2 adds the **tests**
- Week 1 made the code **observable** (logging, exceptions)
- Week 2 adds **persistence** (config, database)

## Dependencies

```
Task 2.1 (Config)
  └─> Task 2.6 (Use Config in NexusMods)

Task 2.2 (Database)
  ├─> Task 2.3 (MD5 uses DownloadRecord)
  └─> Task 2.7 (--dry-run needs DB)

Task 2.4 (Utils)
  └─> Task 2.3 (MD5 in Utils)

Task 2.5 (Tests)
  └─> Task 2.1 (tests Config)
```

## Notes for Week 3+

After Week 2 is complete, the codebase will be ready for:

**Week 3:**
- CLI improvements (proper arg parser, subcommands)
- LiveUI improvements (terminal width, speed/ETA)
- API coverage (update checking, file info)

**Week 4+:**
- Download manager (concurrent downloads, resume)
- GUI foundation (restructure done in Week 1)
- Advanced features (category sorting, auto-rename)

## Estimated Total Time

- **Minimum:** 8-10 hours (skip testing, just config + database)
- **Recommended:** 12-15 hours (includes tests and verification)
- **Ideal:** 15-18 hours (includes all polish and documentation)

## References

- Week 1 Report: `WEEK1_IMPLEMENTATION.md`
- Original tasks: `TASKS.md`
- Improvements doc: `IMPROVEMENTS.md`
- ChatGPT feedback: Incorporated throughout
- Revised plan: Plan ID 44cc1e4e-e7ce-43c1-a712-7aaa1ff6d10b

## Conclusion

Week 2 builds on Week 1's solid architectural foundation by adding practical operational features. The focus shifts from "how the code is structured" to "what the code can do."

After Week 2:
- Configuration will be manageable and testable
- Downloads will be tracked and verifiable
- The codebase will have test coverage
- Users will have `--dry-run` and `--force` options
- The code will be more maintainable (no duplication)

This sets the stage for Week 3's user-facing improvements and Week 4's advanced features.
