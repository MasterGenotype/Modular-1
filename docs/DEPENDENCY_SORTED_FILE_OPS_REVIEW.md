# Codebase Review: Dependency-Sorted File Operations Reference Architecture

## Overview

This document maps the concepts from the **"Dependency-Sorted File Operations Across Multiple Archives Using Database Record Abstractions and Staging"** reference architecture against the current Modular codebase, identifying existing strengths, architectural gaps, and actionable recommendations.

The reference describes a system for performing **ordered, dependency-aware file operations across archives** using database-backed change sets, DAG-based topological sorting, two-phase staging-then-commit pipelines, and robust security controls for archive extraction.

---

## 1. Current Codebase Strengths

### 1.1 Dependency Graph Infrastructure (Strong Foundation)

**Location:** `src/Modular.Core/Dependencies/DependencyGraph.cs`

The existing `DependencyGraph` class already implements key algorithms needed by the reference architecture:

- **Topological sorting** via DFS with temporary marks — correctly returns `null` on cycle detection
- **Cycle detection** via DFS with recursion stack
- **Path finding** (transitive dependency checks) via BFS
- **Thread-safe** with lock-based synchronization

This is a solid foundation. The reference architecture's DAG-based execution planning can directly leverage this class, though it currently models **mod-level** dependencies (`ModNode` / `DependencyEdge`) rather than **file-operation-level** dependencies.

### 1.2 File Conflict Detection (Partial Coverage)

**Location:** `src/Modular.Core/Dependencies/FileConflictIndex.cs`

The `FileConflictIndex` already implements:
- Path normalization (lowercase, forward slashes)
- Multi-provider conflict detection (multiple mods writing to the same game path)
- Conflict type classification: `Overwrite`, `IdenticalFiles`, `MergeCandidate`
- Content-based deduplication via file hashes
- Per-mod file registration/unregistration

This maps well to the reference's **conflict resolution model**. The reference adds explicit conflict policies per change set (`fail-on-conflict`, `last-writer-wins`, `content-aware-merge`) which could be layered on top.

### 1.3 Conflict Resolution Strategies

**Location:** `src/Modular.Core/Dependencies/ConflictResolver.cs`

Already provides automated resolution strategies:
- `Automatic`, `Manual`, `Conservative`, `Aggressive` modes
- Suggestion generation per conflict type with confidence scoring
- Resolution actions: `ChangeVersion`, `RemoveMod`, `AdjustLoadOrder`, `ReplaceWithAlternative`

### 1.4 Download Engine (Production-Grade)

**Location:** `src/Modular.Core/Downloads/DownloadEngine.cs`

- HTTP streaming with 8KB buffers
- Resumable downloads via Range headers
- Concurrent download control via `SemaphoreSlim`
- MD5/SHA1/SHA256 checksum verification
- Progress callbacks with throttling
- URL re-resolution for expired links

### 1.5 Download Queue with Persistence

**Location:** `src/Modular.Core/Downloads/DownloadQueue.cs`

- JSON-based durable queue surviving restarts
- Priority-based processing
- Exponential backoff retry
- Pause/resume capability
- Crash recovery (resets in-progress items to pending on load)

### 1.6 Database Infrastructure

**Location:** `src/Modular.Core/Database/ModularDatabase.cs`

- SQLite with WAL mode enabled for concurrent access
- Schema versioning with migration framework
- Tables for: downloads, metadata_cache, rate_limits, download_history

### 1.7 Installer Framework

**Location:** `src/Modular.Core/Installers/`

- Priority-based installer selection with confidence scoring
- Three-phase workflow: Detect → Analyze (create plan) → Install (execute plan)
- `InstallPlan` with typed `FileOperation` records (`Copy`, `Extract`, `CreateDirectory`, `Patch`, `Merge`, `Symlink`)
- `InstallManifest` tracking installed files, directories, and backups for uninstall
- Three built-in installers: FOMOD (priority 100), BepInEx (priority 80), LooseFile (priority 1, fallback)

### 1.8 Plugin Architecture

**Location:** `src/Modular.Core/Plugins/`

- MEF-based composition with `AssemblyLoadContext` isolation
- Topological sorting of plugin dependencies (the plugin loader already has its own topo-sort)
- Hot reload support via `PluginLoadContext`
- SDK contracts for extending: `IModBackend`, `IModInstaller`, `IMetadataEnricher`, `IUiExtension`

---

## 2. Architectural Gaps

### 2.1 Archive Format Support — ZIP-Only

**Impact: High**

The codebase exclusively uses `System.IO.Compression.ZipFile` for all archive operations. Every installer (`FomodInstaller`, `BepInExInstaller`, `LooseFileInstaller`) opens archives via `ZipFile.OpenRead()`.

The reference architecture covers ZIP, TAR, TAR.GZ, 7z, and ISO 9660, and emphasizes format-specific constraints:

| Format | Reference Concern | Modular Status |
|--------|-------------------|----------------|
| ZIP | Random access friendly; path sanitization needed | Supported (only format) |
| TAR/TGZ | Stream-oriented; sequential extraction planning needed | Not supported |
| 7z | Solid compression creates implicit extraction dependencies | Not supported |
| ISO 9660 | Image/session-oriented semantics | Not supported |

**Game modding archives commonly use 7z and RAR**, making this a significant functional gap.

**Recommendation:** Introduce an `IArchiveProvider` abstraction:
```csharp
public interface IArchiveProvider
{
    string FormatId { get; }
    bool CanHandle(string archivePath);
    Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(string archivePath, CancellationToken ct);
    Task ExtractEntryAsync(string archivePath, ArchiveEntry entry, string destPath, CancellationToken ct);
    Task ExtractAllAsync(string archivePath, string destDir, CancellationToken ct);
}
```
Implementations could wrap `System.IO.Compression` (ZIP), `SharpCompress` (7z/RAR/TAR), or shell out to `bsdtar`/`7z` CLI tools.

### 2.2 No Staging Phase — Direct Extraction to Target

**Impact: High**

Current installers extract files **directly to the target game directory**. For example, `LooseFileInstaller.InstallAsync()` calls `entry.ExtractToFile(destPath, overwrite: true)` directly into the game path.

The reference architecture mandates a **two-phase "stage then commit" pipeline**:
1. **Stage:** Extract to an isolated staging directory, compute checksums, normalize metadata, preflight for conflicts
2. **Commit:** Apply atomically via directory swap (`os.replace`), OverlayFS, or file-level atomic replace

**Consequences of the current approach:**
- **No atomic rollback:** If installation fails mid-way, the game directory is left in a partially-modified state
- **No integrity verification** of extracted contents before they're written to the target
- **No conflict preflighting:** Conflicts are only detectable after files are already written
- **Backup-only recovery:** The installers do create per-file backups, but restoring from backups is manual and error-prone

**Recommendation:** Add a `StagingManager` that:
1. Extracts to `~/.config/Modular/staging/{changeset-id}/`
2. Verifies checksums of staged files
3. Runs conflict detection against the target
4. Commits via atomic rename (same filesystem) or file-level copy + verify
5. Records the change set in the database for rollback capability

### 2.3 No File-Operation-Level DAG

**Impact: Medium-High**

The existing `DependencyGraph` operates at the **mod level** (nodes are `ModNode` = mod + version). The reference architecture describes an **operation-level DAG** where individual file operations (mkdir, extract, verify, write, set-metadata) are nodes with explicit dependency edges:

- "Directory must exist before writing a child file"
- "File must be staged and checksum-verified before commit"
- "Conflicting paths must be resolved before either writer proceeds"

The `InstallPlan.Operations` list is currently a flat `List<FileOperation>` without ordering guarantees or dependency tracking between operations.

**Recommendation:** Either:
- (a) Extend the existing `DependencyGraph` to be generic (`DependencyGraph<T>` with `IGraphNode` interface), or
- (b) Create a specialized `FileOperationGraph` for installation-time operation ordering

### 2.4 No Archive Inventory / Entry Registry

**Impact: Medium**

The reference architecture maintains an **archive registry** and **entry inventory** as database records:
```sql
CREATE TABLE archive (archive_id, uri, format, size_bytes, content_sha256, ...);
CREATE TABLE archive_entry (entry_id, archive_id, inner_path, entry_type, size_bytes, ...);
```

The current codebase has no persistent record of archive contents. Each installer re-scans the archive from scratch on every operation. There is no deduplication of identical entries across archives, no content-addressed blob store, and no way to query "which archives contain file X?"

**Recommendation:** Add `archive` and `archive_entry` tables to `ModularDatabase` schema (version 2 migration). This enables:
- Faster re-analysis without re-reading archives
- Cross-archive deduplication
- Provenance tracking ("this file came from archive A, entry B")

### 2.5 No Content-Addressed Blob Store

**Impact: Medium**

The reference architecture uses content-addressed storage (SHA-256 as the blob identity) for:
- Deduplication across archives
- Integrity verification
- Provenance tracking

Modular currently computes MD5 for download verification but does not track checksums of **individual extracted files**. The `FileConflictIndex` accepts an optional `fileHash` but it's not consistently populated.

**Recommendation:** Compute SHA-256 for each extracted file during staging. Store in a `blob` table. Reference blobs from operations and path versions.

### 2.6 No Change Set / Provenance Tracking

**Impact: Medium**

The reference architecture defines a `changeset` table that groups related operations with:
- State machine: `planned → staging → ready → committing → committed → failed → rolled_back`
- Policy JSON (conflict resolution, metadata normalization)
- Provenance (what came from where, when, by what transformation)

Modular's `InstallManifest` partially covers this (tracks installed files, directories, backups), but it:
- Has no state machine for the installation lifecycle
- Has no link to source archive entries
- Has no rollback mechanism beyond manual backup restoration
- Is not persisted in the database (it's returned as an in-memory object)

**Recommendation:** Persist `InstallManifest` data in a database `changeset` table with full lifecycle tracking. This enables reliable uninstall and rollback.

### 2.7 Archive Extraction Security

**Impact: High**

The current installers have **minimal path traversal protection**:

- `LooseFileInstaller` strips common root directories but doesn't explicitly block `..` sequences or absolute paths in archive entries
- `BepInExInstaller` and `FomodInstaller` extract entries using `entry.ExtractToFile()` with paths constructed from archive entry names
- No symlink handling — archive entries with symlink type are not explicitly blocked or rewritten
- `FileUtils.SanitizeFilename()` replaces invalid characters but does not perform path traversal checks

The reference architecture emphasizes:
- **Path sanitization** (reject `..`, absolute paths, symlink chains)
- **OS-level traversal prevention** (Linux `openat2` with `RESOLVE_BENEATH`, Go `os.Root`)
- **Symlink/hardlink/special file handling** (tarfile can contain device nodes)
- **Staged extraction** as a security boundary (extract to jail, verify, then commit)

**Recommendation:** Add a `PathSanitizer` utility that:
```csharp
public static class PathSanitizer
{
    public static bool IsSafePath(string entryPath, string targetRoot)
    {
        // Reject absolute paths
        // Reject .. traversal
        // Resolve and verify final path is under targetRoot
        // Reject symlinks pointing outside targetRoot
    }
}
```
Call this before every extraction operation. The staging approach (2.2) also provides defense-in-depth since staged files are isolated from the game directory.

### 2.8 No Atomic Commit Strategy

**Impact: Medium**

The reference architecture provides three commit strategies:
1. **Directory atomic swap** — build new tree in staging, then `rename()`/`os.replace()` the whole directory
2. **OverlayFS** — Linux union mount with lowerdir (game) + upperdir (staged changes)
3. **File-level atomic replace** — per-file `ReplaceFile` on Windows with attribute preservation

Modular currently writes files one-at-a-time directly to the target. There is no atomicity guarantee at any level.

**Recommendation:** For Modular's use case (game mod management), **file-level atomic replace with journaling** is the most practical:
1. Write each file to a temp path in the same directory
2. Record the operation in a journal (database)
3. Atomic rename temp → final
4. Mark journal entry as committed
5. On crash recovery: check journal, undo uncommitted operations

Directory-level atomic swap may be impractical since game directories contain many unmodified files.

### 2.9 Concurrency and Locking

**Impact: Low-Medium**

The reference discusses PostgreSQL advisory locks and multi-worker coordination. For Modular's single-node desktop context, this is less critical, but relevant for:
- Concurrent mod installations (currently no protection against two installs writing to the same game directory)
- SQLite write contention (WAL helps but doesn't eliminate)

**Recommendation:** Add a per-game-directory lock (file-based `flock` on Linux/macOS, named mutex on Windows) to prevent concurrent installations to the same target.

### 2.10 No Version Tracking for Target Paths

**Impact: Medium**

The reference defines a `path_version` table tracking what was written to each target path, by which change set, with blob references and tombstones for deletions.

Modular's `InstallManifest` tracks installed files but doesn't track version history. This means:
- No way to know "what was here before mod A was installed"
- No clean rollback to a previous state when multiple mods modify the same path
- No conflict detection across installation sessions (only within a single `FileConflictIndex` instance)

**Recommendation:** Add path versioning to the database. At minimum, track: `(target_root, rel_path, changeset_id, blob_sha256, is_tombstone)`.

---

## 3. Summary Matrix

| Reference Concept | Modular Status | Gap Severity |
|---|---|---|
| DAG + topological sort | Exists for mods, not file ops | Medium-High |
| Multi-format archive support | ZIP only | High |
| Archive inventory / entry registry | Not implemented | Medium |
| Content-addressed blobs | Not implemented | Medium |
| Staging phase (isolated extraction) | Not implemented | High |
| Atomic commit strategy | Not implemented | Medium |
| Change set / provenance tracking | Partial (InstallManifest, in-memory) | Medium |
| Conflict detection | Exists (FileConflictIndex) | Low (good) |
| Conflict resolution strategies | Exists (ConflictResolver) | Low (good) |
| Path traversal / extraction security | Minimal protection | High |
| Dependency resolution | Exists (GreedyDependencyResolver) | Low (good) |
| Download engine with integrity checks | Exists (DownloadEngine) | Low (good) |
| Persistent download queue | Exists (DownloadQueue) | Low (good) |
| Database with WAL + migrations | Exists (ModularDatabase) | Low (good) |
| Concurrency / locking | Basic (per-object locks) | Low-Medium |
| Target path versioning | Not implemented | Medium |
| Plugin-based extensibility | Excellent (MEF + isolated contexts) | Low (strength) |

---

## 4. Prioritized Implementation Roadmap

### Phase 1: Security Hardening (Highest Priority)
1. **Path sanitizer** for archive extraction — prevent traversal attacks
2. **Symlink/hardlink policy** — reject or rewrite dangerous entries
3. **Entry type validation** — block device nodes, FIFOs, etc.

### Phase 2: Staging Pipeline
1. **Staging directory management** — isolated extraction area
2. **Post-extraction integrity verification** — SHA-256 of staged files
3. **Conflict preflighting** — detect issues before modifying the game directory
4. **Atomic commit** — file-level rename with journal

### Phase 3: Multi-Format Archives
1. **`IArchiveProvider` abstraction** — decouple installers from ZIP
2. **7z support** — critical for game modding (via SharpCompress or CLI wrapper)
3. **RAR support** — common in legacy mod archives
4. **TAR/TGZ support** — Linux game mods

### Phase 4: Database Record Abstractions
1. **Archive registry** + entry inventory tables
2. **Change set table** with lifecycle state machine
3. **Path versioning** for rollback capability
4. **Content-addressed blob store** (optional, for deduplication)

### Phase 5: Operation-Level DAG
1. **File operation graph** — extend or mirror `DependencyGraph` for operations
2. **Dependency edge generation** — parents-before-children, stage-before-commit
3. **Parallel execution buckets** — operations at the same topological level can run concurrently

---

## 5. Key Architectural Decisions Needed

Before implementation, the following design decisions should be made:

1. **Archive format priority:** Which non-ZIP formats are most needed? 7z is almost certainly #1 for game modding.

2. **Staging lifetime:** How long to keep staged files? Options: delete after commit, keep for N days, keep until space pressure.

3. **Atomicity level:** Full directory swap (heavy but clean) vs. file-level journaled replace (lighter, more compatible)?

4. **Blob store:** Full content-addressed storage (enables deduplication) vs. simpler hash-and-verify (lower complexity)?

5. **Concurrency model:** Single-installation serialization (simplest) vs. per-path locking (enables parallel installs to different game directories)?

6. **OverlayFS consideration:** Is Linux kernel OverlayFS viable for the target user base, or should the system stay purely userspace?

---

## 6. Conclusion

The Modular codebase has a **strong foundation** in several areas that align with the reference architecture: dependency graphs with topological sorting, file conflict detection, download integrity verification, persistent queuing, and plugin extensibility.

The primary gaps are in the **installation pipeline**: no staging phase, no atomic commit, ZIP-only archive support, and minimal extraction security. These are the areas where the reference architecture provides the most actionable guidance.

The existing `DependencyGraph`, `FileConflictIndex`, `ConflictResolver`, and `InstallerManager` classes provide natural extension points — the reference architecture can be implemented incrementally by adding staging to the installer workflow, broadening archive format support, and enriching the database schema with change sets and path versioning.
