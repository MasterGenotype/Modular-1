# Modular

An extensible C#/.NET mod manager for downloading, searching, installing, and managing game modifications from multiple mod repositories. Supports NexusMods and GameBanana out of the box, with a plugin system for adding more sources.

Provides both CLI and GUI (Avalonia) interfaces, SSO authentication, mod collections, Steam game detection, intelligent rate limiting, and real-time progress tracking.

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
- [Packaging](#packaging)
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

Modular is an extensible mod manager that streamlines game mod management through a unified interface. It downloads mods from multiple sources (NexusMods, GameBanana, and more via plugins), automatically organizes them into structured directories (`Domain/Category/Mod_Id`), and renames folders from numeric IDs to human-readable names.

The project implements a clean **plugin-based architecture** with a stable SDK for extensions, separating concerns between the CLI interface, GUI, core business logic, plugin system, and a modern fluent HTTP client library. This enables community-developed backends, installers, metadata enrichers, and UI extensions without modifying the core codebase.

## Features

### Core Functionality
- **Plugin Architecture** - Extensible system for community-developed backends, installers, metadata enrichers, and UI extensions
- **Multi-Repository Support** - Download mods from NexusMods and GameBanana with extensible backend abstraction for additional sources
- **Mod Search & Discovery** - Full-text search with fuzzy re-ranking, thumbnail previews, trending/latest/updated feeds via NexusMods GraphQL API
- **Embedded Mod Browser** - View NexusMods mod pages in an in-app WebView browser without leaving the application
- **Mod Collections** - Create, manage, download, and share curated sets of mods with version tracking
- **SSO Authentication** - Browser-based NexusMods authentication flow via WebSocket SSO (no manual API key required)
- **Automatic Organization** - Organizes mods into game-specific directories with optional category-based subdirectories
- **Smart Renaming** - Converts numeric mod ID folders to human-readable names using API metadata
- **Download Verification** - MD5/SHA256 checksum verification with persistent SQLite download history database
- **Tracking Validation** - Cross-validates API tracking against NexusMods web tracking center via web scraping
- **Metadata Enrichment** - Plugin system for transforming backend-specific data into canonical format
- **Custom Installers** - Extensible installer framework for handling different mod formats (FOMOD, BepInEx, loose files, Steam mods)
- **Steam Game Detection** - Automatic scanning of Steam library folders to detect installed games and game engines
- **Mod Installation & Rollback** - Install mod archives with changeset tracking, backups, and uninstall support
- **Snapshot Management** - Save and restore mod installation state

### Network & Performance
- **Rate Limit Compliance** - Built-in rate limiter respects NexusMods API limits (20,000 requests/day, 500/hour)
- **Retry Logic** - Automatic retry with exponential backoff via Polly resilience policies
- **Fluent HTTP API** - Modern chainable HTTP client with middleware support
- **HTTP Caching** - Response caching to reduce redundant API calls
- **Progress Callbacks** - Real-time progress tracking decoupled from UI

### Configuration & Usability
- **Flexible Configuration** - Supports environment variables, config files, and command-line arguments
- **Real-Time Progress** - Live progress bars for all download and organization operations
- **Persistent State** - Rate limiter and download history persist between sessions via SQLite
- **Credential Store** - Secure API key storage via config-based credential management
- **Arch Linux Package** - PKGBUILD for building and installing via `makepkg`

## Architecture

Modular follows a clean **plugin-based architecture** with clear separation of concerns:

```
┌─────────────────────────────────────────────────────────────┐
│                    GUI Layer (Modular.Gui)                  │
│       Avalonia UI, MVVM, visual mod management + plugins    │
├─────────────────────────────────────────────────────────────┤
│                    CLI Layer (Modular.Cli)                  │
│  Spectre.Console.Cli commands, interactive menu, progress   │
├─────────────────────────────────────────────────────────────┤
│                  Core Library (Modular.Core)                │
│   Plugin System, Backend Abstraction, Authentication (SSO), │
│   Metadata, Installers, Dependencies, Diagnostics, Profiles,│
│   Archives, Collections, GameDetection, Security, Snapshots │
├─────────────────────────────────────────────────────────────┤
│                    SDK Layer (Modular.Sdk)                  │
│    Stable contracts: IModBackend, ISearchableBackend,       │
│    IMetadataEnricher, IModInstaller, IUiExtension,          │
│    IArchiveReader, IModCollectionRepository, IPluginMetadata │
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

1. **GUI Layer (`Modular.Gui`)** - Cross-platform graphical interface built with Avalonia UI 11.3 using MVVM architecture and Material Icons, featuring visual mod browsing with thumbnail previews, search with embedded WebView mod page browser, download queue management, game detection, mod installation, collections, profiles, snapshots, plugin management, and settings configuration
2. **CLI Layer (`Modular.Cli`)** - Command-line interface using Spectre.Console.Cli with structured command groups (download, search, browse, install, collection, profile, plugins, diagnostics, telemetry) and rich progress visualization
3. **Core Library (`Modular.Core`)** - Class library containing all business logic:
   - **Plugin System** - Dynamic loading via AssemblyLoadContext, MEF composition, plugin marketplace integration
   - **Backend Abstraction** - Unified interface (IModBackend) with capability flags, search (ISearchableBackend), and registry
   - **Authentication & Security** - SSO flows (NexusMods WebSocket), OAuth2, API key management, credential store
   - **Metadata & Installers** - Enrichers for canonical schema, extensible installer framework (FOMOD, BepInEx, loose files, Steam)
   - **Archives** - Archive reading (ZIP, SharpCompress), blob storage, inventory service
   - **Collections** - Mod collection repository and service for curated mod sets
   - **Game Detection** - Steam library scanning, game discovery, engine detection
   - **Dependencies & Profiles** - Mod dependency resolution, conflict detection, profile management, operation graphs
   - **Snapshots** - Save and restore mod installation state
   - **Diagnostics & Telemetry** - Health checks, performance metrics, error reporting
   - **Database** - SQLite-backed download repository, metadata cache, changeset tracking
4. **SDK Layer (`Modular.Sdk`)** - Stable plugin contracts defining extension points: `IModBackend`, `ISearchableBackend`, `IMetadataEnricher`, `IModInstaller`, `IUiExtension`, `IArchiveReader`, `IModCollectionRepository`, `IPluginMetadata`, and shared data models (`BackendMod`, `BackendModFile`, `ModCollection`, `BackendCapabilities`)
5. **Fluent HTTP Layer (`Modular.FluentHttp`)** - Modern fluent-style HTTP client library with chainable request building, middleware filters, and type-safe responses

## Project Structure

```
Modular/
├── Modular.sln                           # Visual Studio solution file
├── BUILD.md                              # Build and installation guide
├── Makefile                              # Build shortcuts
├── pkg/
│   └── PKGBUILD                          # Arch Linux package build script
├── src/
│   ├── Modular.Gui/                      # GUI application (Avalonia 11.3)
│   │   ├── Modular.Gui.csproj
│   │   ├── Program.cs                    # Entry point and DI setup
│   │   ├── App.axaml(.cs)                # Application and theme config
│   │   ├── ViewLocator.cs                # View-ViewModel resolution
│   │   ├── Views/                        # XAML views
│   │   │   ├── MainWindow.axaml          # Main shell window
│   │   │   ├── NexusModsView.axaml       # NexusMods tracked mods
│   │   │   ├── NexusSearchView.axaml     # NexusMods search
│   │   │   ├── GameBananaView.axaml      # GameBanana subscriptions
│   │   │   ├── GameBananaSearchView.axaml # GameBanana search
│   │   │   ├── GameBananaPanelView.axaml # GameBanana panel
│   │   │   ├── DownloadQueueView.axaml   # Download queue
│   │   │   ├── LibraryView.axaml         # Mod library
│   │   │   ├── InstallView.axaml         # Mod installation
│   │   │   ├── InstalledModsView.axaml   # Installed mods list
│   │   │   ├── ModListView.axaml         # Generic mod list
│   │   │   ├── ModManagerView.axaml      # Mod manager
│   │   │   ├── GameDetectionView.axaml   # Steam game detection
│   │   │   ├── CollectionView.axaml      # Mod collections
│   │   │   ├── ProfilesView.axaml        # Profile management
│   │   │   ├── ProfilesCollectionsView.axaml # Profiles & collections
│   │   │   ├── PluginsView.axaml         # Plugin management
│   │   │   ├── SnapshotView.axaml        # Snapshot management
│   │   │   ├── BackupsView.axaml         # Backup management
│   │   │   └── SettingsView.axaml        # Settings
│   │   ├── ViewModels/                   # MVVM view models
│   │   ├── Services/                     # GUI-specific services
│   │   │   ├── DialogService.cs          # Dialog management
│   │   │   ├── IDialogService.cs         # Dialog interface
│   │   │   ├── DownloadHistoryService.cs # Download history
│   │   │   └── ThumbnailService.cs       # Thumbnail loading
│   │   ├── Messages/                     # MVVM messages
│   │   ├── Converters/                   # Value converters
│   │   ├── Models/                       # GUI data models
│   │   └── Assets/                       # Icons, images, resources
│   ├── Modular.Cli/                      # CLI application
│   │   ├── Modular.Cli.csproj
│   │   ├── Program.cs                    # Entry point and command registration
│   │   ├── Commands/                     # CLI command implementations
│   │   │   ├── InteractiveCommand.cs     # Default interactive mode
│   │   │   ├── DownloadCommand.cs        # Download tracked mods
│   │   │   ├── SearchCommand.cs          # Search mods with fuzzy re-ranking
│   │   │   ├── BrowseCommand.cs          # Browse trending/latest/updated
│   │   │   ├── RenameCommand.cs          # Rename mod folders
│   │   │   ├── FetchCommand.cs           # Fetch and cache metadata
│   │   │   ├── LoginCommand.cs           # NexusMods SSO authentication
│   │   │   ├── InstallCommand.cs         # Install mod archives
│   │   │   ├── UninstallCommand.cs       # Uninstall by changeset
│   │   │   ├── ListInstalledCommand.cs   # List installed mods
│   │   │   ├── SteamInstallCommand.cs    # Steam mod installation
│   │   │   ├── CollectionCommand.cs      # Collection management
│   │   │   ├── Diagnostics/              # Diagnostics command group
│   │   │   ├── GameDetection/            # Game detection commands
│   │   │   ├── Plugins/                  # Plugin management commands
│   │   │   ├── Profile/                  # Profile management commands
│   │   │   └── Telemetry/                # Telemetry management commands
│   │   ├── Infrastructure/               # DI and service configuration
│   │   │   ├── ServiceConfiguration.cs   # Service registration
│   │   │   └── TypeRegistrar.cs          # Spectre.Console DI adapter
│   │   └── UI/                           # Terminal UI components
│   │       └── LiveProgressDisplay.cs    # Progress visualization
│   ├── Modular.Core/                     # Core business logic library
│   │   ├── Modular.Core.csproj
│   │   ├── Archives/                     # Archive handling
│   │   │   ├── ArchiveInventoryService.cs # Archive content analysis
│   │   │   ├── ArchiveReaderFactory.cs   # Archive reader creation
│   │   │   ├── BlobStore.cs              # Content-addressable blob storage
│   │   │   ├── SharpCompressArchiveReader.cs # Multi-format archive reader
│   │   │   └── ZipArchiveReader.cs       # ZIP archive reader
│   │   ├── Authentication/               # Auth strategies (SSO, OAuth2)
│   │   │   └── NexusSsoClient.cs         # NexusMods SSO WebSocket client
│   │   ├── Backends/                     # Backend implementations
│   │   │   ├── IModBackend.cs            # Backend interface (Core)
│   │   │   ├── BackendRegistry.cs        # Backend discovery and registry
│   │   │   ├── SdkTypeAliases.cs         # SDK type aliasing
│   │   │   ├── NexusMods/                # NexusMods backend
│   │   │   │   ├── NexusModsBackend.cs   # NexusMods API integration
│   │   │   │   ├── NexusModsGraphQlClient.cs # GraphQL search client
│   │   │   │   ├── NexusModsMetadataEnricher.cs # Metadata enricher
│   │   │   │   ├── NexusModsModels.cs    # NexusMods data models
│   │   │   │   └── NexusModsVersionProvider.cs # Version provider
│   │   │   └── GameBanana/               # GameBanana backend
│   │   │       ├── GameBananaBackend.cs   # GameBanana API integration
│   │   │       ├── GameBananaMetadataEnricher.cs # Metadata enricher
│   │   │       ├── GameBananaModels.cs    # GameBanana data models
│   │   │       └── GameBananaVersionProvider.cs # Version provider
│   │   ├── Collections/                  # Mod collections
│   │   │   ├── ModCollectionRepository.cs # Collection persistence
│   │   │   └── ModCollectionService.cs   # Collection business logic
│   │   ├── Configuration/
│   │   │   ├── AppSettings.cs            # Configuration model
│   │   │   └── ConfigurationService.cs   # Configuration loading/validation
│   │   ├── Database/                     # Data persistence (SQLite)
│   │   │   ├── ModularDatabase.cs        # SQLite database manager
│   │   │   ├── SqliteDownloadRepository.cs # SQLite download repository
│   │   │   ├── IDownloadRepository.cs    # Download repository interface
│   │   │   ├── IMetadataCache.cs         # Metadata cache interface
│   │   │   ├── DownloadDatabase.cs       # Download history persistence
│   │   │   ├── DownloadRecord.cs         # Download record model
│   │   │   ├── DownloadStatus.cs         # Download status enum
│   │   │   └── ModMetadataCache.cs       # Mod metadata caching
│   │   ├── Dependencies/                 # Dependency resolution
│   │   │   ├── DependencyGraph.cs        # Mod dependency graph
│   │   │   ├── DependencyEdge.cs         # Graph edge model
│   │   │   ├── ModNode.cs               # Graph node model
│   │   │   ├── FileConflictIndex.cs     # File conflict detection
│   │   │   ├── ConflictResolver.cs      # Conflict resolution strategies
│   │   │   ├── GreedyDependencyResolver.cs # PubGrub-inspired version resolver
│   │   │   ├── AggregateVersionProvider.cs # Multi-backend version aggregation
│   │   │   ├── OperationGraph.cs        # Dependency-ordered operations
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
│   │   ├── GameDetection/               # Steam game detection
│   │   │   ├── SteamLocator.cs          # Steam installation finder
│   │   │   ├── SteamLibraryScanner.cs   # Library folder scanner
│   │   │   ├── SteamGameScanner.cs      # Game discovery
│   │   │   └── EngineDetection.cs       # Game engine detection
│   │   ├── Http/
│   │   │   ├── ModularHttpClient.cs      # HTTP client wrapper
│   │   │   ├── RetryConfig.cs            # Retry configuration
│   │   │   ├── HttpCache.cs             # Response caching
│   │   │   └── HttpServiceExtensions.cs # HTTP service DI extensions
│   │   ├── Installers/                   # Installer framework
│   │   │   ├── InstallerManager.cs       # Installer orchestration
│   │   │   ├── ModInstallationService.cs # High-level install service
│   │   │   ├── StagingManager.cs        # Staging directory management
│   │   │   ├── ChangesetManager.cs      # Install/uninstall changeset tracking
│   │   │   ├── FomodInstaller.cs        # FOMOD format support
│   │   │   ├── BepInExInstaller.cs      # BepInEx plugin installer
│   │   │   ├── LooseFileInstaller.cs    # Simple file extraction
│   │   │   └── Steam/                   # Steam-specific installers
│   │   │       ├── SteamModInstaller.cs  # Steam mod installer
│   │   │       ├── SteamConstraintSolver.cs # Dependency constraint solver
│   │   │       └── SteamModMetadata.cs   # Steam mod metadata
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
│   │   ├── Security/                     # Credential management
│   │   │   ├── ICredentialStore.cs       # Credential store interface
│   │   │   └── ConfigCredentialStore.cs  # Config-based credential store
│   │   ├── Services/
│   │   │   ├── IRenameService.cs          # Rename service interface
│   │   │   ├── RenameService.cs          # Mod renaming and organization
│   │   │   └── TrackingValidatorService.cs # Web scraping validation
│   │   ├── Snapshots/                    # Snapshot management
│   │   │   └── SnapshotManager.cs        # Save/restore mod state
│   │   ├── Telemetry/                    # Performance metrics
│   │   │   └── TelemetryService.cs       # Metrics collection
│   │   ├── Utilities/
│   │   │   ├── FileUtils.cs              # File operation utilities
│   │   │   ├── FuzzyMatcher.cs           # Fuzzy string matching for search
│   │   │   ├── HashUtility.cs            # Hash computation utilities
│   │   │   ├── KeyValuesParser.cs        # Key-value file parser (Steam VDF)
│   │   │   ├── Md5Calculator.cs          # MD5 checksum calculation
│   │   │   └── PathSanitizer.cs          # Path sanitization utilities
│   │   └── Versioning/                   # Version comparison
│   │       ├── SemanticVersion.cs        # SemVer implementation
│   │       └── VersionRange.cs           # Version range constraints
│   ├── Modular.Sdk/                      # Plugin SDK contracts
│   │   ├── Modular.Sdk.csproj
│   │   ├── IPluginMetadata.cs            # Plugin metadata contract
│   │   ├── Archives/                     # Archive contracts
│   │   │   └── IArchiveReader.cs         # Archive reader interface
│   │   ├── Backends/                     # Backend contracts
│   │   │   ├── IModBackend.cs            # Backend interface
│   │   │   ├── ISearchableBackend.cs     # Search interface
│   │   │   ├── BackendCapabilities.cs    # Feature flags per backend
│   │   │   ├── DownloadOptions.cs        # Download configuration
│   │   │   ├── DownloadProgress.cs       # Progress reporting
│   │   │   └── Common/                   # Shared backend models
│   │   │       ├── BackendMod.cs         # Unified mod model
│   │   │       ├── BackendModFile.cs     # Unified file model
│   │   │       └── FileFilter.cs         # File filter options
│   │   ├── Collections/                  # Collection contracts
│   │   │   ├── IModCollectionRepository.cs # Collection repository interface
│   │   │   └── ModCollection.cs          # Collection model
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
│       └── ExamplePlugin.cs              # Sample metadata enricher
├── tests/
│   ├── Modular.Core.Tests/               # Core library tests
│   │   ├── Modular.Core.Tests.csproj
│   │   ├── ConfigurationTests.cs
│   │   ├── DatabaseTests.cs
│   │   ├── UtilityTests.cs
│   │   ├── FuzzyMatcherTests.cs
│   │   ├── IntegrationTests.cs
│   │   ├── SteamModInstallerTests.cs
│   │   ├── VersionProviderTests.cs
│   │   ├── VersionRangeTests.cs
│   │   └── Backends/                     # Backend-specific tests
│   │       ├── BackendRegistryTests.cs
│   │       ├── NexusModsBackendTests.cs
│   │       └── GameBananaBackendTests.cs
│   └── Modular.FluentHttp.Tests/         # Fluent HTTP client tests
│       ├── Modular.FluentHttp.Tests.csproj
│       └── FluentClientTests.cs
├── docs/                                 # Documentation
│   ├── plans/                            # Implementation plans
│   ├── archive/                          # Historical docs
│   ├── fluent/                           # Fluent HTTP docs
│   └── *.md                              # Various guides (see Documentation section)
└── pkg/                                  # Distribution packaging
    └── PKGBUILD                          # Arch Linux PKGBUILD
```

## Plugin System

Modular features a robust plugin architecture enabling community-developed extensions without modifying the core codebase.

### Extension Points

**Mod Backends (`IModBackend`)** - Add support for new mod repositories
- Implement `GetUserModsAsync`, `GetModFilesAsync`, `DownloadModsAsync`
- Optionally implement `ISearchableBackend` for full-text search support
- Declare backend capabilities via `BackendCapabilities` (game domains, categories, search, rate limits, etc.)
- Examples: NexusMods, GameBanana, Modrinth, CurseForge

**Metadata Enrichers (`IMetadataEnricher`)** - Transform backend-specific data to canonical format
- Map backend-specific fields to unified schema
- Infer missing metadata (dependencies, install instructions)
- Enable cross-source queries and searches

**Mod Installers (`IModInstaller`)** - Handle installation workflows
- `DetectAsync` - Analyze archive structure and determine compatibility
- `AnalyzeAsync` - Generate installation plan with file operations
- `InstallAsync` - Execute installation with progress reporting
- Examples: FOMOD installers, BepInEx plugins, loose file extraction, Steam mod installer

**UI Extensions (`IUiExtension`)** - Add custom panels to the GUI
- Integrate seamlessly into the Avalonia UI
- Access core services via dependency injection
- Respond to lifecycle events (activation/deactivation)

**Archive Readers (`IArchiveReader`)** - Support additional archive formats
- Built-in: ZIP and SharpCompress (7z, RAR, TAR, etc.)

**Plugin Metadata (`IPluginMetadata`)** - Declare plugin identity
- Id, DisplayName, Version, Description, Author

### Plugin Loading

Plugins are loaded dynamically at runtime using:
- **AssemblyLoadContext** - Isolated loading with dependency resolution
- **MEF (Managed Extensibility Framework)** - Attribute-based composition via `System.Composition`
- **Plugin Marketplace** - Centralized plugin discovery, installation, and updates

### Plugin Development

1. Reference `Modular.Sdk.csproj` (stable contracts)
2. Implement `IPluginMetadata` and desired interface (`IModBackend`, `IMetadataEnricher`, etc.)
3. Create `plugin.json` manifest with metadata and entry assembly
4. Build and package as a DLL
5. Install to `~/.config/Modular/plugins/`

See [`docs/PLUGIN_DEVELOPMENT.md`](docs/PLUGIN_DEVELOPMENT.md) and [`examples/ExamplePlugin/`](examples/ExamplePlugin/) for complete guides.

## Key Components

### Backend Abstraction (`src/Modular.Core/Backends/`)

Unified interface for mod repositories:
- **IModBackend** - Standard operations across all backends
- **ISearchableBackend** - Optional full-text search interface
- **BackendRegistry** - Runtime backend discovery and selection
- **BackendCapabilities** - Feature flags (game domains, file categories, search, MD5 verification, rate limiting)

Implementations:
- **NexusModsBackend** - Full API + GraphQL integration with SSO authentication, search, browse feeds (trending/latest/updated)
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

### Dependency Resolution (`src/Modular.Core/Dependencies/GreedyDependencyResolver.cs`)

PubGrub-inspired version constraint solver:
- Resolves mod dependencies to a consistent set of versions
- Detects circular dependencies and incompatible mods
- Topological sort for correct install order
- Version range constraint propagation
- Requires `IModVersionProvider` implementation per backend (see [Implementation Guide](docs/IMPLEMENTATION_GUIDE.md))

### Installer Framework (`src/Modular.Core/Installers/`)

Extensible mod installation system:
- **InstallerManager** - Orchestrates installer selection by priority and confidence
- **ModInstallationService** - High-level install/uninstall orchestration with game directory resolution
- **StagingManager** - Manages staging directories for atomic installations
- **ChangesetManager** - Tracks installed files for rollback/uninstall support
- **FomodInstaller** - Parses FOMOD `ModuleConfig.xml` (simplified; UI selection not yet integrated)
- **BepInExInstaller** - Detects and installs BepInEx plugins
- **LooseFileInstaller** - Simple archive extraction fallback
- **SteamModInstaller** - Steam game mod installation with dependency constraint solving

### Archive System (`src/Modular.Core/Archives/`)

Multi-format archive handling:
- **ArchiveReaderFactory** - Creates readers based on archive format
- **ZipArchiveReader** - .NET built-in ZIP support
- **SharpCompressArchiveReader** - 7z, RAR, TAR, GZ support via SharpCompress
- **ArchiveInventoryService** - Analyzes archive contents for installer selection
- **BlobStore** - Content-addressable blob storage for deduplication

### Collections (`src/Modular.Core/Collections/`)

Mod collection management:
- **ModCollectionService** - Create, update, download, verify, and share mod collections
- **ModCollectionRepository** - Persistent collection storage
- Export/import collections as JSON for sharing
- Check for updates across all collection mods

### Game Detection (`src/Modular.Core/GameDetection/`)

Automatic Steam game discovery:
- **SteamLocator** - Finds Steam installation directory across platforms
- **SteamLibraryScanner** - Parses `libraryfolders.vdf` to find all library paths
- **SteamGameScanner** - Discovers installed games and their metadata
- **EngineDetection** - Identifies game engines (Unity, Unreal, Source, etc.) for installer hints

### NexusRateLimiter (`src/Modular.Core/RateLimiting/NexusRateLimiter.cs`)

Tracks NexusMods API rate limits from response headers:
- Parses `x-rl-daily-remaining` and `x-rl-hourly-remaining` headers
- Stores Unix timestamp reset times
- Implements async waiting when limits exhausted
- Supports state persistence between sessions

### Database (`src/Modular.Core/Database/`)

SQLite-backed data persistence:
- **ModularDatabase** - SQLite database initialization and management
- **SqliteDownloadRepository** - Download history with full query support
- **ModMetadataCache** - API response caching to reduce network calls
- **IDownloadRepository** / **IMetadataCache** - Repository interfaces for testability
- Stores: game_domain, mod_id, file_id, filename, filepath, checksums, download time, status

### ConfigurationService (`src/Modular.Core/Configuration/ConfigurationService.cs`)

Configuration management using Microsoft.Extensions.Configuration:
- Location: `~/.config/Modular/config.json`
- Precedence: Environment variables > Config file > Defaults
- Settings: API keys, paths, preferences

### Security (`src/Modular.Core/Security/`)

Credential management:
- **ICredentialStore** - Abstract credential storage interface
- **ConfigCredentialStore** - Config file-based credential persistence

### RenameService (`src/Modular.Core/Services/RenameService.cs`)

Mod folder organization and renaming:
- Renames numeric ID folders to human-readable names
- Organizes mods into category subdirectories
- Caches metadata to reduce API calls

### Snapshot Manager (`src/Modular.Core/Snapshots/SnapshotManager.cs`)

Mod state snapshot management:
- Save current mod installation state
- Restore previous snapshots for rollback

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
    .AsResponseAsync();

// Access response data
var user = response.As<UserInfo>();
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
    .WithJsonBody(new { key = "value" })
    .AsResponseAsync();

// GET with query parameters
var response = await client
    .GetAsync("/api/search")
    .WithArgument("q", "skyrim")
    .WithArgument("page", "1")
    .AsResponseAsync();

// Download with progress
await client
    .GetAsync("/files/download/123")
    .DownloadToAsync("/path/to/file", new Progress<(long downloaded, long total)>(
        p => Console.WriteLine($"{p.downloaded}/{p.total}")));
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
| **Spectre.Console** | Rich terminal UI and progress | 0.49.1 |
| **Spectre.Console.Cli** | CLI command framework | 0.49.1 |
| **System.Composition** | MEF plugin composition | 10.0.3 |
| **Microsoft.Extensions.Configuration** | Configuration management | 8.0.0 |
| **Microsoft.Extensions.Logging** | Logging abstractions | 8.0.0 |
| **Microsoft.Extensions.Hosting** | DI and hosting (CLI) | 8.0.0 |
| **Microsoft.Extensions.Http** | HttpClient factory | 8.0.0 |
| **Microsoft.Extensions.Http.Polly** | Polly integration for HTTP | 8.0.0 |
| **Microsoft.Extensions.DependencyInjection** | DI container (GUI) | 8.0.0 |
| **Microsoft.Data.Sqlite** | SQLite database access | 8.0.0 |
| **Polly** | Resilience and transient fault handling | 8.3.0 |
| **SharpCompress** | Multi-format archive support (7z, RAR, TAR) | 0.36.0 |
| **System.Text.Json** | JSON serialization | 8.0.5 |
| **Avalonia** | Cross-platform GUI framework | 11.3.11 |
| **Avalonia.Themes.Fluent** | Fluent design theme | 11.3.11 |
| **Avalonia.Fonts.Inter** | Inter font family | 11.3.11 |
| **Avalonia.Controls.DataGrid** | DataGrid control | 11.3.11 |
| **Material.Icons.Avalonia** | Material Design icons | 2.1.10 |
| **CommunityToolkit.Mvvm** | MVVM helpers and attributes | 8.3.2 |
| **WebView.Avalonia.Desktop** | Embedded browser (WebView) | 11.0.0.1 |

## Building

### Prerequisites

- .NET SDK 8.0 or later
  - Check with: `dotnet --version`
- `webkit2gtk` (Linux only, required for the embedded WebView mod browser in the GUI)
  - Arch Linux: `sudo pacman -S webkit2gtk`
  - Debian/Ubuntu: `sudo apt install libwebkit2gtk-4.1-dev`
  - Fedora: `sudo dnf install webkit2gtk4.1-devel`

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

**Using the Makefile (Recommended):**
```bash
# Install CLI
make install

# Install GUI
make install-gui

# Create desktop entry
make install-desktop

# Uninstall
make uninstall-all
```

**Self-Contained (Manual):**
```bash
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o ~/.local/share/modular
cp ~/.local/share/modular/modular ~/.local/bin/modular
chmod +x ~/.local/bin/modular
```

## Packaging

### Arch Linux (PKGBUILD)

An Arch Linux `PKGBUILD` is provided in the `pkg/` directory for building and installing both the CLI and GUI as system packages:

```bash
cd pkg
makepkg -si
```

This installs:
- `modular` CLI to `/usr/bin/modular`
- `modular-gui` to `/usr/bin/modular-gui`
- A desktop entry for the GUI application

### Cross-Platform Publishing

```bash
# Publish for all platforms
make publish-all

# Or individually
make publish-linux
make publish-windows
make publish-macos
make publish-macos-arm
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

This presents a menu with options for NexusMods downloads, GameBanana downloads, renaming, and more.

### Commands

```bash
# Download tracked mods
modular download stardewvalley
modular download --backend nexusmods skyrimspecialedition
modular download --all

# Search for mods
modular search SKSE64 --game skyrimspecialedition
modular search "armor retexture" --sort downloads --limit 10

# Browse discovery feeds
modular browse trending --game skyrimspecialedition
modular browse latest --game stardewvalley
modular browse updated --game skyrimspecialedition --period 1w

# Install/uninstall mods
modular install mod.zip --game 730
modular install mod.zip --game "Counter-Strike" --dry-run
modular uninstall a1b2c3d4e5f6
modular installed --game 730

# Rename mod folders
modular rename stardewvalley
modular rename --organize-by-category

# Fetch and cache metadata
modular fetch stardewvalley

# Authenticate
modular login

# Manage collections
modular collection create "My Skyrim Build" --game skyrimspecialedition
modular collection add "My Skyrim Build" 1234 --file-id 5678
modular collection download "My Skyrim Build" --verify
modular collection export "My Skyrim Build" --output ./export.json
modular collection import ./export.json
modular collection check-updates "My Skyrim Build"

# Steam game detection
modular detect-games
modular detect-games --engines --verbose
modular detect-engine 730

# Steam mod installation
modular steam-install /path/to/game --manifest mods.json
modular steam-install /path/to/game --archive mod.zip --dry-run

# Profile management
modular profile list
modular profile export my-profile --format archive
modular profile import ./my-profile.json

# Plugin management
modular plugins list
modular plugins list --marketplace
modular plugins install my-plugin
modular plugins update
modular plugins remove my-plugin

# Diagnostics
modular diagnostics run
modular diagnostics run --json
modular diagnostics validate ./my-plugin

# Telemetry
modular telemetry summary --days 7
modular telemetry export --output ./telemetry.json
modular telemetry clear
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
- **Mod Search** - Search NexusMods and GameBanana with thumbnail previews and fuzzy re-ranking
- **Embedded Mod Browser** - View NexusMods mod pages in an embedded WebView without leaving the app
- **Download Queue** - Visual download queue with progress tracking, pause/resume, and drag-and-drop reordering
- **Mod Installation** - Install mods to game directories with visual progress
- **Installed Mods Management** - View and manage installed mods
- **Mod Library** - Browse and manage downloaded mods with search and filtering
- **Game Detection** - Scan for installed Steam games and detect game engines
- **Collections** - Create and manage mod collections visually
- **Profiles** - Profile management with export/import
- **Snapshots & Backups** - Save/restore mod state and manage backups
- **Plugin Management** - Install, update, and remove plugins from the GUI
- **Update Checking** - Check for mod updates with visual status indicators
- **Download History** - Track download statistics and history
- **Settings Management** - Configure all options through a visual interface
- **Keyboard Shortcuts** - Quick access to common operations

### Building the GUI

```bash
# Build the GUI
make build-gui

# Or using dotnet directly
dotnet build src/Modular.Gui/Modular.Gui.csproj -c Release
```

### Running the GUI

```bash
# Run via Makefile
make run-gui

# Or using dotnet directly
dotnet run --project src/Modular.Gui/Modular.Gui.csproj
```

### Publishing the GUI

```bash
# Publish for Linux (self-contained)
make publish-linux

# Publish for Windows (self-contained)
make publish-windows

# Publish for macOS
make publish-macos
make publish-macos-arm

# Output is in publish/linux/gui/, publish/windows/gui/, etc.
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

**NexusMods Search View**
- Full-text search across NexusMods catalog with fuzzy re-ranking
- Filter by game, sort by relevance/downloads/endorsements
- Thumbnail previews in search result listings
- Detail panel with mod info, stats, and summary when a mod is selected
- Embedded WebView to browse mod pages inline (toggle on/off)
- Open mod page in external browser

**GameBanana View**
- Displays subscribed mods from your GameBanana account
- Browse by game with search filtering
- Add mods to download queue

**GameBanana Search View**
- Search GameBanana for mods

**Downloads View**
- Active download queue with real-time progress
- Drag-and-drop to reorder queue
- Pause, resume, or cancel individual downloads
- Download history statistics panel

**Install View**
- Install mod archives to game directories
- Visual progress and changeset tracking

**Installed Mods View**
- View all installed mods with changeset details
- Uninstall mods with rollback support

**Library View**
- Browse all downloaded mods
- Search and filter by name or game
- View mod details and file locations

**Game Detection View**
- Scan Steam libraries for installed games
- Detect game engines for installer hints

**Collections View**
- Create, manage, and share mod collections
- Browse NexusMods collections online with thumbnail previews

**Profiles View**
- Manage mod profiles with export/import

**Snapshots & Backups View**
- Save and restore mod installation snapshots
- Manage file backups

**Plugins View**
- Browse installed and marketplace plugins
- Install, update, and remove plugins

**Settings View**
- Configure API keys (NexusMods, GameBanana)
- Set download path and organization options
- Toggle verification and auto-rename features

## Testing

```bash
# Run all tests
make test
# or: dotnet test Modular.sln

# Run specific test project
make test-core
make test-http

# Run a single test by filter
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"

# Run with verbose output
dotnet test --verbosity normal
```

### Test Coverage

- Configuration loading and validation
- Utility functions (filename sanitization, MD5 calculation, path sanitization)
- Fuzzy string matching for search re-ranking
- Database operations (add, find, query, remove)
- Backend registry and discovery
- NexusMods backend operations
- GameBanana backend operations
- Version providers and version range constraints
- Steam mod installer operations
- Integration tests
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

Additional documentation is available in the [`docs/`](docs/) directory. See the [`docs/README.md`](docs/README.md) for a full index.

### Key Documents

| Document | Description |
|----------|-------------|
| [`BUILD.md`](BUILD.md) | Build, install, and uninstall instructions |
| [`docs/PLUGIN_DEVELOPMENT.md`](docs/PLUGIN_DEVELOPMENT.md) | Guide to creating Modular plugins |
| [`docs/API-BACKENDS-GUIDE.md`](docs/API-BACKENDS-GUIDE.md) | Implementing backend plugins for new mod sources |
| [`docs/GAMEBANANA-API-GUIDE.md`](docs/GAMEBANANA-API-GUIDE.md) | GameBanana API integration reference |
| [`docs/NEXUSMODS_SSO_INTEGRATION.md`](docs/NEXUSMODS_SSO_INTEGRATION.md) | NexusMods SSO/WebSocket authentication |
| [`docs/fluent/README.md`](docs/fluent/README.md) | Fluent HTTP client user guide |
| [`docs/EVOLUTION_SUMMARY.md`](docs/EVOLUTION_SUMMARY.md) | Project evolution history and milestones |

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

This project is not yet licensed. A license will be added in a future release.

## Acknowledgments

- [NexusMods](https://www.nexusmods.com/) for their comprehensive modding API
- [GameBanana](https://gamebanana.com/) for their mod hosting platform
- [Spectre.Console](https://spectreconsole.net/) for CLI framework and rich terminal UI
- [Avalonia](https://avaloniaui.net/) for cross-platform GUI framework
- [Polly](https://github.com/App-vNext/Polly) for resilience and transient fault handling
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) for multi-format archive support
- [Microsoft.Extensions](https://github.com/dotnet/runtime) for configuration, logging, and DI
