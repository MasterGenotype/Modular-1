# Modular

A sophisticated C# .NET 8.0 command-line application for automating the downloading, organizing, and management of game modifications from multiple mod repositories. Features a modern fluent HTTP client API, intelligent rate limiting, and real-time progress tracking.

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

## Overview

Modular streamlines game mod management by providing a unified interface to download mods from various sources (NexusMods, GameBanana), automatically organize them into structured directories (`Domain/Category/Mod_Name`), and rename folders from numeric IDs to human-readable names.

The project implements a clean three-layer architecture separating concerns between the CLI interface, core business logic, and a modern fluent HTTP client library.

## Features

### Core Functionality
- **Multi-Repository Support** - Download mods from NexusMods and GameBanana with extensible architecture for additional sources
- **Automatic Organization** - Organizes mods into game-specific directories with optional category-based subdirectories
- **Smart Renaming** - Converts numeric mod ID folders to human-readable names using API metadata
- **Download Verification** - MD5 checksum verification with persistent download history database
- **Tracking Validation** - Cross-validates API tracking against NexusMods web tracking center

### Network & Performance
- **Rate Limit Compliance** - Built-in rate limiter respects NexusMods API limits (20,000 requests/day, 500/hour)
- **Retry Logic** - Automatic retry with exponential backoff for failed network requests
- **Fluent HTTP API** - Modern chainable HTTP client with middleware filter pipeline
- **Progress Callbacks** - Real-time progress tracking with Spectre.Console visualizations

### Configuration & Usability
- **Flexible Configuration** - Supports environment variables, config files, and command-line arguments
- **Real-Time Progress** - Live progress bars for all download and organization operations
- **Persistent State** - Rate limiter and download history persist between sessions
- **Self-Contained Deployment** - Single executable with no external runtime dependencies

## Architecture

Modular follows a clean **three-layer architecture**:

```
┌─────────────────────────────────────────────────────────────┐
│                   CLI Layer (Modular.Cli)                   │
│         Interactive menu, progress display, commands        │
├─────────────────────────────────────────────────────────────┤
│                Core Library (Modular.Core)                  │
│     NexusMods API, GameBanana API, Database, Config,        │
│     RateLimiter, RenameService, Utilities, Exceptions       │
├─────────────────────────────────────────────────────────────┤
│            Fluent HTTP Client (Modular.FluentHttp)          │
│     Modern fluent API, middleware filters, retry policies   │
└─────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

1. **CLI Layer (`Modular.Cli`)** - Interactive command-line interface with menu-driven operations and real-time progress visualization via Spectre.Console
2. **Core Library (`Modular.Core`)** - Contains all business logic including API integrations, rate limiting, file operations, and data persistence
3. **Fluent HTTP Layer (`Modular.FluentHttp`)** - Modern fluent-style HTTP client library with chainable request building, middleware filters, and type-safe responses

## Project Structure

```
Modular-1/
├── Modular.sln                          # Solution file
├── Makefile                             # Build automation
├── BUILD.md                             # Build instructions
├── src/
│   ├── Modular.Cli/                     # CLI executable
│   │   ├── Program.cs                   # Entry point and commands
│   │   ├── Modular.Cli.csproj
│   │   └── UI/
│   │       └── LiveProgressDisplay.cs   # Spectre.Console progress UI
│   │
│   ├── Modular.Core/                    # Core business logic
│   │   ├── Modular.Core.csproj
│   │   ├── Configuration/
│   │   │   ├── AppSettings.cs           # Settings model
│   │   │   └── ConfigurationService.cs  # Config loading/validation
│   │   ├── Database/
│   │   │   ├── DownloadDatabase.cs      # JSON persistence
│   │   │   └── DownloadRecord.cs        # Record model
│   │   ├── Exceptions/
│   │   │   └── ModularExceptions.cs     # Exception hierarchy
│   │   ├── Http/
│   │   │   ├── ModularHttpClient.cs     # Base HTTP client
│   │   │   └── RateLimiterAdapter.cs    # Rate limiter bridge
│   │   ├── Models/
│   │   │   ├── NexusModels.cs           # NexusMods API models
│   │   │   ├── GameBananaModels.cs      # GameBanana API models
│   │   │   └── ...
│   │   ├── RateLimiting/
│   │   │   ├── IRateLimiter.cs          # Rate limiter interface
│   │   │   └── NexusRateLimiter.cs      # NexusMods rate limiter
│   │   ├── Services/
│   │   │   ├── NexusModsService.cs      # NexusMods operations
│   │   │   ├── GameBananaService.cs     # GameBanana operations
│   │   │   ├── RenameService.cs         # Folder renaming
│   │   │   └── TrackingValidatorService.cs
│   │   └── Utilities/
│   │       └── FileUtils.cs             # File/path utilities
│   │
│   └── Modular.FluentHttp/              # Fluent HTTP library
│       ├── Modular.FluentHttp.csproj
│       ├── Interfaces/
│       │   ├── IFluentClient.cs         # Client interface
│       │   ├── IFluentRequest.cs        # Request builder
│       │   ├── IFluentResponse.cs       # Response handler
│       │   ├── IHttpFilter.cs           # Middleware filter
│       │   └── IRateLimiter.cs          # Rate limiter interface
│       ├── Implementation/
│       │   ├── FluentClient.cs          # Main client
│       │   ├── FluentClientFactory.cs   # Factory
│       │   ├── FluentRequest.cs         # Request builder
│       │   └── FluentResponse.cs        # Response wrapper
│       └── Filters/
│           └── BuiltInFilters.cs        # Auth, logging, retry filters
│
├── tests/
│   ├── Modular.Core.Tests/
│   │   ├── UtilityTests.cs
│   │   ├── DatabaseTests.cs
│   │   └── ConfigurationTests.cs
│   └── Modular.FluentHttp.Tests/
│       └── FluentClientTests.cs
│
└── docs/                                # Documentation
    ├── CODEBASE_REVIEW_RECOMMENDATIONS.md
    ├── CSHARP_MIGRATION_INSTRUCTIONS.md
    ├── IMPROVEMENTS.md
    ├── RATE_LIMITING_FIXES_SUMMARY.md
    ├── TRACKING_VALIDATION.md
    └── CATEGORY_ORGANIZATION.md
```

## Key Components

### NexusModsService (`Services/NexusModsService.cs`)

Complete NexusMods API integration:
- Fetches tracked mods grouped by game domain
- Retrieves file metadata and categories
- Generates time-limited download links
- Downloads files with progress callbacks
- Integrates with rate limiter for API compliance

### GameBananaService (`Services/GameBananaService.cs`)

GameBanana platform integration:
- Fetches user's subscribed mods via API
- Downloads mod files with progress tracking
- Organizes downloads by mod name

### NexusRateLimiter (`RateLimiting/NexusRateLimiter.cs`)

Tracks NexusMods API rate limits from response headers:
- Parses `x-rl-daily-remaining` and `x-rl-hourly-remaining` headers
- Implements proactive waiting when approaching limits
- Supports state persistence between sessions
- Thread-safe with lock-based synchronization

### DownloadDatabase (`Database/DownloadDatabase.cs`)

JSON-based download history persistence:
- Stores: game domain, mod ID, file ID, filename, filepath, MD5, timestamp, status
- Operations: add, find, query by domain/mod, status updates
- Thread-safe with automatic saving

### ConfigurationService (`Configuration/ConfigurationService.cs`)

Configuration management with multiple sources:
- Location: `~/.config/Modular/config.json`
- Precedence: Environment variables > Config file > Defaults
- Validation with descriptive error messages

### LiveProgressDisplay (`UI/LiveProgressDisplay.cs`)

Real-time terminal UI using Spectre.Console:
- Progress bars with percentage completion
- Interactive numbered menus
- Status messages (info, success, warning, error)

## Fluent HTTP Client

Modular includes a complete fluent HTTP client library (`Modular.FluentHttp`) providing a modern, chainable API for HTTP operations.

### Basic Usage

```csharp
using Modular.FluentHttp;

// Create client with factory
var factory = new FluentClientFactory();
var client = factory.Create("https://api.nexusmods.com");

// Make a request with fluent API
var response = await client
    .Request("/v1/users/validate.json")
    .WithHeader("apikey", apiKey)
    .WithHeader("accept", "application/json")
    .GetAsync();

// Deserialize response
var user = await response.AsAsync<UserValidation>();
```

### Features

- **Method Chaining** - Build requests fluently with chainable methods
- **Async/Await** - Fully asynchronous with `CancellationToken` support
- **Middleware Filters** - Request/response interception pipeline
- **Type-Safe Responses** - Generic deserialization with `System.Text.Json`
- **Retry Policies** - Configurable retry with exponential backoff
- **Progress Callbacks** - Download progress tracking
- **Rate Limiting** - Integrated rate limiter support

### Request Building

```csharp
// GET with headers
var response = await client
    .Request("/api/endpoint")
    .WithHeader("Authorization", "Bearer token")
    .WithQueryParam("page", "1")
    .GetAsync(cancellationToken);

// POST with JSON body
var response = await client
    .Request("/api/endpoint")
    .WithJsonBody(new { key = "value" })
    .PostAsync();

// Download with progress
await client
    .Request("/files/download/123")
    .WithProgress((downloaded, total) => Console.WriteLine($"{downloaded}/{total}"))
    .DownloadAsync("/path/to/file");
```

### Middleware Filters

Built-in filters for common operations:

```csharp
// Add filters to client
client.AddFilter(new AuthenticationFilter(apiKey));
client.AddFilter(new RateLimitFilter(rateLimiter));
client.AddFilter(new LoggingFilter(logger));
client.AddFilter(new RetryFilter(maxRetries: 3));
```

Filters execute in priority order for requests and reverse order for responses.

## Design Patterns

| Pattern | Location | Purpose |
|---------|----------|---------|
| **Builder Pattern** | FluentRequest | Chainable request configuration |
| **Filter Pipeline** | IHttpFilter | Request/response middleware |
| **Factory Pattern** | FluentClientFactory | Client instantiation |
| **Repository Pattern** | DownloadDatabase | Data persistence abstraction |
| **Strategy Pattern** | IRateLimiter | Pluggable rate limiting |
| **Dependency Injection** | Service constructors | Testability and flexibility |

## Error Handling

Modular uses a custom exception hierarchy for precise error handling:

| Exception | Description |
|-----------|-------------|
| `ModularException` | Base exception with context information |
| `NetworkException` | HTTP errors, timeouts, DNS failures |
| `ApiException` | API-specific errors with status codes |
| `RateLimitException` | 429 responses with retry-after support |
| `AuthException` | 401/403 authentication failures |
| `ParseException` | JSON deserialization errors |
| `FileSystemException` | File I/O errors |
| `ConfigException` | Configuration validation errors |

Example usage:
```csharp
try
{
    await nexusService.DownloadFilesAsync(domain, progress);
}
catch (RateLimitException ex)
{
    Console.WriteLine($"Rate limited, retry after: {ex.RetryAfter}");
}
catch (AuthException ex)
{
    Console.WriteLine($"Authentication failed: {ex.Message}");
}
catch (NetworkException ex)
{
    Console.WriteLine($"Network error: {ex.Message}");
}
```

## Dependencies

### Runtime Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Extensions.Configuration | 8.0.0 | Configuration management |
| Microsoft.Extensions.Configuration.Json | 8.0.0 | JSON config provider |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 8.0.0 | Environment variable support |
| Microsoft.Extensions.Logging.Abstractions | 8.0.0 | Logging interface |
| Microsoft.Extensions.Options | 8.0.0 | Options pattern |
| System.Text.Json | 8.0.5 | JSON serialization |
| System.CommandLine | 2.0.0-beta4 | CLI argument parsing |
| Spectre.Console | 0.49.1 | Terminal UI |

### Test Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| xunit | 2.6.6 | Test framework |
| FluentAssertions | 6.12.0 | Assertion library |
| Moq | 4.20.70 | Mocking framework |

## Building

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build Commands

```bash
# Clone the repository
git clone https://github.com/MasterGenotype/Modular-1.git
cd Modular-1

# Build (Debug)
make build
# or: dotnet build

# Build (Release, self-contained)
make release
# or: dotnet publish src/Modular.Cli -c Release -r linux-x64 --self-contained

# Install to ~/.local/bin
make install

# Uninstall
make uninstall

# Run tests
make test
# or: dotnet test

# Clean build artifacts
make clean
```

### Build Output

- **Debug**: `src/Modular.Cli/bin/Debug/net8.0/`
- **Release**: `src/Modular.Cli/bin/Release/net8.0/linux-x64/publish/modular`

## Configuration

### Environment Variables

| Variable | Description |
|----------|-------------|
| `NEXUS_API_KEY` | NexusMods API key (required for NexusMods features) |
| `GB_USER_ID` | GameBanana User ID (required for GameBanana features) |

### Config File

Configuration is stored in `~/.config/Modular/config.json`:

```json
{
  "nexus_api_key": "your-api-key",
  "gamebanana_user_id": "your-user-id",
  "mods_directory": "~/Games/Mods-Lists",
  "default_categories": ["main", "optional"],
  "auto_rename": true,
  "organize_by_category": true,
  "verify_downloads": false,
  "validate_tracking": false,
  "max_concurrent_downloads": 1,
  "verbose": false,
  "database_path": "~/.config/Modular/downloads.json",
  "rate_limit_state_path": "~/.config/Modular/rate_limit_state.json"
}
```

**Configuration Precedence:** Environment variables > Config file > Defaults

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `nexus_api_key` | string | - | NexusMods API key |
| `gamebanana_user_id` | string | - | GameBanana user ID |
| `mods_directory` | string | `~/Games/Mods-Lists` | Base directory for downloads |
| `default_categories` | string[] | `["main", "optional"]` | File categories to download |
| `auto_rename` | bool | `true` | Rename mod folders to human-readable names |
| `organize_by_category` | bool | `true` | Create category subdirectories |
| `verify_downloads` | bool | `false` | Verify MD5 checksums after download |
| `validate_tracking` | bool | `false` | Validate tracking against web interface |
| `max_concurrent_downloads` | int | `1` | Maximum parallel downloads |
| `verbose` | bool | `false` | Enable verbose logging |

## Usage

### Interactive Mode

```bash
modular
```

Presents a menu with options:
1. NexusMods - Download tracked mods
2. GameBanana - Download subscribed mods
3. Rename - Convert ID folders to names

### Command-Line Mode

```bash
# Download mods for a specific game
modular skyrimspecialedition

# Filter by categories
modular skyrimspecialedition --categories main optional

# Dry run (preview without downloading)
modular skyrimspecialedition --dry-run

# Force re-download existing files
modular skyrimspecialedition --force

# Organize into category subdirectories
modular skyrimspecialedition --organize-by-category

# Enable verbose output
modular skyrimspecialedition --verbose

# Rename command only
modular rename skyrimspecialedition

# GameBanana downloads
modular gamebanana
```

### Output Structure

After downloading and organizing, mods are stored as:

```
~/Games/Mods-Lists/
├── skyrimspecialedition/
│   ├── Main/
│   │   ├── SkyUI/
│   │   │   └── SkyUI_5_2_SE-12604-5-2SE.7z
│   │   └── SKSE64/
│   │       └── skse64_2_02_06.7z
│   └── Optional/
│       └── Better_Dialogue_Controls/
│           └── Better Dialogue Controls-1429-1-2-1.zip
├── stardewvalley/
│   └── Main/
│       └── SMAPI/
│           └── SMAPI 4.0.0.zip
└── gamebanana/
    └── Cool_Mod/
        └── cool_mod_v1.zip
```

## Testing

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"

# Run specific test project
dotnet test tests/Modular.Core.Tests
dotnet test tests/Modular.FluentHttp.Tests
```

### Test Coverage

- **UtilityTests** - Path expansion, filename sanitization
- **DatabaseTests** - Record CRUD, persistence, status queries
- **ConfigurationTests** - Config loading, environment variable overrides
- **FluentClientTests** - Request building, response handling

## Workflows

### NexusMods Download Workflow

```
1. Load Configuration
   └─ Read config file and environment variables
   └─ Validate API key

2. Initialize Services
   └─ Load rate limiter state
   └─ Load download database

3. Fetch Tracked Mods
   └─ GET /v1/user/tracked_mods.json
   └─ Group mods by game domain

4. For Each Mod:
   └─ Fetch file metadata
   └─ Filter by categories (main, optional, etc.)
   └─ Generate time-limited download links
   └─ Download with progress tracking
   └─ Verify MD5 checksum (optional)
   └─ Record in database

5. Post-Processing
   └─ Rename folders (ID → human-readable name)
   └─ Organize into category subdirectories
   └─ Save rate limiter state
```

### GameBanana Download Workflow

```
1. Load Configuration
   └─ Validate GameBanana user ID

2. Fetch Subscribed Mods
   └─ Query GameBanana API

3. For Each Mod:
   └─ Get downloadable files list
   └─ Create sanitized folder
   └─ Download with progress tracking

4. Files organized in gamebanana/ directory
```

## Documentation

Additional documentation is available in the `/docs/` directory:

| Document | Description |
|----------|-------------|
| `CODEBASE_REVIEW_RECOMMENDATIONS.md` | Architecture and improvement recommendations |
| `CSHARP_MIGRATION_INSTRUCTIONS.md` | C++ to C# migration guide |
| `IMPROVEMENTS.md` | Feature improvements and enhancements |
| `RATE_LIMITING_FIXES_SUMMARY.md` | Rate limiting implementation details |
| `TRACKING_VALIDATION.md` | Web scraping validation methodology |
| `CATEGORY_ORGANIZATION.md` | Category-based organization system |

## Contributing

1. Fork this repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes following the existing code style
4. Add tests for new functionality
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

### Code Style

- .NET 8.0 with nullable reference types enabled
- 4-space indentation
- PascalCase for types and public members
- camelCase for private fields (with `_` prefix)
- XML documentation on public APIs
- `TreatWarningsAsErrors` enabled

## License

MIT License - See LICENSE file for details.

## Acknowledgments

- [NexusMods](https://www.nexusmods.com/) for their comprehensive modding API
- [GameBanana](https://gamebanana.com/) for their mod hosting platform
- [Spectre.Console](https://spectreconsole.net/) for the excellent terminal UI library
- [System.CommandLine](https://github.com/dotnet/command-line-api) for CLI parsing
