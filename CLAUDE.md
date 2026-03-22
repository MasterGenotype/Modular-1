# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Modular is an extensible mod manager for downloading, organizing, and managing game modifications from multiple repositories (NexusMods, GameBanana, extensible via plugins). It provides both a CLI (Spectre.Console) and GUI (Avalonia 11.3) interface, built on .NET 8.0 / C# 12.

## Build & Test Commands

```bash
# Build all projects (Debug)
make build                    # or: dotnet build Modular.sln

# Build individual projects
make build-cli                # CLI only
make build-gui                # GUI only
make build-core               # Core library only

# Run all tests
make test                     # or: dotnet test Modular.sln

# Run specific test project
make test-core                # dotnet test tests/Modular.Core.Tests/
make test-http                # dotnet test tests/Modular.FluentHttp.Tests/

# Run a single test
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"

# Run CLI/GUI directly (dev mode)
dotnet run --project src/Modular.Cli/Modular.Cli.csproj -- --help
make run-gui

# Release build + install CLI to ~/.local/bin/
make install

# Clean
make clean

# Build example plugin
make plugin-example
make plugin-install            # Install to ~/.config/Modular/plugins/
```

## Architecture

Five-layer plugin-based design with this dependency flow:

```
GUI (Modular.Gui)    ──┐
CLI (Modular.Cli)    ──┤──> Core (Modular.Core) ──> FluentHttp (Modular.FluentHttp)
                       │         │
                       └─────────┴──> SDK (Modular.Sdk)
```

- **SDK** (`src/Modular.Sdk/`) — Stable plugin contracts with zero Core dependencies. Defines `IModBackend`, `IMetadataEnricher`, `IModInstaller`, `IUiExtension`, `IPluginMetadata`. Plugins reference only this.
- **Core** (`src/Modular.Core/`) — Business logic: backend implementations, plugin loading, authentication, dependency resolution, rate limiting, downloads, installers, configuration.
- **FluentHttp** (`src/Modular.FluentHttp/`) — Custom fluent HTTP client with middleware filter pipeline, retry policies, and rate limiter integration.
- **CLI** (`src/Modular.Cli/`) — Command-line interface using Spectre.Console.Cli with 18+ commands in `Commands/`.
- **GUI** (`src/Modular.Gui/`) — Avalonia 11.3 desktop app with MVVM (CommunityToolkit.Mvvm).

### Key Subsystems in Core

- **Backends** (`Backends/`) — `NexusModsBackend` and `GameBananaBackend` implement `IModBackend`. `BackendRegistry` manages registration with case-insensitive ID lookup.
- **Plugins** (`Plugins/`) — `AssemblyLoadContext`-based isolation with MEF composition. Discovers plugins from `~/.config/Modular/plugins/` via `plugin.json` manifests. `ErrorBoundary` isolates plugin failures (permissive/strict policies).
- **Dependencies** (`Dependencies/`) — PubGrub-inspired semantic version solver (`GreedyDependencyResolver`), file conflict detection (`FileConflictIndex`), operation DAG (`OperationGraph`).
- **Authentication** (`Authentication/`) — NexusMods WebSocket-based browser SSO flow.
- **Rate Limiting** (`RateLimiting/`) — Thread-safe `NexusRateLimiter` parses `x-rl-*` response headers. Dual-tier: 20K daily, 500 hourly.
- **Installers** (`Installers/`) — `FomodInstaller`, `BepInExInstaller`, `LooseFileInstaller` with confidence-based detection.

### Legacy Code

`NexusModsService` and `GameBananaService` in `src/Modular.Core/Services/` are `[Obsolete]`. Use the backend abstraction (`NexusModsBackend`, `GameBananaBackend`) instead.

## Code Style

- C# 12 / .NET 8.0, implicit usings, file-scoped namespaces
- 4-space indentation
- `PascalCase` for types and public members, `_camelCase` for private fields
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Warnings treated as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)
- Async methods take `CancellationToken`, long operations use `IProgress<T>`

## Environment Variables

- `NEXUS_API_KEY` — NexusMods API key (alternative to SSO)
- `GB_USER_ID` — GameBanana user ID

## Configuration

- Config file: `~/.config/Modular/config.json`
- Plugins directory: `~/.config/Modular/plugins/`
- Default download path: `~/Mods`
