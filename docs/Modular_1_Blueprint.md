
# Blueprint for Evolving Modular-1 into a Next-Generation Mod Manager

## Repository baseline and current capabilities

Modular-1 is organised as a multi-project C#/.NET solution with a clear separation between core logic, CLI, GUI, and a custom fluent HTTP layer. The solution file includes projects for a core library, a fluent HTTP library, CLI, GUI, and test projects. 

The README describes Modular-1 as a cross-platform mod download/organisation tool with both CLI and an Avalonia-based GUI, built around a layered architecture and a “modern fluent HTTP client API,” retry/rate-limiting, and progress reporting. The same README documents end-to-end flows already implemented: multi-repository support (currently NexusMods and GameBanana), automatic folder organisation, optional MD5 verification, and a persistent download history and rate-limit state.

At the code level, Modular-1 has already taken an important “next-gen” step: a backend abstraction that normalises different mod sources under a shared interface and capability flags. `IModBackend` defines core operations (list user mods, list files, resolve download URL, bulk download, mod info), and `BackendRegistry` supports runtime selection of configured backends. `BackendCapabilities` is a feature flag set that the CLI and GUI can use to adapt UX and behaviour based on what a backend supports (game...

Two concrete backend implementations exist:
- A NexusMods backend that uses the public API for tracked mods, file listings, and download-link resolution and persists both download history and metadata cache. It declares capabilities including game domains, file categories, MD5 verification, and rate limiting.
- A GameBanana backend that uses the “apiv11” endpoints, and implements courtesy throttling with a 500ms delay between requests; it does not currently record download history in the core database (it uses a simple “file exists” check in the shown implementation).

Rate limiting is implemented as a pluggable interface (`IRateLimiter`) and a Nexus-specific implementation that parses Nexus response headers (daily/hourly remaining and reset timestamps), maintains a thread-safe state, and persists it to disk. The README also notes the project’s intent to comply with Nexus rate limits (20,000 requests/day; 500/hour).

Configuration is managed via a JSON config file in the user profile (default `~/.config/Modular/config.json`) with environment variable overrides; default paths for downloads JSON DB, rate-limit state, and metadata cache are also under `~/.config/Modular/`.

The GUI uses DI, MVVM, and view models that already expose key workflows: browse tracked mods per domain, check for updates by comparing downloaded records against latest file lists, queue downloads (currently simulated rather than executing a real streaming download), browse the on-disk library, and update settings. The GUI uses Avalonia data binding and MVVM conventions, which are a good fit for progressive, reactive UI updates once background tasks and event streams are formalised.

A key operational constraint to design around is Nexus’s policies and behaviour: Nexus’s own help documentation confirms rate limiting at 20,000 requests per 24 hours and (after that) 500 per hour. Nexus’s acceptable-use policy also makes it clear that public-facing applications should be registered, not built around personal keys beyond personal/testing use. Additionally, Nexus download-link retrieval via the API has historically been restricted for non-premium users, with “permission to get download li...

## Plugin and extension architecture

### Recommended approach

Because Modular-1 already has a backend abstraction and a registry, the highest-leverage “next-gen” extension point is to turn “compile-time registered backends” into “runtime discovered plugins” that can ship independently. The same mechanism can be generalised to other extension types (metadata enrichers, installers, rule engines, UI panels, workflow steps) while keeping the core stable.

A robust in-process plugin system in modern .NET typically needs three building blocks:

- **Dynamic loading + isolation**: `AssemblyLoadContext` exists specifically to support loading and unloading assemblies in isolated contexts in .NET (including collectible contexts for unloading), and is the modern replacement for older AppDomain-based patterns (since .NET Core supports a single application domain).

- **Discovery + composition**: The Managed Extensibility Framework (MEF) is the established .NET approach for building composable applications with discoverable parts (exports/imports) and metadata; MEF 2 is available via `System.Composition` as a lightweight, attribute-driven composition system.

- **A stable host contract**: Plugins must reference a small “Modular SDK” assembly containing only interfaces, records, and version-tolerant contracts. Your existing `IModBackend`, `BackendMod`, `BackendModFile`, `FileFilter`, and `BackendCapabilities` are already close to that.

### Patterns to evaluate, with pros/cons

**MEF-based composition (in-process)**: MEF can discover parts in plugin assemblies and wire up imports/exports (including metadata per export) which fits well for “declare extension points; load at runtime.”  
Risk: MEF in .NET Framework and MEF 2 (`System.Composition`) differ; you need to standardise the runtime and packaging story (net8.0+ suggests you can stay modern).

**Reflection + DI registration (in-process)**: Scan plugin assemblies for known interfaces/attributes, then call a plugin-provided registration method to add services into a DI container. This aligns with how the GUI uses DI today.  
Risk: version conflicts (plugin dependency graphs) and load/unload reliability can become painful unless you isolate each plugin in its own `AssemblyLoadContext` and strictly control host ↔ plugin references.

**Out-of-process plugins (“micro-extensions”)**: Run plugins as separate processes that communicate over gRPC/HTTP/stdio. This is the most robust isolation boundary for security and dependency conflicts, and enables cross-language scripting. Trade-off: higher complexity, need a local RPC protocol and process supervision.

### Extension points to formalise

The key is to define explicit extension contracts rather than letting plugins reach into internal services. A practical split:

- **Source/backends**: Promote `IModBackend` as the canonical backend/plugin interface; keep “capabilities” as the feature negotiation mechanism for UI and automation.

- **Metadata transforms**: Add an interface such as `IMetadataEnricher` that can:
  - map backend-native fields into the canonical schema
  - infer dependencies (when the source doesn’t provide them)
  - attach install instructions (FOMOD, BepInEx, etc.)

This mirrors the project’s existing metadata cache pattern (reduce calls via persisted cache) but makes it pluggable and multi-source.

- **Installers + workflows**: Mod managers typically need install-time logic (archive layout detection, patching, option selection). Nexus’s own developer docs for FOMOD highlight that many mods ship with installer logic and varying archive structures (including nested archives), which strongly suggests install should be a first-class pipeline stage rather than a “post download rename” step.

- **UI widgets/panels**: The current GUI is MVVM-driven and page-based (NexusMods/GameBanana/Downloads/Library/Settings), so a safe extension point is “register a page” by providing a view model + a view factory (or a declarative UI model).

### Plugin packaging and distribution

A “community-ready” ecosystem typically needs a safe, discoverable plugin marketplace mechanism. A proven pattern in the mod-manager world is shipping a signed “extensions manifest” hosted by the vendor and consumed by the app; the Vortex backend repository describes an `extensions-manifest.json` that Vortex uses to parameterise and discover extensions.  
For Modular-1, an analogous approach is:

- A core-managed “plugin index” (JSON) that points to plugin packages, versions, hashes, and minimum host version.
- Local plugin packages stored under `~/.config/Modular/plugins/` (or similar), keeping consistent with your existing config and state locations.

## Dependency modelling and conflict resolution

### What “dependency management” must cover for mods

Unlike language package managers, mods introduce at least three distinct “dependency axes”:

- **Hard requirements**: “mod A requires mod B (and version constraints)”
- **Load order / precedence rules**: for systems where the last writer wins (loose file overrides, plugin order)
- **File conflicts**: two mods ship the same file path; a manager must pick precedence or merge

Your current model is “tracked mods → files → download,” with update checks based on file recency, and organisation/renaming based on cached mod metadata. There is no explicit dependency graph yet, so “next-gen dependency management” is a new core subsystem.

### Algorithms and structures worth borrowing

**Semantic versioning and ranges**: If Modular-1 intends to express mod version constraints, adopting Semantic Versioning concepts and common range syntax is the most interoperable baseline. SemVer 2.0.0 defines the core versioning rules, and npm’s documentation clearly describes comparator/range semantics that users recognise.

**NuGet-like resolution rules**: NuGet documents dependency resolution as a set of predictable rules (lowest applicable version, floating versions, direct-dependency-wins, cousin dependencies). Even if you don’t copy NuGet’s exact semantics, the documentation is valuable because it explains how to keep resolution deterministic and explainable.

**Cargo-style lockfile approach**: Cargo’s resolver builds dependency graphs with features logic and produces a lock file that pins resolved versions. That model maps well to mod “profiles” and reproducible modlists.

**PubGrub for human-grade conflict explanations**: PubGrub is a modern version solving algorithm designed to be fast and produce clear error messages; it is widely referenced as “next-generation version solving.” A concrete advantage for a mod manager is surfacing conflicts as “because A requires B>=x and C requires B<y, there is no solution,” rather than “resolution failed.”

### Proposed canonical dependency graph model

To support both “package-style” dependency constraints and “mod-style” conflicts, represent the system as a labelled directed multigraph:

- Node = `(mod identity, version)` (or “versionless” for sources without versioning)
- Edge types:
  - `requires` with constraint set
  - `optional` with constraint
  - `incompatible/conflicts` (hard negative edge)
  - `embedded` (bundled dependency; often not separately installed)

This aligns directly with Modrinth’s dependency types for versions (required/optional/incompatible/embedded), which is useful both for interoperability and for UI terminology.

On top of the graph, you’ll need two additional data sets:

- **File-level conflict index**: mapping from target game path → list of providers (mods/files); computed after download+install staging.
- **Rule constraints**: environment constraints (game version, loader, platform) similar to “features” and “target constraints” in package ecosystems.

### Conflict detection and automatic remediation

A “robust automation” goal implies three outputs:

- **Detect**: produce a minimal conflicting set (ideally PubGrub-style explanations for version constraints; and “conflict sets” for file path collisions).
- **Suggest**: propose resolutions (upgrade/downgrade, replace with alternative, adjust precedence)
- **Apply**: update the plan and lockfile/profile

## Unified metadata schema and API gateway

### Why a canonical schema matters in Modular-1 specifically

Right now, backend implementations map source-specific fields into a common `BackendMod`/`BackendModFile` representation (IDs, name, author, summary, update time, thumbnail URL, file MD5, category, upload time, direct URL vs resolved URL). This is a strong base, but “next-gen” features need more:

- dependency metadata and constraints
- changelogs/release notes
- install instructions and supported game/loader versions
- provenance and trust (signatures, hashes, source IDs)
- community signals (endorsements, downloads, ratings) where available

Modrinth is an example of an API that already exposes “dependencies” as a first-class concept (both via version objects and a dedicated dependencies endpoint).

### Proposed canonical schema

A practical schema should separate **project-level identity** from **version/file-level artefacts**, and treat dependencies as versioned relationships when possible. Below is a schema draft that stays compatible with your existing types but adds what dependency management and automation need.

```json
{
  "mod": {
    "canonical_id": "string (host-generated stable id)",
    "source": { "backend_id": "nexusmods|gamebanana|modrinth|...", "project_id": "string", "slug": "string|null", "url": "string|null" },
    "name": "string",
    "summary": "string|null",
    "authors": [{ "name": "string", "id": "string|null" }],
    "tags": ["string"],
    "game": { "id": "string|null", "domain": "string|null", "name": "string|null" },
    "categories": [{ "id": "string|int|null", "name": "string|null" }],
    "assets": { "thumbnail_url": "string|null" },
    "timestamps": { "updated_at": "iso8601|null", "published_at": "iso8601|null" }
  },
  "versions": [
    {
      "version_id": "string (source version id if present)",
      "version_number": "string|null",
      "release_channel": "stable|beta|alpha|null",
      "changelog": "string|null",
      "dependencies": [
        { "type": "required|optional|incompatible|embedded", "target": { "project_id": "string", "version_id": "string|null" }, "constraint": "string|null" }
      ],
      "files": [
        {
          "file_id": "string",
          "file_name": "string",
          "size_bytes": "int|null",
          "hashes": { "md5": "string|null", "sha256": "string|null" },
          "uploaded_at": "iso8601|null",
          "download": { "direct_url": "string|null", "requires_resolution": "bool" }
        }
      ],
      "install": { "format": "fomod|loose|plugin|unknown", "instructions": "object|null" }
    }
  ]
}
```

### API gateway abstractions

Your backend interface already pushes toward an “API-gateway-like” pattern: a uniform query surface with per-backend feature flags and a shared progress model. To make this “robust” across many sources, formalise an internal gateway layer with:

- **Auth strategies**: key-based, OAuth2, and SSO/websocket flows must be supported per backend.
- **Pagination + backoff**: even your GameBanana backend implements courteous throttling and paging logic, indicating that the gateway should have a first-class “request budget + scheduling” concept.
- **Rate-limit awareness**: Nexus provides rate limit headers that you already parse and persist, and Nexus’s own help centre explains the limits and their purpose.
- **Unified search**: GameBanana already implements `SearchModsAsync`, and Modrinth has a rich query API; putting this behind a shared gateway allows a cross-source catalogue UI.

## Synchronization, updates, and download pipeline

### Update detection: polling, conditional requests, and deltas

Your current update checking in the GUI is essentially “poll latest file listing; compare uploaded time or file ID against the latest downloaded record.” That works, but it scales poorly across many sources without a smarter caching contract.

A next-gen approach layers three mechanisms:

- **Conditional requests (ETag / If-None-Match)**.
- **Range requests for resumable downloads**.
- **Delta / manifest strategies**.

### Rate-limit aware scheduling

Nexus rate limiting is now both documented and implemented in your code using response headers, with persisted state between sessions. This should evolve into a multi-backend scheduler with:

- a per-backend “budget” (daily/hourly tokens, concurrent request limits)
- prioritisation (UI actions > background sync > prefetch thumbnails)
- backoff and circuit breaking for failing endpoints

### Making the GUI download queue real

The GUI’s `DownloadQueueViewModel` currently simulates progress (increments 0–100%) after resolving a download URL. Converting this to a production-grade download engine requires:

- streaming download with byte-level progress callbacks.
- durable queue state (persist pending/completed; restart-safe).
- resumable downloads (Range requests when supported).
- per-backend authentication and URL-expiry handling.

### UI/UX workflows for dependency graphs and conflict resolution

Once you introduce a dependency solver, the UI must visualise:

- dependency graph (with “why” explanations)
- conflict sets (version constraints and file collisions)
- resolution choices (change version, pick alternative, set precedence)
