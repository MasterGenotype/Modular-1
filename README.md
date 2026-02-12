# Modular

A sophisticated C#/.NET application for automating the downloading, organizing, and management of game modifications from multiple mod repositories. Features both a command-line interface and a graphical user interface built with Avalonia, a modern fluent HTTP client API, intelligent rate limiting, and real-time progress tracking.

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
- [GUI Application](#gui-application)
- [Testing](#testing)
- [Workflows](#workflows)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [License](#license)
- [Acknowledgments](#acknowledgments)

## Overview

Modular streamlines game mod management by providing a unified interface to download mods from various sources (NexusMods, GameBanana), automatically organize them into structured directories (`Domain/Category/Mod_Id`), and rename folders from numeric IDs to human-readable names.

The project implements a clean three-layer architecture separating concerns between the CLI interface, core business logic, and a modern fluent HTTP client library.

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
│                    GUI Layer (Modular.Gui)                  │
│         Avalonia UI, MVVM, visual mod management            │
├─────────────────────────────────────────────────────────────┤
│                    CLI Layer (Modular.Cli)                  │
│            Interactive menu, progress display, I/O          │
├─────────────────────────────────────────────────────────────┤
│                  Core Library (Modular.Core)                │
│     NexusMods API, GameBanana API, Database, Config,        │
│     RateLimiter, Rename, TrackingValidator, Utils           │
├─────────────────────────────────────────────────────────────┤
│             Fluent HTTP Client (Modular.FluentHttp)         │
│    Modern fluent API, middleware filters, retry policies    │
└─────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

1. **GUI Layer (`Modular.Gui`)** - Cross-platform graphical interface built with Avalonia UI using MVVM architecture, featuring visual mod browsing, download queue management, and settings configuration
2. **CLI Layer (`Modular.Cli`)** - Interactive command-line interface with menu-driven operations and real-time progress visualization through LiveProgressDisplay
3. **Core Library (`Modular.Core`)** - Class library containing all business logic including HTTP operations, API integrations, rate limiting, file operations, and data persistence
4. **Fluent HTTP Layer (`Modular.FluentHttp`)** - Modern fluent-style HTTP client library with chainable request building, middleware filters, and type-safe responses

## Project Structure

```
Modular/
├── Modular.sln                           # Visual Studio solution file
├── BUILD.md                              # Build and installation guide
├── Makefile                              # Build shortcuts
├── src/
│   ├── Modular.Gui/                      # GUI application (Avalonia)
│   │   ├── Modular.Gui.csproj
│   │   ├── Program.cs                    # Entry point and DI setup
│   │   ├── App.axaml(.cs)                # Application and theme config
│   │   ├── Views/                        # XAML views
│   │   ├── ViewModels/                   # MVVM view models
│   │   └── Services/                     # GUI-specific services
│   ├── Modular.Cli/                      # CLI application
│   │   ├── Modular.Cli.csproj
│   │   ├── Program.cs                    # Entry point and command handlers
│   │   └── UI/
│   │       └── LiveProgressDisplay.cs    # Terminal progress visualization
│   ├── Modular.Core/                     # Core business logic library
│   │   ├── Modular.Core.csproj
│   │   ├── Configuration/
│   │   │   ├── AppSettings.cs            # Configuration model
│   │   │   └── ConfigurationService.cs   # Configuration loading/validation
│   │   ├── Database/
│   │   │   ├── DownloadDatabase.cs       # Download history persistence
│   │   │   ├── DownloadRecord.cs         # Download record model
│   │   │   └── ModMetadataCache.cs       # Mod metadata caching
│   │   ├── Exceptions/
│   │   │   └── ModularException.cs       # Custom exception hierarchy
│   │   ├── Http/
│   │   │   ├── ModularHttpClient.cs      # HTTP client wrapper
│   │   │   └── RetryPolicy.cs            # Retry configuration
│   │   ├── Models/
│   │   │   ├── TrackedMod.cs             # Tracked mod model
│   │   │   ├── ModFile.cs                # Mod file model
│   │   │   ├── DownloadLink.cs           # Download link model
│   │   │   ├── GameCategory.cs           # Game category model
│   │   │   └── ValidationResult.cs       # Validation result model
│   │   ├── RateLimiting/
│   │   │   ├── IRateLimiter.cs           # Rate limiter interface
│   │   │   └── NexusRateLimiter.cs       # NexusMods rate limiter
│   │   ├── Services/
│   │   │   ├── NexusModsService.cs       # NexusMods API integration
│   │   │   ├── GameBananaService.cs      # GameBanana API integration
│   │   │   ├── RenameService.cs          # Mod renaming and organization
│   │   │   └── TrackingValidatorService.cs # Web scraping validation
│   │   └── Utilities/
│   │       ├── FileUtils.cs              # File operation utilities
│   │       └── Md5Calculator.cs          # MD5 checksum calculation
│   └── Modular.FluentHttp/               # Fluent HTTP client library
│       ├── Modular.FluentHttp.csproj
│       ├── Interfaces/
│       │   ├── IFluentClient.cs          # Main client interface
│       │   ├── IRequest.cs               # Request builder interface
│       │   ├── IResponse.cs              # Response handler interface
│       │   ├── IHttpFilter.cs            # Middleware filter interface
│       │   └── IRetryConfig.cs           # Retry policy configuration
│       ├── Implementation/
│       │   ├── FluentClient.cs           # Main client implementation
│       │   ├── FluentRequest.cs          # Request builder
│       │   ├── FluentResponse.cs         # Response handler
│       │   └── RequestOptions.cs         # Request options model
│       └── Filters/
│           └── HttpFilters.cs            # Built-in middleware filters
├── tests/
│   ├── Modular.Core.Tests/               # Core library tests
│   │   ├── Modular.Core.Tests.csproj
│   │   ├── ConfigurationTests.cs
│   │   ├── DatabaseTests.cs
│   │   └── UtilityTests.cs
│   └── Modular.FluentHttp.Tests/         # Fluent HTTP client tests
│       ├── Modular.FluentHttp.Tests.csproj
│       └── FluentClientTests.cs
└── docs/                                 # Documentation
```

## Key Components

### NexusModsService (`src/Modular.Core/Services/NexusModsService.cs`)

Complete NexusMods API integration:
- Fetches tracked mods with domain information
- Retrieves file IDs for mods
- Generates time-limited download links
- Downloads files with progress callbacks
- Integrates with NexusRateLimiter for compliance

### NexusRateLimiter (`src/Modular.Core/RateLimiting/NexusRateLimiter.cs`)

Tracks NexusMods API rate limits from response headers:
- Parses `x-rl-daily-remaining` and `x-rl-hourly-remaining` headers
- Stores Unix timestamp reset times
- Implements async waiting when limits exhausted
- Supports state persistence between sessions

### GameBananaService (`src/Modular.Core/Services/GameBananaService.cs`)

GameBanana platform integration:
- Fetches user's subscribed mods
- Extracts file URLs from mod pages
- Downloads files with optional progress tracking
- No rate limiting required

### DownloadDatabase (`src/Modular.Core/Database/DownloadDatabase.cs`)

JSON-based download history persistence:
- Stores: game_domain, mod_id, file_id, filename, filepath, MD5, download time, status
- Operations: add, find, query by domain/mod, verification updates, removal
- Human-readable format for debugging

### ConfigurationService (`src/Modular.Core/Configuration/ConfigurationService.cs`)

Configuration management using Microsoft.Extensions.Configuration:
- Location: `~/.config/Modular/config.json`
- Precedence: Environment variables > Config file > Defaults
- Settings: API keys, paths, preferences

### RenameService (`src/Modular.Core/Services/RenameService.cs`)

Mod folder organization and renaming:
- Renames numeric ID folders to human-readable names
- Organizes mods into category subdirectories
- Caches metadata to reduce API calls

### LiveProgressDisplay (`src/Modular.Cli/UI/LiveProgressDisplay.cs`)

Real-time terminal progress visualization:
- Progress bars with percentage completion
- Status updates for scanning, downloading, organizing
- Interactive menu system
- Decoupled from core logic via callbacks

## Fluent HTTP Client

Modular includes a complete fluent HTTP client library. This provides a modern, chainable API for HTTP operations.

### Basic Usage

```csharp
using Modular.FluentHttp.Implementation;

// Create client
var client = FluentClientFactory.Create("https://api.nexusmods.com");

// Make a request with fluent API
var response = await client
    .GetAsync("/v1/users/validate.json")
    .WithHeader("apikey", apiKey)
    .WithHeader("accept", "application/json")
    .SendAsync();

// Access response data
var json = await response.AsJsonAsync<UserInfo>();
```

### Features

- **Method Chaining** - Build requests fluently with chainable methods
- **Async Support** - Full async/await support throughout
- **Middleware Filters** - Request/response interception pipeline
- **Type-Safe Responses** - Generic deserialization methods
- **Retry Policies** - Configurable retry with exponential backoff
- **Progress Callbacks** - Download progress tracking
- **Rate Limiting** - Integrated rate limiter support

### Request Building

```csharp
// POST with JSON body
var response = await client
    .PostAsync("/api/endpoint")
    .WithHeader("Content-Type", "application/json")
    .WithJsonBody(new { key = "value" })
    .SendAsync();

// GET with query parameters
var response = await client
    .GetAsync("/api/search")
    .WithQueryParam("q", "skyrim")
    .WithQueryParam("page", "1")
    .SendAsync();

// Download with progress
await client
    .GetAsync("/files/download/123")
    .WithProgress((downloaded, total) =>
        Console.WriteLine($"{downloaded}/{total}"))
    .DownloadAsync("/path/to/file");
```

### Client Configuration

```csharp
var client = FluentClientFactory.Create("https://api.example.com")
    .SetUserAgent("Modular/1.0")
    .SetBearerAuth(token)
    .SetRetryPolicy(maxRetries: 3, initialDelayMs: 1000)
    .SetConnectionTimeout(TimeSpan.FromSeconds(30))
    .SetRateLimiter(rateLimiter);
```

### Middleware Filters

Filters intercept requests and responses for cross-cutting concerns:

```csharp
// Add custom filter
client.AddFilter(new LoggingFilter(logger));

// Built-in filters
client.AddFilter(new AuthenticationFilter(apiKey));
client.AddFilter(new RateLimitFilter(rateLimiter));
```

## Design Patterns

### Builder Pattern (Fluent API)
Request/response configuration via method chaining improves code readability and discoverability.

### Middleware Filter Pattern
Request/response interception pipeline with filters for authentication, rate limiting, logging, and error handling.

### Dependency Injection
Services accept `ILogger<T>` and configuration via constructor parameters, enabling testability and flexibility.

### Repository Pattern
`DownloadDatabase` and `ModMetadataCache` abstract data persistence from business logic.

### Strategy Pattern
- Retry policies via `IRetryConfig`
- Rate limiting strategies via `IRateLimiter`

### Factory Pattern
`FluentClientFactory.Create()` separates interface from implementation.

## Error Handling

Modular uses a custom exception hierarchy for precise error handling:

| Exception | Description |
|-----------|-------------|
| `ModularException` | Base exception with context information |
| `ConfigurationException` | Configuration validation errors |
| `ApiException` | HTTP 4xx/5xx errors |
| `RateLimitException` | 429 responses with retry-after support |

Example usage:
```csharp
try
{
    await nexusService.DownloadFilesAsync(...);
}
catch (RateLimitException ex)
{
    Console.WriteLine($"Rate limited, retry after: {ex.RetryAfter}s");
}
catch (ModularException ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

## Dependencies

| Dependency | Purpose | Version |
|------------|---------|---------|
| **.NET SDK** | Runtime and build | 8.0+ |
| **System.CommandLine** | CLI argument parsing | 2.0+ |
| **Microsoft.Extensions.Configuration** | Configuration management | 8.0.0 |
| **Microsoft.Extensions.Logging** | Logging abstractions | 8.0.0 |
| **System.Text.Json** | JSON serialization | 8.0.5 |

## Building

### Prerequisites

- .NET SDK 8.0 or later
  - Check with: `dotnet --version`

### Installing .NET SDK

**Debian/Ubuntu:**
```bash
# Add Microsoft package repository
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

sudo apt update
sudo apt install dotnet-sdk-8.0
```

**Arch Linux:**
```bash
sudo pacman -S dotnet-sdk
```

**Fedora:**
```bash
sudo dnf install dotnet-sdk-8.0
```

### Build Commands

```bash
# Clone the repository
git clone https://github.com/MasterGenotype/Modular-1.git
cd Modular-1

# Build in Release mode
dotnet build -c Release

# Build in Debug mode
dotnet build -c Debug
```

### Installing to ~/.local/bin

**Framework-Dependent (Recommended):**
```bash
# Publish the CLI project
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -o ~/.local/share/modular --self-contained false

# Create launcher script
cat > ~/.local/bin/modular << 'EOF'
#!/bin/bash
exec dotnet "$HOME/.local/share/modular/Modular.Cli.dll" "$@"
EOF

chmod +x ~/.local/bin/modular
```

**Self-Contained (No .NET Runtime Required):**
```bash
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r linux-x64 --self-contained -o ~/.local/share/modular
cp ~/.local/share/modular/Modular.Cli ~/.local/bin/modular
chmod +x ~/.local/bin/modular
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
modular
```

This presents a menu with options:
1. NexusMods - Download tracked mods
2. GameBanana - Download subscribed mods
3. Rename - Rename mod folders to human-readable names

### Command-Line Arguments

```bash
# Download mods for a specific game domain
modular skyrimspecialedition

# Filter by category
modular skyrimspecialedition --categories main optional

# Dry run (show what would be downloaded)
modular skyrimspecialedition --dry-run

# Force re-download of existing files
modular skyrimspecialedition --force

# Organize mods into category subdirectories
modular skyrimspecialedition --organize-by-category

# Verbose output
modular skyrimspecialedition --verbose
```

### Subcommands

```bash
# Rename mod folders
modular rename skyrimspecialedition

# Rename all game domains
modular rename

# Download from GameBanana
modular gamebanana

# Fetch and cache metadata without renaming
modular fetch skyrimspecialedition
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

## GUI Application

Modular includes a full-featured graphical user interface built with Avalonia UI, providing a visual way to manage mods across multiple platforms.

### GUI Features

- **Multi-Platform Browsing** - Browse NexusMods tracked mods and GameBanana subscriptions in dedicated views
- **Download Queue** - Visual download queue with progress tracking, pause/resume, and drag-and-drop reordering
- **Mod Library** - Browse and manage downloaded mods with search and filtering
- **Update Checking** - Check for mod updates with visual status indicators
- **Download History** - Track download statistics and history
- **Settings Management** - Configure all options through a visual interface
- **Keyboard Shortcuts** - Quick access to common operations

### Building the GUI

```bash
# Build the GUI
make gui

# Or using dotnet directly
dotnet build src/Modular.Gui/Modular.Gui.csproj -c Release
```

### Running the GUI

```bash
# Run via Makefile
make gui-run

# Or using dotnet directly
dotnet run --project src/Modular.Gui/Modular.Gui.csproj
```

### Publishing the GUI

```bash
# Publish for Linux (self-contained)
make gui-publish-linux

# Publish for Windows (self-contained)
make gui-publish-windows

# Output is in publish/gui-linux-x64/ or publish/gui-win-x64/
```

### GUI Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+R` | Refresh current view |
| `Ctrl+D` | Start downloads |
| `Ctrl+Q` | Quit application |
| `Escape` | Cancel current operation |

### GUI Views

**NexusMods View**
- Displays all tracked mods from your NexusMods account
- Shows mod name, game, category, and update status
- Select mods and add to download queue
- Check for updates across all tracked mods

**GameBanana View**
- Displays subscribed mods from your GameBanana account
- Browse by game with search filtering
- Add mods to download queue

**Downloads View**
- Active download queue with real-time progress
- Drag-and-drop to reorder queue
- Pause, resume, or cancel individual downloads
- Download history statistics panel

**Library View**
- Browse all downloaded mods
- Search and filter by name or game
- View mod details and file locations

**Settings View**
- Configure API keys (NexusMods, GameBanana)
- Set download path and organization options
- Toggle verification and auto-rename features

## Testing

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Modular.Core.Tests/
dotnet test tests/Modular.FluentHttp.Tests/

# Run with verbose output
dotnet test --verbosity normal
```

### Test Coverage

- Configuration loading and validation
- Utility functions (filename sanitization, MD5 calculation)
- Database operations (add, find, query, remove)
- Fluent HTTP client request building

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
| `GUI_RECOMMENDATIONS.md` | GUI implementation recommendations |
| `CODEBASE_REVIEW_RECOMMENDATIONS.md` | Comprehensive codebase review |

## Contributing

1. Fork this repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes following the existing code style
4. Add tests for new functionality
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

### Code Style

- C# 12 / .NET 8.0
- 4-space indentation
- PascalCase for types and public members, camelCase for private fields with underscore prefix
- File-scoped namespaces
- Nullable reference types enabled
- Treat warnings as errors

## License

MIT License - See LICENSE file for details.

## Acknowledgments

- [NexusMods](https://www.nexusmods.com/) for their comprehensive modding API
- [GameBanana](https://gamebanana.com/) for their mod hosting platform
- [System.CommandLine](https://github.com/dotnet/command-line-api) for CLI argument parsing
- [Microsoft.Extensions](https://github.com/dotnet/runtime) for configuration and logging
