# AGENTS.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Build Commands

**Configure and build (using presets):**
```bash
cmake --preset default
cmake --build build
```

**Debug build:**
```bash
cmake --preset debug
cmake --build build
```

**Traditional build (without presets):**
```bash
cmake .
make
```

The executable is output to `build/Modular`.

## Dependencies

- C++17 compiler (g++ or clang++)
- CURL (`libcurl`)
- nlohmann/json (install via package manager or from https://github.com/nlohmann/json)
- CMake 3.20+
- Ninja (optional, used by presets)

## Environment Variables

- `API_KEY` - NexusMods API key (alternatively stored in `~/.config/Modular/api_key.txt`)
- `GB_USER_ID` - GameBanana user ID (required for GameBanana downloads)

## Architecture

### Core Modules

- **NexusMods** (`src/NexusMods.cpp`, `include/NexusMods.h`) - Handles NexusMods API interactions: tracking mods, fetching file IDs, generating download links, downloading files. Uses CURL for HTTP requests and nlohmann/json for parsing.

- **GameBanana** (`src/GameBanana.cpp`, `include/GameBanana.h`) - Handles GameBanana API interactions: fetching subscribed mods, extracting mod IDs from URLs, downloading mod files.

- **Rename** (`src/Rename.cpp`, `include/Rename.h`) - Directory utilities: scanning game domains and mod IDs, fetching mod names from API, renaming directories, merging directory structures.

- **LiveUI** (`src/LiveUI.cpp`, `include/LiveUI.h`) - Terminal progress bar component using ANSI escape codes. Provides a two-line repainting UI with operation label, progress bar, and status line.

### Entry Point

`main.cpp` provides two modes:
1. **CLI mode** - Pass game domains as arguments: `./build/Modular skyrimspecialedition --categories main,optional`
2. **Menu mode** - Interactive menu when run without arguments

### Data Flow

Mods are stored in `~/Games/Mods-Lists/{game_domain}/{mod_id}/`. The rename operation fetches mod names from NexusMods API and renames directories from mod IDs to human-readable names.

## Compiler Settings

Warnings are treated as errors (`-Werror` / `/WX`). The project uses `-Wall -Wextra -Wpedantic` on GCC/Clang.
