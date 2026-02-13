# Adaptation Recommendations: Superior Features from Competing Mod Managers

This document analyzes superior features found in leading mod managers (Mod Organizer 2, Vortex, Thunderstore, Nexus Mod Manager) and provides actionable recommendations for adapting Modular to incorporate the best capabilities from each.

**Source:** Research conducted via [Perplexity AI](https://www.perplexity.ai/search/research-superior-features-and-ZDpejoz0T_ua08lv9FWrJg#2) comparing superior features across major mod management tools.

**References:**
- [MO2 Mod Managers Comparison Wiki](https://github.com/ModOrganizer2/modorganizer/wiki/Mod-Managers-Comparison)
- [Best Mod Manager for PC Games - PulseGeek](https://pulsegeek.com/articles/best-mod-manager-for-pc-games-top-picks-compared/)
- [MO2 vs Vortex Comparison - BuiltToFrag](https://builttofrag.com/vortex-vs-mod-organizer-2/)
- [Best Mod Managers Guide - ToolsNova](https://toolsnova.com/best-mod-managers/)

---

## Executive Summary

Modular is a well-architected C#/.NET 8.0 mod downloading and organization tool with CLI and GUI interfaces, supporting NexusMods and GameBanana backends. However, competing tools like **Mod Organizer 2 (MO2)** and **Vortex** offer several superior features in areas including virtual file systems, conflict management, mod profiles, dependency resolution, and plugin extensibility.

This document identifies **28 specific features** across **8 categories** where Modular can be improved by adapting proven approaches from competitors. Each recommendation includes a priority level, estimated complexity, the source of inspiration, and how it maps to Modular's existing architecture.

---

## Table of Contents

1. [Virtual File System & Mod Isolation](#1-virtual-file-system--mod-isolation)
2. [Conflict Detection & Resolution](#2-conflict-detection--resolution)
3. [Mod Profiles & Instance Management](#3-mod-profiles--instance-management)
4. [Dependency & Load Order Management](#4-dependency--load-order-management)
5. [Download & Update Management](#5-download--update-management)
6. [Plugin & Extensibility Architecture](#6-plugin--extensibility-architecture)
7. [User Experience & Interface](#7-user-experience--interface)
8. [Problem Detection & Diagnostics](#8-problem-detection--diagnostics)

---

## 1. Virtual File System & Mod Isolation

### What Competitors Do

**MO2** uses a User-space Virtual File System (USVFS) that intercepts file system calls at the OS level, creating a virtual overlay of mod files without ever touching the actual game directory. Each mod is stored in its own folder, and the VFS assembles them into a coherent view at runtime. This means:
- The game folder stays pristine (zero modifications to original files)
- Mods can be enabled/disabled instantly without file copying
- "Overwritten" files are actually "overridden" virtually, preserving all originals
- Uninstalling a mod is as simple as unchecking a box

**Vortex** uses hardlinks/symlinks to achieve a similar (though less complete) isolation effect, deploying mods via links rather than copies.

### Current State in Modular

Modular downloads mods into organized directories (`~/Mods/{game}/{category}/`) and provides folder renaming. There is no virtual file system, no mod isolation layer, and no non-destructive deployment mechanism. Mods are managed as downloaded archives/files without a deployment pipeline to game directories.

### Recommendations

#### 1.1 Implement a Mod Staging Directory System
**Priority:** High | **Complexity:** Medium | **Inspired by:** MO2

Create a staging area where each mod is stored in its own isolated directory, separate from the game's actual mod folder. This is the foundation for non-destructive mod management.

**Adaptation for Modular:**
```
~/.local/share/modular/staging/{game_domain}/
    ├── mod_12345/          # Each mod in its own folder
    │   ├── manifest.json   # Mod metadata + file list
    │   └── files/          # Extracted mod contents
    ├── mod_67890/
    │   ├── manifest.json
    │   └── files/
    └── ...
```

**Implementation approach:**
- Add a `StagingService` to `Modular.Core` that manages the staging directory
- After download, extract archives into the staging area instead of directly into the game directory
- Store a `manifest.json` per mod with metadata: mod ID, version, file hashes, extraction date, enabled state
- The staging directory becomes the single source of truth for installed mods

**Files to modify:**
- `src/Modular.Core/Services/` - New `StagingService.cs`
- `src/Modular.Core/Models/` - New `StagedMod.cs` model
- `src/Modular.Core/Configuration/AppSettings.cs` - Add staging path config

#### 1.2 Implement Symlink/Hardlink Deployment
**Priority:** Medium | **Complexity:** Medium | **Inspired by:** Vortex

Deploy mods from the staging area to the game directory using symlinks (Linux/macOS) or hardlinks, rather than copying files. This preserves the original game directory and enables instant enable/disable.

**Adaptation for Modular:**
- Add a `DeploymentService` that creates links from staging to game directories
- Support three deployment modes: `symlink` (default on Linux), `hardlink`, and `copy` (fallback)
- Track deployed files in a deployment manifest so they can be cleanly removed
- On "undeploy," remove only the links/copies that Modular created

**Files to modify:**
- `src/Modular.Core/Services/` - New `DeploymentService.cs`
- `src/Modular.Core/Utilities/FileUtils.cs` - Add symlink/hardlink helpers

#### 1.3 Archive Extraction Pipeline
**Priority:** High | **Complexity:** Low | **Inspired by:** MO2, Vortex

Add automatic extraction of downloaded archives (`.zip`, `.7z`, `.rar`) into the staging area.

**Adaptation for Modular:**
- Integrate `SharpCompress` or `System.IO.Compression` for archive handling
- Auto-detect archive format and extract to staging
- Handle nested archives and common mod packaging patterns (e.g., FOMOD installer scripts)
- Keep the original archive as a backup in a separate `archives/` directory

**New dependency:** `SharpCompress` NuGet package

---

## 2. Conflict Detection & Resolution

### What Competitors Do

**MO2** provides granular conflict detection with visual indicators:
- Color-coded flags on the mod list show winning/losing/bidirectional conflicts
- Clicking a mod highlights all conflicting mods
- Users manually set mod priority (drag-and-drop ordering) to resolve conflicts
- Individual file-level overrides are possible

**Vortex** uses an automatic rule-based system:
- Auto-detects conflicts when two mods modify the same file
- Prompts users with a dialog to choose which mod should win
- Stores rules (load-after, load-before) that persist across sessions
- Dependency checker validates mod relationships

### Current State in Modular

Modular has no conflict detection. Mods are downloaded as files and organized by category without tracking which files each mod contains or whether multiple mods modify the same game files.

### Recommendations

#### 2.1 File-Level Conflict Detection
**Priority:** High | **Complexity:** Medium | **Inspired by:** MO2

Track which files each staged mod provides and detect when multiple mods supply the same file path.

**Adaptation for Modular:**
- During staging (extraction), record every file path each mod provides in its `manifest.json`
- Build an in-memory index mapping `relative_file_path -> List<ModId>`
- When deploying, check the index for conflicts (same path, multiple mods)
- Report conflicts to the user with clear identification of which mods overlap

**Implementation in `Modular.Core`:**
```csharp
public class ConflictDetectionService
{
    public Dictionary<string, List<int>> BuildFileIndex(IEnumerable<StagedMod> mods);
    public List<FileConflict> DetectConflicts(IEnumerable<StagedMod> enabledMods);
}

public record FileConflict(string RelativePath, List<StagedMod> ConflictingMods);
```

**Files to modify:**
- `src/Modular.Core/Services/` - New `ConflictDetectionService.cs`
- `src/Modular.Core/Models/` - New `FileConflict.cs`

#### 2.2 Priority-Based Conflict Resolution
**Priority:** Medium | **Complexity:** Low | **Inspired by:** MO2

Allow users to set mod priority order. When conflicts exist, the higher-priority mod's files win.

**Adaptation for Modular:**
- Add a `priority` field to each staged mod (integer, higher = wins)
- During deployment, for conflicting files, deploy only the highest-priority mod's version
- In the GUI (`Modular.Gui`), support drag-and-drop reordering of the mod list
- In the CLI, provide a `modular priority set <mod_id> <priority>` command

#### 2.3 Conflict Visualization in GUI
**Priority:** Low | **Complexity:** Medium | **Inspired by:** MO2

Add visual conflict indicators to the Avalonia GUI.

**Adaptation for Modular.Gui:**
- Add color-coded conflict flags to the mod list (green = winning, red = losing, yellow = both)
- Clicking a mod highlights all mods it conflicts with
- Add a "Conflicts" tab/panel showing per-file conflict details
- Integrate into the existing `ModListViewModel`

**Files to modify:**
- `src/Modular.Gui/ViewModels/ModListViewModel.cs`
- `src/Modular.Gui/Views/` - Update mod list view XAML

---

## 3. Mod Profiles & Instance Management

### What Competitors Do

**MO2** provides full profile support:
- Multiple profiles per game, each with its own enabled/disabled mod set, priority order, and INI/save overrides
- Profiles share the same mod pool but can independently toggle any mod
- Cloning profiles for experimentation is trivial
- Profile-specific saves and game configuration

**Vortex** offers similar profiles with per-profile mod enable/disable and game-specific configuration.

**Thunderstore** allows exporting profiles to share with friends, including mod lists and configs.

### Current State in Modular

Modular has a single configuration per game domain. There is no concept of profiles, mod sets, or per-profile configuration. The `AppSettings` model in `Modular.Core/Configuration/` stores global settings only.

### Recommendations

#### 3.1 Implement Mod Profiles
**Priority:** High | **Complexity:** Medium | **Inspired by:** MO2, Vortex

Add a profile system that allows multiple mod configurations per game.

**Adaptation for Modular:**
```
~/.config/Modular/profiles/{game_domain}/
    ├── default/
    │   ├── profile.json     # Profile metadata
    │   ├── enabled_mods.json # List of enabled mod IDs + priority order
    │   └── overrides/       # Profile-specific config file overrides
    ├── testing/
    │   ├── profile.json
    │   ├── enabled_mods.json
    │   └── overrides/
    └── minimal/
        └── ...
```

**Profile model:**
```csharp
public class ModProfile
{
    public string Name { get; set; }
    public string GameDomain { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ProfileModEntry> Mods { get; set; }  // ModId + enabled + priority
}
```

**Files to modify:**
- `src/Modular.Core/Models/` - New `ModProfile.cs`
- `src/Modular.Core/Services/` - New `ProfileService.cs`
- `src/Modular.Core/Configuration/AppSettings.cs` - Add active profile setting

#### 3.2 Profile Import/Export
**Priority:** Medium | **Complexity:** Low | **Inspired by:** Thunderstore

Allow users to export a profile as a shareable file and import profiles from others.

**Adaptation for Modular:**
- Export: Serialize profile to a `.modular-profile` JSON file containing mod IDs, versions, sources, and priority order
- Import: Parse the file, check which mods are already staged, download missing ones, and create the profile
- Support both CLI (`modular profile export <name>`) and GUI export/import

#### 3.3 Profile Cloning
**Priority:** Low | **Complexity:** Low | **Inspired by:** MO2

Allow cloning an existing profile as a starting point for experimentation.

**CLI:** `modular profile clone <source> <new_name>`

---

## 4. Dependency & Load Order Management

### What Competitors Do

**MO2** integrates with LOOT (Load Order Optimization Tool) to automatically sort plugin load orders based on a community-maintained database. It also provides:
- Missing master detection (warns when a required plugin is missing)
- Form 43 plugin detection (identifies outdated plugin formats)
- Manual load order overrides

**Vortex** provides:
- Automatic dependency resolution using mod metadata
- Rule-based load ordering (load-after, load-before constraints)
- Visual dependency graph
- One-click dependency installation from NexusMods

### Current State in Modular

Modular tracks mods as individual downloads without dependency awareness. The `DownloadDatabase` records what was downloaded but not what each mod requires. The backend interfaces (`IModBackend`) don't expose dependency information.

### Recommendations

#### 4.1 Dependency Metadata Extraction
**Priority:** Medium | **Complexity:** Medium | **Inspired by:** Vortex

Extract and store dependency information from mod metadata.

**Adaptation for Modular:**
- When fetching mod info from NexusMods/GameBanana, parse the `requirements` or `dependencies` fields
- Store dependencies in the mod's staging manifest
- Add a `DependencyService` that can resolve the full dependency tree for a mod

**NexusMods API fields to parse:**
- Mod description often contains requirements (parse manually)
- File `requirements` field in the files API response

**GameBanana API fields:**
- `_aPrerequisites` in the mod submission data

**Files to modify:**
- `src/Modular.Core/Models/` - Add `ModDependency` to existing models
- `src/Modular.Core/Backends/NexusMods/NexusModsBackend.cs` - Parse dependency fields
- `src/Modular.Core/Backends/GameBanana/GameBananaBackend.cs` - Parse prerequisite fields
- `src/Modular.Core/Services/` - New `DependencyService.cs`

#### 4.2 Missing Dependency Detection
**Priority:** High | **Complexity:** Low | **Inspired by:** MO2

Warn users when a mod's dependencies are not installed.

**Adaptation for Modular:**
- After staging/enabling a mod, check if all dependencies are also staged and enabled
- Display warnings in both CLI and GUI
- Offer to auto-download missing dependencies from the same source

#### 4.3 Dependency Graph Visualization (GUI)
**Priority:** Low | **Complexity:** High | **Inspired by:** Vortex

Add a visual dependency graph to the Avalonia GUI showing relationships between installed mods.

**Files to modify:**
- `src/Modular.Gui/Views/` - New dependency graph view
- `src/Modular.Gui/ViewModels/` - New `DependencyGraphViewModel.cs`

---

## 5. Download & Update Management

### What Competitors Do

**Vortex** provides:
- One-click downloads directly from the NexusMods website via `nxm://` protocol handler
- Automatic update checking with version comparison
- Batch update for all mods at once
- Download speed display and queue management

**MO2** provides:
- Built-in update checker using the `updated` mods endpoint
- Version pinning to prevent unwanted updates
- Mod update filtering by time period (1 day, 1 week, 1 month)
- Visual indicators for mods with available updates

**Thunderstore** provides:
- Profile-based batch downloads
- Automatic dependency resolution during download

### Current State in Modular

Modular handles downloads well with rate limiting, progress tracking, and MD5 verification. It supports concurrent downloads (config exists but noted as not fully utilized in the codebase review). It lacks: protocol handler registration, automatic update checking, version pinning, and batch update operations.

### Recommendations

#### 5.1 Automatic Update Checking
**Priority:** High | **Complexity:** Medium | **Inspired by:** MO2, Vortex

Implement periodic or on-demand checking for mod updates.

**Adaptation for Modular:**
- Use the NexusMods `updated_mods` endpoint (already noted in `IMPROVEMENTS.md`) to efficiently check for updates
- For GameBanana, compare `_tsDateUpdated` against last download timestamp
- Store the last-known version/timestamp per mod in the staging manifest
- Display update status in both CLI (`modular check-updates <game>`) and GUI

**Implementation:**
```csharp
public class UpdateCheckService
{
    public async Task<List<ModUpdateInfo>> CheckForUpdatesAsync(
        string gameDomain,
        string period = "1w",  // 1d, 1w, 1m
        CancellationToken ct = default);
}

public record ModUpdateInfo(
    int ModId,
    string ModName,
    string CurrentVersion,
    string LatestVersion,
    DateTime UpdatedAt);
```

**Files to modify:**
- `src/Modular.Core/Services/` - New `UpdateCheckService.cs`
- `src/Modular.Core/Backends/Common/` - Add update check to `IModBackend` interface

#### 5.2 Version Pinning
**Priority:** Medium | **Complexity:** Low | **Inspired by:** MO2

Allow users to pin a mod to a specific version, preventing it from being flagged for updates.

**Adaptation for Modular:**
- Add a `pinned_version` field to the staged mod manifest
- When checking updates, skip pinned mods
- CLI: `modular pin <mod_id>` and `modular unpin <mod_id>`
- GUI: Right-click context menu option "Pin version"

#### 5.3 NXM Protocol Handler Registration
**Priority:** Medium | **Complexity:** Medium | **Inspired by:** Vortex

Register Modular as a handler for `nxm://` URLs, enabling one-click downloads from the NexusMods website.

**Adaptation for Modular:**
- On Linux: Register a `.desktop` file with `MimeType=x-scheme-handler/nxm`
- Parse `nxm://` URLs to extract game domain, mod ID, and file ID
- Queue the download through existing backend infrastructure
- This feature is already partially supported by the NexusMods SSO integration (see `docs/NEXUSMODS_SSO_INTEGRATION.md`)

**Files to modify:**
- `src/Modular.Cli/Program.cs` - Add NXM URL argument parsing
- New: Linux `.desktop` file for protocol registration

#### 5.4 Batch Update Operations
**Priority:** Medium | **Complexity:** Low | **Inspired by:** Vortex

Allow updating all outdated mods in a single operation.

**CLI:** `modular update-all <game_domain>`
**GUI:** "Update All" button in the mod list toolbar

#### 5.5 Download Queue Persistence
**Priority:** Medium | **Complexity:** Low | **Inspired by:** MO2 (already in CODEBASE_REVIEW_RECOMMENDATIONS.md)

Persist the download queue to disk so it survives application restarts. This was already identified in `docs/CODEBASE_REVIEW_RECOMMENDATIONS.md` Section 5.2 and should be prioritized.

---

## 6. Plugin & Extensibility Architecture

### What Competitors Do

**MO2** has a full plugin system:
- Python-based plugin API for extending functionality
- Plugin types: installers, previews, diagnostics, tools
- Community-contributed plugins for game-specific features
- Plugins can add new UI panels, tools, and diagnostics

**Vortex** uses:
- JavaScript/TypeScript extension API
- Extensions hosted on NexusMods for easy installation
- Extensions can add game support, UI elements, and tools
- Robust extension lifecycle management

### Current State in Modular

Modular has an extensible backend system (`IModBackend` interface with `BackendRegistry`) that supports adding new mod sources. However, there is no general-purpose plugin system for extending functionality beyond backends.

### Recommendations

#### 6.1 Formalize the Backend Plugin Interface
**Priority:** High | **Complexity:** Low | **Inspired by:** MO2 plugin architecture

The existing `IModBackend` interface is a good foundation. Formalize it as the plugin contract and add dynamic loading.

**Adaptation for Modular:**
- Move `IModBackend` into a separate `Modular.Contracts` project (new shared assembly)
- Support loading backend implementations from external DLLs at runtime
- Use `System.Reflection` to scan a `plugins/` directory for assemblies implementing `IModBackend`
- This allows third-party backends (Thunderstore, CurseForge, Steam Workshop) without modifying core code

**Files to modify/create:**
- New project: `src/Modular.Contracts/` with `IModBackend`, `IModSource`, shared models
- `src/Modular.Core/Backends/BackendRegistry.cs` - Add dynamic assembly loading

#### 6.2 Installer Plugin Interface
**Priority:** Medium | **Complexity:** Medium | **Inspired by:** MO2

Add an installer plugin system for handling complex mod packaging formats (FOMOD, BAIN, etc.).

**Adaptation for Modular:**
```csharp
public interface IModInstaller
{
    string Name { get; }
    bool CanHandle(string archivePath, IReadOnlyList<string> fileList);
    Task<InstallResult> InstallAsync(
        string archivePath,
        string stagingPath,
        IInstallerDialogService dialog,
        CancellationToken ct);
}
```

- Default installer: simple extraction
- FOMOD installer: parse `ModuleConfig.xml` and present options to user
- BAIN installer: handle BAIN-style packages with sub-packages

#### 6.3 Diagnostic Plugin Interface
**Priority:** Low | **Complexity:** Low | **Inspired by:** MO2

Allow plugins that scan installed mods and report potential issues.

```csharp
public interface IDiagnosticPlugin
{
    string Name { get; }
    Task<List<DiagnosticResult>> RunAsync(IEnumerable<StagedMod> mods, CancellationToken ct);
}

public record DiagnosticResult(
    DiagnosticSeverity Severity,  // Info, Warning, Error
    string Message,
    string? ModId,
    string? SuggestedFix);
```

---

## 7. User Experience & Interface

### What Competitors Do

**Vortex:**
- Intuitive drag-and-drop interface with minimal learning curve
- Dashboard with at-a-glance status for all managed games
- Built-in notification system for updates, conflicts, and errors
- Theme support (light/dark)
- Integrated browser for NexusMods

**MO2:**
- Powerful but complex interface targeted at power users
- Customizable columns and views
- Category-based filtering with custom categories
- Mod metadata editing
- Integrated BSA/BA2 browser

**Thunderstore:**
- Clean, modern interface
- Profile sharing and community features
- Minimal configuration required

### Current State in Modular

Modular has both CLI (`Modular.Cli` with Spectre.Console) and GUI (`Modular.Gui` with Avalonia + Material Icons). The GUI uses MVVM with `CommunityToolkit.Mvvm`. Current GUI features include mod listing, a library view, and thumbnail services.

### Recommendations

#### 7.1 Dashboard View
**Priority:** Medium | **Complexity:** Medium | **Inspired by:** Vortex

Add a dashboard/home screen to the GUI showing an overview across all managed games.

**Adaptation for Modular.Gui:**
- Show cards for each game with: number of mods, updates available, last sync date
- Quick-action buttons per game: "Check Updates", "Download All", "Open Folder"
- Activity feed showing recent downloads and operations

**Files to modify:**
- `src/Modular.Gui/ViewModels/` - New `DashboardViewModel.cs`
- `src/Modular.Gui/Views/` - New `DashboardView.axaml`

#### 7.2 Notification System
**Priority:** Medium | **Complexity:** Low | **Inspired by:** Vortex

Add an in-app notification system for important events.

**Adaptation for Modular:**
- Download completed / failed notifications
- Update available notifications
- Conflict detected notifications
- Rate limit warnings
- Use Avalonia's notification infrastructure or a toast-style overlay

#### 7.3 Advanced Filtering & Search
**Priority:** Medium | **Complexity:** Low | **Inspired by:** MO2

Enhance the mod list with powerful filtering capabilities.

**Adaptation for Modular.Gui:**
- Text search across mod names and descriptions
- Filter by: status (installed/not installed/update available), category, source (NexusMods/GameBanana)
- Custom categories/tags that users can assign to mods
- Column sorting (name, date, size, status, priority)
- Save filter presets

**Files to modify:**
- `src/Modular.Gui/ViewModels/ModListViewModel.cs` - Add filter logic
- `src/Modular.Gui/Views/` - Add filter UI controls

#### 7.4 Context Menu Actions
**Priority:** Low | **Complexity:** Low | **Inspired by:** MO2

Add right-click context menus to the mod list in the GUI.

**Actions:**
- Open mod page in browser
- Check for updates (single mod)
- Reinstall / Verify files
- Pin/Unpin version
- Set priority
- View conflicts
- Open in file manager
- Remove/Delete

#### 7.5 CLI Completions
**Priority:** Low | **Complexity:** Low | **Inspired by:** (already in CODEBASE_REVIEW_RECOMMENDATIONS.md)

Add shell completion generation for bash, zsh, and fish. Already identified in Section 6.3 of `CODEBASE_REVIEW_RECOMMENDATIONS.md` -- `System.CommandLine` (already a dependency) has built-in support for this.

---

## 8. Problem Detection & Diagnostics

### What Competitors Do

**MO2** automatically detects:
- Missing master files (plugins that require files not present)
- Form 43 plugins (Skyrim LE format used in SE, causing instability)
- Overwritten INI files
- Files in the overwrite folder
- Plugins exceeding the 255 limit

**Vortex** provides:
- Automated conflict scanning on deployment
- Plugin dependency validation
- Circular dependency detection
- Game-specific health checks

### Current State in Modular

Modular has MD5 verification for downloads and basic file validation. There are no game-specific diagnostics or automated problem detection beyond download integrity checks.

### Recommendations

#### 8.1 Download Integrity Verification
**Priority:** High | **Complexity:** Low | **Inspired by:** MO2, Vortex (partially exists)

Enhance the existing MD5 verification to be more comprehensive.

**Adaptation for Modular:**
- Already has MD5 checking -- extend to verify after staging (not just after download)
- Add file count verification (expected vs actual extracted files)
- Add a `modular verify <game>` command that re-checks all staged mod integrity
- In GUI, show verification status per mod (verified / unverified / failed)

#### 8.2 Mod Health Check System
**Priority:** Medium | **Complexity:** Medium | **Inspired by:** MO2

Implement a diagnostic system that scans installed mods for common issues.

**Adaptation for Modular:**
```csharp
public class ModHealthCheckService
{
    public async Task<HealthReport> RunChecksAsync(string gameDomain, CancellationToken ct)
    {
        // Check for: missing dependencies, file conflicts, orphaned files,
        // outdated mods, duplicate mods, staging inconsistencies
    }
}

public class HealthReport
{
    public List<HealthIssue> Issues { get; set; }
    public int TotalMods { get; set; }
    public int HealthyMods { get; set; }
}
```

**Checks to implement:**
1. Missing dependencies (mod A requires mod B, but B is not staged)
2. Unresolved file conflicts (no priority set)
3. Orphaned staging directories (no longer tracked)
4. Duplicate mods (same mod ID staged multiple times)
5. Failed verification (hash mismatches)
6. Outdated mods (updates available)

**Files to modify:**
- `src/Modular.Core/Services/` - New `ModHealthCheckService.cs`
- `src/Modular.Core/Models/` - New `HealthReport.cs`, `HealthIssue.cs`

#### 8.3 Status Command Enhancement
**Priority:** Medium | **Complexity:** Low | **Inspired by:** MO2 (already partially in CODEBASE_REVIEW_RECOMMENDATIONS.md)

Enhance the proposed `status` command (from Section 6.2 of `CODEBASE_REVIEW_RECOMMENDATIONS.md`) to include mod health information.

**CLI output example:**
```
$ modular status skyrimspecialedition

  Game:              Skyrim Special Edition
  Profile:           default
  Staged Mods:       45
  Enabled Mods:      42
  Updates Available:  3
  Conflicts:          2 (resolved)
  Health Issues:      1 warning

  API Status:
    NexusMods:       OK (Daily: 19,542/20,000 | Hourly: 487/500)
    GameBanana:      OK

  Disk Usage:
    Staging:         2.3 GB
    Archives:        4.1 GB
    Total:           6.4 GB
```

---

## Implementation Roadmap

### Phase 1: Foundation (Builds on existing architecture)

| # | Feature | Priority | Complexity | Depends On |
|---|---------|----------|------------|------------|
| 1.1 | Staging Directory System | High | Medium | - |
| 1.3 | Archive Extraction Pipeline | High | Low | 1.1 |
| 2.1 | File-Level Conflict Detection | High | Medium | 1.1 |
| 5.1 | Automatic Update Checking | High | Medium | - |
| 8.1 | Enhanced Integrity Verification | High | Low | 1.1 |

### Phase 2: Core Features

| # | Feature | Priority | Complexity | Depends On |
|---|---------|----------|------------|------------|
| 1.2 | Symlink/Hardlink Deployment | Medium | Medium | 1.1 |
| 2.2 | Priority-Based Conflict Resolution | Medium | Low | 2.1 |
| 3.1 | Mod Profiles | High | Medium | 1.1 |
| 4.1 | Dependency Metadata Extraction | Medium | Medium | 1.1 |
| 4.2 | Missing Dependency Detection | High | Low | 4.1 |
| 6.1 | Formalize Backend Plugin Interface | High | Low | - |

### Phase 3: Enhanced UX

| # | Feature | Priority | Complexity | Depends On |
|---|---------|----------|------------|------------|
| 3.2 | Profile Import/Export | Medium | Low | 3.1 |
| 5.2 | Version Pinning | Medium | Low | 5.1 |
| 5.3 | NXM Protocol Handler | Medium | Medium | - |
| 5.4 | Batch Update Operations | Medium | Low | 5.1 |
| 7.1 | Dashboard View | Medium | Medium | - |
| 7.2 | Notification System | Medium | Low | - |
| 7.3 | Advanced Filtering & Search | Medium | Low | - |
| 8.2 | Mod Health Check System | Medium | Medium | 2.1, 4.2 |

### Phase 4: Advanced & Polish

| # | Feature | Priority | Complexity | Depends On |
|---|---------|----------|------------|------------|
| 2.3 | Conflict Visualization (GUI) | Low | Medium | 2.1 |
| 3.3 | Profile Cloning | Low | Low | 3.1 |
| 4.3 | Dependency Graph (GUI) | Low | High | 4.1 |
| 5.5 | Download Queue Persistence | Medium | Low | - |
| 6.2 | Installer Plugin Interface | Medium | Medium | 6.1 |
| 6.3 | Diagnostic Plugin Interface | Low | Low | 6.1 |
| 7.4 | Context Menu Actions | Low | Low | - |
| 7.5 | CLI Completions | Low | Low | - |
| 8.3 | Enhanced Status Command | Medium | Low | 8.2 |

---

## Competitive Positioning Summary

| Feature Area | MO2 | Vortex | Modular (Current) | Modular (After Adaptation) |
|---|---|---|---|---|
| Virtual File System | USVFS (excellent) | Hardlinks (good) | None | Staging + Symlinks (good) |
| Conflict Detection | Granular with flags | Rule-based auto | None | File-level + priority (good) |
| Mod Profiles | Full profiles + saves | Basic profiles | None | Profiles + export (good) |
| Dependency Management | LOOT integration | Auto-resolution | None | Metadata + detection (basic) |
| Download Management | Update checking | One-click + updates | Strong downloads | Full lifecycle (excellent) |
| Extensibility | Python plugins | JS extensions | Backend interface | Plugin contracts (good) |
| Multi-Source Support | NexusMods only | NexusMods + some | NexusMods + GameBanana | Extensible (excellent) |
| Cross-Platform | Windows focus | Windows focus | Linux + Windows + macOS | Linux + Windows + macOS (excellent) |
| Update Checking | Built-in | Built-in | None | Built-in (good) |
| GUI | Powerful/complex | Intuitive/simple | Avalonia MVVM | Enhanced Avalonia (good) |

### Modular's Unique Advantages to Preserve

While adapting features from competitors, Modular should preserve and emphasize its existing strengths:

1. **Cross-platform first** - MO2 and Vortex are Windows-focused; Modular runs on Linux, macOS, and Windows via .NET 8.0 + Avalonia
2. **Multi-source support** - Already supports NexusMods and GameBanana with an extensible backend system
3. **Modern architecture** - Clean .NET 8.0 codebase with MVVM, DI, and separation of concerns
4. **Rate limiting compliance** - Robust API rate limit handling that competitors often lack
5. **Dual interface** - Both CLI (for automation/scripting) and GUI (for interactive use)
6. **Fluent HTTP client** - Custom `Modular.FluentHttp` provides a clean, testable HTTP abstraction

---

## Relationship to Existing Documentation

This document builds upon and references recommendations from existing project documentation:

| Existing Document | Relationship |
|---|---|
| `CODEBASE_REVIEW_RECOMMENDATIONS.md` | Internal code quality improvements; this document adds feature-level recommendations from competitive analysis |
| `IMPROVEMENTS.md` | Original improvement list from C++ era; many items already implemented in C# migration |
| `TASKS.md` | Implementation task list from C++ era; superseded by current .NET architecture |
| `API-BACKENDS-GUIDE.md` | Backend extension guide; Section 6.1 here recommends formalizing this further |
| `GUI_RECOMMENDATIONS.md` | GUI-specific recommendations; Section 7 here adds competitive feature analysis |
| `MODULAR_INTEGRATION_COMPARISON.md` | HTTP client comparison; complements the extensibility recommendations here |

---

## Conclusion

By systematically adapting the proven features from MO2 (virtual file system, conflict management, profiles, diagnostics) and Vortex (automatic resolution, one-click downloads, intuitive UX) while preserving Modular's unique strengths (cross-platform, multi-source, modern .NET architecture), the project can evolve from a mod downloader into a comprehensive mod management platform.

The phased implementation roadmap prioritizes features that provide the most user value with the least disruption to the existing architecture, starting with the staging system foundation and building up to advanced features like plugin extensibility and visual diagnostics.
