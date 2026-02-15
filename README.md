# Modular

A next-generation, extensible C#/.NET mod manager for automating the downloading, organizing, and management of game modifications from multiple mod repositories. Features a plugin architecture, SSO authentication, both CLI and GUI interfaces built with Avalonia, a modern fluent HTTP client API, intelligent rate limiting, and real-time progress tracking.

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

Modular is a next-generation mod manager that streamlines game mod management through a unified, extensible interface. It downloads mods from multiple sources (NexusMods, GameBanana, and more via plugins), automatically organizes them into structured directories (`Domain/Category/Mod_Id`), and renames folders from numeric IDs to human-readable names.

The project implements a clean **plugin-based architecture** with a stable SDK for extensions, separating concerns between the CLI interface, GUI, core business logic, plugin system, and a modern fluent HTTP client library. This enables community-developed backends, installers, metadata enrichers, and UI extensions without modifying the core codebase.

## Features

### Core Functionality
- **Plugin Architecture** - Extensible system for community-developed backends, installers, metadata enrichers, and UI extensions
- **Multi-Repository Support** - Download mods from NexusMods and GameBanana with extensible backend abstraction for additional sources
- **SSO Authentication** - Browser-based NexusMods authentication flow via WebSocket SSO (no manual API key required)
- **Automatic Organization** - Organizes mods into game-specific directories with optional category-based subdirectories
- **Smart Renaming** - Converts numeric mod ID folders to human-readable names using API metadata
- **Download Verification** - MD5/SHA256 checksum verification with persistent download history database
- **Tracking Validation** - Cross-validates API tracking against NexusMods web tracking center via web scraping
- **Metadata Enrichment** - Plugin system for transforming backend-specific data into canonical format
- **Custom Installers** - Extensible installer framework for handling different mod formats (FOMOD, loose files, etc.)

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

Modular follows a clean **plugin-based architecture** with clear separation of concerns:

```
┌─────────────────────────────────────────────────────────────┐
│                    GUI Layer (Modular.Gui)                  │
│       Avalonia UI, MVVM, visual mod management + plugins    │
├─────────────────────────────────────────────────────────────┤
│                    CLI Layer (Modular.Cli)                  │
│        Interactive menu, Spectre.Console, progress I/O      │
├─────────────────────────────────────────────────────────────┤
│                  Core Library (Modular.Core)                │
│   Plugin System, Backend Abstraction, Authentication (SSO), │
│   Metadata, Installers, Dependencies, Diagnostics, Profiles │
├─────────────────────────────────────────────────────────────┤
│                    SDK Layer (Modular.Sdk)                  │
│    Stable contracts for plugins: interfaces, records,       │
│    IModBackend, IMetadataEnricher, IModInstaller            │
├─────────────────────────────────────────────────────────────┤
│             Fluent HTTP Client (Modular.FluentHttp)         │
│    Modern fluent API, middleware filters, retry policies    │
└─────────────────────────────────────────────────────────────┘
            ↑                                   ↑
            └───────────────┬───────────────────┘
                      Community Plugins
         (Backends, Installers, Enrichers, UI Extensions)
```

### Layer Responsibilities

1. **GUI Layer (`Modular.Gui`)** - Cross-platform graphical interface built with Avalonia UI 11.3 using MVVM architecture and Material Icons, featuring visual mod browsing, download queue management, plugin UI extensions, and settings configuration
2. **CLI Layer (`Modular.Cli`)** - Interactive command-line interface using Spectre.Console and System.CommandLine with menu-driven operations and rich progress visualization
3. **Core Library (`Modular.Core`)** - Class library containing all business logic:
   - **Plugin System** - Dynamic loading via AssemblyLoadContext, MEF composition, plugin marketplace integration
   - **Backend Abstraction** - Unified interface (IModBackend) with capability flags and registry
   - **Authentication** - SSO flows (NexusMods WebSocket), OAuth2, API key management
   - **Metadata & Installers** - Enrichers for canonical schema, extensible installer framework
   - **Dependencies & Profiles** - Mod dependency resolution, conflict detection, profile management
   - **Diagnostics & Telemetry** - Health checks, performance metrics, error reporting
4. **SDK Layer (`Modular.Sdk`)** - Stable plugin contracts defining extension points: `IModBackend`, `IMetadataEnricher`, `IModInstaller`, `IUiExtension`, and shared data models
5. **Fluent HTTP Layer (`Modular.FluentHttp`)** - Modern fluent-style HTTP client library with chainable request building, middleware filters, and type-safe responses

## Project Structure

```
Modular/
├── Modular.sln                           # Visual Studio solution file
├── BUILD.md                              # Build and installation guide
├── Makefile                              # Build shortcuts
├── src/
│   ├── Modular.Gui/                      # GUI application (Avalonia 11.3)
│   │   ├── Modular.Gui.csproj
│   │   ├── Program.cs                    # Entry point and DI setup
│   │   ├── App.axaml(.cs)                # Application and theme config
│   │   ├── Views/                        # XAML views
│   │   ├── ViewModels/                   # MVVM view models
│   │   ├── Services/                     # GUI-specific services
│   │   ├── Converters/                   # Value converters
│   │   ├── Models/                       # GUI data models
│   │   └── Assets/                       # Icons, images, resources
│   ├── Modular.Cli/                      # CLI application
│   │   ├── Modular.Cli.csproj
│   │   ├── Program.cs                    # Entry point and command handlers
│   │   └── UI/                           # Terminal UI components
│   ├── Modular.Core/                     # Core business logic library
│   │   ├── Modular.Core.csproj
│   │   ├── Authentication/               # Auth strategies (SSO, OAuth2)
│   │   │   └── NexusSsoClient.cs         # NexusMods SSO WebSocket client
│   │   ├── Backends/                     # Backend implementations
│   │   │   ├── IModBackend.cs            # Backend interface
│   │   │   ├── BackendRegistry.cs        # Backend discovery and registry
│   │   │   ├── BackendCapabilities.cs    # Feature flags per backend
│   │   │   ├── NexusMods/                # NexusMods backend
│   │   │   └── GameBanana/               # GameBanana backend
│   │   ├── Configuration/
│   │   │   ├── AppSettings.cs            # Configuration model
│   │   │   └── ConfigurationService.cs   # Configuration loading/validation
│   │   ├── Database/
│   │   │   ├── DownloadDatabase.cs       # Download history persistence
│   │   │   ├── DownloadRecord.cs         # Download record model
│   │   │   └── ModMetadataCache.cs       # Mod metadata caching
│   │   ├── Dependencies/                 # Dependency resolution
│   │   │   ├── DependencyGraph.cs        # Mod dependency graph
│   │   │   ├── DependencyEdge.cs         # Graph edge model
│   │   │   ├── ModNode.cs               # Graph node model
│   │   │   ├── FileConflictIndex.cs     # File conflict detection
│   │   │   ├── ConflictResolver.cs      # Conflict resolution strategies
│   │   │   ├── PubGrubResolver.cs       # PubGrub-inspired version resolver
│   │   │   ├── ModProfile.cs            # Mod profile/collection model
│   │   │   └── ResolutionResult.cs      # Resolution result model
│   │   ├── Diagnostics/                  # Health checks and diagnostics
│   │   │   └── DiagnosticService.cs      # System diagnostics
│   │   ├── Exceptions/
│   │   │   └── ModularException.cs       # Custom exception hierarchy
│   │   ├── Downloads/                    # Download orchestration
│   │   │   ├── DownloadQueue.cs          # Download queue management
│   │   │   └── DownloadEngine.cs         # Production-grade download handler
│   │   ├── ErrorHandling/               # Error isolation
│   │   │   ├── ErrorBoundary.cs          # Plugin error isolation
│   │   │   └── RetryPolicy.cs           # Retry configuration
│   │   ├── Http/
│   │   │   ├── ModularHttpClient.cs      # HTTP client wrapper
│   │   │   ├── RetryPolicy.cs            # Retry logic
│   │   │   └── HttpCache.cs             # Response caching
│   │   ├── Installers/                   # Installer framework
│   │   │   ├── InstallerManager.cs       # Installer orchestration
│   │   │   ├── FomodInstaller.cs        # FOMOD format support
│   │   │   ├── BepInExInstaller.cs      # BepInEx plugin installer
│   │   │   └── LooseFileInstaller.cs    # Simple file extraction
│   │   ├── Metadata/                     # Metadata enrichment
│   │   │   ├── IMetadataEnricher.cs      # Enricher interface
│   │   │   ├── CanonicalMod.cs          # Unified metadata schema
│   │   │   ├── CanonicalFile.cs         # File representation
│   │   │   ├── CanonicalVersion.cs      # Version representation
│   │   │   └── ModDependency.cs         # Dependency model
│   │   ├── Models/
│   │   │   ├── TrackedMod.cs             # Tracked mod model
│   │   │   ├── ModFile.cs                # Mod file model
│   │   │   ├── DownloadLink.cs           # Download link model
│   │   │   ├── GameCategory.cs           # Game category model
│   │   │   └── ValidationResult.cs       # Validation result model
│   │   ├── Plugins/                      # Plugin system core
│   │   │   ├── PluginLoader.cs           # AssemblyLoadContext-based loader
│   │   │   ├── PluginComposer.cs         # MEF composition
│   │   │   ├── PluginManifest.cs         # Plugin metadata
│   │   │   ├── PluginLoadContext.cs      # Isolated load context
│   │   │   └── PluginMarketplace.cs      # Plugin discovery/installation
│   │   ├── Profiles/                     # Profile management
│   │   │   └── ProfileExporter.cs        # Profile export/import
│   │   ├── RateLimiting/
│   │   │   ├── IRateLimiter.cs           # Rate limiter interface
│   │   │   ├── NexusRateLimiter.cs       # NexusMods rate limiter
│   │   │   └── RateLimitScheduler.cs     # Request scheduling
│   │   ├── Services/
│   │   │   ├── NexusModsService.cs       # NexusMods API (legacy, use NexusModsBackend)
│   │   │   ├── GameBananaService.cs      # GameBanana API (legacy, use GameBananaBackend)
│   │   │   ├── RenameService.cs          # Mod renaming and organization
│   │   │   └── TrackingValidatorService.cs # Web scraping validation
│   │   ├── Telemetry/                    # Performance metrics
│   │   │   └── TelemetryService.cs       # Metrics collection
│   │   ├── Utilities/
│   │   │   ├── FileUtils.cs              # File operation utilities
│   │   │   └── Md5Calculator.cs          # MD5 checksum calculation
│   │   └── Versioning/                   # Version comparison
│   │       ├── SemanticVersion.cs        # SemVer implementation
│   │       └── VersionRange.cs           # Version range constraints
│   ├── Modular.Sdk/                      # Plugin SDK contracts
│   │   ├── Modular.Sdk.csproj
│   │   ├── Backends/                     # Backend contracts
│   │   │   ├── IModBackend.cs            # Backend interface
│   │   │   └── BackendModels.cs          # Shared models
│   │   ├── Installers/                   # Installer contracts
│   │   │   └── IModInstaller.cs          # Installer interface
│   │   ├── Metadata/                     # Metadata contracts
│   │   │   └── IMetadataEnricher.cs      # Enricher interface
│   │   └── UI/                           # UI extension contracts
│   │       └── IUiExtension.cs           # UI extension interface
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
│       ├── Filters/
│       │   └── HttpFilters.cs            # Built-in middleware filters
│       └── Retry/                        # Retry policy implementations
├── examples/
│   └── ExamplePlugin/                    # Example plugin project
│       ├── ExamplePlugin.csproj
│       ├── plugin.json                   # Plugin manifest
│       └── ExampleBackend.cs             # Sample backend implementation
├── tests/
│   ├── Modular.Core.Tests/               # Core library tests
│   │   ├── Modular.Core.Tests.csproj
│   │   ├── ConfigurationTests.cs
│   │   ├── DatabaseTests.cs
│   │   ├── UtilityTests.cs
│   │   └── Backends/                     # Backend-specific tests
│   │       ├── BackendRegistryTests.cs
│   │       ├── NexusModsBackendTests.cs
│   │       └── GameBananaBackendTests.cs
│   └── Modular.FluentHttp.Tests/         # Fluent HTTP client tests
│       ├── Modular.FluentHttp.Tests.csproj
│       └── FluentClientTests.cs
└── docs/                                 # Documentation
    ├── PLUGIN_DEVELOPMENT.md             # Plugin development guide
    ├── NEXUSMODS_SSO_INTEGRATION.md      # SSO integration docs
    ├── API-BACKENDS-GUIDE.md             # Backend development guide
    ├── GAMEBANANA-API-GUIDE.md           # GameBanana API guide
    ├── Modular_1_Blueprint.md            # Architecture blueprint
    └── fluent/                           # Fluent HTTP docs
```

## Plugin System

Modular features a robust plugin architecture enabling community-developed extensions without modifying the core codebase.

### Extension Points

**Mod Backends (`IModBackend`)** - Add support for new mod repositories
- Implement `ListUserModsAsync`, `ListFilesAsync`, `DownloadAsync`
- Declare backend capabilities via `BackendCapabilities` (game domains, categories, rate limits, etc.)
- Examples: NexusMods, GameBanana, Modrinth, CurseForge

**Metadata Enrichers (`IMetadataEnricher`)** - Transform backend-specific data to canonical format
- Map backend-specific fields to unified schema
- Infer missing metadata (dependencies, install instructions)
- Enable cross-source queries and searches

**Mod Installers (`IModInstaller`)** - Handle installation workflows
- `DetectAsync` - Analyze archive structure and determine compatibility
- `AnalyzeAsync` - Generate installation plan with file operations
- `InstallAsync` - Execute installation with progress reporting
- Examples: FOMOD installers, BepInEx plugins, loose file extraction

**UI Extensions (`IUiExtension`)** - Add custom panels to the GUI
- Integrate seamlessly into the Avalonia UI
- Access core services via dependency injection
- Respond to lifecycle events (activation/deactivation)

### Plugin Loading

Plugins are loaded dynamically at runtime using:
- **AssemblyLoadContext** - Isolated loading with dependency resolution
- **MEF (Managed Extensibility Framework)** - Attribute-based composition via `System.Composition`
- **Plugin Marketplace** - Centralized plugin discovery, installation, and updates

### Plugin Development

1. Reference `Modular.Sdk.csproj` (stable contracts)
2. Implement desired interface (`IModBackend`, `IMetadataEnricher`, etc.)
3. Create `plugin.json` manifest with metadata and entry assembly
4. Build and package as a DLL
5. Install to `~/.config/Modular/plugins/`

See [`docs/PLUGIN_DEVELOPMENT.md`](docs/PLUGIN_DEVELOPMENT.md) and [`examples/ExamplePlugin/`](examples/ExamplePlugin/) for complete guides.

## Key Components

### Backend Abstraction (`src/Modular.Core/Backends/`)

Unified interface for mod repositories:
- **IModBackend** - Standard operations across all backends
- **BackendRegistry** - Runtime backend discovery and selection
- **BackendCapabilities** - Feature flags (game domains, file categories, MD5 verification, rate limiting)

Implementations:
- **NexusModsBackend** - Full API integration with SSO authentication
- **GameBananaBackend** - API v11 integration with courtesy throttling

### Authentication (`src/Modular.Core/Authentication/`)

**NexusMods SSO Integration** - Browser-based authentication flow
- **WebSocket SSO Protocol** - Connects to `wss://sso.nexusmods.com` for interactive authorization
- **No Manual API Key Required** - Users authenticate via browser instead of copying API keys
- **Automatic Token Persistence** - API key saved to config after successful authentication
- **Fallback Support** - Manual API key configuration still supported for headless/CI environments

The SSO flow:
1. Generate UUID and connect to SSO WebSocket
2. Open browser to `https://www.nexusmods.com/sso?id={uuid}`
3. User logs in and authorizes Modular
4. API key received via WebSocket
5. Key saved to `~/.config/Modular/config.json`

See [`docs/NEXUSMODS_SSO_INTEGRATION.md`](docs/NEXUSMODS_SSO_INTEGRATION.md) for implementation details.

### Download Engine (`src/Modular.Core/Downloads/DownloadEngine.cs`)

Production-grade download handler:
- Streaming downloads with resumable support (HTTP Range headers)
- Concurrent download control via SemaphoreSlim
- MD5/SHA256 checksum verification
- Real-time progress tracking with callbacks
- Automatic retry with exponential backoff

### Dependency Resolution (`src/Modular.Core/Dependencies/PubGrubResolver.cs`)

PubGrub-inspired version constraint solver:
- Resolves mod dependencies to a consistent set of versions
- Detects circular dependencies and incompatible mods
- Topological sort for correct install order
- Version range constraint propagation
- Requires `IModVersionProvider` implementation per backend (see [Implementation Guide](docs/IMPLEMENTATION_GUIDE.md))

### Installer Framework (`src/Modular.Core/Installers/`)

Extensible mod installation system:
- **InstallerManager** - Orchestrates installer selection by priority and confidence
- **FomodInstaller** - Parses FOMOD `ModuleConfig.xml` (simplified; UI selection not yet integrated)
- **BepInExInstaller** - Detects and installs BepInEx plugins
- **LooseFileInstaller** - Simple archive extraction fallback

### NexusRateLimiter (`src/Modular.Core/RateLimiting/NexusRateLimiter.cs`)

Tracks NexusMods API rate limits from response headers:
- Parses `x-rl-daily-remaining` and `x-rl-hourly-remaining` headers
- Stores Unix timestamp reset times
- Implements async waiting when limits exhausted
- Supports state persistence between sessions

### Legacy Services (Deprecated)

> **Note:** `NexusModsService` and `GameBananaService` in `src/Modular.Core/Services/` are marked `[Obsolete]`. New code should use `NexusModsBackend` and `GameBananaBackend` via the backend abstraction instead. The CLI still references these services for some commands pending migration (see [Implementation Guide](docs/IMPLEMENTATION_GUIDE.md)).

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
    .SetTimeout(TimeSpan.FromSeconds(30))
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

### Plugin Architecture Patterns

**MEF Composition (System.Composition)** - Attribute-based plugin discovery
- Export plugins via `[Export]` attributes
- Import dependencies via `[Import]` attributes
- Metadata-driven selection (priority, capabilities)

**AssemblyLoadContext Isolation** - Isolated plugin loading
- Each plugin loads in separate `AssemblyLoadContext`
- Dependency version conflicts avoided
- Optional collectible contexts for plugin unloading

**Backend Abstraction Pattern** - Unified interface across mod sources
- `IModBackend` interface with capability flags
- `BackendRegistry` for runtime discovery
- Feature negotiation via `BackendCapabilities`

### HTTP & Communication Patterns

**Builder Pattern (Fluent API)** - Chainable HTTP request building
- Request/response configuration via method chaining
- Improves code readability and discoverability

**Middleware Filter Pattern** - Request/response interception
- Filters for authentication, rate limiting, logging, error handling
- Composable pipeline with ordered execution

**Authentication Strategies** - Multiple auth mechanisms
- WebSocket SSO (NexusMods)
- OAuth2 flows (extensible)
- API key-based (fallback)

### Core Architecture Patterns

**Dependency Injection** - Constructor-based DI throughout
- Services accept `ILogger<T>` and configuration
- Enables testability and flexibility
- Used in GUI (Avalonia DI) and CLI (Microsoft.Extensions.Hosting)

**Repository Pattern** - Data persistence abstraction
- `DownloadDatabase` - JSON-based download history
- `ModMetadataCache` - API response caching
- Abstract storage from business logic

**Strategy Pattern** - Pluggable algorithms
- Retry policies via `IRetryConfig`
- Rate limiting strategies via `IRateLimiter`
- Installer selection via `IModInstaller`

**Factory Pattern** - Object creation abstraction
- `FluentClientFactory.Create()` - HTTP client creation
- `PluginLoader.LoadPlugin()` - Plugin instantiation
- Separates interface from implementation

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
| **System.CommandLine** | CLI argument parsing | 2.0.0-beta4 |
| **Spectre.Console** | Rich terminal UI and progress | 0.49.1 |
| **System.Composition** | MEF plugin composition | 10.0.3 |
| **Microsoft.Extensions.Configuration** | Configuration management | 8.0.0 |
| **Microsoft.Extensions.Logging** | Logging abstractions | 8.0.0 |
| **Microsoft.Extensions.Hosting** | DI and hosting (CLI) | 8.0.0 |
| **Microsoft.Extensions.Options** | Options pattern | 8.0.0 |
| **System.Text.Json** | JSON serialization | 8.0.5 |
| **Avalonia** | Cross-platform GUI framework | 11.3.11 |
| **Avalonia.Themes.Fluent** | Fluent design theme | 11.3.11 |
| **Avalonia.Controls.DataGrid** | DataGrid control | 11.3.11 |
| **Material.Icons.Avalonia** | Material Design icons | 2.1.10 |
| **CommunityToolkit.Mvvm** | MVVM helpers and attributes | 8.3.2 |

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
- Backend registry and discovery
- NexusMods backend operations
- GameBanana backend operations
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

### Architecture & Design

| Document | Description |
|----------|-------------|
| [`Modular_1_Blueprint.md`](docs/Modular_1_Blueprint.md) | Next-generation architecture blueprint and evolution roadmap |
| [`IMPLEMENTATION_GUIDE.md`](docs/IMPLEMENTATION_GUIDE.md) | Guide to completing placeholder and partial implementations |
| [`CODEBASE_REVIEW_RECOMMENDATIONS.md`](docs/CODEBASE_REVIEW_RECOMMENDATIONS.md) | Comprehensive codebase review and recommendations |
| [`EVOLUTION_SUMMARY.md`](docs/EVOLUTION_SUMMARY.md) | Project evolution history and milestones |

### Plugin Development

| Document | Description |
|----------|-------------|
| [`PLUGIN_DEVELOPMENT.md`](docs/PLUGIN_DEVELOPMENT.md) | Complete guide to creating Modular plugins |
| [`API-BACKENDS-GUIDE.md`](docs/API-BACKENDS-GUIDE.md) | Implementing backend plugins for new mod repositories |
| [`GAMEBANANA-API-GUIDE.md`](docs/GAMEBANANA-API-GUIDE.md) | GameBanana API integration guide |

### Authentication & APIs

| Document | Description |
|----------|-------------|
| [`NEXUSMODS_SSO_INTEGRATION.md`](docs/NEXUSMODS_SSO_INTEGRATION.md) | NexusMods SSO/WebSocket authentication implementation |
| [`RATE_LIMITING_FIXES_SUMMARY.md`](docs/RATE_LIMITING_FIXES_SUMMARY.md) | Rate limiting implementation details |
| [`TRACKING_VALIDATION.md`](docs/TRACKING_VALIDATION.md) | Web scraping validation methodology |
| [`NEXUSMODS_TRACKING_ANALYSIS.md`](docs/NEXUSMODS_TRACKING_ANALYSIS.md) | API tracking analysis and discrepancies |

### Features & Workflows

| Document | Description |
|----------|-------------|
| [`CATEGORY_ORGANIZATION.md`](docs/CATEGORY_ORGANIZATION.md) | Category-based organization system |
| [`REORGANIZE_GUIDE.md`](docs/REORGANIZE_GUIDE.md) | Mod reorganization workflows |
| [`PRIORITIZED_DOWNLOADS.md`](docs/PRIORITIZED_DOWNLOADS.md) | Priority-based download queue |
| [`IMPROVEMENTS.md`](docs/IMPROVEMENTS.md) | Feature improvements and API enhancements |

### GUI Development

| Document | Description |
|----------|-------------|
| [`GUI_RECOMMENDATIONS.md`](docs/GUI_RECOMMENDATIONS.md) | GUI implementation recommendations and patterns |

### HTTP Client Library

| Document | Description |
|----------|-------------|
| [`fluent/README.md`](docs/fluent/README.md) | Fluent HTTP client user guide |
| [`fluent/INTERFACES.md`](docs/fluent/INTERFACES.md) | Fluent API interface documentation |
| [`MODULAR_INTEGRATION_COMPARISON.md`](docs/MODULAR_INTEGRATION_COMPARISON.md) | Fluent vs traditional HTTP client comparison |

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
