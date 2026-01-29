# AGENTS.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Build Commands

**Build:**
```bash
make build
```

**Build release and install to ~/.local/bin/:**
```bash
make install
```

**Run tests:**
```bash
make test
```

**Clean:**
```bash
make clean
```

The executable is installed to `~/.local/bin/modular`.

## Dependencies

- .NET 8.0 SDK

## Environment Variables

- `API_KEY` - NexusMods API key (alternatively stored in `~/.config/Modular/api_key.txt`)
- `GB_USER_ID` - GameBanana user ID (required for GameBanana downloads)

## Architecture

### Core Modules

- **Modular.Core** (`src/Modular.Core/`) - Core business logic including NexusMods and GameBanana API interactions, configuration, database, HTTP client, rate limiting, rename/reorganize services.

- **Modular.FluentHttp** (`src/Modular.FluentHttp/`) - Fluent HTTP client library for making API requests.

- **Modular.Cli** (`src/Modular.Cli/`) - Command-line interface application.

### Entry Point

`src/Modular.Cli/Program.cs` provides CLI commands for:
1. Downloading mods from NexusMods and GameBanana
2. Renaming downloaded mod directories to human-readable names

### Data Flow

Mods are stored in `~/Games/Mods-Lists/{game_domain}/{mod_id}/`. The rename operation fetches mod names from NexusMods API and renames directories from mod IDs to human-readable names.

## Compiler Settings

Warnings are treated as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).
