# Modular

A sophisticated C++ command-line application for automating the downloading, organizing, and management of game modifications from multiple mod repositories. Features a modern fluent HTTP client API, intelligent rate limiting, and real-time progress tracking.

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Architecture](#architecture)
- [Project Structure](#project-structure)
- [Key Components](#key-components)
- [Fluent HTTP Client](#fluent-http-client)
- [Design Patterns](#design-patterns)
- [Error Handling](#error-handling)
- [Dependencies](#dependencies)
- [Building](#building)
- [Configuration](#configuration)
- [Usage](#usage)
- [Testing](#testing)
- [Workflows](#workflows)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [License](#license)
- [Acknowledgments](#acknowledgments)

## Overview

Modular streamlines game mod management by providing a unified interface to download mods from various sources (NexusMods, GameBanana), automatically organize them into structured directories (`Domain/Category/Mod_Id`), and rename folders from numeric IDs to human-readable names.

The project implements a clean three-layer architecture separating concerns between the CLI interface, core business logic, and a modern fluent HTTP client library inspired by .NET's FluentHttpClient.

## Features

### Core Functionality
- **Multi-Repository Support** - Download mods from NexusMods and GameBanana with extensible architecture for additional sources
- **Automatic Organization** - Organizes mods into game-specific directories with optional category-based subdirectories
- **Smart Renaming** - Converts numeric mod ID folders to human-readable names using API metadata
- **Download Verification** - MD5 checksum verification with persistent download history database
- **Tracking Validation** - Cross-validates API tracking against NexusMods web tracking center via web scraping

### Network & Performance
- **Rate Limit Compliance** - Built-in rate limiter respects NexusMods API limits (20,000 requests/day, 500/hour)
- **Retry Logic** - Automatic retry with exponential backoff for failed network requests
- **Fluent HTTP API** - Modern chainable HTTP client with middleware support
- **Progress Callbacks** - Real-time progress tracking decoupled from UI

### Configuration & Usability
- **Flexible Configuration** - Supports environment variables, config files, and command-line arguments
- **Real-Time Progress** - Live progress bars for all download and organization operations
- **Persistent State** - Rate limiter and download history persist between sessions

## Architecture

Modular follows a clean **three-layer architecture**:

```
┌─────────────────────────────────────────────────────────────┐
│                     CLI Layer (modular-cli)                 │
│            Interactive menu, progress display, I/O          │
├─────────────────────────────────────────────────────────────┤
│                  Core Library (libmodular-core)             │
│    NexusMods API, GameBanana API, Database, Config,         │
│    RateLimiter, Rename, TrackingValidator, Utils            │
├─────────────────────────────────────────────────────────────┤
│              Fluent HTTP Client (fluent_client)             │
│    Modern fluent API, middleware filters, retry policies    │
└─────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

1. **CLI Layer (`modular-cli`)** - Interactive command-line interface with menu-driven operations and real-time progress visualization through LiveUI
2. **Core Library (`libmodular-core`)** - Static library containing all business logic including HTTP operations, API integrations, rate limiting, file operations, and data persistence
3. **Fluent HTTP Layer (`fluent_client`)** - Modern fluent-style HTTP client library with chainable request building, middleware filters, and type-safe responses

## Project Structure

```
Modular/
├── CMakeLists.txt              # CMake build configuration
├── CMakePresets.json           # Build presets (Release/Debug)
├── include/
│   ├── core/                   # Core library headers
│   │   ├── HttpClient.h        # HTTP client with retry logic
│   │   ├── RateLimiter.h       # API rate limit enforcement
│   │   ├── NexusMods.h         # NexusMods API integration
│   │   ├── GameBanana.h        # GameBanana API integration
│   │   ├── Database.h          # Download history persistence
│   │   ├── Config.h            # Configuration management
│   │   ├── Rename.h            # Mod renaming and organization
│   │   ├── TrackingValidator.h # Web scraping validation
│   │   ├── HtmlParser.h        # HTML parsing utilities
│   │   ├── Utils.h             # File/string utilities
│   │   ├── Exceptions.h        # Exception hierarchy
│   │   └── ILogger.h           # Logging interface
│   ├── cli/
│   │   └── LiveUI.h            # Terminal progress visualization
│   └── fluent/                 # Fluent HTTP client headers
│       ├── IFluentClient.h     # Main client interface
│       ├── IRequest.h          # Request builder interface
│       ├── IResponse.h         # Response handler interface
│       ├── IHttpFilter.h       # Middleware filter interface
│       ├── IBodyBuilder.h      # Request body builder
│       ├── IRetryConfig.h      # Retry policy configuration
│       ├── IRequestCoordinator.h # Retry dispatch coordinator
│       └── IRateLimiter.h      # Rate limiter interface
├── src/
│   ├── core/                   # Core library implementation
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
│   ├── cli/
│   │   ├── main.cpp            # CLI entry point and menu
│   │   └── LiveUI.cpp          # Progress bar implementation
│   └── fluent/                 # Fluent HTTP client implementation
│       ├── FluentClient.cpp    # Main client implementation
│       ├── Request.cpp         # Request builder
│       ├── Response.cpp        # Response handler
│       ├── BodyBuilder.cpp     # JSON/form body construction
│       ├── HttpClientBridge.cpp # Bridge to CURL
│       ├── RetryCoordinator.cpp # Retry logic management
│       ├── Filters.cpp         # Built-in middleware filters
│       └── NexusModsClient.cpp # High-level NexusMods client
├── tests/                      # Unit tests (Catch2)
│   ├── test_main.cpp
│   ├── test_config.cpp
│   ├── test_utils.cpp
│   ├── test_database.cpp
│   └── test_fluent.cpp         # Fluent API tests
├── docs/                       # Documentation
└── build/                      # Build output (generated)
```

## Key Components

### HttpClient (`src/core/HttpClient.cpp`)

Instance-based HTTP client with CURL handle ownership:
- GET requests for JSON/text data
- File downloads with progress callbacks
- Retry policy with exponential backoff
- Response header parsing for rate limit tracking
- Non-copyable (CURL handle), but movable
- Conditional retry logic (5xx errors, not 4xx except 429)

### RateLimiter (`src/core/RateLimiter.cpp`)

Tracks NexusMods API rate limits from response headers:
- Parses `x-rl-daily-remaining` and `x-rl-hourly-remaining` headers
- Stores Unix timestamp reset times
- Implements blocking backoff when limits exhausted
- Supports state persistence between sessions

### NexusMods API (`src/core/NexusMods.cpp`)

Complete NexusMods API integration:
- Fetches tracked mods with domain information
- Retrieves file IDs for mods
- Generates time-limited download links
- Downloads files with progress callbacks
- Integrates with RateLimiter for compliance

### GameBanana API (`src/core/GameBanana.cpp`)

GameBanana platform integration:
- Fetches user's subscribed mods
- Extracts file URLs from mod pages
- Downloads files with optional progress tracking
- No rate limiting required

### Database (`src/core/Database.cpp`)

JSON-based download history persistence:
- Stores: game_domain, mod_id, file_id, filename, filepath, MD5, download time, status
- Operations: add, find, query by domain/mod, verification updates, removal
- Human-readable format for debugging

### Config (`src/core/Config.cpp`)

Struct-based configuration management:
- Location: `~/.config/Modular/config.json`
- Precedence: Environment variables > Config file > Defaults
- Settings: API keys, paths, preferences

### TrackingValidator (`src/core/TrackingValidator.cpp`)

Web scraping validation for NexusMods:
- Scrapes web tracking center using cookies
- Validates API tracking against web tracking
- Reports mismatches (API-only, web-only mods)
- Maps game domains to game IDs

### LiveUI (`src/cli/LiveUI.cpp`)

Real-time terminal progress visualization:
- Progress bars with percentage completion
- Status updates for scanning, downloading, organizing
- Decoupled from core logic via callbacks

## Fluent HTTP Client

Modular includes a complete fluent HTTP client library inspired by .NET's FluentHttpClient. This provides a modern, chainable API for HTTP operations.

### Basic Usage

```cpp
#include "fluent/FluentClient.h"

// Create client
auto client = createFluentClient("https://api.nexusmods.com");

// Make a request with fluent API
auto response = client
    ->request("/v1/users/validate.json")
    ->withHeader("apikey", apiKey)
    ->withHeader("accept", "application/json")
    ->get()
    ->asJson();

// Access response data
std::string username = response["name"];
```

### Features

- **Method Chaining** - Build requests fluently with chainable methods
- **Async Support** - Async/await via `std::future`
- **Middleware Filters** - Request/response interception pipeline
- **Type-Safe Responses** - Typed deserialization methods
- **Retry Policies** - Configurable retry with exponential backoff
- **Progress Callbacks** - Download progress tracking
- **Rate Limiting** - Integrated rate limiter support

### Request Building

```cpp
// POST with JSON body
auto response = client
    ->request("/api/endpoint")
    ->withHeader("Content-Type", "application/json")
    ->withBody()
        ->json({{"key", "value"}})
    ->post();

// GET with query parameters
auto response = client
    ->request("/api/search")
    ->withQueryParam("q", "skyrim")
    ->withQueryParam("page", "1")
    ->get();

// Download with progress
auto response = client
    ->request("/files/download/123")
    ->withProgress([](size_t downloaded, size_t total) {
        std::cout << downloaded << "/" << total << std::endl;
    })
    ->download("/path/to/file");
```

### Middleware Filters

Built-in filters for common operations:

```cpp
// Authentication filter
client->addFilter(std::make_unique<AuthenticationFilter>(apiKey));

// Rate limiting filter
client->addFilter(std::make_unique<RateLimitFilter>(rateLimiter));

// Logging filter
client->addFilter(std::make_unique<LoggingFilter>(logger));

// Error handling filter
client->addFilter(std::make_unique<ErrorHandlingFilter>());
```

Filters execute in priority order for requests (high to low) and reverse for responses.

### NexusModsClient

High-level client for NexusMods operations:

```cpp
#include "fluent/NexusModsClient.h"

NexusModsClient nexus(apiKey, rateLimiter);

// Validate API key
auto user = nexus.validateKey();

// Get tracked mods
auto mods = nexus.getTrackedMods();

// Download a mod file
nexus.downloadFile(domain, modId, fileId, "/path/to/save", progressCallback);
```

## Design Patterns

### Builder Pattern (Fluent API)
Request/response configuration via method chaining improves code readability and discoverability.

### Middleware Filter Pattern
Request/response interception pipeline with filters executing in priority order. Built-in filters handle authentication, rate limiting, logging, and error handling.

### Dependency Injection
HttpClient takes `RateLimiter&` and `ILogger&` as constructor parameters, enabling testability and flexibility.

### RAII (Resource Acquisition Is Initialization)
- `CurlGlobal`: RAII wrapper for CURL global initialization
- `HttpClient`: Owns and manages CURL easy handle lifetime

### Strategy Pattern
- Retry policies via `IRetryConfig`
- Rate limiting strategies
- Logger implementations (`StderrLogger`, `NullLogger`)

### Factory Pattern
`createFluentClient()` factory function separates interface from implementation.

## Error Handling

Modular uses a custom exception hierarchy for precise error handling:

| Exception | Description |
|-----------|-------------|
| `ModularException` | Base exception with context (URL, response snippet) |
| `NetworkException` | CURL errors, timeouts, DNS failures |
| `ApiException` | HTTP 4xx/5xx errors |
| `RateLimitException` | 429 responses with retry-after support |
| `AuthException` | 401/403 authentication failures |
| `ParseException` | JSON parsing errors |
| `FileSystemException` | File I/O errors |
| `ConfigException` | Configuration validation errors |

Example usage:
```cpp
try {
    auto response = nexus.downloadFile(...);
} catch (const RateLimitException& e) {
    std::cerr << "Rate limited, retry after: " << e.retryAfter() << "s\n";
} catch (const AuthException& e) {
    std::cerr << "Authentication failed: " << e.what() << "\n";
} catch (const NetworkException& e) {
    std::cerr << "Network error: " << e.what() << "\n";
}
```

## Dependencies

| Dependency | Purpose | Version |
|------------|---------|---------|
| **C++20** | Language standard | - |
| **CMake** | Build system | 3.20+ |
| **CURL (libcurl)** | HTTP/HTTPS requests | - |
| **nlohmann/json** | JSON parsing/serialization | 3.11+ |
| **OpenSSL (libcrypto)** | MD5 checksum calculation | - |
| **Catch2** | Unit testing framework | v3.5.2 |

## Building

### Prerequisites

- CMake 3.20 or later
- C++20 compatible compiler (GCC 10+, Clang 10+, or MSVC 2019+)
- libcurl development files
- OpenSSL development files
- nlohmann/json

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

### Installing Dependencies

**Debian/Ubuntu:**
```bash
sudo apt install libcurl4-openssl-dev libssl-dev nlohmann-json3-dev cmake ninja-build
```

**Arch Linux:**
```bash
sudo pacman -S curl openssl nlohmann-json cmake ninja
```

**Fedora:**
```bash
sudo dnf install libcurl-devel openssl-devel json-devel cmake ninja-build
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
  "verify_downloads": true,
  "validate_tracking": false,
  "max_concurrent_downloads": 1
}
```

**Configuration Precedence:** Environment variables > Config file > Defaults

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `nexus_api_key` | string | - | NexusMods API key |
| `gamebanana_user_id` | string | - | GameBanana user ID |
| `download_path` | string | `~/Mods` | Base directory for downloads |
| `organize_by_category` | bool | `false` | Create category subdirectories |
| `auto_rename` | bool | `true` | Rename mod folders to human-readable names |
| `verify_downloads` | bool | `true` | Verify MD5 checksums after download |
| `validate_tracking` | bool | `false` | Validate tracking against web interface |
| `max_concurrent_downloads` | int | `1` | Maximum parallel downloads |

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

### Output Structure

After downloading and organizing, mods are stored as:

```
~/Mods/
├── skyrimspecialedition/
│   ├── Weapons/
│   │   ├── Better_Swords/
│   │   │   └── Better Swords-1234-1-0.zip
│   │   └── Enhanced_Bows/
│   │       └── Enhanced Bows-5678-2-1.zip
│   └── Armor/
│       └── Steel_Plate_Redux/
│           └── Steel Plate Redux-9012-1-5.zip
└── fallout4/
    └── Weapons/
        └── Laser_Musket_Plus/
            └── Laser Musket Plus-3456-1-0.zip
```

## Testing

```bash
# Build with tests enabled
cmake --preset default -DBUILD_TESTS=ON
cmake --build build

# Run core library tests
./build/modular-tests

# Run fluent API tests
./build/fluent-tests
```

### Test Coverage

- Configuration loading and validation
- Utility functions (filename sanitization, MD5 calculation)
- Database operations (add, find, query, remove)
- Fluent HTTP client request building
- Rate limiter state management

## Workflows

### NexusMods Download Workflow

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Load Configuration                                       │
│    - Read config file and environment variables             │
│    - Validate API key with NexusMods                        │
├─────────────────────────────────────────────────────────────┤
│ 2. Fetch Tracked Mods                                       │
│    - GET /v1/user/tracked_mods.json                         │
│    - Group mods by domain (game)                            │
├─────────────────────────────────────────────────────────────┤
│ 3. Optional: Validate Tracking                              │
│    - Scrape web tracking center                             │
│    - Compare with API results                               │
│    - Report any discrepancies                               │
├─────────────────────────────────────────────────────────────┤
│ 4. For Each Mod:                                            │
│    a. Fetch file IDs                                        │
│    b. Generate download links (time-limited)                │
│    c. Download with progress tracking                       │
│    d. Verify MD5 checksum                                   │
│    e. Save to download history database                     │
├─────────────────────────────────────────────────────────────┤
│ 5. Post-Processing                                          │
│    - Rename folders (ID → human-readable name)              │
│    - Organize into category subdirectories                  │
└─────────────────────────────────────────────────────────────┘
```

### GameBanana Download Workflow

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Fetch Subscribed Mods                                    │
│    - Query GameBanana API with User ID                      │
│    - Get list of subscribed mod IDs                         │
├─────────────────────────────────────────────────────────────┤
│ 2. For Each Mod:                                            │
│    a. Extract mod ID from profile URL                       │
│    b. Fetch downloadable files list                         │
│    c. Create sanitized folder for mod                       │
│    d. Download files with progress tracking                 │
├─────────────────────────────────────────────────────────────┤
│ 3. Files organized by mod in base directory                 │
└─────────────────────────────────────────────────────────────┘
```

## Documentation

Additional documentation is available in the `/docs/` directory:

| Document | Description |
|----------|-------------|
| `IMPROVEMENTS.md` | Feature improvements and API enhancements |
| `RATE_LIMITING_FIXES_SUMMARY.md` | Rate limiting implementation details |
| `TRACKING_VALIDATION.md` | Web scraping validation methodology |
| `CATEGORY_ORGANIZATION.md` | Category-based organization system |
| `fluent/README.md` | Fluent HTTP client user guide |
| `fluent/INTERFACES.md` | Fluent API interface documentation |
| `MODULAR_INTEGRATION_COMPARISON.md` | Fluent vs traditional client comparison |

## Contributing

1. Fork this repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes following the existing code style
4. Add tests for new functionality
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

### Code Style

- C++20 standard
- 4-space indentation
- PascalCase for classes, camelCase for functions/variables
- Header guards using `#pragma once`
- All warnings enabled (`-Wall -Wextra -Wpedantic`)

## License

MIT License - See LICENSE file for details.

## Acknowledgments

- [NexusMods](https://www.nexusmods.com/) for their comprehensive modding API
- [GameBanana](https://gamebanana.com/) for their mod hosting platform
- [nlohmann/json](https://github.com/nlohmann/json) for the excellent JSON library
- [Catch2](https://github.com/catchorg/Catch2) for the testing framework
- [libcurl](https://curl.se/libcurl/) for reliable HTTP operations
