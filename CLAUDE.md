# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Modular is an extensible mod manager for downloading, organizing, and managing game modifications from multiple repositories (NexusMods, GameBanana, extensible via plugins). It provides both a CLI (Spectre.Console) and GUI (Avalonia 11.3) interface, built on .NET 9.0 / C# 12.

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
make test-switch              # dotnet test tests/Modular.Switch.Tests/

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

- **Backends** (`Backends/`) — `NexusModsBackend`, `GameBananaBackend` and `ModIoBackend` implement `IModBackend`. mod.io games are numeric ids (BG3 is 6715) and every request goes to the game's `g-{id}.modapi.io` host; see `docs/MODIO-API-GUIDE.md`. `BackendRegistry` manages registration with case-insensitive ID lookup.
- **Plugins** (`Plugins/`) — `AssemblyLoadContext`-based isolation with MEF composition. Discovers plugins from `~/.config/Modular/plugins/` via `plugin.json` manifests. `ErrorBoundary` isolates plugin failures (permissive/strict policies).
- **Dependencies** (`Dependencies/`) — PubGrub-inspired semantic version solver (`GreedyDependencyResolver`), file conflict detection (`FileConflictIndex`), operation DAG (`OperationGraph`).
- **Authentication** (`Authentication/`) — NexusMods WebSocket-based browser SSO flow.
- **Rate Limiting** (`RateLimiting/`) — Thread-safe `NexusRateLimiter` parses `x-rl-*` response headers. Dual-tier: 20K daily, 500 hourly.
- **Installers** (`Installers/`) — `FomodInstaller`, `BepInExInstaller`, `LooseFileInstaller` with confidence-based detection.

### Switch (`src/Modular.Switch/`)

- `SwitchInstallRouting` picks each title's installer. Most titles use `SwitchModInstaller` (one LayeredFS folder per mod).
- Tears of the Kingdom (`Totk/`, TitleID `0100F2C0115B6000`) merges mods into one output, a port of TKMM v2.0.0-rc1 on TkSharp. See `docs/switch-totk-merge.md`.
- TkSharp is the `lib/TkSharp` submodule (`git submodule update --init`): MasterGenotype/TkSharp, branch `modular/rc1-sharpcompress-0.50` = 797e76ec + SharpCompress 0.50 patches. Follow rc1 only; never backport from later TKMM/TkSharp; don't edit `lib/TkSharp` without the user's say-so.
- TkSharp has process-wide, non-thread-safe state: one `TotkMergeService` per process, one operation at a time; tests touching it use the `TkSharp` xUnit collection.

### Legacy Code

`NexusModsService` and `GameBananaService` in `src/Modular.Core/Services/` are `[Obsolete]`. Use the backend abstraction (`NexusModsBackend`, `GameBananaBackend`) instead.

## Code Style

- C# 12 / .NET 9.0, implicit usings, file-scoped namespaces
- 4-space indentation
- `PascalCase` for types and public members, `_camelCase` for private fields
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Warnings treated as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)
- Async methods take `CancellationToken`, long operations use `IProgress<T>`

## Environment Variables

- `NEXUS_API_KEY` — NexusMods API key (alternative to SSO)
- `GB_USER_ID` — GameBanana user ID
- `MODIO_ACCESS_TOKEN` — mod.io OAuth token (subscriptions); `MODIO_API_KEY` — mod.io read-only API key

## Configuration

- Config file: `~/.config/Modular/config.json`
- Plugins directory: `~/.config/Modular/plugins/`
- Default download path: `~/Mods`
- Downloads are laid out `<mods>/<backend>/<game>/<mod>` (`ModLibraryLayout`); `ModLibraryMigrator` moves older layouts, driven by Library → Re-organize and `modular rename`
