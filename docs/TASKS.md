# Modular - Implementation Task List

Step-by-step tasks for implementing improvements from `IMPROVEMENTS.md`.

---

## Phase 1: Foundation & Stability

### Task 1.1: Create Shared HTTP Client
**Estimated time:** 2-3 hours
**Files:** `include/HttpClient.h`, `src/HttpClient.cpp`

**Steps:**
1. Create `include/HttpClient.h`:
   ```cpp
   #pragma once
   #include <string>
   #include <vector>
   #include <functional>
   #include <map>
   
   struct HttpResponse {
       long status_code;
       std::string body;
       std::map<std::string, std::string> headers;
   };
   
   using ProgressCallback = std::function<void(size_t downloaded, size_t total)>;
   using Headers = std::vector<std::string>;
   
   class HttpClient {
   public:
       static void globalInit();
       static void globalCleanup();
       
       static HttpResponse get(const std::string& url, const Headers& headers = {});
       static bool downloadFile(const std::string& url, const std::string& outputPath, 
                                ProgressCallback progress = nullptr);
       
       static void setRetryCount(int retries);
       static void setTimeout(int seconds);
   };
   ```

2. Create `src/HttpClient.cpp`:
   - Move `WriteCallback` from NexusMods.cpp
   - Add header parsing callback to capture response headers
   - Implement retry logic with exponential backoff (1s, 2s, 4s, 8s, 16s)
   - Add progress callback support using `CURLOPT_XFERINFOFUNCTION`

3. Update `CMakeLists.txt` to include new source file

4. Move CURL init to `main.cpp`:
   ```cpp
   int main() {
       HttpClient::globalInit();
       // ... existing code ...
       HttpClient::globalCleanup();
   }
   ```

5. Refactor `NexusMods.cpp` to use `HttpClient::get()`

6. Refactor `GameBanana.cpp` to use `HttpClient::get()` and `HttpClient::downloadFile()`

7. Remove duplicate `WriteCallback` functions from both files

8. Build and test: `cmake --preset default && cmake --build build`

---

### Task 1.2: Implement Rate Limiter
**Estimated time:** 2 hours
**Files:** `include/RateLimiter.h`, `src/RateLimiter.cpp`
**Depends on:** Task 1.1

**Steps:**
1. Create `include/RateLimiter.h`:
   ```cpp
   #pragma once
   #include <chrono>
   #include <string>
   
   class RateLimiter {
   public:
       void updateFromHeaders(const std::map<std::string, std::string>& headers);
       bool canMakeRequest() const;
       void waitIfNeeded();
       
       int getDailyRemaining() const;
       int getHourlyRemaining() const;
       std::chrono::system_clock::time_point getDailyReset() const;
       std::chrono::system_clock::time_point getHourlyReset() const;
       
       void saveState(const std::string& path);
       void loadState(const std::string& path);
       
   private:
       int daily_limit_ = 20000;
       int daily_remaining_ = 20000;
       int hourly_limit_ = 500;
       int hourly_remaining_ = 500;
       std::chrono::system_clock::time_point daily_reset_;
       std::chrono::system_clock::time_point hourly_reset_;
   };
   ```

2. Create `src/RateLimiter.cpp`:
   - Parse headers: `X-RL-Daily-Remaining`, `X-RL-Hourly-Remaining`, etc.
   - Implement `waitIfNeeded()` to sleep until reset if limits exhausted
   - Save/load state to `~/.config/Modular/rate_limit_state.json`

3. Integrate into `NexusMods.cpp`:
   ```cpp
   extern RateLimiter g_rateLimiter;
   
   HttpResponse http_get(...) {
       g_rateLimiter.waitIfNeeded();
       auto response = HttpClient::get(url, headers);
       g_rateLimiter.updateFromHeaders(response.headers);
       return response;
   }
   ```

4. Display remaining requests in LiveUI status line

5. Test by making requests and verifying header parsing

---

### Task 1.3: Create Exception Hierarchy
**Estimated time:** 1 hour
**Files:** `include/Exceptions.h`

**Steps:**
1. Create `include/Exceptions.h`:
   ```cpp
   #pragma once
   #include <stdexcept>
   #include <string>
   
   namespace modular {
   
   class Exception : public std::runtime_error {
   public:
       explicit Exception(const std::string& msg) : std::runtime_error(msg) {}
   };
   
   class NetworkException : public Exception {
   public:
       explicit NetworkException(const std::string& msg) : Exception(msg) {}
   };
   
   class ApiException : public Exception {
   public:
       ApiException(int status, const std::string& msg) 
           : Exception(msg), status_code_(status) {}
       int statusCode() const { return status_code_; }
   private:
       int status_code_;
   };
   
   class RateLimitException : public ApiException {
   public:
       explicit RateLimitException(const std::string& msg) 
           : ApiException(429, msg) {}
   };
   
   class AuthException : public ApiException {
   public:
       explicit AuthException(const std::string& msg) 
           : ApiException(401, msg) {}
   };
   
   class ParseException : public Exception {
   public:
       explicit ParseException(const std::string& msg) : Exception(msg) {}
   };
   
   } // namespace modular
   ```

2. Update `HttpClient.cpp` to throw appropriate exceptions:
   - `NetworkException` on CURL errors
   - `ApiException` on 4xx/5xx responses
   - `RateLimitException` on 429

3. Update `NexusMods.cpp` and `GameBanana.cpp`:
   - Replace empty catch blocks with proper error handling
   - Log errors or propagate to caller

4. Update `main.cpp` to catch and display user-friendly error messages

---

### Task 1.4: Add API Key Validation
**Estimated time:** 30 minutes
**Files:** `src/NexusMods.cpp`, `include/NexusMods.h`
**Depends on:** Task 1.1, 1.3

**Steps:**
1. Add to `include/NexusMods.h`:
   ```cpp
   struct UserInfo {
       std::string name;
       bool is_premium;
       bool is_supporter;
   };
   
   std::optional<UserInfo> validateApiKey();
   ```

2. Implement in `src/NexusMods.cpp`:
   ```cpp
   std::optional<UserInfo> validateApiKey() {
       std::string url = "https://api.nexusmods.com/v1/users/validate.json";
       // ... make request and parse response
   }
   ```

3. Call on startup in `main.cpp` before any other NexusMods operations:
   ```cpp
   auto user = validateApiKey();
   if (!user) {
       std::cerr << "Invalid API key\n";
       return 1;
   }
   std::cout << "Logged in as: " << user->name << "\n";
   ```

4. Test with valid and invalid API keys

---

## Phase 2: Configuration & CLI

### Task 2.1: Create Configuration System
**Estimated time:** 2-3 hours
**Files:** `include/Config.h`, `src/Config.cpp`

**Steps:**
1. Create `include/Config.h`:
   ```cpp
   #pragma once
   #include <string>
   #include <vector>
   #include <filesystem>
   
   struct Config {
       // NexusMods
       std::string nexus_api_key;
       std::vector<std::string> default_categories = {"main", "optional"};
       
       // GameBanana
       std::string gamebanana_user_id;
       
       // Storage
       std::filesystem::path mods_directory;
       
       // Preferences
       bool auto_rename = true;
       bool verify_downloads = false;
       int max_concurrent_downloads = 1;
       bool verbose = false;
       
       static Config& instance();
       void load(const std::filesystem::path& path = "");
       void save(const std::filesystem::path& path = "");
       static std::filesystem::path defaultPath();
   };
   ```

2. Create `src/Config.cpp`:
   - Default path: `~/.config/Modular/config.json`
   - Use nlohmann/json for serialization
   - Merge file config with environment variables (env takes precedence)
   - Validate required fields

3. Update `main.cpp`:
   - Load config early in `main()`
   - Replace `getApiKey()` with `Config::instance().nexus_api_key`
   - Replace `getDefaultModsDirectory()` with config value

4. Update `GameBanana.cpp`:
   - Use `Config::instance().gamebanana_user_id` instead of env var

5. Create default config file on first run if it doesn't exist

---

### Task 2.2: Improve CLI Argument Parsing
**Estimated time:** 2 hours
**Files:** `src/main.cpp`, new `include/CLI.h`, `src/CLI.cpp`

**Steps:**
1. Create `include/CLI.h`:
   ```cpp
   #pragma once
   #include <string>
   #include <vector>
   #include <optional>
   
   struct CLIOptions {
       std::string command;  // download, update, rename, list, config, ""
       std::vector<std::string> game_domains;
       std::string categories = "main,optional";
       std::optional<std::string> output_dir;
       std::optional<std::string> config_file;
       bool dry_run = false;
       bool force = false;
       bool verbose = false;
       bool use_gamebanana = false;
       bool show_help = false;
       bool show_version = false;
   };
   
   CLIOptions parseArgs(int argc, char* argv[]);
   void printHelp();
   void printVersion();
   ```

2. Create `src/CLI.cpp`:
   - Parse `--flag` and `--flag=value` and `--flag value` formats
   - Support `-h`, `--help`, `-v`, `--version`
   - Support subcommands: `download`, `update`, `rename`, `list`
   - Validate arguments and print errors for unknown flags

3. Update `main.cpp`:
   ```cpp
   int main(int argc, char* argv[]) {
       auto opts = parseArgs(argc, argv);
       
       if (opts.show_help) { printHelp(); return 0; }
       if (opts.show_version) { printVersion(); return 0; }
       
       Config::instance().load(opts.config_file.value_or(""));
       if (opts.verbose) Config::instance().verbose = true;
       
       // Route to appropriate function based on command
   }
   ```

4. Add `--help` output documenting all flags

---

### Task 2.3: Add Subcommands
**Estimated time:** 1-2 hours
**Files:** `src/main.cpp`
**Depends on:** Task 2.2

**Steps:**
1. Refactor menu functions into standalone commands:
   ```cpp
   int cmdDownload(const CLIOptions& opts);
   int cmdUpdate(const CLIOptions& opts);
   int cmdRename(const CLIOptions& opts);
   int cmdList(const CLIOptions& opts);
   int cmdConfig(const CLIOptions& opts);
   ```

2. Update `main()` to dispatch based on command:
   ```cpp
   if (opts.command == "download") return cmdDownload(opts);
   if (opts.command == "update") return cmdUpdate(opts);
   // etc.
   ```

3. Keep menu mode as default when no command/args provided

4. Test each subcommand works correctly

---

## Phase 3: API Coverage

### Task 3.1: Add NexusMods Update Checking
**Estimated time:** 2 hours
**Files:** `src/NexusMods.cpp`, `include/NexusMods.h`
**Depends on:** Task 1.1, 1.2

**Steps:**
1. Add to `include/NexusMods.h`:
   ```cpp
   struct ModUpdateInfo {
       int mod_id;
       std::string name;
       int latest_file_id;
       time_t latest_update;
   };
   
   std::vector<ModUpdateInfo> getRecentlyUpdatedMods(
       const std::string& game_domain, 
       const std::string& period = "1w"  // 1d, 1w, 1m
   );
   
   std::vector<ModUpdateInfo> checkForUpdates(
       const std::string& game_domain,
       const std::vector<int>& mod_ids
   );
   ```

2. Implement `getRecentlyUpdatedMods()`:
   - Call `https://api.nexusmods.com/v1/games/{domain}/mods/updated.json?period={period}`
   - Parse response into `ModUpdateInfo` structs

3. Implement `checkForUpdates()`:
   - Cross-reference tracked mods with recently updated list
   - Return only mods that have updates

4. Add `--check-updates` flag handling in CLI

5. Display update info in user-friendly format

---

### Task 3.2: Add GameBanana Game Filtering
**Estimated time:** 1 hour
**Files:** `src/GameBanana.cpp`, `include/GameBanana.h`

**Steps:**
1. Add game ID parameter to `fetchSubscribedMods()`:
   ```cpp
   std::vector<...> fetchSubscribedMods(
       const std::string& userId,
       std::optional<int> gameId = std::nullopt
   );
   ```

2. If gameId provided, append filter to URL:
   ```cpp
   url += "?_aFilters[Generic_Game]=" + std::to_string(*gameId);
   ```

3. Create mapping file `data/gamebanana_games.json`:
   ```json
   {
     "skyrim": 110,
     "fallout4": 1234,
     ...
   }
   ```

4. Add CLI option `--game` for GameBanana filtering

---

### Task 3.3: Add File Verification
**Estimated time:** 1-2 hours
**Files:** `src/NexusMods.cpp`, `src/GameBanana.cpp`
**Depends on:** Task 2.1

**Steps:**
1. Add MD5 calculation function in `HttpClient.cpp` or new utility file:
   ```cpp
   std::string calculateMD5(const std::filesystem::path& file);
   ```

2. For NexusMods, add `getFileInfo()`:
   ```cpp
   struct FileInfo {
       int file_id;
       std::string name;
       std::string md5;
       size_t size;
   };
   
   FileInfo getFileInfo(const std::string& game_domain, int mod_id, int file_id);
   ```

3. After download, verify MD5 if `Config::instance().verify_downloads` is true

4. If verification fails, delete file and report error (or retry)

---

## Phase 4: Download Management

### Task 4.1: Add Download Progress Callbacks
**Estimated time:** 1-2 hours
**Files:** `src/HttpClient.cpp`, `src/LiveUI.cpp`
**Depends on:** Task 1.1

**Steps:**
1. In `HttpClient::downloadFile()`, use CURL progress callback:
   ```cpp
   curl_easy_setopt(curl, CURLOPT_XFERINFOFUNCTION, progressCallback);
   curl_easy_setopt(curl, CURLOPT_XFERINFODATA, &userProgressData);
   curl_easy_setopt(curl, CURLOPT_NOPROGRESS, 0L);
   ```

2. Create progress struct:
   ```cpp
   struct DownloadProgress {
       size_t downloaded;
       size_t total;
       std::string filename;
   };
   ```

3. Update LiveUI to display byte progress:
   ```cpp
   void setDownloadProgress(const std::string& filename, size_t current, size_t total);
   ```

4. Format as: `Downloading: file.zip (2.3 MB / 10.5 MB)`

5. Update NexusMods and GameBanana download code to pass callbacks

---

### Task 4.2: Add Download Resume Support
**Estimated time:** 1-2 hours
**Files:** `src/HttpClient.cpp`
**Depends on:** Task 4.1

**Steps:**
1. Before download, check if partial file exists:
   ```cpp
   if (fs::exists(outputPath)) {
       existing_size = fs::file_size(outputPath);
   }
   ```

2. If partial file exists, use Range header:
   ```cpp
   curl_easy_setopt(curl, CURLOPT_RESUME_FROM_LARGE, existing_size);
   ```

3. Open file in append mode instead of write mode

4. Handle 416 (Range Not Satisfiable) by restarting download

5. Test by interrupting a download and resuming

---

### Task 4.3: Add Download History Tracking
**Estimated time:** 2 hours
**Files:** `include/Database.h`, `src/Database.cpp`
**Depends on:** Task 2.1

**Steps:**
1. Create `include/Database.h`:
   ```cpp
   #pragma once
   #include <string>
   #include <optional>
   #include <chrono>
   
   struct DownloadRecord {
       int mod_id;
       int file_id;
       std::string filename;
       std::string md5;
       std::chrono::system_clock::time_point downloaded_at;
   };
   
   class Database {
   public:
       static Database& instance();
       
       void recordDownload(const std::string& game, const DownloadRecord& record);
       std::optional<DownloadRecord> getDownload(const std::string& game, int mod_id, int file_id);
       std::vector<DownloadRecord> getModDownloads(const std::string& game, int mod_id);
       bool hasDownloaded(const std::string& game, int mod_id, int file_id);
       
       void save();
       void load();
       
   private:
       std::filesystem::path dbPath();
   };
   ```

2. Implement using JSON file at `~/.config/Modular/downloads.json`

3. Before downloading, check if already downloaded (skip if `--force` not set)

4. After successful download, record in database

5. Update `--dry-run` to show what would be downloaded vs skipped

---

## Phase 5: Code Quality

### Task 5.1: Consolidate Duplicate Code
**Estimated time:** 1 hour
**Files:** Multiple

**Steps:**
1. Move `sanitizeFilename()` to new `include/Utils.h`:
   ```cpp
   #pragma once
   #include <string>
   
   namespace modular::utils {
       std::string sanitizeFilename(const std::string& name);
       std::string escapeSpaces(const std::string& url);
       std::string shortStatus(const std::string& s, size_t maxLen);
   }
   ```

2. Create `src/Utils.cpp` with implementations

3. Update `main.cpp` and `GameBanana.cpp` to use shared function

4. Move `escape_spaces()` from `NexusMods.cpp` to Utils

5. Move `short_status()` from `main.cpp` to Utils

6. Build and verify no regressions

---

### Task 5.2: Add Namespace
**Estimated time:** 30 minutes
**Files:** All source files

**Steps:**
1. Wrap all code in `namespace modular { ... }`

2. Update includes and forward declarations as needed

3. Use `using namespace modular;` in main.cpp if needed for brevity

4. Verify builds and runs correctly

---

### Task 5.3: Improve LiveUI Terminal Handling
**Estimated time:** 1 hour
**Files:** `src/LiveUI.cpp`, `include/LiveUI.h`

**Steps:**
1. Add terminal width detection:
   ```cpp
   #include <sys/ioctl.h>
   
   int getTerminalWidth() {
       struct winsize w;
       if (ioctl(STDOUT_FILENO, TIOCGWINSZ, &w) == 0) {
           return w.ws_col;
       }
       return 80;  // fallback
   }
   ```

2. Adjust progress bar width based on terminal width

3. Add TTY detection:
   ```cpp
   bool isTTY() { return isatty(STDOUT_FILENO); }
   ```

4. If not TTY, use simple line-based output instead of ANSI repainting

5. Test in both terminal and piped output scenarios

---

## Phase 6: Testing

### Task 6.1: Set Up Test Framework
**Estimated time:** 1 hour
**Files:** `CMakeLists.txt`, `tests/`

**Steps:**
1. Add Catch2 as dependency (header-only):
   ```cmake
   include(FetchContent)
   FetchContent_Declare(
       Catch2
       GIT_REPOSITORY https://github.com/catchorg/Catch2.git
       GIT_TAG v3.4.0
   )
   FetchContent_MakeAvailable(Catch2)
   ```

2. Create `tests/CMakeLists.txt`:
   ```cmake
   add_executable(tests
       test_main.cpp
       test_utils.cpp
       test_rate_limiter.cpp
   )
   target_link_libraries(tests PRIVATE Catch2::Catch2WithMain)
   ```

3. Create `tests/test_main.cpp`:
   ```cpp
   #define CATCH_CONFIG_MAIN
   #include <catch2/catch_all.hpp>
   ```

4. Add test target to build

---

### Task 6.2: Write Unit Tests
**Estimated time:** 2-3 hours
**Files:** `tests/test_*.cpp`
**Depends on:** Task 6.1

**Steps:**
1. Create `tests/test_utils.cpp`:
   ```cpp
   TEST_CASE("sanitizeFilename removes invalid characters") {
       REQUIRE(sanitizeFilename("test/file") == "test_file");
       REQUIRE(sanitizeFilename("a:b*c?d") == "a_b_c_d");
   }
   ```

2. Create `tests/test_rate_limiter.cpp`:
   ```cpp
   TEST_CASE("RateLimiter parses headers correctly") {
       // Test header parsing
   }
   
   TEST_CASE("RateLimiter blocks when limit reached") {
       // Test waitIfNeeded behavior
   }
   ```

3. Create `tests/test_config.cpp`:
   - Test loading/saving config
   - Test default values
   - Test validation

4. Run tests: `cmake --build build --target tests && ./build/tests`

---

## Phase 7: Graphical User Interface

### Task 7.1: Restructure Project for Core/UI Separation
**Estimated time:** 2-3 hours
**Files:** `CMakeLists.txt`, directory restructure

**Steps:**
1. Create new directory structure:
   ```bash
   mkdir -p src/core src/cli src/gui include/core include/cli include/gui
   ```

2. Move existing files to core:
   ```bash
   mv src/NexusMods.cpp src/core/
   mv src/GameBanana.cpp src/core/
   mv src/Rename.cpp src/core/
   mv include/NexusMods.h include/core/
   mv include/GameBanana.h include/core/
   mv include/Rename.h include/core/
   ```

3. Move CLI files:
   ```bash
   mv src/main.cpp src/cli/main_cli.cpp
   mv src/LiveUI.cpp src/cli/
   mv include/LiveUI.h include/cli/
   ```

4. Update `CMakeLists.txt` for library + executables:
   ```cmake
   # Core library
   add_library(modular-core STATIC
       src/core/NexusMods.cpp
       src/core/GameBanana.cpp
       src/core/Rename.cpp
       src/core/HttpClient.cpp
       src/core/Config.cpp
       src/core/DownloadManager.cpp
   )
   target_include_directories(modular-core PUBLIC include/core)
   target_link_libraries(modular-core PUBLIC CURL::libcurl nlohmann_json::nlohmann_json)
   
   # CLI executable
   add_executable(modular-cli
       src/cli/main_cli.cpp
       src/cli/LiveUI.cpp
   )
   target_link_libraries(modular-cli PRIVATE modular-core)
   
   # GUI executable (added later)
   # add_executable(modular-gui ...)
   ```

5. Update `#include` paths in all source files

6. Build and verify both targets work:
   ```bash
   cmake --preset default
   cmake --build build
   ./build/modular-cli --help
   ```

---

### Task 7.2: Create Signal/Event System
**Estimated time:** 2 hours
**Files:** `include/core/Signal.h`

**Steps:**
1. Create thread-safe signal system in `include/core/Signal.h`:
   ```cpp
   #pragma once
   #include <functional>
   #include <vector>
   #include <mutex>
   #include <algorithm>
   
   template<typename... Args>
   class Signal {
   public:
       using Slot = std::function<void(Args...)>;
       using SlotId = size_t;
       
       SlotId connect(Slot slot) {
           std::lock_guard<std::mutex> lock(mutex_);
           slots_.push_back({nextId_, std::move(slot)});
           return nextId_++;
       }
       
       void disconnect(SlotId id) {
           std::lock_guard<std::mutex> lock(mutex_);
           slots_.erase(
               std::remove_if(slots_.begin(), slots_.end(),
                   [id](const auto& p) { return p.first == id; }),
               slots_.end());
       }
       
       void emit(Args... args) {
           std::vector<Slot> slotsCopy;
           {
               std::lock_guard<std::mutex> lock(mutex_);
               for (const auto& [id, slot] : slots_) {
                   slotsCopy.push_back(slot);
               }
           }
           for (auto& slot : slotsCopy) {
               slot(args...);
           }
       }
       
   private:
       std::vector<std::pair<SlotId, Slot>> slots_;
       std::mutex mutex_;
       SlotId nextId_ = 0;
   };
   ```

2. Add signals to `DownloadManager`:
   ```cpp
   class DownloadManager {
   public:
       Signal<std::string, size_t, size_t> onProgress;  // filename, current, total
       Signal<std::string, bool> onComplete;            // filename, success
       Signal<std::string, std::string> onError;        // filename, error message
       Signal<int> onQueueChanged;                      // queue size
   };
   ```

3. Update `LiveUI` to use signals instead of direct calls

---

### Task 7.3: Set Up Dear ImGui with SDL2
**Estimated time:** 2-3 hours
**Files:** `CMakeLists.txt`, `src/gui/`
**Alternative:** Skip to Task 7.4 if preferring Qt

**Steps:**
1. Install dependencies:
   ```bash
   # Artix/Arch
   sudo pacman -S sdl2 glew
   ```

2. Add ImGui to `CMakeLists.txt`:
   ```cmake
   include(FetchContent)
   
   FetchContent_Declare(
       imgui
       GIT_REPOSITORY https://github.com/ocornut/imgui.git
       GIT_TAG v1.90.1
   )
   FetchContent_MakeAvailable(imgui)
   
   find_package(SDL2 REQUIRED)
   find_package(OpenGL REQUIRED)
   
   # ImGui library
   add_library(imgui STATIC
       ${imgui_SOURCE_DIR}/imgui.cpp
       ${imgui_SOURCE_DIR}/imgui_draw.cpp
       ${imgui_SOURCE_DIR}/imgui_tables.cpp
       ${imgui_SOURCE_DIR}/imgui_widgets.cpp
       ${imgui_SOURCE_DIR}/imgui_demo.cpp
       ${imgui_SOURCE_DIR}/backends/imgui_impl_sdl2.cpp
       ${imgui_SOURCE_DIR}/backends/imgui_impl_opengl3.cpp
   )
   target_include_directories(imgui PUBLIC 
       ${imgui_SOURCE_DIR} 
       ${imgui_SOURCE_DIR}/backends
   )
   target_link_libraries(imgui PUBLIC SDL2::SDL2 OpenGL::GL)
   ```

3. Create `src/gui/main_gui.cpp` with basic window:
   ```cpp
   #include <SDL.h>
   #include <imgui.h>
   #include <imgui_impl_sdl2.h>
   #include <imgui_impl_opengl3.h>
   #include <GL/gl.h>
   
   int main(int argc, char* argv[]) {
       SDL_Init(SDL_INIT_VIDEO);
       
       SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
       SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 3);
       
       SDL_Window* window = SDL_CreateWindow(
           "Modular",
           SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
           1280, 720,
           SDL_WINDOW_OPENGL | SDL_WINDOW_RESIZABLE
       );
       
       SDL_GLContext gl_context = SDL_GL_CreateContext(window);
       SDL_GL_MakeCurrent(window, gl_context);
       SDL_GL_SetSwapInterval(1);  // VSync
       
       IMGUI_CHECKVERSION();
       ImGui::CreateContext();
       ImGui::StyleColorsDark();
       
       ImGui_ImplSDL2_InitForOpenGL(window, gl_context);
       ImGui_ImplOpenGL3_Init("#version 330");
       
       bool running = true;
       while (running) {
           SDL_Event event;
           while (SDL_PollEvent(&event)) {
               ImGui_ImplSDL2_ProcessEvent(&event);
               if (event.type == SDL_QUIT) running = false;
           }
           
           ImGui_ImplOpenGL3_NewFrame();
           ImGui_ImplSDL2_NewFrame();
           ImGui::NewFrame();
           
           // Your UI code here
           ImGui::ShowDemoWindow();  // Remove after testing
           
           ImGui::Render();
           glClearColor(0.1f, 0.1f, 0.1f, 1.0f);
           glClear(GL_COLOR_BUFFER_BIT);
           ImGui_ImplOpenGL3_RenderDrawData(ImGui::GetDrawData());
           SDL_GL_SwapWindow(window);
       }
       
       ImGui_ImplOpenGL3_Shutdown();
       ImGui_ImplSDL2_Shutdown();
       ImGui::DestroyContext();
       SDL_GL_DeleteContext(gl_context);
       SDL_DestroyWindow(window);
       SDL_Quit();
       
       return 0;
   }
   ```

4. Add GUI target to CMake:
   ```cmake
   add_executable(modular-gui src/gui/main_gui.cpp)
   target_link_libraries(modular-gui PRIVATE modular-core imgui)
   ```

5. Build and run:
   ```bash
   cmake --build build
   ./build/modular-gui
   ```

---

### Task 7.4: Set Up Qt 6 (Alternative to 7.3)
**Estimated time:** 2-3 hours
**Files:** `CMakeLists.txt`, `src/gui/`

**Steps:**
1. Install Qt6:
   ```bash
   # Artix/Arch
   sudo pacman -S qt6-base qt6-tools
   ```

2. Update `CMakeLists.txt`:
   ```cmake
   find_package(Qt6 COMPONENTS Widgets REQUIRED)
   set(CMAKE_AUTOMOC ON)
   set(CMAKE_AUTORCC ON)
   set(CMAKE_AUTOUIC ON)
   
   add_executable(modular-gui
       src/gui/main_gui.cpp
       src/gui/MainWindow.cpp
   )
   target_link_libraries(modular-gui PRIVATE modular-core Qt6::Widgets)
   ```

3. Create `src/gui/MainWindow.h`:
   ```cpp
   #pragma once
   #include <QMainWindow>
   #include <QTableWidget>
   #include <QComboBox>
   #include <QProgressBar>
   #include <QVBoxLayout>
   
   class MainWindow : public QMainWindow {
       Q_OBJECT
   public:
       explicit MainWindow(QWidget* parent = nullptr);
       
   private slots:
       void onGameChanged(int index);
       void onRefreshClicked();
       void onDownloadClicked();
       
   private:
       void setupUI();
       void loadTrackedMods();
       
       QComboBox* gameSelector_;
       QTableWidget* modTable_;
       QProgressBar* progressBar_;
   };
   ```

4. Create `src/gui/MainWindow.cpp`:
   ```cpp
   #include "MainWindow.h"
   #include <QPushButton>
   #include <QMenuBar>
   #include <QStatusBar>
   #include <QHeaderView>
   
   MainWindow::MainWindow(QWidget* parent) : QMainWindow(parent) {
       setupUI();
       setWindowTitle("Modular - Mod Manager");
       resize(1024, 768);
   }
   
   void MainWindow::setupUI() {
       auto* central = new QWidget(this);
       auto* layout = new QVBoxLayout(central);
       
       // Game selector
       auto* topBar = new QHBoxLayout();
       gameSelector_ = new QComboBox();
       gameSelector_->addItems({"skyrimspecialedition", "fallout4", "cyberpunk2077"});
       topBar->addWidget(new QLabel("Game:"));
       topBar->addWidget(gameSelector_);
       topBar->addStretch();
       
       auto* refreshBtn = new QPushButton("Refresh");
       auto* downloadBtn = new QPushButton("Download All");
       topBar->addWidget(refreshBtn);
       topBar->addWidget(downloadBtn);
       layout->addLayout(topBar);
       
       // Mod table
       modTable_ = new QTableWidget();
       modTable_->setColumnCount(4);
       modTable_->setHorizontalHeaderLabels({"Mod Name", "Status", "Size", "Updated"});
       modTable_->horizontalHeader()->setStretchLastSection(true);
       modTable_->setSelectionBehavior(QAbstractItemView::SelectRows);
       layout->addWidget(modTable_);
       
       // Progress bar
       progressBar_ = new QProgressBar();
       progressBar_->setVisible(false);
       layout->addWidget(progressBar_);
       
       setCentralWidget(central);
       
       // Connections
       connect(gameSelector_, QOverload<int>::of(&QComboBox::currentIndexChanged),
               this, &MainWindow::onGameChanged);
       connect(refreshBtn, &QPushButton::clicked, this, &MainWindow::onRefreshClicked);
       connect(downloadBtn, &QPushButton::clicked, this, &MainWindow::onDownloadClicked);
       
       // Menu bar
       auto* fileMenu = menuBar()->addMenu("&File");
       fileMenu->addAction("&Settings...", this, []() { /* TODO */ });
       fileMenu->addSeparator();
       fileMenu->addAction("E&xit", this, &QWidget::close);
       
       statusBar()->showMessage("Ready");
   }
   ```

5. Create `src/gui/main_gui.cpp`:
   ```cpp
   #include <QApplication>
   #include "MainWindow.h"
   
   int main(int argc, char* argv[]) {
       QApplication app(argc, argv);
       app.setApplicationName("Modular");
       app.setOrganizationName("Modular");
       
       MainWindow window;
       window.show();
       
       return app.exec();
   }
   ```

6. Build and run:
   ```bash
   cmake --build build
   ./build/modular-gui
   ```

---

### Task 7.5: Implement Mod List View
**Estimated time:** 3-4 hours
**Files:** `src/gui/ModListView.cpp` or integrated into MainWindow
**Depends on:** Task 7.3 or 7.4

**Steps:**
1. Create mod data model:
   ```cpp
   struct ModEntry {
       int modId;
       std::string name;
       std::string status;  // "Not Downloaded", "Downloaded", "Update Available"
       std::string size;
       std::string lastUpdated;
       bool selected = false;
   };
   ```

2. Implement mod fetching from core:
   ```cpp
   void MainWindow::loadTrackedMods() {
       auto modIds = get_tracked_mods_for_domain(currentGame_);
       mods_.clear();
       
       for (int modId : modIds) {
           ModEntry entry;
           entry.modId = modId;
           // Fetch mod info from API
           // Check if already downloaded
           mods_.push_back(entry);
       }
       
       refreshModTable();
   }
   ```

3. For Dear ImGui, create table:
   ```cpp
   void renderModList() {
       if (ImGui::BeginTable("ModList", 5, 
           ImGuiTableFlags_Borders | ImGuiTableFlags_Sortable | 
           ImGuiTableFlags_RowBg | ImGuiTableFlags_ScrollY)) {
           
           ImGui::TableSetupColumn("", ImGuiTableColumnFlags_WidthFixed, 30);
           ImGui::TableSetupColumn("Name", ImGuiTableColumnFlags_WidthStretch);
           ImGui::TableSetupColumn("Status", ImGuiTableColumnFlags_WidthFixed, 120);
           ImGui::TableSetupColumn("Size", ImGuiTableColumnFlags_WidthFixed, 80);
           ImGui::TableSetupColumn("Actions", ImGuiTableColumnFlags_WidthFixed, 100);
           ImGui::TableHeadersRow();
           
           for (auto& mod : mods_) {
               ImGui::TableNextRow();
               
               ImGui::TableNextColumn();
               ImGui::Checkbox("##sel", &mod.selected);
               
               ImGui::TableNextColumn();
               ImGui::TextUnformatted(mod.name.c_str());
               
               ImGui::TableNextColumn();
               // Color-coded status
               if (mod.status == "Update Available") {
                   ImGui::TextColored(ImVec4(1,1,0,1), "%s", mod.status.c_str());
               } else if (mod.status == "Downloaded") {
                   ImGui::TextColored(ImVec4(0,1,0,1), "%s", mod.status.c_str());
               } else {
                   ImGui::TextUnformatted(mod.status.c_str());
               }
               
               ImGui::TableNextColumn();
               ImGui::TextUnformatted(mod.size.c_str());
               
               ImGui::TableNextColumn();
               ImGui::PushID(mod.modId);
               if (ImGui::Button("Download")) {
                   downloadMod(mod.modId);
               }
               ImGui::PopID();
           }
           
           ImGui::EndTable();
       }
   }
   ```

4. For Qt, implement custom model or populate QTableWidget

5. Add sorting by clicking column headers

6. Add search/filter text field

---

### Task 7.6: Implement Download Panel
**Estimated time:** 2-3 hours
**Files:** `src/gui/DownloadPanel.cpp`
**Depends on:** Task 7.2, 7.5

**Steps:**
1. Connect to DownloadManager signals:
   ```cpp
   downloadManager_.onProgress.connect([this](std::string file, size_t cur, size_t total) {
       // Queue UI update (thread-safe)
       pendingUpdates_.push({file, cur, total});
   });
   ```

2. Create download queue display:
   ```cpp
   struct DownloadItem {
       std::string filename;
       float progress = 0;
       std::string speed;
       std::string eta;
       bool active = false;
   };
   
   void renderDownloadPanel() {
       ImGui::Begin("Downloads");
       
       for (auto& dl : downloads_) {
           ImGui::Text("%s", dl.filename.c_str());
           ImGui::SameLine(200);
           ImGui::ProgressBar(dl.progress, ImVec2(200, 0));
           ImGui::SameLine();
           ImGui::Text("%s - %s", dl.speed.c_str(), dl.eta.c_str());
           
           if (dl.active) {
               ImGui::SameLine();
               if (ImGui::Button("Cancel")) {
                   cancelDownload(dl.filename);
               }
           }
       }
       
       ImGui::Separator();
       ImGui::Text("Queue: %d items", (int)downloadQueue_.size());
       
       ImGui::End();
   }
   ```

3. Implement speed calculation:
   ```cpp
   void updateSpeed(DownloadItem& item, size_t bytesNow) {
       auto now = std::chrono::steady_clock::now();
       auto elapsed = now - item.lastUpdate;
       auto bytes = bytesNow - item.lastBytes;
       
       double seconds = std::chrono::duration<double>(elapsed).count();
       double bytesPerSec = bytes / seconds;
       
       item.speed = formatBytes(bytesPerSec) + "/s";
       
       if (bytesPerSec > 0 && item.totalBytes > 0) {
           double remaining = (item.totalBytes - bytesNow) / bytesPerSec;
           item.eta = formatTime(remaining);
       }
       
       item.lastUpdate = now;
       item.lastBytes = bytesNow;
   }
   ```

4. Add pause/resume/cancel functionality

---

### Task 7.7: Implement Settings Dialog
**Estimated time:** 2 hours
**Files:** `src/gui/SettingsDialog.cpp`
**Depends on:** Task 2.1 (Config system)

**Steps:**
1. Create settings dialog:
   ```cpp
   void renderSettingsDialog() {
       if (!showSettings_) return;
       
       ImGui::Begin("Settings", &showSettings_);
       
       ImGui::SeparatorText("NexusMods");
       static char apiKey[256];
       ImGui::InputText("API Key", apiKey, sizeof(apiKey), 
                        ImGuiInputTextFlags_Password);
       
       ImGui::SeparatorText("GameBanana");
       static char userId[64];
       ImGui::InputText("User ID", userId, sizeof(userId));
       
       ImGui::SeparatorText("Storage");
       static char modsDir[512];
       ImGui::InputText("Mods Directory", modsDir, sizeof(modsDir));
       ImGui::SameLine();
       if (ImGui::Button("Browse...")) {
           // Open file dialog
       }
       
       ImGui::SeparatorText("Downloads");
       static int maxConcurrent = 3;
       ImGui::SliderInt("Concurrent Downloads", &maxConcurrent, 1, 10);
       
       static bool verifyDownloads = false;
       ImGui::Checkbox("Verify downloads (MD5)", &verifyDownloads);
       
       ImGui::SeparatorText("Appearance");
       static int themeIdx = 0;
       if (ImGui::Combo("Theme", &themeIdx, "Dark\0Light\0Classic\0")) {
           switch (themeIdx) {
               case 0: ImGui::StyleColorsDark(); break;
               case 1: ImGui::StyleColorsLight(); break;
               case 2: ImGui::StyleColorsClassic(); break;
           }
       }
       
       ImGui::Separator();
       if (ImGui::Button("Save")) {
           saveSettings();
           showSettings_ = false;
       }
       ImGui::SameLine();
       if (ImGui::Button("Cancel")) {
           showSettings_ = false;
       }
       
       ImGui::End();
   }
   ```

2. For Qt, create QDialog with form layout

3. Load/save from Config system

---

### Task 7.8: Add Async Operations
**Estimated time:** 2-3 hours
**Files:** Multiple GUI files
**Depends on:** Task 7.2

**Steps:**
1. Create async task runner:
   ```cpp
   #include <future>
   #include <queue>
   
   class AsyncRunner {
   public:
       template<typename Func>
       void run(Func&& f) {
           futures_.push_back(std::async(std::launch::async, std::forward<Func>(f)));
       }
       
       void processCompleted() {
           futures_.erase(
               std::remove_if(futures_.begin(), futures_.end(),
                   [](auto& f) { 
                       return f.wait_for(std::chrono::seconds(0)) == std::future_status::ready;
                   }),
               futures_.end());
       }
       
   private:
       std::vector<std::future<void>> futures_;
   };
   ```

2. Use for API calls:
   ```cpp
   void MainWindow::onRefreshClicked() {
       statusBar_->showMessage("Loading...");
       
       asyncRunner_.run([this]() {
           auto mods = get_tracked_mods_for_domain(currentGame_);
           
           // Queue UI update
           QMetaObject::invokeMethod(this, [this, mods]() {
               populateModList(mods);
               statusBar_->showMessage("Ready");
           }, Qt::QueuedConnection);
       });
   }
   ```

3. For Dear ImGui, process updates each frame:
   ```cpp
   // In main loop
   asyncRunner_.processCompleted();
   
   // Process pending UI updates
   while (!pendingUpdates_.empty()) {
       auto update = pendingUpdates_.pop();
       applyUpdate(update);
   }
   ```

4. Add loading spinners/indicators

5. Ensure all API calls are async to prevent UI freezing

---

## Checklist Summary

```
Phase 1: Foundation & Stability
[ ] 1.1 Create Shared HTTP Client
[ ] 1.2 Implement Rate Limiter
[ ] 1.3 Create Exception Hierarchy
[ ] 1.4 Add API Key Validation

Phase 2: Configuration & CLI
[ ] 2.1 Create Configuration System
[ ] 2.2 Improve CLI Argument Parsing
[ ] 2.3 Add Subcommands

Phase 3: API Coverage
[ ] 3.1 Add NexusMods Update Checking
[ ] 3.2 Add GameBanana Game Filtering
[ ] 3.3 Add File Verification

Phase 4: Download Management
[ ] 4.1 Add Download Progress Callbacks
[ ] 4.2 Add Download Resume Support
[ ] 4.3 Add Download History Tracking

Phase 5: Code Quality
[ ] 5.1 Consolidate Duplicate Code
[ ] 5.2 Add Namespace
[ ] 5.3 Improve LiveUI Terminal Handling

Phase 6: Testing
[ ] 6.1 Set Up Test Framework
[ ] 6.2 Write Unit Tests

Phase 7: Graphical User Interface
[ ] 7.1 Restructure Project for Core/UI Separation
[ ] 7.2 Create Signal/Event System
[ ] 7.3 Set Up Dear ImGui with SDL2 (Option A)
[ ] 7.4 Set Up Qt 6 (Option B)
[ ] 7.5 Implement Mod List View
[ ] 7.6 Implement Download Panel
[ ] 7.7 Implement Settings Dialog
[ ] 7.8 Add Async Operations
```

---

## Notes

- Build after each task to catch issues early
- Commit after each completed task with descriptive message
- Each task should take 30 minutes to 4 hours
- Tasks within a phase can often be done in parallel
- Skip optional tasks (file verification, testing) if time-constrained
- For GUI: Choose either Dear ImGui (Task 7.3) OR Qt (Task 7.4), not both
- GUI Phase depends on Phase 1 (HttpClient, Exceptions) and Phase 2 (Config)
- Total estimated time with GUI: ~45-55 hours

## Recommended Order for GUI Focus

If the primary goal is to build the GUI quickly:

1. Task 1.1 (HTTP Client) - Required for core
2. Task 1.3 (Exceptions) - Better error handling
3. Task 2.1 (Config) - Settings storage
4. Task 5.1 (Consolidate code) - Cleaner core
5. Task 7.1 (Restructure) - Separate core/UI
6. Task 7.2 (Signals) - Event system
7. Task 7.3 or 7.4 (UI Framework) - Pick one
8. Task 7.5-7.8 (UI Components) - Build the interface

This path skips CLI improvements and focuses on getting a working GUI.
