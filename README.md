# Modular

A C++ command-line application for automating the downloading, organizing, and renaming of game modifications from multiple mod repositories.

## Overview

Modular streamlines game mod management by providing an interface to download mods from various sources (NexusMods, GameBanana), automatically organize them into structured directories by Domain/Category/Mod_Id/Archived-Mod. Work In Progress

## Features

- **Multi-Repository Support** - Download mods from NexusMods and GameBanana with extensible architecture for additional sources
- **Automatic Organization** - Organizes mods into game-specific directories with optional category-based subdirectories
- **Smart Renaming** - Converts numeric mod ID folders to human-readable names using API metadata
- **Rate Limit Compliance** - Built-in rate limiter respects NexusMods API limits (20,000/day, 500/hour)
- **Download Verification** - MD5 checksum verification with persistent download history database
- **Tracking Validation** - Cross-validates API tracking against web tracking center for accuracy
- **Real-Time Progress** - Live progress bars for all download and organization operations -WIP-
- **Flexible Configuration** - Supports environment variables, config files, and command-line arguments
- **Retry Logic** - Automatic retry with exponential backoff for failed network requests

## Project Structure

```
Modular/
├── CMakeLists.txt           # CMake build configuration
├── CMakePresets.json        # Build presets (Release/Debug)
├── include/
│   ├── core/                # Core library headers
│   │   ├── HttpClient.h     # HTTP client with retry logic
│   │   ├── RateLimiter.h    # API rate limit enforcement
│   │   ├── NexusMods.h      # NexusMods API integration
│   │   ├── GameBanana.h     # GameBanana API integration
│   │   ├── Database.h       # Download history persistence
│   │   ├── Config.h         # Configuration management
│   │   ├── Rename.h         # Mod renaming and organization
│   │   ├── TrackingValidator.h  # Web scraping validation
│   │   ├── HtmlParser.h     # HTML parsing utilities
│   │   ├── Utils.h          # File/string utilities
│   │   ├── Exceptions.h     # Exception hierarchy
│   │   └── ILogger.h        # Logging interface
│   └── cli/
│       └── LiveUI.h         # Terminal progress visualization
├── src/
│   ├── core/                # Core library implementation
│   │   ├── HttpClient.cpp
│   │   ├── RateLimiter.cpp
│   │   ├── NexusMods.cpp
│   │   ├── GameBanana.cpp
│   │   ├── Database.cpp
│   │   ├── Config.cpp
│   │   ├── Rename.cpp
│   │   ├── TrackingValidator.cpp
│   │   ├── HtmlParser.cpp
│   │   └── Utils.cpp
│   └── cli/
│       ├── main.cpp         # CLI entry point and menu
│       └── LiveUI.cpp       # Progress bar implementation
├── tests/                   # Unit tests (Catch2)
│   ├── test_main.cpp
│   ├── test_config.cpp
│   ├── test_utils.cpp
│   └── test_database.cpp
├── docs/                    # Documentation
└── build/                   # Build output (generated)
```

## Architecture

Modular follows a clean three-layer architecture:

1. **Core Library (`libmodular-core`)** - Static library containing all business logic
2. **CLI Executable (`modular-cli`)** - Command-line interface consuming the core library
3. **LiveUI Component** - Real-time terminal progress visualization

## Dependencies

| Dependency | Purpose |
|------------|---------|
| **C++17** | Language standard |
| **CURL (libcurl)** | HTTP/HTTPS requests and file downloads |
| **nlohmann/json** | JSON parsing and serialization |
| **OpenSSL (libcrypto)** | MD5 checksum calculation |
| **Catch2 v3.5.2** | Unit testing framework (fetched automatically) | - git submodule -

## Building

### Prerequisites

- CMake 3.20 or later
- C++17 compatible compiler (GCC, Clang, or MSVC)
- libcurl development files
- OpenSSL development files
- nlohmann/json (install via package manager or from [GitHub](https://github.com/nlohmann/json))

### Linux Build

```bash
# Clone the repository
git clone https://github.com/MasterGenotype/Modular-1.git
cd Modular-1

# Configure and build (using presets)
cmake --preset default
cmake --build build

# Or manually
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
make
```

### Debug Build

```bash
cmake --preset debug
cmake --build build
```

### Installing Dependencies (Debian/Ubuntu)

```bash
sudo apt install libcurl4-openssl-dev libssl-dev nlohmann-json3-dev cmake ninja-build
```

### Installing Dependencies (Arch Linux)

```bash
sudo pacman -S curl openssl nlohmann-json cmake ninja
```

### Cross-Compiling for Windows

```bash
# Ensure MinGW-w64 is installed
cmake -DCMAKE_TOOLCHAIN_FILE=/path/to/mingw-toolchain.cmake --preset default
cmake --build build
```

## Configuration

### Environment Variables

| Variable | Description |
|----------|-------------|
| `NEXUS_API_KEY` | Your NexusMods API key (required for NexusMods features) |
| `GB_USER_ID` | Your GameBanana User ID (required for GameBanana features) |

### Config File

Configuration is stored in `~/.config/Modular/config.json`:

```json
{
  "nexus_api_key": "your-api-key",
  "gamebanana_user_id": "your-user-id",
  "download_path": "/path/to/mods",
  "organize_by_category": true,
  "auto_rename": true,
  "verify_downloads": true
}
```

Environment variables take precedence over config file values.

## Usage

### Interactive Mode

```bash
./build/modular-cli
```

This presents a menu with options:
1. Download mods from GameBanana
2. Download mods from NexusMods
3. Rename mods (convert IDs to names)
4. Exit

### Command-Line Arguments

```bash
# Download mods for specific game domains
./build/modular-cli --domain skyrimspecialedition --domain fallout4

# Filter by category
./build/modular-cli --domain skyrimspecialedition --category weapons

# Dry run (show what would be downloaded)
./build/modular-cli --domain skyrimspecialedition --dry-run

# Force re-download of existing files
./build/modular-cli --domain skyrimspecialedition --force

# Organize mods into category subdirectories
./build/modular-cli --domain skyrimspecialedition --organize-by-category
```

## Testing

```bash
# Build and run tests
cmake --preset default -DBUILD_TESTS=ON
cmake --build build
./build/modular-tests
```

## How It Works

### NexusMods Workflow

1. Fetches your tracked mods from the NexusMods API
2. Optionally validates tracking against the web tracking center
3. Generates time-limited download links for each mod file
4. Downloads files with progress tracking and rate limit compliance
5. Verifies MD5 checksums when available
6. Renames mod folders using fetched metadata
7. Organizes into category subdirectories if enabled

### GameBanana Workflow

1. Fetches your subscribed mods using your User ID
2. Extracts file URLs from mod pages
3. Downloads files with progress tracking
4. Organizes into sanitized subdirectories

## Documentation

Additional documentation is available in the `/docs/` directory:

- `IMPROVEMENTS.md` - Feature improvements and API enhancements
- `RATE_LIMITING_FIXES_SUMMARY.md` - Rate limiting implementation details
- `TRACKING_VALIDATION.md` - Web scraping validation methodology
- `CATEGORY_ORGANIZATION.md` - Category-based organization system

## Contributing

1. Fork this repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

MIT License - See LICENSE file for details.

## Acknowledgments

- [NexusMods](https://www.nexusmods.com/) for their comprehensive modding API
- [GameBanana](https://gamebanana.com/) for their mod hosting platform
- [nlohmann/json](https://github.com/nlohmann/json) for the excellent JSON library
- [Catch2](https://github.com/catchorg/Catch2) for the testing framework
