# Modular - Improvement Instructions

This document outlines improvements identified by comparing the codebase against API documentation.

---

## 1. Rate Limiting & API Compliance

### NexusMods Rate Limiting (CRITICAL)

The NexusMods API enforces strict rate limits:
- **20,000 requests per 24-hour period**
- **500 requests per hour** after daily limit reached
- Resets at 00:00 GMT (daily) and on the hour (hourly)

**Current Issues:**
- `NexusMods.cpp` uses hardcoded delays (`sleep_for(seconds(1))`) without tracking actual limits
- No parsing of rate limit headers from API responses
- No warning when approaching limits

**Improvements:**
1. Parse rate limit headers from every response:
   - `X-RL-Daily-Limit` / `X-RL-Daily-Remaining`
   - `X-RL-Hourly-Limit` / `X-RL-Hourly-Remaining`
2. Add a `RateLimiter` class that tracks remaining requests
3. Implement backoff when limits are low
4. Display remaining API calls in LiveUI
5. Save/restore rate limit state between sessions

**Location:** `src/NexusMods.cpp`, new file `src/RateLimiter.cpp`

---

## 2. HTTP Client Improvements

### Shared HTTP Infrastructure

**Current Issues:**
- Duplicate `WriteCallback` functions in both `NexusMods.cpp` and `GameBanana.cpp`
- Inconsistent error handling between modules
- No retry logic in GameBanana (NexusMods has 5-retry logic)
- CURL initialization scattered across files

**Improvements:**
1. Create unified `HttpClient` class in `src/HttpClient.cpp`:
   ```cpp
   class HttpClient {
       static HttpResponse get(const std::string& url, const Headers& headers = {});
       static bool downloadFile(const std::string& url, const fs::path& outputPath, ProgressCallback cb = nullptr);
       static void setUserAgent(const std::string& ua);
   };
   ```
2. Implement configurable retry logic with exponential backoff
3. Add timeout configuration
4. Support progress callbacks for downloads (integrate with LiveUI)
5. Centralize CURL global init/cleanup in `main.cpp`

**Location:** New files `src/HttpClient.cpp`, `include/HttpClient.h`

---

## 3. NexusMods API Coverage

### Missing Endpoints

The current implementation only uses a subset of the API. Add support for:

| Endpoint | Purpose | Priority |
|----------|---------|----------|
| `getModInfo` | Get detailed mod metadata (description, author, etc.) | High |
| `getLatestUpdated` | Check for mod updates | High |
| `getRecentlyUpdatedMods` | Bulk update checking (1d/1w/1m periods) | Medium |
| `getFileInfo` | Get specific file details before download | Medium |
| `getFileByMD5` | Verify downloaded files / find duplicates | Medium |
| `validateKey` | Verify API key is valid on startup | High |
| `getEndorsements` | Show user's endorsement status | Low |
| `endorseMod` | Allow endorsing from CLI | Low |
| `trackMod/untrackMod` | Manage tracked mods programmatically | Medium |

**Specific Improvements:**

1. **Mod Update Checking:** Add `--check-updates` flag that uses `getRecentlyUpdatedMods` to efficiently check for updates across all tracked mods

2. **API Key Validation:** On startup, call `validateKey` to verify the key works and display user info

3. **File Verification:** After download, optionally verify MD5 hash using `getFileByMD5`

**Location:** `src/NexusMods.cpp`, `include/NexusMods.h`

---

## 4. GameBanana API Coverage

### Missing Functionality

**Current Implementation:**
- Only fetches subscriptions and downloads files
- No game filtering
- No update checking

**Improvements:**

1. **Game Filtering:** Add ability to filter subscriptions by game:
   ```
   GET https://gamebanana.com/apiv11/Member/{userId}/Subscriptions?_aFilters[Generic_Game]={gameId}
   ```

2. **Update Checking:** Compare `_tsDateUpdated` against last download timestamp

3. **Better Mod Discovery:** Add support for browsing mods:
   ```
   GET https://gamebanana.com/apiv11/Mod/Index?_aFilters[Generic_Game]={gameId}&_nPerpage=50
   ```

4. **Mod Details:** Fetch full mod info including description, author, download count

5. **Store Game ID Mapping:** Create a local database/config mapping game names to GameBanana IDs

**Location:** `src/GameBanana.cpp`, `include/GameBanana.h`

---

## 5. Error Handling & Resilience

### Current Issues

- Silent failures in many API calls (empty catch blocks)
- No distinction between network errors and API errors
- JSON parse errors silently ignored
- No retry on transient failures

### Improvements

1. **Custom Exception Hierarchy:**
   ```cpp
   class ModularException : public std::exception { ... };
   class NetworkException : public ModularException { ... };
   class ApiException : public ModularException { ... };  // HTTP 4xx/5xx
   class RateLimitException : public ApiException { ... };
   class AuthException : public ApiException { ... };     // 401/403
   class ParseException : public ModularException { ... };
   ```

2. **Structured Error Responses:** Parse API error messages from JSON responses

3. **Retry Strategy:** Implement retry with exponential backoff for:
   - Network timeouts
   - 5xx server errors
   - 429 rate limit (with respect to Retry-After header)

4. **Logging:** Add optional verbose/debug logging mode

**Location:** New file `include/Exceptions.h`, updates throughout

---

## 6. Configuration System

### Current State

- API key loaded from env or file, but limited configuration
- No persistent settings
- Hardcoded paths (`~/Games/Mods-Lists/`)

### Improvements

1. **Config File Support:** Create `~/.config/Modular/config.json`:
   ```json
   {
     "nexusmods": {
       "api_key": "...",
       "default_categories": ["main", "optional"]
     },
     "gamebanana": {
       "user_id": "..."
     },
     "storage": {
       "mods_directory": "~/Games/Mods-Lists"
     },
     "preferences": {
       "auto_rename": true,
       "verify_downloads": false,
       "max_concurrent_downloads": 3
     }
   }
   ```

2. **CLI Override:** Allow config values to be overridden via CLI flags

3. **Config Validation:** Validate config on load, provide helpful error messages

**Location:** New files `src/Config.cpp`, `include/Config.h`

---

## 7. Download Management

### Current Limitations

- Downloads are sequential
- No resume capability
- No integrity verification
- Progress only shown per-operation, not per-file

### Improvements

1. **Concurrent Downloads:** Use thread pool for parallel downloads (respect rate limits)

2. **Resume Support:** Check existing file size, use `Range` header for partial downloads

3. **Integrity Verification:**
   - For NexusMods: Use MD5 from file info
   - For GameBanana: `_sMd5Checksum` field (when available)

4. **Download Queue:** Implement a proper queue system:
   ```cpp
   class DownloadManager {
       void enqueue(DownloadTask task);
       void start(int concurrency = 1);
       void pause();
       void resume();
       float getProgress();
   };
   ```

5. **Per-File Progress:** Pass progress callbacks to CURL for real-time progress

**Location:** New file `src/DownloadManager.cpp`

---

## 8. CLI Improvements

### Current State

- Basic CLI with `--categories` flag
- Interactive menu mode

### Improvements

1. **Expanded CLI Flags:**
   ```
   ./Modular <game_domain> [options]
   
   Options:
     --categories <list>    Filter by categories (default: main,optional)
     --check-updates        Check for mod updates without downloading
     --dry-run              Show what would be downloaded
     --force                Re-download existing files
     --output <dir>         Override output directory
     --verbose              Enable verbose logging
     --config <file>        Use alternate config file
     --gamebanana           Use GameBanana instead of NexusMods
     --list-games           List available game domains
   ```

2. **Subcommands:**
   ```
   ./Modular download <game>     Download mods
   ./Modular update <game>       Check/download updates
   ./Modular rename <game>       Rename mod folders
   ./Modular list                List tracked mods
   ./Modular config              Show/edit configuration
   ```

3. **Argument Parser:** Use a proper argument parsing library or implement robust parsing

**Location:** `src/main.cpp`, new file `src/CLI.cpp`

---

## 9. LiveUI Improvements

### Current Limitations

- Fixed 2-line display
- No individual file progress
- Terminal width not considered

### Improvements

1. **Terminal Width Detection:** Adjust progress bar to terminal width

2. **Multi-Line Status:** Show multiple concurrent operations:
   ```
   [##########          ] 50% Downloading: ModName1.zip (2.3 MB / 4.6 MB)
   [################    ] 80% Downloading: ModName2.zip (8.0 MB / 10.0 MB)
   [                    ]  0% Queued: ModName3.zip
   Overall: 5/20 files complete
   ```

3. **Speed/ETA Display:** Show download speed and estimated time remaining

4. **Non-TTY Mode:** Detect when not connected to terminal, use simple line output

**Location:** `src/LiveUI.cpp`, `include/LiveUI.h`

---

## 10. Data Persistence & Caching

### Current State

- Download links saved to text file
- No caching of API responses
- No tracking of downloaded files

### Improvements

1. **Download History Database:** Track what has been downloaded:
   ```json
   {
     "skyrimspecialedition": {
       "12345": {
         "mod_name": "Example Mod",
         "files": [
           {"file_id": 67890, "filename": "example.zip", "downloaded_at": "2024-01-01T12:00:00Z", "md5": "..."}
         ]
       }
     }
   }
   ```

2. **API Response Cache:** Cache mod info responses with TTL (reduce API calls)

3. **Incremental Sync:** Only download new/updated files based on history

**Location:** New files `src/Database.cpp`, `include/Database.h`

---

## 11. Code Quality

### Refactoring Tasks

1. **Consolidate Duplicate Code:**
   - `sanitizeFilename` exists in both `main.cpp` and `GameBanana.cpp`
   - HTTP callback functions duplicated

2. **Const Correctness:** Add `const` to function parameters and methods where appropriate

3. **Use Modern C++ Features:**
   - Replace raw loops with `<algorithm>` where clearer
   - Use `std::optional` for potentially missing values
   - Use `string_view` for string parameters that aren't modified

4. **Namespace Organization:** Put all code under `modular::` namespace

5. **Header Organization:** Forward declare where possible to reduce compile times

---

## 12. Testing

### Current State

- No tests exist

### Improvements

1. **Unit Tests:** Add tests for:
   - URL parsing/construction
   - JSON response parsing
   - Filename sanitization
   - Rate limit tracking

2. **Integration Tests:** Mock HTTP responses to test full workflows

3. **Test Framework:** Consider Google Test or Catch2

**Location:** New directory `tests/`

---

## Priority Order

1. **High Priority (Stability & Compliance):**
   - Rate limiting implementation (#1)
   - Error handling improvements (#5)
   - API key validation (#3)

2. **Medium Priority (Usability):**
   - Configuration system (#6)
   - CLI improvements (#8)
   - Shared HTTP client (#2)

3. **Lower Priority (Features):**
   - Additional API endpoints (#3, #4)
   - Download management (#7)
   - Data persistence (#10)
   - LiveUI improvements (#9)

---

## 13. Graphical User Interface

### Framework Options

| Framework | Pros | Cons | Best For |
|-----------|------|------|----------|
| **Qt 6** | Full-featured, cross-platform, professional look, excellent docs | Large dependency, licensing (LGPL/commercial) | Full-featured desktop app |
| **Dear ImGui** | Lightweight, fast iteration, immediate mode, easy to embed | Less native look, requires render backend | Developer tools, quick UI |
| **GTK 4/gtkmm** | Native Linux look, good theming, C++ bindings | Linux-focused, less cross-platform | Linux-first apps |
| **wxWidgets** | Native look on all platforms, mature | Dated API, verbose | Cross-platform without Qt |

**Recommendation:** **Dear ImGui** with SDL2/OpenGL backend for rapid development, or **Qt 6** for a polished end-user application.

### Architecture Changes Required

1. **Separate Core Logic from UI:**
   ```
   src/
   ├── core/           # Business logic (API, downloads, config)
   │   ├── NexusMods.cpp
   │   ├── GameBanana.cpp
   │   ├── DownloadManager.cpp
   │   └── Config.cpp
   ├── cli/            # Command-line interface
   │   ├── main_cli.cpp
   │   └── LiveUI.cpp
   └── gui/            # Graphical interface
       ├── main_gui.cpp
       ├── MainWindow.cpp
       ├── ModListView.cpp
       ├── DownloadPanel.cpp
       └── SettingsDialog.cpp
   ```

2. **Event-Driven Architecture:**
   - Core emits signals/events for progress, completion, errors
   - UI subscribes to events and updates display
   - Async operations don't block UI thread

3. **Build Targets:**
   - `modular-cli` - Command-line version
   - `modular-gui` - Graphical version
   - `libmodular` - Shared library with core logic

### GUI Features

**Main Window:**
- Game selector dropdown
- Mod list with columns: Name, Status, Size, Last Updated
- Search/filter bar
- Source tabs (NexusMods / GameBanana)

**Mod List View:**
- Checkbox selection for batch operations
- Status indicators (downloaded, update available, new)
- Context menu (download, update, remove, open page)
- Sorting by any column

**Download Panel:**
- Active downloads with progress bars
- Download queue
- Speed and ETA display
- Pause/resume/cancel buttons

**Settings Dialog:**
- API key configuration
- Download directory selection
- Concurrent download limit
- Category filters
- Theme selection

**System Tray (optional):**
- Background download support
- Notifications for completed downloads
- Quick access menu

### Threading Model

```cpp
// Core runs downloads in background threads
class DownloadManager {
    // Signals emitted from worker thread
    Signal<DownloadProgress> onProgress;
    Signal<DownloadComplete> onComplete;
    Signal<DownloadError> onError;
};

// GUI connects to signals (Qt example)
connect(downloadManager, &DownloadManager::onProgress,
        this, &MainWindow::updateProgress, Qt::QueuedConnection);
```

### Dear ImGui Quick-Start Structure

```cpp
// main_gui.cpp with Dear ImGui + SDL2 + OpenGL
int main() {
    // Initialize SDL2 + OpenGL
    // Initialize ImGui
    
    while (running) {
        // Poll events
        // Start ImGui frame
        
        // Main menu bar
        if (ImGui::BeginMainMenuBar()) {
            if (ImGui::BeginMenu("File")) {
                if (ImGui::MenuItem("Settings")) showSettings = true;
                if (ImGui::MenuItem("Exit")) running = false;
                ImGui::EndMenu();
            }
            ImGui::EndMainMenuBar();
        }
        
        // Game selector
        ImGui::Combo("Game", &selectedGame, games, gameCount);
        
        // Mod table
        if (ImGui::BeginTable("Mods", 4, tableFlags)) {
            ImGui::TableSetupColumn("Name");
            ImGui::TableSetupColumn("Status");
            ImGui::TableSetupColumn("Size");
            ImGui::TableSetupColumn("Actions");
            ImGui::TableHeadersRow();
            
            for (auto& mod : mods) {
                ImGui::TableNextRow();
                // ... render mod row
            }
            ImGui::EndTable();
        }
        
        // Download progress
        for (auto& dl : activeDownloads) {
            ImGui::ProgressBar(dl.progress, ImVec2(-1, 0), dl.filename.c_str());
        }
        
        // Render
        ImGui::Render();
        // Swap buffers
    }
}
```

### Qt Quick-Start Structure

```cpp
// MainWindow.h
class MainWindow : public QMainWindow {
    Q_OBJECT
public:
    explicit MainWindow(QWidget* parent = nullptr);
    
private slots:
    void onGameChanged(int index);
    void onDownloadClicked();
    void onProgressUpdate(int modId, float progress);
    
private:
    QComboBox* gameSelector_;
    QTableView* modTable_;
    ModListModel* modModel_;
    QProgressBar* downloadProgress_;
    DownloadManager* downloadManager_;
};
```

### Dependencies to Add

**For Dear ImGui:**
```cmake
# CMakeLists.txt
find_package(SDL2 REQUIRED)
find_package(OpenGL REQUIRED)

FetchContent_Declare(
    imgui
    GIT_REPOSITORY https://github.com/ocornut/imgui.git
    GIT_TAG v1.90.1
)
FetchContent_MakeAvailable(imgui)
```

**For Qt 6:**
```cmake
find_package(Qt6 REQUIRED COMPONENTS Widgets Network)
target_link_libraries(modular-gui PRIVATE Qt6::Widgets Qt6::Network)
```

---

## Implementation Notes

- Maintain backward compatibility with existing directory structure
- All new features should be optional/configurable
- Preserve the simple menu-driven interface as default
- Document all new CLI flags in `--help` output
- Build both CLI and GUI versions from same codebase
- GUI should work without requiring CLI knowledge
