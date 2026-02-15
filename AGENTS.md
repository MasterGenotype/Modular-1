# AGENTS.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Build Commands

```bash
# Build all projects (Debug)
make build
# or: dotnet build Modular.sln

# Build Release and install CLI to ~/.local/bin/
make install

# Run all tests
make test
# or: dotnet test Modular.sln

# Run specific test project
dotnet test tests/Modular.Core.Tests/
dotnet test tests/Modular.FluentHttp.Tests/

# Run a single test by filter
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"

# Clean build artifacts
make clean
```

### GUI Commands

```bash
# Build and run GUI
make run-gui
# or: dotnet run --project src/Modular.Gui/Modular.Gui.csproj

# Install GUI
make install-gui
```

### Plugin Development

```bash
# Build example plugin
make plugin-example

# Install example plugin to ~/.config/Modular/plugins/
make plugin-install
```

## Architecture

Five-layer plugin-based architecture:

```
GUI (Modular.Gui)         - Avalonia 11.3, MVVM, visual mod management
CLI (Modular.Cli)         - Spectre.Console, System.CommandLine
Core (Modular.Core)       - Business logic, backends, auth, plugins, installers
SDK (Modular.Sdk)         - Stable plugin contracts: IModBackend, IMetadataEnricher, IModInstaller, IUiExtension
FluentHttp                - Fluent HTTP client with middleware filters
```

### Key Extension Points

- **IModBackend** (`src/Modular.Sdk/Backends/`) - Add new mod repository sources
- **IMetadataEnricher** (`src/Modular.Sdk/Metadata/`) - Transform backend data to canonical format
- **IModInstaller** (`src/Modular.Sdk/Installers/`) - Handle mod installation workflows
- **IUiExtension** (`src/Modular.Sdk/UI/`) - Add custom GUI panels

### Important Subsystems

- **Backend Abstraction** (`src/Modular.Core/Backends/`) - NexusModsBackend, GameBananaBackend implement IModBackend
- **Plugin System** (`src/Modular.Core/Plugins/`) - AssemblyLoadContext-based loading, MEF composition
- **Authentication** (`src/Modular.Core/Authentication/`) - NexusMods WebSocket SSO flow
- **Rate Limiting** (`src/Modular.Core/RateLimiting/`) - Parses NexusMods x-rl-* headers
- **Dependency Resolution** (`src/Modular.Core/Dependencies/`) - PubGrub-inspired version solver

### Legacy Services (Deprecated)

`NexusModsService` and `GameBananaService` in `src/Modular.Core/Services/` are `[Obsolete]`. Use the backend abstraction (`NexusModsBackend`, `GameBananaBackend`) instead.

## Code Style

- C# 12 / .NET 8.0
- 4-space indentation
- PascalCase for types and public members, _camelCase for private fields
- File-scoped namespaces
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Warnings treated as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)

## Environment Variables

- `NEXUS_API_KEY` - NexusMods API key (or use SSO authentication)
- `GB_USER_ID` - GameBanana user ID

## Configuration

Config file: `~/.config/Modular/config.json`
Plugins directory: `~/.config/Modular/plugins/`
Default download path: `~/Mods`
