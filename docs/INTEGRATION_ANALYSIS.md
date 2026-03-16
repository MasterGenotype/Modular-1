# Modular-1 Integration Analysis: External Resource Evaluation

## Table of Contents
1. [Technical Summary of External Resources](#1-technical-summary-of-external-resources)
2. [Mapping to Modular-1 Modules](#2-mapping-to-modular-1-modules)
3. [Stepwise Integration Plan](#3-stepwise-integration-plan)
4. [Risk Analysis and Recommendations](#4-risk-analysis-and-recommendations)

---

## 1. Technical Summary of External Resources

### 1.1 Resource A: Dependency-Sorted File Operations Across Multiple Archives

**Core Concept:** A system for performing ordered, dependency-aware file operations across many archives using database-backed "change sets" with immutable source facts, explicit dependency DAGs, and topological sorting for safe execution order.

**Key Technical Components:**

| Component | Description |
|---|---|
| **Archive Facts Model** | Immutable records of archive identity (path/URI, size, mtime, checksum) and entry inventory (inner path, type, metadata, offsets, per-entry hash) |
| **Change Set Abstraction** | A "proposed target state delta" containing a DAG of operations that can be accepted/rejected as a unit |
| **Operation DAG** | Nodes: `ListArchive`, `ExtractEntryToStage`, `Transform`, `WriteFile`, `Mkdir`, `SetMetadata`, `Delete`, `OverlayApply`, `CommitSwap`, `Verify`. Edges encode ordering constraints |
| **Two-Phase Commit** | Stage phase (extract/transform into staging area, compute checksums, preflight conflicts) then Commit phase (atomic swap, overlay mount, or file-level replace) |
| **Staging Strategies** | Temp directory + atomic rename, OverlayFS (Linux), FUSE union mounts, CoW cloning (reflinks) |
| **Conflict Resolution** | Fail-on-conflict, last-writer-wins (deterministic layer ordering), content-aware merge |
| **Database Schema** | Relational tables: `archive`, `archive_entry`, `blob` (content-addressed), `changeset`, `operation`, `op_dep` (DAG edges), `path_version` (target versioning) |
| **Concurrency Model** | DB-level logical locks + filesystem advisory locks; idempotent operations with crash-recovery journaling |
| **Security Model** | Path traversal defense (`openat2`/`RESOLVE_BENEATH`), symlink/hardlink policy, extraction filter enforcement |
| **Integrity** | SHA-256 content addressing for blobs, optional GPG/minisign signature verification |

**Archive Format Coverage:** ZIP (random access), TAR/TGZ (streaming), 7z (solid compression constraints), ISO 9660. Tooling: `libarchive`/`bsdtar` for cross-format, language stdlibs for zip/tar.

**Algorithms:**
- Kahn's algorithm for topological sort of operation DAG
- Content-addressed blob deduplication (Git-style hash identity)
- Directed acyclic graph cycle detection (DFS with recursion stack)

### 1.2 Resource B: C# Steam Game Catalog Constructor with Engine Detection

**Core Concept:** A C# composition-root pattern for detecting installed Steam games, inferring game engine via local file heuristics, and storing results in SQLite/PostgreSQL.

**Key Technical Components:**

| Component | Description |
|---|---|
| **Steam Root Location** | Platform-specific discovery: Windows registry (`HKCU/SteamPath`), Linux (`~/.local/share/Steam`), macOS (`~/Library/Application Support/Steam`) |
| **Library Enumeration** | Parse `libraryfolders.vdf` (Valve KeyValues format) to find all library roots |
| **Installed Game Detection** | Enumerate `steamapps/appmanifest_*.acf` files, parse KeyValues to extract `appid`, `installdir`, `SizeOnDisk`, `StateFlags` |
| **Install Path Resolution** | `<library>/steamapps/common/<installdir>` mapping |
| **Engine Detection Heuristics** | File-presence checks: Unity (`UnityPlayer.dll`, `*_Data/`, `GameAssembly.dll`), Unreal (`*.pak` in Content/Paks), Source (`*.vpk`), Godot (`data.pck`) |
| **Confidence Scoring** | Each detector returns engine family + confidence float + evidence list |
| **Composite Detector Pattern** | `IGameEngineDetector` interface with `CompositeEngineDetector` aggregating specialized detectors |
| **Database Schema** | `steam_app` (AppID, display name), `steam_install` (per-library install), `engine_detection` (family, confidence, evidence JSON) |
| **Architecture** | DI/Generic Host, `IAsyncEnumerable<T>` streaming, `CancellationToken` throughout, repository pattern with Dapper or EF Core |

**KeyValues Parser:** Custom parser for Valve's `.vdf`/`.acf` text format (not JSON, not INI).

---

## 2. Mapping to Modular-1 Modules

### 2.1 Resource A → Modular-1 Mapping

| Resource A Concept | Modular-1 Target | Current State | Integration Type |
|---|---|---|---|
| **Operation DAG + Topological Sort** | `Dependencies/DependencyGraph.cs` | Exists for mod dependencies; uses DFS-based topo sort | **Extend** — generalize `DependencyGraph` to support file-operation nodes, not just `ModNode` |
| **Change Set (staged batch)** | `Installers/InstallerManager.cs` + `InstallPlan` | `InstallPlan` is a flat list of `FileOperation`s with no dependency edges | **Enhance** — add dependency edges to `InstallPlan.Operations`, add changeset state tracking |
| **Two-Phase Stage+Commit** | `Installers/*.cs` (all installers) | Current installers extract directly to target; no staging area; `.backup` files for rollback | **New capability** — add staging directory pattern to `IModInstaller` workflow |
| **Conflict Detection** | `Dependencies/FileConflictIndex.cs` | Tracks file-level conflicts between mods (path → provider list) | **Integrate** — wire `FileConflictIndex` into install planning; add policy-based resolution |
| **Content-Addressed Blobs** | `Database/ModularDatabase.cs` | No blob store; downloads tracked by URL/path/MD5 | **New table** — add `blob` table; extend `DownloadRecord` to reference blob hashes |
| **Archive Inventory** | `Installers/*.cs` (DetectAsync/AnalyzeAsync) | Each installer reads ZIP entries inline; no persistent inventory | **New layer** — archive registry + entry inventory tables in `ModularDatabase` |
| **Multi-Format Support** | `Installers/*.cs` | ZIP only (`System.IO.Compression.ZipFile`) | **Extend** — add 7z/RAR/TAR support via SharpCompress or process-based `bsdtar` |
| **Integrity/Checksums** | `Downloads/DownloadEngine.cs` | MD5/SHA1/SHA256 for downloads; no per-entry verification post-extract | **Enhance** — add post-extraction hash verification in install pipeline |
| **Atomic Commit** | Not present | Direct file writes; no atomic directory swap | **New capability** — implement staging dir + `Directory.Move` or file-level atomic replace |
| **Path Traversal Defense** | Not present | No explicit path sanitization during extraction | **Critical gap** — add path normalization + traversal checks before extraction |
| **Database Concurrency** | `Database/ModularDatabase.cs` | SQLite with WAL mode; single connection | **Adequate for current scale** — already uses WAL; add advisory lock pattern if multi-process |

### 2.2 Resource B → Modular-1 Mapping

| Resource B Concept | Modular-1 Target | Current State | Integration Type |
|---|---|---|---|
| **Steam Root Discovery** | New: `GameDetection/SteamLocator.cs` | No game detection exists; game paths are manual config | **New module** — auto-detect Steam install paths |
| **Library Enumeration** | New: `GameDetection/SteamLibraryScanner.cs` | Not present | **New module** — parse `libraryfolders.vdf` |
| **KeyValues Parser** | New: `Utilities/KeyValuesParser.cs` | Not present (no VDF/ACF parsing) | **New utility** — lightweight parser for Valve KeyValues format |
| **Installed Game Detection** | New: `GameDetection/SteamGameScanner.cs` | Users manually configure `game_domain` in settings | **High-value feature** — auto-populate game list from local Steam |
| **Engine Detection** | New: `GameDetection/EngineDetector.cs` | `BepInExInstaller` detects BepInEx (a framework, not engine); no engine-level detection | **New capability** — enables smarter installer selection and mod compatibility |
| **Composite Detector Pattern** | `Installers/InstallerManager.cs` | Already uses priority + confidence pattern for installer selection | **Reuse pattern** — engine detectors follow same `Detect → Confidence → Select` flow |
| **Database Schema (steam_app, etc.)** | `Database/ModularDatabase.cs` | Schema v1 has downloads, metadata_cache, rate_limits, download_history | **Schema migration** — add game detection tables in schema v2 |
| **DI/Generic Host** | `Cli/Program.cs`, `Gui/Program.cs` | Already uses `Microsoft.Extensions.Hosting` and DI | **Compatible** — register new services in existing DI container |
| **IAsyncEnumerable Streaming** | Not widely used | Most methods return `Task<List<T>>` | **Pattern adoption** — use `IAsyncEnumerable` for game scanning to stream results |

### 2.3 Cross-Resource Synergies

Several concepts from both resources complement each other when applied to Modular-1:

1. **Archive inventory (A) + Game detection (B)** → When auto-detecting games, also inventory their existing mod archives for the dependency planner
2. **Staging + atomic commit (A) + Engine detection (B)** → Engine-aware staging (e.g., Unity games need `BepInEx/` structure, Unreal needs `~mods/` or `Paks/`)
3. **Content-addressed blobs (A) + Install manifests (existing)** → Deduplication across mods that share identical files (common in texture/mesh replacements)
4. **Conflict resolution policies (A) + FileConflictIndex (existing)** → Complete the existing conflict detection with actionable resolution

---

## 3. Stepwise Integration Plan

### Phase 1: Security Hardening & Archive Foundation (Priority: Critical)

**Rationale:** The existing installers extract ZIP files with no path traversal protection. This is the highest-risk gap.

#### Step 1.1: Path Traversal Defense
- **Target files:** `src/Modular.Core/Installers/LooseFileInstaller.cs`, `FomodInstaller.cs`, `BepInExInstaller.cs`
- **Action:** Add a `PathSanitizer` utility that:
  - Rejects entries containing `..` segments
  - Rejects absolute paths
  - Normalizes separators
  - Validates resolved path stays within target directory
- **Code location:** New file `src/Modular.Core/Utilities/PathSanitizer.cs`
- **Testing checkpoint:** Unit tests with adversarial paths (`../../../etc/passwd`, `C:\Windows\System32\`, symlink entries)

#### Step 1.2: Multi-Format Archive Support
- **Target:** `src/Modular.Core/Installers/`
- **Action:** Add `IArchiveReader` abstraction over `System.IO.Compression.ZipFile` + SharpCompress (supports 7z, RAR, TAR, GZ)
- **Code locations:**
  - New: `src/Modular.Sdk/Archives/IArchiveReader.cs` (interface)
  - New: `src/Modular.Core/Archives/ZipArchiveReader.cs`
  - New: `src/Modular.Core/Archives/SharpCompressArchiveReader.cs`
- **Dependency:** Add `SharpCompress` NuGet package to `Modular.Core.csproj`
- **Testing checkpoint:** Extract test archives in ZIP, 7z, RAR, TAR.GZ formats; verify identical output

#### Step 1.3: Post-Extraction Integrity Verification
- **Target:** All installers' `InstallAsync` methods
- **Action:** After extraction, compute SHA-256 of each extracted file and compare against archive entry CRC/hash
- **Reuse:** Leverage existing `DownloadEngine.ComputeHashAsync` pattern (refactor to shared utility)
- **Testing checkpoint:** Corrupt a test archive entry; verify detection

### Phase 2: Staged Installation Pipeline (Priority: High)

**Rationale:** Direct-to-target extraction prevents rollback on partial failure and blocks conflict detection.

#### Step 2.1: Staging Directory Infrastructure
- **Target:** `src/Modular.Sdk/Installers/IModInstaller.cs`, `src/Modular.Core/Installers/InstallerManager.cs`
- **Action:**
  - Add `StagingDirectory` concept to `InstallContext`
  - Modify installers to extract to `staging/<changeset-id>/` instead of target
  - Add commit step: move staged files to target (atomic where filesystem allows)
- **New types:** `StagingManager` in `src/Modular.Core/Installers/StagingManager.cs`
- **Testing checkpoint:** Install a mod; verify files appear in staging first; verify commit moves them; verify failed install leaves target untouched

#### Step 2.2: Operation DAG for Install Plans
- **Target:** `src/Modular.Sdk/Installers/IModInstaller.cs` (`InstallPlan`, `FileOperation`)
- **Action:**
  - Add `DependsOn` property to `FileOperation` (list of operation indices or IDs)
  - Add `OperationId` to `FileOperation`
  - Generalize `DependencyGraph` or create `OperationGraph` for file-operation scheduling
  - Automatic dependency inference: directory creation before child file writes
- **Testing checkpoint:** Create plan with out-of-order operations; verify topo sort produces valid execution order

#### Step 2.3: Conflict-Aware Installation
- **Target:** `src/Modular.Core/Dependencies/FileConflictIndex.cs`, `InstallerManager.cs`
- **Action:**
  - Wire `FileConflictIndex` into `InstallerManager.CreateInstallPlanAsync`
  - Before executing a plan, register all target paths and check for conflicts
  - Add `ConflictPolicy` enum: `FailOnConflict`, `LastWriterWins`, `AskUser`
  - Store policy in `InstallContext`
- **Testing checkpoint:** Install two mods targeting same file; verify conflict detection and policy enforcement

### Phase 3: Game Detection & Steam Integration (Priority: High)

**Rationale:** Currently users must manually configure game paths. Auto-detection dramatically improves UX.

#### Step 3.1: Valve KeyValues Parser
- **Target:** New file `src/Modular.Core/Utilities/KeyValuesParser.cs`
- **Action:** Implement a lightweight parser for Valve's VDF/ACF text format
  - Handle nested `"key" { ... }` blocks
  - Handle `"key" "value"` pairs
  - Handle comments (`//`)
  - Return a tree of `KeyValuesNode` objects
- **Testing checkpoint:** Parse real `libraryfolders.vdf` and `appmanifest_*.acf` samples; round-trip test

#### Step 3.2: Steam Root & Library Discovery
- **Target:** New directory `src/Modular.Core/GameDetection/`
- **Files:**
  - `ISteamLocator.cs` — interface
  - `SteamLocator.cs` — platform-specific root discovery (Linux: `~/.local/share/Steam`, Windows: registry, macOS: `~/Library/Application Support/Steam`)
  - `SteamLibraryScanner.cs` — parse `libraryfolders.vdf`, enumerate library roots
- **Testing checkpoint:** On Linux test environment, verify correct root detection; on CI, use mock filesystem

#### Step 3.3: Installed Game Scanner
- **Target:** `src/Modular.Core/GameDetection/SteamGameScanner.cs`
- **Action:**
  - Enumerate `steamapps/appmanifest_*.acf` per library root
  - Parse each manifest for `appid`, `installdir`, `name`, `SizeOnDisk`, `StateFlags`
  - Return `IAsyncEnumerable<SteamGameInstall>` for streaming
  - Map `installdir` to `<library>/steamapps/common/<installdir>`
- **Testing checkpoint:** Mock filesystem with sample manifests; verify correct AppID extraction and path mapping

#### Step 3.4: Engine Detection Framework
- **Target:** `src/Modular.Core/GameDetection/`
- **Files:**
  - `IEngineDetector.cs` — interface matching existing `IModInstaller` pattern (Detect → Confidence → Evidence)
  - `UnityEngineDetector.cs` — checks for `UnityPlayer.dll`, `*_Data/`, `GameAssembly.dll`
  - `UnrealEngineDetector.cs` — checks for `*.pak` in Content/Paks
  - `SourceEngineDetector.cs` — checks for `*.vpk`
  - `GodotEngineDetector.cs` — checks for `data.pck`
  - `CompositeEngineDetector.cs` — aggregates all detectors, returns highest-confidence result
- **Testing checkpoint:** Create mock game directories with engine-specific files; verify correct detection; test edge cases (mixed signals)

#### Step 3.5: Database Schema v2 Migration
- **Target:** `src/Modular.Core/Database/ModularDatabase.cs`
- **Action:** Add migration from schema v1 → v2:
  ```sql
  CREATE TABLE detected_games (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      steam_appid INTEGER NOT NULL,
      display_name TEXT,
      install_path TEXT NOT NULL,
      library_root TEXT NOT NULL,
      size_bytes INTEGER,
      first_seen_utc TEXT NOT NULL,
      last_seen_utc TEXT NOT NULL,
      UNIQUE(steam_appid, library_root)
  );
  CREATE TABLE engine_detection (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      steam_appid INTEGER NOT NULL,
      library_root TEXT NOT NULL,
      engine_family TEXT NOT NULL,
      confidence REAL NOT NULL,
      evidence_json TEXT,
      detector_name TEXT NOT NULL,
      detected_at_utc TEXT NOT NULL,
      UNIQUE(steam_appid, library_root)
  );
  ```
- **Testing checkpoint:** Fresh DB creates v2 schema; existing v1 DB migrates cleanly

### Phase 4: Enhanced Archive Management (Priority: Medium)

#### Step 4.1: Archive Registry & Inventory
- **Target:** `src/Modular.Core/Database/ModularDatabase.cs` (schema), new `src/Modular.Core/Archives/ArchiveInventoryService.cs`
- **Action:**
  - Add `archive` and `archive_entry` tables (from Resource A schema)
  - Persist archive inventories on first scan
  - Enable "what's in this archive?" queries without re-reading the file
- **Testing checkpoint:** Inventory a test archive; query entries; verify cache hit on second access

#### Step 4.2: Content-Addressed Blob Store
- **Target:** New `src/Modular.Core/Archives/BlobStore.cs`
- **Action:**
  - Add `blob` table (sha256 → storage path)
  - On extraction, compute hash and store in blob store
  - Enable deduplication: if blob already exists, skip extraction and link
- **Testing checkpoint:** Extract same file from two different archives; verify single blob stored

#### Step 4.3: Changeset Tracking & Provenance
- **Target:** `src/Modular.Core/Database/ModularDatabase.cs`, new `src/Modular.Core/Installers/ChangesetManager.cs`
- **Action:**
  - Add `changeset` table with state machine (planned → staging → ready → committing → committed → failed → rolled_back)
  - Record which archive entries were extracted, what transformations applied, final target paths
  - Enable "what changed in this install?" and "rollback this install" queries
- **Testing checkpoint:** Install a mod; query changeset; rollback; verify target restored

### Phase 5: CLI & GUI Integration (Priority: Medium)

#### Step 5.1: CLI Game Detection Commands
- **Target:** `src/Modular.Cli/Commands/`
- **New commands:**
  - `detect-games` — scan for installed Steam games, display results
  - `detect-engine <appid|path>` — detect game engine for a specific game
  - `scan-archives` — inventory downloaded mod archives
- **Testing checkpoint:** Run commands; verify output matches expected games

#### Step 5.2: GUI Game Browser View
- **Target:** `src/Modular.Gui/Views/`, `src/Modular.Gui/ViewModels/`
- **Action:**
  - New `GameBrowserView.axaml` showing detected games with engine badges
  - Filter/search by engine type
  - Click game → show available mods for that game domain
- **Testing checkpoint:** UI renders detected games; engine badges display correctly

#### Step 5.3: Staging Visibility in UI
- **Target:** `src/Modular.Gui/Views/DownloadQueueView.axaml`
- **Action:** Show staging status during install (staged → verified → committed)
- **Testing checkpoint:** Install mod via GUI; observe stage/commit progress

### Phase 6: Advanced Features (Priority: Low)

#### Step 6.1: OverlayFS/Virtual Install Support (Linux only)
- Implement optional OverlayFS-based mod deployment for games that support it
- Requires elevated privileges; gate behind explicit opt-in

#### Step 6.2: Cross-Platform Atomic Commit
- Windows: `ReplaceFile` API via P/Invoke for file-level atomic replacement
- macOS: `clonefile()` acceleration for staging tree creation
- Linux: reflink support detection and usage

#### Step 6.3: Binary String Scanning for Engine Detection
- Optional deep scan mode that reads first 16 MiB of executables for engine signatures
- Gate behind user opt-in setting due to performance and reverse-engineering concerns

---

## 4. Risk Analysis and Recommendations

### 4.1 Risk Matrix

| Risk | Severity | Likelihood | Mitigation |
|---|---|---|---|
| **Path traversal in archive extraction** | Critical | High (current code has no protection) | Phase 1.1 — implement `PathSanitizer` immediately |
| **ZIP-only format support limits usability** | High | High (7z and RAR are common mod formats) | Phase 1.2 — add SharpCompress |
| **No rollback on partial install failure** | High | Medium (network/disk errors during extraction) | Phase 2.1 — staging directory pattern |
| **Steam detection breaks on non-standard installs** | Medium | Medium (Flatpak, custom paths) | Graceful fallback; manual override in settings |
| **Engine detection false positives** | Low | Medium (mixed-engine games, renamed files) | Confidence scoring + evidence trail; never auto-act on low confidence |
| **Schema migration corrupts existing data** | High | Low (SQLite is robust) | Backup before migration; transaction-wrapped DDL; integration tests |
| **SharpCompress dependency adds attack surface** | Medium | Low | Pin version; monitor CVEs; fuzz test with adversarial archives |
| **Performance: deep directory scanning for engine detection** | Medium | Medium (large Steam libraries with 100+ games) | Use `IAsyncEnumerable` streaming; top-level file checks only; no recursive scan by default |
| **Legal: Steam branding/reverse engineering** | Medium | Low (read-only file inspection) | No binary disassembly; no Steamworks SDK; clear attribution disclaimers |

### 4.2 Dependency Considerations

| New Dependency | Purpose | License | Alternatives |
|---|---|---|---|
| **SharpCompress** | 7z, RAR, TAR, GZ support | MIT | `SevenZipSharp` (LGPL), process-based `bsdtar` |
| **Microsoft.Data.Sqlite** | Already present | MIT | — |
| **Dapper** (optional) | Lighter SQL layer | Apache 2.0 | Continue with raw `SqliteCommand` (current approach) |

### 4.3 Compatibility Notes

- **Existing `IModInstaller` contract:** Changes to `InstallPlan` and `FileOperation` (adding `OperationId`, `DependsOn`) should be additive with defaults, preserving backward compatibility for existing plugin implementors
- **Existing `DependencyGraph`:** The existing graph operates on `ModNode`; a new `OperationGraph` should be a separate class to avoid coupling file-operation concerns with version-resolution concerns
- **Database schema versioning:** `ModularDatabase` already has `PRAGMA user_version` migration support; Phase 3.5 slots into the existing `MigrateSchemaAsync` pattern
- **.NET 8.0 baseline:** All proposed changes are compatible with .NET 8.0; no features require .NET 10

### 4.4 Optional Enhancements

1. **Provenance logging (Resource A):** Append-only audit log of all file operations with timestamps and blob hashes — valuable for debugging "which mod broke my game" scenarios
2. **Parallel extraction (Resource A):** ZIP's random-access nature allows parallel entry extraction; schedule independent operations concurrently using the existing `SemaphoreSlim` pattern from `DownloadEngine`
3. **External metadata enrichment (Resource B):** Query IGDB or SteamDB for game metadata (engine, genre, release date) as secondary enrichment after local heuristic detection
4. **Mod profile snapshots:** Combine changeset tracking with profile export to create reproducible mod configurations (like a "lock file" for mods)

### 4.5 Testing Strategy Summary

| Test Type | Scope | Tools |
|---|---|---|
| **Unit tests** | PathSanitizer, KeyValuesParser, DAG sort, conflict detection, engine heuristics | xUnit (existing test projects) |
| **Integration tests** | Full install pipeline with staging, DB migrations, archive multi-format | Real filesystem in temp directories |
| **Adversarial tests** | Malicious archives (traversal, symlinks, oversized entries, unicode edge cases) | Crafted test archives in test fixtures |
| **Platform tests** | Steam detection on Linux/Windows/macOS paths | CI matrix (already configured in `build.yml`) |
| **Regression tests** | Existing installer behavior unchanged after refactoring | Run existing test suite after each phase |

---

## Summary

The two external resources map directly onto gaps and enhancement opportunities in Modular-1:

- **Resource A** addresses the installer pipeline's lack of staging, atomicity, dependency-ordered operations, and archive security — all critical for a mod manager handling untrusted archives.
- **Resource B** addresses the user experience gap of manual game configuration and enables engine-aware mod installation logic.

The recommended integration order prioritizes **security (path traversal)** first, then **reliability (staging/rollback)**, then **UX (game detection)**, and finally **advanced features (blob store, overlay mounts)**. Each phase has clear testing checkpoints and preserves backward compatibility with the existing plugin SDK.
