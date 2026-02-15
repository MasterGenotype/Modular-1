# Conceptual Replacements Analysis

A strategic review of areas where fundamentally different approaches would yield better outcomes — covering framework selection, architectural patterns, project orchestration, and design limitations that compound over time.

This document complements the existing [Architecture Analysis](ARCHITECTURE_ANALYSIS.md) and [Codebase Review Recommendations](CODEBASE_REVIEW_RECOMMENDATIONS.md) by focusing on **conceptual alternatives** rather than individual bugs or tactical fixes.

---

## 1. Data Layer: JSON Files → Embedded Database

### Current Approach

Five independent JSON file stores, each implementing their own load/save/lock pattern:

| Store | File | Locking | Purpose |
|-------|------|---------|---------|
| `DownloadDatabase` | `downloads.json` | `lock (_lock)` | Download history |
| `ModMetadataCache` | `metadata_cache.json` | `lock (_lock)` | API response cache |
| `NexusRateLimiter` | state file | `lock (_lock)` | Rate limit counters |
| `DownloadHistoryService` | `download_history.json` | `lock (_lock)` | Download statistics |
| `ConfigurationService` | `config.json` | none | Application settings |

Each store independently:
- Deserializes the entire file into memory on load
- Serializes the entire object graph on every save
- Uses coarse-grained `lock` for thread safety
- Has no transactional guarantees (crash between write and flush = corruption)
- Performs O(n) linear scans for queries

### Why This Is a Conceptual Problem

This isn't just a performance issue — it's a **data integrity architecture** problem. The application manages downloads (financial-equivalent in the gaming context: users pay for premium API access), and there is no crash recovery, no atomicity, and no way to answer "what happened?" after a failure. Five independent stores also means five independent failure modes and no cross-store consistency.

### Recommended Replacement: SQLite via `Microsoft.Data.Sqlite`

SQLite is the standard embedded database for exactly this use case. A single `modular.db` file replaces all five JSON stores with:

- **ACID transactions**: Download record + history update + cache update in one atomic operation
- **WAL mode**: Concurrent readers with a single writer, no application-level locking needed
- **Indexed queries**: `CREATE INDEX idx_downloads_domain ON downloads(game_domain, mod_id)` replaces O(n) scans
- **Schema migrations**: Versioned schema changes instead of hoping JSON shape doesn't drift
- **Built-in integrity**: `PRAGMA integrity_check` detects corruption; WAL journaling prevents it

**What changes:**
- `DownloadDatabase`, `ModMetadataCache`, `DownloadHistoryService`, and rate limiter state all become tables in one database
- The five separate lock objects become SQLite's internal locking
- `SaveAsync()` full-file rewrites become `INSERT`/`UPDATE` statements
- Linear scans become indexed queries

**What stays the same:**
- `ConfigurationService` can remain JSON — it's a single small document with infrequent writes, and human-editability is valuable

**Migration path:** Extract `IDownloadRepository` and `IMetadataCache` interfaces first (as noted in existing Architecture Analysis #11), then swap the implementation behind those interfaces.

---

## 2. State Management: Scattered ViewModels → Centralized Event Bus

### Current Approach

Six ViewModels each maintain their own state independently. Cross-ViewModel communication happens through direct references held by `MainWindowViewModel`:

```
MainWindowViewModel
├── ModListViewModel         (holds mod list state)
├── GameBananaViewModel      (holds GameBanana state)
├── DownloadQueueViewModel   (holds download state)
├── LibraryViewModel         (holds library state)
├── SettingsViewModel        (holds settings state)
└── PluginsViewModel         (holds plugin state)
```

`MainWindowViewModel` orchestrates by reaching into child ViewModels directly:

- `ModListViewModel.GetSelectedMods()` → feeds into `DownloadQueueViewModel.EnqueueManyAsync()`
- `DownloadQueueViewModel` completes a download → updates `DownloadHistoryService` → but `LibraryViewModel` doesn't know
- Settings changes in `SettingsViewModel` → saved to disk → but running backends don't pick up new API keys without restart

### Why This Is a Conceptual Problem

The MVVM pattern handles View↔ViewModel binding well, but provides **no mechanism for ViewModel↔ViewModel communication**. The current solution (parent ViewModel holds references to all children and orchestrates directly) creates a hub-and-spoke coupling where `MainWindowViewModel` must know the internal details of every child ViewModel. This doesn't scale — every new feature that crosses ViewModel boundaries requires modifying the parent.

Common symptoms in the codebase:
- `DownloadQueueViewModel` is 491 lines because it contains download orchestration, HTTP logic, file I/O, and history tracking — responsibilities that should be distributed
- `MainWindowViewModel` contains type-checking switch statements to determine which child to refresh
- No ViewModel can react to events from another without the parent mediating

### Recommended Replacement: Pub/Sub Event Aggregator

Add a lightweight message bus that ViewModels publish to and subscribe from, decoupling them from direct references:

```
┌─────────────────────────────────────────────────┐
│              IEventAggregator                    │
│  (singleton, registered in DI container)         │
├─────────────────────────────────────────────────┤
│  Publish<DownloadCompletedEvent>(event)          │
│  Subscribe<DownloadCompletedEvent>(handler)      │
│  Subscribe<SettingsChangedEvent>(handler)        │
│  Subscribe<ModSelectedEvent>(handler)            │
└─────────────────────────────────────────────────┘
     ↑              ↑              ↑
     │              │              │
DownloadQueue   LibraryVM     SettingsVM
  publishes      subscribes    publishes
```

**Options:**
- **CommunityToolkit.Mvvm `WeakReferenceMessenger`** — already available since CommunityToolkit.Mvvm is a dependency. This is the zero-cost option: `WeakReferenceMessenger.Default.Send(new DownloadCompletedMessage(...))`. No new packages needed.
- **Prism `EventAggregator`** — more features (filtering, thread marshalling), but adds a dependency
- **Custom `IEventAggregator`** — ~50 lines of code for basic pub/sub if minimal footprint is desired

The CommunityToolkit option is the clear winner since the dependency already exists.

---

## 3. HTTP Architecture: Three Clients → One Strategy

### Current Approach

Three independent HTTP implementations coexist:

1. **`Modular.FluentHttp`** (separate project, ~700 LOC) — fluent builder with filters, used by some backend calls
2. **`Modular.Core.Http.ModularHttpClient`** (~230 LOC) — wrapper with retry/rate limiting, used by other backend calls
3. **Raw `HttpClient`** in `DownloadEngine` and `DownloadQueueViewModel` — direct usage, no shared policies

Each implements its own retry logic, timeout handling, and rate limiting — differently.

### Why This Is a Conceptual Problem

This isn't about which HTTP library is "best." The problem is that **HTTP resilience policy** (retry, circuit breaking, rate limiting, timeout) is an application-level concern that should be defined once and applied consistently. Three implementations means:

- A rate limit fix in `FluentHttp` doesn't apply to `DownloadEngine`
- A retry policy change in `ModularHttpClient` doesn't affect `FluentHttp`
- `DownloadEngine` creates `new HttpClient()` in its constructor, leaking sockets (a well-known .NET anti-pattern)
- There's no circuit breaker anywhere — if NexusMods is down, every request across all three clients independently retries and times out

### Recommended Replacement: `IHttpClientFactory` + Polly

This is the standard .NET approach and eliminates all three custom implementations:

```csharp
// One place to define all HTTP policies
services.AddHttpClient("nexusmods", client =>
{
    client.BaseAddress = new Uri("https://api.nexusmods.com/");
    client.DefaultRequestHeaders.Add("apikey", settings.NexusApiKey);
})
.AddPolicyHandler(Policy.WaitAndRetryAsync(3, retryAttempt =>
    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))))
.AddPolicyHandler(Policy.CircuitBreakerAsync(5, TimeSpan.FromMinutes(1)));

services.AddHttpClient("gamebanana", client =>
{
    client.BaseAddress = new Uri("https://gamebanana.com/apiv11/");
})
.AddPolicyHandler(Policy.WaitAndRetryAsync(2, _ => TimeSpan.FromSeconds(1)));

services.AddHttpClient("downloads")
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All
});
```

**What this eliminates:**
- The entire `Modular.FluentHttp` project (~700 LOC)
- `ModularHttpClient` (~230 LOC)
- Manual `HttpClient` construction in `DownloadEngine`
- All custom retry implementations
- Socket exhaustion issues (connection pooling is automatic)
- DNS rotation issues (handler lifetime is managed)

**What this adds:**
- Circuit breakers (automatic failure isolation per backend)
- Bulkhead isolation (limit concurrent requests per backend)
- Centralized telemetry hooks
- Named clients resolvable via DI

The custom `NexusRateLimiter` would remain as a Polly policy or delegating handler — it has domain-specific logic (parsing NexusMods rate limit headers) that a generic policy can't provide.

---

## 4. Plugin Isolation: MEF + AssemblyLoadContext → Process-Level Isolation

### Current Approach

Plugins load into isolated `AssemblyLoadContext` instances but share the same process:

- **Assembly isolation**: Plugin DLLs load in separate contexts, preventing version conflicts for shared dependencies
- **No execution isolation**: Plugins run on the same thread pool, access the same memory space, and can call any .NET API
- **No capability restriction**: A plugin can access the filesystem, network, and other plugins' state via reflection
- **MEF composition coexists with manual reflection**: `PluginComposer` uses MEF `[Export]`/`[Import]` attributes, but `PluginLoader` does its own `Assembly.GetTypes()` discovery — two parallel discovery mechanisms

### Why This Is a Conceptual Problem

`AssemblyLoadContext` provides **version isolation**, not **security isolation**. A plugin can:
- Read/write any file the application can access
- Make arbitrary network requests
- Access other plugins' loaded assemblies via reflection
- Crash the entire application with an unhandled exception in a thread pool work item
- Consume unbounded memory or CPU

For a mod manager that downloads and executes community-contributed plugins, this is a meaningful trust boundary gap. The `ErrorBoundary` class catches exceptions in synchronous plugin calls, but can't protect against:
- Background thread crashes
- Resource exhaustion
- Intentional data exfiltration

### Recommended Replacement: Tiered Isolation

Rather than one-size-fits-all, implement isolation proportional to trust:

**Tier 1 — Trusted (built-in backends):** Run in-process, current approach is fine. NexusMods and GameBanana backends are first-party code.

**Tier 2 — Semi-trusted (community plugins from marketplace):** Run in-process but with a capability-based service provider:
```csharp
// Plugin receives only the services it declares in manifest
var pluginServices = new ServiceCollection();
pluginServices.AddSingleton(pluginLogger);          // Always provided
pluginServices.AddSingleton(pluginSettings);         // Scoped to plugin
if (manifest.Capabilities.Contains("network"))
    pluginServices.AddSingleton<IFluentClient>(scopedClient);
if (manifest.Capabilities.Contains("filesystem"))
    pluginServices.AddSingleton<IFileService>(sandboxedFileService);
```

**Tier 3 — Untrusted (sideloaded DLLs):** Run in a separate process with IPC. This is the only way to truly protect against crashes, resource exhaustion, and malicious code. Communication via named pipes or gRPC.

This tiered approach is what VS Code uses (extensions run in a separate `ExtensionHost` process) and what browsers use (each tab in a separate process).

### Also: Consolidate Discovery

Pick one plugin discovery mechanism. Since `PluginLoader` already does reflection-based discovery and `PluginComposer`/MEF is redundant, remove the MEF dependency (`System.Composition`) and standardize on the reflection approach — but replace `Activator.CreateInstance()` with `ActivatorUtilities.CreateInstance()` to enable constructor injection.

---

## 5. Build System: GNU Makefile → .NET-Native Build Tool

### Current Approach

A 215-line GNU Makefile with targets for build, test, publish, install, plugin management, and desktop integration.

### Why This Is a Conceptual Problem

The project targets Windows, macOS, and Linux. The Makefile only runs on Unix-like systems (or Windows with WSL/Cygwin/MSYS2). This means:
- Windows contributors can't use the build system without additional tooling
- CI/CD on Windows runners must either install Make or duplicate the build logic in workflow YAML
- The Makefile uses bash-specific constructs (`mkdir -p`, `ln -sf`, `~/.local/bin`) that have no Windows equivalents

### Recommended Replacement: Nuke Build

Nuke is a .NET-native build automation tool where build logic is written in C#:

```csharp
class Build : NukeBuild
{
    [Parameter] readonly Configuration Configuration = Configuration.Debug;

    Target Clean => _ => _
        .Executes(() => DotNetClean(s => s.SetProject(Solution)));

    Target Compile => _ => _
        .DependsOn(Clean)
        .Executes(() => DotNetBuild(s => s
            .SetProjectFile(Solution)
            .SetConfiguration(Configuration)));

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() => DotNetTest(s => s
            .SetProjectFile(Solution)
            .SetConfiguration(Configuration)));

    Target PublishCli => _ => _
        .DependsOn(Test)
        .Executes(() => DotNetPublish(s => s
            .SetProject(CliProject)
            .SetConfiguration(Configuration.Release)
            .SetRuntime(Runtime)
            .SetSelfContained(true)
            .SetPublishSingleFile(true)));
}
```

**Advantages over Make:**
- Cross-platform by default (runs anywhere .NET runs)
- Type-safe build parameters
- IDE support (completion, debugging, refactoring)
- First-class `dotnet` CLI integration
- Can express the same dependency graph (Clean → Build → Test → Publish)

**Alternative:** CAKE (similar concept, DSL-based instead of pure C#). Either works; Nuke has more momentum in the .NET ecosystem currently.

---

## 6. CLI Architecture: Procedural God-Class → Command Pattern

### Current Approach

`Modular.Cli/Program.cs` is 689 lines containing:
- All command definitions (`RootCommand`, `Command`, `Option`, `Argument`)
- All handler implementations (static methods)
- Service initialization (two variants: `InitializeServices` and `InitializeServicesMinimal`)
- Backend configuration and wiring
- Output formatting
- CancellationToken management (duplicated per handler)

### Why This Is a Conceptual Problem

The CLI layer violates every SOLID principle simultaneously:
- **S**: Single class does everything — parsing, orchestration, DI, formatting
- **O**: Adding a new command requires modifying the existing `Program.cs`
- **L**: N/A (no inheritance)
- **I**: Everything depends on everything
- **D**: Concrete service construction inline, no abstractions

More practically: the CLI is **untestable**. All methods are static, services are constructed inline, and there's no way to inject mocks. This means the most user-facing layer of the application has zero test coverage and can't gain any without refactoring.

### Recommended Replacement: Spectre.Console.Cli

Spectre.Console is already a dependency. Its CLI module (`Spectre.Console.Cli`) provides a command pattern where each command is a separate class with injected dependencies:

```csharp
// Each command in its own file
public class DownloadCommand : AsyncCommand<DownloadCommand.Settings>
{
    private readonly IModBackend _backend;
    private readonly DownloadEngine _engine;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<mod-id>")] public int ModId { get; set; }
        [CommandOption("--backend")] public string Backend { get; set; } = "nexusmods";
        [CommandOption("--dry-run")] public bool DryRun { get; set; }
    }

    public DownloadCommand(IModBackend backend, DownloadEngine engine)
    {
        _backend = backend;
        _engine = engine;
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        // Focused command logic, testable with mock backend/engine
    }
}
```

This also replaces `System.CommandLine 2.0.0-beta4` (a pre-release dependency that has been in beta since 2019 with breaking API changes between previews) with a stable library from the same ecosystem already in use.

**Impact:**
- 689-line god-class splits into ~10-15 focused command classes
- Each command is independently testable
- `System.CommandLine` pre-release dependency removed
- Constructor injection replaces static service creation
- Shared concerns (cancellation, error handling, state persistence) move to a base class or interceptor

---

## 7. Dependency Resolution: Greedy Resolver → Real Backtracking Solver

### Current Approach

`PubGrubResolver` is named after the PubGrub algorithm but implements a greedy BFS: it picks the latest version satisfying constraints at each step and never backtracks.

### Why This Is a Conceptual Problem

A greedy resolver fails when:
1. Mod A requires Mod C >= 2.0
2. Mod B requires Mod C < 2.0
3. An older version of Mod A exists that works with Mod C 1.x

The greedy resolver picks latest Mod A, then fails on the Mod C conflict. A backtracking solver would try the older Mod A and find a valid solution.

In the mod ecosystem, this scenario is common: mods frequently have tight version constraints on shared dependencies (game frameworks like BepInEx, SMAPI, etc.).

### Recommended Replacement

**Option A — Implement actual PubGrub:** The algorithm is well-documented ([PubGrub spec](https://github.com/dart-lang/pub/blob/master/doc/solver.md)) and has .NET implementations available. This provides optimal conflict-driven learning with human-readable error messages.

**Option B — Use `NuGet.Versioning` + `NuGet.Resolver`:** NuGet's resolver is battle-tested and handles the exact same problem space (versioned package dependencies with constraints). The `NuGet.Versioning` library also replaces the custom `SemanticVersion` and `VersionRange` implementations.

**Option C — Rename to `GreedyResolver`:** If backtracking isn't worth the complexity, rename the class to set accurate expectations and document the limitation. Users will at least understand why resolution fails when a solution exists.

---

## 8. Authentication: Bare API Keys → OS Credential Storage

### Current Approach

API keys are stored as plaintext strings in `~/.config/Modular/config.json`:

```json
{
    "nexus_api_key": "actual-api-key-in-plaintext",
    "gamebanana_user_id": "12345"
}
```

The NexusMods SSO flow (`NexusSsoClient.cs`) receives the API key via WebSocket and returns it as a bare string. The caller stores it directly in the config file.

### Why This Is a Conceptual Problem

API keys in plaintext config files are:
- Readable by any process running as the same user
- Included in backups unless specifically excluded
- Visible in file manager previews
- Potentially committed to version control if the config directory is in a repo
- Not protected by OS-level credential isolation

For NexusMods specifically, premium API keys grant elevated rate limits and access to premium-only download servers. Key theft is a real concern in the modding community.

### Recommended Replacement: OS Keychain Integration

Use the operating system's credential manager:

| OS | Store | .NET Access |
|----|-------|------------|
| Windows | Windows Credential Manager | `System.Security.Cryptography.ProtectedData` or `Windows.Security.Credentials.PasswordVault` |
| macOS | Keychain | `Security` framework via P/Invoke or `KeychainAccess` |
| Linux | libsecret (GNOME Keyring / KDE Wallet) | `Tmds.DBus` to call Secret Service API |

**Cross-platform wrapper:** The [`Microsoft.Extensions.SecretManager`](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) pattern or the [`StrongBox`](https://github.com/nickvdyck/strongbox) library can abstract across platforms.

**Fallback:** If no credential manager is available (headless Linux, containers), fall back to the current config file approach with a warning at startup.

The config file continues to store non-sensitive settings (mod directory, UI preferences, backend selection). Only secrets (API keys, tokens) move to the credential manager.

---

## 9. GUI ViewModel Responsibilities: Fat ViewModels → Service Extraction

### Current Approach

`DownloadQueueViewModel` (491 lines) is the most egregious example, containing:
- Download queue management (enqueue, dequeue, cancel)
- HTTP download execution with streaming and progress
- File I/O (writing downloaded files to disk)
- Download history recording
- Mod file reorganization (renaming/moving downloaded files)
- Error handling and retry logic
- UI state management (progress bars, status messages)

Other ViewModels have similar scope creep, totaling ~2,000 lines across 6 ViewModels.

### Why This Is a Conceptual Problem

ViewModels should translate between **domain state** and **view state**. They should not contain business logic, I/O operations, or orchestration. When they do:
- Testing requires mocking HTTP, filesystem, and UI simultaneously
- Business logic changes require touching UI-layer code
- The same download logic can't be reused in the CLI without duplicating it
- Error handling is inconsistent because each ViewModel implements its own

### Recommended Replacement: Extract Domain Services

```
Before (current):
  DownloadQueueViewModel
    ├── manages queue state (ViewModel concern ✓)
    ├── executes HTTP downloads (service concern ✗)
    ├── writes files to disk (service concern ✗)
    ├── records download history (service concern ✗)
    └── reorganizes mod files (service concern ✗)

After (proposed):
  IDownloadOrchestrator          (new service in Modular.Core)
    ├── manages download lifecycle
    ├── delegates to DownloadEngine for HTTP
    ├── delegates to IDownloadRepository for history
    └── emits events for progress/completion

  DownloadQueueViewModel         (reduced to ~150 lines)
    ├── subscribes to orchestrator events
    ├── translates events to ObservableCollection updates
    └── exposes commands that delegate to orchestrator
```

This service extraction also enables the CLI to share the same download orchestration logic — currently the CLI and GUI have separate download implementations.

---

## 10. Test Architecture: Minimal Coverage → Structured Test Strategy

### Current State

7 test files covering ~65 tests, focused on:
- Configuration serialization
- Database CRUD operations
- Utility functions
- Backend response deserialization
- Backend registry operations
- Basic FluentHttp client configuration

### What's Missing

| Category | Current Coverage | Gap |
|----------|-----------------|-----|
| Core business logic (downloads, resolution) | 0% | Critical |
| CLI commands | 0% | High |
| GUI ViewModels | 0% | High |
| Integration (multi-component workflows) | 0% | High |
| Plugin loading/lifecycle | 0% | Medium |
| Authentication flow | 0% | Medium |
| Performance/load | 0% | Low |

### Recommended Test Architecture

**Layer 1 — Unit Tests (target: 80% of critical paths)**
- Every service method with business logic
- Every ViewModel command
- Dependency resolver with known conflict scenarios
- Plugin manifest validation
- Rate limiter state transitions

**Layer 2 — Integration Tests (target: key workflows)**
- Download pipeline: mock HTTP server → DownloadEngine → file on disk → history recorded
- Plugin lifecycle: load → discover extensions → compose → unload
- Configuration: load → modify → save → reload → verify
- Backend: authenticate → list mods → download → verify

**Layer 3 — Architecture Tests (using `NetArchTest` or `ArchUnitNET`)**
- ViewModels should not reference `System.Net.Http` directly
- Core should not reference GUI
- SDK should have zero dependencies on Core implementation types
- All services should implement interfaces

**Testing libraries already in use** (xUnit, FluentAssertions, Moq) are appropriate. Consider adding:
- `WireMock.Net` for HTTP integration tests (mock server)
- `Verify` for snapshot testing of serialized outputs
- `Avalonia.Headless` for ViewModel testing with the Avalonia test framework

---

## 11. Project Orchestration: Solution-Only → Formalized Dependency Boundaries

### Current Approach

The Visual Studio solution (`Modular.sln`) defines five projects with implicit dependency relationships. The dependency graph is:

```
Modular.Gui ──→ Modular.Core ──→ Modular.Sdk
Modular.Cli ──→ Modular.Core ──→ Modular.FluentHttp
                                  Modular.Sdk (no deps)
```

### Why This Is a Conceptual Problem

The boundaries between projects are not enforced beyond compilation. Specifically:
- `Modular.Core` contains everything from plugin loading to HTTP clients to database access to authentication — it's a monolith within the "layered" architecture
- `Modular.Sdk` defines plugin contracts but has no mechanism to verify that Core actually implements them correctly (no contract tests)
- `Modular.FluentHttp` is a separate project but could be replaced entirely (see #3) — its separation adds build complexity without proportional benefit

### Recommended Improvements

**Short-term: Split `Modular.Core` along domain boundaries**

`Modular.Core` is ~63 files handling unrelated concerns. A more natural split:

```
Modular.Core.Abstractions    → Interfaces, DTOs, events (no implementation)
Modular.Core.Backends        → Backend implementations, API clients
Modular.Core.Downloads       → Download engine, queue, history
Modular.Core.Plugins         → Plugin loading, discovery, lifecycle
Modular.Core.Data            → Database, cache, configuration persistence
```

This split enables:
- Independent testing of each domain
- Parallel development without merge conflicts in a monolithic Core
- Clear ownership of each concern
- Backends can depend on Abstractions without pulling in Plugins or Data

**Long-term: Architecture Decision Records (ADRs)**

The `/docs/` directory has 24 files but no ADRs. When a decision like "use JSON instead of SQLite" or "build custom HTTP client" is made, it should be documented with context, alternatives considered, and rationale. This prevents future contributors from re-litigating settled decisions or making changes that violate implicit assumptions.

---

## 12. .NET Version: 8.0 (LTS) → Consider .NET 9 or Wait for 10

### Current Approach

All projects target `net8.0` (.NET 8 LTS, supported until November 2026).

### Assessment

Staying on .NET 8 is **reasonable** — it's an LTS release with 10 months of remaining support. However, two upcoming options are worth considering:

**.NET 9 (current, STS — supported until May 2026):**
- `Task.WhenEach` for better concurrent download tracking
- `HybridCache` as a built-in caching abstraction
- Improved `IHttpClientFactory` with keyed services
- Better AOT compilation (relevant for single-file CLI publishing)
- Risk: STS release, support ends before .NET 8

**.NET 10 (ships November 2025, next LTS):**
- Will be the next long-term-supported release
- All .NET 9 improvements plus additional ones
- Natural upgrade target when .NET 8 support approaches end-of-life

**Recommendation:** Stay on .NET 8 for now, plan migration to .NET 10 when it reaches GA. The .NET 9 improvements are "nice to have" but don't justify moving to a shorter support window.

---

## Summary: Priority Matrix

| # | Replacement | Current | Proposed | Effort | Impact |
|---|-----------|---------|----------|--------|--------|
| 1 | Data layer | 5 JSON files | SQLite | Medium | High — crash safety, query performance, single store |
| 2 | State management | Direct VM references | Event bus (CommunityToolkit `WeakReferenceMessenger`) | Low | Medium — decouples ViewModels, enables reactive updates |
| 3 | HTTP architecture | 3 custom clients | `IHttpClientFactory` + Polly | Medium | High — eliminates ~1000 LOC, adds circuit breakers |
| 4 | Plugin isolation | `AssemblyLoadContext` only | Tiered isolation with capability restrictions | High | High — security boundary for community plugins |
| 5 | Build system | GNU Makefile | Nuke Build | Medium | Medium — cross-platform build, IDE integration |
| 6 | CLI architecture | 689-line `Program.cs` | Spectre.Console.Cli command classes | Medium | High — testability, eliminates pre-release dependency |
| 7 | Dependency resolution | Greedy resolver | Real PubGrub or `NuGet.Resolver` | Medium | Medium — finds solutions that currently fail |
| 8 | Credential storage | Plaintext config file | OS keychain integration | Medium | Medium — protects premium API keys |
| 9 | ViewModel design | Fat ViewModels (2000 LOC) | Extracted domain services | Medium | High — testability, CLI/GUI code sharing |
| 10 | Test architecture | 65 tests, core only | Structured 3-layer strategy | High | High — regression prevention, refactoring safety |
| 11 | Project structure | Monolithic `Modular.Core` | Domain-split sub-projects | Medium | Medium — clearer boundaries, parallel development |
| 12 | .NET version | .NET 8.0 | .NET 10 (when GA) | Low | Low — incremental improvements |

### Suggested Execution Order

1. **Extract interfaces** (#9 service extraction, #11 abstractions) — enables everything else
2. **Consolidate HTTP** (#3) — reduces code volume, fixes active bugs
3. **Add event bus** (#2) — low effort, immediate decoupling benefit
4. **Replace CLI architecture** (#6) — enables CLI testing
5. **Migrate to SQLite** (#1) — requires interfaces from step 1
6. **Expand test coverage** (#10) — now possible after steps 1-4
7. **Credential storage** (#8) — security improvement
8. **Build system** (#5) — quality of life
9. **Plugin isolation** (#4) — significant effort, security-driven priority
10. **Dependency resolution** (#7) — correctness improvement
11. **.NET version** (#12) — when .NET 10 ships
