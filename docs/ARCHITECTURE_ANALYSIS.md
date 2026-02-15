# Modular-1 Architecture Analysis

An objective review of technology choices, design patterns, and areas where better alternatives exist.

---

## 1. Custom FluentHttp Library vs. Established Alternatives

**Current:** A hand-rolled `Modular.FluentHttp` library (7 files, ~700 LOC) wrapping `System.Net.Http.HttpClient` with a fluent builder, retry logic, filters, and rate limiting.

**Problems:**

- `SetConnectionTimeout` and `SetRequestTimeout` both set the same `_httpClient.Timeout` property — they are identical methods with different names (`FluentClient.cs:84-85`)
- Filter pipeline calls `filter.OnRequest(request)` synchronously despite being in an async context — no `async` filter support (`FluentClient.cs:125-126`)
- Rate limiter has a race condition: `WaitIfNeededAsync()` and `ReserveRequest()` are called as two separate operations without atomic check-and-reserve (`FluentClient.cs:103-104`). Another thread can slip between the wait and the reservation.
- `FluentClientFactory.Create()` creates a new `HttpClient` per call with no pooling — this leaks sockets and doesn't respect DNS TTL rotation
- No circuit-breaker pattern: if a backend is down, every request still waits for timeout + retries before failing

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **Polly + IHttpClientFactory** (recommended) | Microsoft's built-in `IHttpClientFactory` handles `HttpClient` lifecycle correctly (connection pooling, DNS rotation). Polly provides battle-tested retry, circuit breaker, timeout, and rate limiting policies. This is the standard .NET approach. |
| **Refit** | Type-safe REST client generation from interfaces. Eliminates manual URL building and JSON deserialization boilerplate. Pairs with Polly for resilience. |
| **Flurl** | If a fluent API is specifically desired, Flurl is a mature, well-maintained fluent HTTP library that already does what `Modular.FluentHttp` attempts. |

**Impact:** Replacing FluentHttp with `IHttpClientFactory` + Polly would eliminate ~700 lines of custom code, fix the socket exhaustion and DNS rotation issues, and provide circuit breakers and bulkhead isolation the current code lacks.

---

## 2. Three Competing HTTP Client Implementations

**Current:** There are **three** separate HTTP client abstractions in the codebase:

1. **`Modular.FluentHttp`** — the custom fluent library (separate project, used by backends)
2. **`Modular.Core.Http.ModularHttpClient`** — another wrapper with retry and rate limiting (`ModularHttpClient.cs`, ~230 LOC)
3. **`DownloadEngine`** — uses raw `HttpClient` directly, bypassing both wrappers (`DownloadEngine.cs:21`)

All three wrap `HttpClient`, all implement retry with exponential backoff, all integrate with rate limiting, and all handle timeouts — differently from each other.

**Additional issue in ModularHttpClient:** Its `Dispose()` is a no-op with a comment claiming "We don't dispose _httpClient as it may be managed by IHttpClientFactory" (`ModularHttpClient.cs:219-226`) — but there is no `IHttpClientFactory` usage anywhere in the project. The comment is aspirational documentation for code that was never written.

**Problem:** Three different HTTP approaches mean inconsistent retry behavior, inconsistent timeout handling, and three places to fix any bug. `DownloadEngine` creates its own `HttpClient` in its constructor (`new HttpClient(...)`) which duplicates the socket exhaustion problem.

**Recommendation:** Consolidate to a single HTTP strategy. With `IHttpClientFactory` + Polly, all three can be replaced by named/typed `HttpClient` instances with policies attached via DI.

---

## 3. JSON Flat-File Database vs. Embedded Database

**Current:** `DownloadDatabase` stores all records in a single JSON file loaded into a `List<DownloadRecord>` in memory, protected by a coarse `object _lock`. Queries are linear scans.

**Problems:**

- **No atomicity**: A crash between download completion and `SaveAsync()` loses the record. There's no write-ahead log or journaling. `SaveAsync()` rewrites the entire file (`DownloadDatabase.cs:178-179`).
- **Full serialization on every save**: The entire database is re-serialized on each `SaveAsync()` call, scaling poorly as download history grows.
- **Linear search**: `FindRecord` does O(n) scans with `.FirstOrDefault()` (`DownloadDatabase.cs:60`). With thousands of downloads, this becomes measurable.
- **No concurrent read/write**: The global `lock` means all readers block all writers and vice-versa.
- **Multiple independent JSON stores**: `DownloadDatabase`, `ModMetadataCache`, `NexusRateLimiter` state, `DownloadHistoryService`, and configuration all independently implement the same load-from-JSON/save-to-JSON pattern with their own locking — duplicated infrastructure repeated across the codebase.

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **SQLite via Microsoft.Data.Sqlite** (recommended) | Zero-config embedded database. ACID transactions protect against corruption. Indexed queries replace linear scans. WAL mode allows concurrent readers with a single writer. Single file, no server. First-class .NET support. |
| **LiteDB** | If a document-oriented (no SQL) approach is preferred, LiteDB is an embedded NoSQL database for .NET with BSON storage, ACID transactions, and indexing. Single DLL, no native dependencies. |

**Impact:** A single SQLite database would replace all JSON stores, provide crash safety, indexed lookups, and eliminate the duplicated serialization infrastructure.

---

## 4. System.CommandLine (Beta) for CLI

**Current:** `System.CommandLine 2.0.0-beta4` for CLI argument parsing.

**Problem:** This package has been in beta/prerelease since 2019. While it's a Microsoft project, it has not shipped a stable release and its API has changed between preview versions. The `SetHandler` API used throughout `Program.cs` was deprecated in later previews.

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **Spectre.Console.Cli** (recommended) | Already a dependency (Spectre.Console is used for terminal UI in `LiveProgressDisplay`). Adding `Spectre.Console.Cli` from the same ecosystem provides command parsing with the same styling, avoiding an extra dependency entirely. |
| **Cocona** | Builds on `Microsoft.Extensions.Hosting` (already used in the CLI project). Define commands as simple methods with parameters. Much less boilerplate than System.CommandLine. |
| **CliFx** | Clean, attribute-based CLI framework. Stable releases. Strong typing for arguments and options. |

**Impact:** The CLI `Program.cs` is 689 lines of procedural command setup and handler wiring. A framework like Spectre.Console.Cli would reduce this significantly while providing better testability and eliminating the pre-release dependency.

---

## 5. God-Class CLI Entry Point

**Current:** `Program.cs` in `Modular.Cli` is 689 lines containing all command definitions, all handler implementations, service initialization, backend configuration, and a logger factory — in a single static class with static methods.

**Problems:**

- No separation of concerns: command definition, business logic orchestration, DI setup, and output formatting are all interleaved
- Untestable: all methods are `static`, services are created inline, and there's no way to inject mocks
- Duplicated patterns: every command handler (`RunCommandMode`, `RunRenameCommand`, `RunFetchCommand`, `RunDownloadCommand`) repeats the same CancellationTokenSource setup, try/catch/OperationCanceledException pattern, and state-saving logic
- `InitializeServices()` and `InitializeServicesMinimal()` are nearly identical with subtle differences (one validates config, one doesn't)

**Recommendation:** Break into separate command classes (one per file), use constructor injection, and share common concerns (cancellation, state persistence, error handling) via a base class or middleware. A CLI framework like Spectre.Console.Cli or Cocona naturally enforces this structure.

---

## 6. Duplicate Type Names Across Namespaces

**Current:** Several types are duplicated with the same name in different namespaces:

1. **`DownloadStatus`** exists in both:
   - `Modular.Core.Database.DownloadStatus` (Pending, Downloading, Success, Verified, HashMismatch, Failed) — `Database/DownloadStatus.cs:9`
   - `Modular.Core.Downloads.DownloadStatus` (Pending, InProgress, Paused, Completed, Failed) — `Downloads/DownloadQueue.cs:440`

   These represent different state machines for the same conceptual thing (a download's lifecycle), with different states and different consumers.

2. **`DownloadOptions`** exists in both:
   - `Modular.Core.Downloads.DownloadOptions` (AllowResume, ExpectedHash, etc.) — `DownloadEngine.cs:246`
   - `Modular.Core.Backends.DownloadOptions` (DryRun, Force, Filter, etc.) — used in backend download commands

3. **`DownloadProgress`** exists in both:
   - `Modular.Core.Downloads.DownloadProgress` (BytesDownloaded, TotalBytes, Speed) — `DownloadEngine.cs:300`
   - `Modular.Core.Backends.DownloadProgress` (Phase, Status, Completed, Total) — used by backends

**Problem:** This causes confusion, requires fully-qualified type names to disambiguate, and suggests the download pipeline has unclear ownership between "engine" and "backend" layers.

**Recommendation:** Unify each pair into a single type. The backend-level types should compose or extend the engine-level types rather than shadowing them.

---

## 7. Obsolete Code Still in Active Use

**Current:** `GameBananaService` and `NexusModsService` are marked `[Obsolete]` but still actively called:

- `RunGameBananaCommand()` calls `GameBananaService` directly (`Program.cs:363`)
- `RunCommandMode()` calls `NexusModsService` directly (`Program.cs:200-206`)
- The CLI suppresses the compiler warning globally with `#pragma warning disable CS0618` (`Program.cs:18`)

**Problem:** Two parallel implementations (legacy services + backend system) creates maintenance burden. A fix in one won't be reflected in the other. The pragma suppression hides what should be compilation failures.

**Recommendation:** Complete the migration to the backend system and remove:
- `GameBananaService.cs`
- `NexusModsService.cs`
- The `gamebanana` subcommand (replace with `download --backend gamebanana`)
- The `#pragma warning disable` suppression

---

## 8. Blocking Async in DI Container

**Current:** The GUI's `Program.cs` calls `.GetAwaiter().GetResult()` on async operations during DI service registration (`Modular.Gui/Program.cs:66,74,84,125`):

```csharp
services.AddSingleton(sp =>
{
    var configService = sp.GetRequiredService<ConfigurationService>();
    return configService.LoadAsync().GetAwaiter().GetResult();
});
```

**Problem:** `.GetAwaiter().GetResult()` on async code risks deadlocks, particularly in UI contexts with a `SynchronizationContext`. The code comments acknowledge this and work around it by pre-resolving singletons before Avalonia starts (lines 30-34), but this is fragile — any change to service resolution order or any lazy-resolved service could reintroduce the deadlock.

**Better Alternatives:**
- Perform async initialization in `App.OnFrameworkInitializationCompleted` with proper `await`
- Use `IHostedService` or a startup initialization pattern to run async work before the UI loop begins
- `Microsoft.Extensions.Hosting` supports async startup natively — the GUI could adopt `IHost` the same way the CLI project could

---

## 9. Thread Safety via `lock` vs. Concurrent Collections

**Current:** Nearly all thread-safe state uses `object _lock` with `lock()` blocks: `DownloadDatabase`, `ModMetadataCache`, `NexusRateLimiter`, `DependencyGraph`.

**Problem:** Using `lock` for read-heavy workloads serializes all access. The metadata cache is overwhelmingly reads with rare writes — a classic case for `ConcurrentDictionary` or `ReaderWriterLockSlim`.

**Recommendation:**
- `ModMetadataCache`: Replace `Dictionary` + `lock` with `ConcurrentDictionary<string, T>` — independent key-value entries with heavily read-biased access
- `NexusRateLimiter`: Keep `lock` (state is coupled between fields)
- `DownloadDatabase`: Would be resolved by migrating to SQLite

---

## 10. Plugin System: Unused MEF Dependency and No Constructor Injection

**Current:** `System.Composition` (MEF) is a dependency of `Modular.Core`, and a `PluginComposer` class exists. However, `PluginLoader` does its own manual reflection-based discovery (`PluginLoader.cs:133-135`):

```csharp
var metadataTypes = assembly.GetTypes()
    .Where(t => typeof(IPluginMetadata).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
```

Plugins are instantiated via parameterless `Activator.CreateInstance()` (`PluginLoader.cs:144`), meaning plugins cannot receive any services through their constructors.

**Problems:**
- MEF is dead weight if reflection-based discovery is what's actually used
- Plugins can't receive `ILogger`, `AppSettings`, `IFluentClient`, or any other service — they're isolated from the DI container
- `Activator.CreateInstance()` requires a parameterless constructor, which constrains plugin design

**Recommendation:**
- Remove the `System.Composition` dependency or consolidate to one discovery mechanism
- Use `ActivatorUtilities.CreateInstance()` from `Microsoft.Extensions.DependencyInjection` to allow plugins to accept constructor-injected services
- Provide a `IServiceProvider` or scoped service collection to plugins so they can resolve what they need

---

## 11. No Data Access Abstractions

**Current:** `DownloadDatabase` and `ModMetadataCache` are concrete classes passed directly as constructor parameters throughout the codebase. They're registered as singletons in DI and consumed directly.

**Problem:** Without interfaces:
- Unit tests can't mock the data layer without integration-test-level setup
- Swapping the storage implementation (e.g., JSON → SQLite) requires touching every consumer
- The `DownloadDatabase` class mixes persistence concerns (file I/O) with query logic (LINQ over in-memory list) and thread safety (locking)

**Recommendation:** Extract `IDownloadDatabase` and `IModMetadataCache` interfaces. The current concrete classes become one implementation, and a future SQLite-backed implementation can be swapped in via DI without changing consumers.

---

## 12. CancellationToken Handler Leak in Interactive Mode

**Current:** In `RunInteractiveMode()`, the loop runs indefinitely (`while (true)`) and each iteration through `RunBackendDownload` or `RunCommandMode` registers a new `Console.CancelKeyPress` handler. However, `RunInteractiveMode()` itself doesn't register one, while the methods it calls via command subflows do.

More concerning: `RunCommandMode`, `RunRenameCommand`, `RunFetchCommand`, and `RunDownloadCommand` each create a `CancellationTokenSource` with `using` and register `Console.CancelKeyPress` handlers (`Program.cs:185-189, 250-255, 306-310, 526-531`), but these handlers are never unregistered. Each invocation adds another handler to the event.

**Problem:** Over multiple interactive iterations, handlers accumulate. This is a memory leak and could cause unexpected behavior if stale handlers fire.

**Recommendation:** Use a single `CancellationTokenSource` at the interactive mode level, or explicitly unsubscribe handlers after each command completes.

---

## 13. Simplified PubGrub Resolver

**Current:** `PubGrubResolver` (`Dependencies/PubGrubResolver.cs`) is described as a "PubGrub-inspired" dependency resolver, but it's actually a straightforward greedy version selector — it picks the latest version satisfying constraints and never backtracks.

**Problem:** Real PubGrub (used by Dart/pub, Swift Package Manager, etc.) provides backtracking when a version selection leads to a conflict downstream. The current implementation returns a hard failure when no version satisfies constraints at a given step, even if an earlier version selection could have avoided the conflict. This means it can fail to find valid solutions that exist.

**Recommendation:** Either:
- Rename to something like `GreedyResolver` to set accurate expectations, or
- Implement actual backtracking if dependency conflicts are expected to be common in the mod ecosystem
- Consider using the `NuGet.Versioning` library for version parsing/comparison instead of the custom `SemanticVersion` and `VersionRange` implementations

---

## 14. Makefile vs. .NET-Native Build Orchestration

**Current:** A ~215-line GNU Makefile orchestrating all build, test, install, publish, and plugin targets.

**Problem:** Makefiles are not portable to Windows without additional tooling (requires WSL, Cygwin, or similar). Given this is a cross-platform .NET project targeting Windows/macOS/Linux, the build orchestration itself isn't cross-platform.

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **Nuke Build** (recommended) | .NET-native build automation. Write build logic in C#. Cross-platform. First-class `dotnet` integration. Can replace the entire Makefile with type-safe build steps. |
| **CAKE** | Similar to Nuke but uses a DSL. Also .NET-native and cross-platform. |
| **PowerShell + bash scripts** | Lighter-weight option: `build.ps1` for Windows, `build.sh` for Unix. |

---

## 15. Configuration Model Issues

**Current:** `AppSettings` is a mutable POCO with public setters. Settings are mutated at runtime (e.g., `settings.Verbose = verbose` in `Program.cs:199`, `settings.DefaultCategories = categories.ToList()` in `Program.cs:197`).

**Problems:**
- Mutable settings shared across the entire app make it unclear what the "source of truth" is at any point in execution
- No validation on individual property values (e.g., `MaxConcurrentDownloads` could be set to 0 or -1)
- Runtime CLI flag overrides (like `--verbose`) modify the same object as persistent config, conflating two different concerns

**Recommendation:**
- Use the Options pattern (`IOptions<AppSettings>` / `IOptionsSnapshot<AppSettings>`) from `Microsoft.Extensions.Options` (already a dependency) instead of passing raw `AppSettings` instances
- Implement `IValidateOptions<AppSettings>` for startup validation
- Separate runtime overrides (CLI flags) from persistent configuration into different types

---

## 16. Test Coverage Gaps

**Current:** 7 test files covering core library and FluentHttp. No tests for:
- `Modular.Cli` (the entire CLI layer is untested)
- `Modular.Gui` (no ViewModel tests despite using MVVM)
- `DownloadEngine`
- `PubGrubResolver`
- `PluginLoader`
- `NexusSsoClient`
- Any integration or end-to-end tests

**Problem:** The most complex and bug-prone components (download pipeline, dependency resolution, plugin loading, CLI command orchestration) have zero test coverage. The existing tests cover simpler components.

**Recommendation:**
- Add ViewModel tests for the GUI (CommunityToolkit.Mvvm view models are designed to be testable)
- Add integration tests for the download pipeline with a mock HTTP server
- The CLI's god-class structure (see #5) must be refactored before it can be meaningfully tested

---

## 17. .NET 8 vs. .NET 9

**Current:** All projects target `net8.0`.

**.NET 9** (released November 2024) offers relevant improvements:
- `System.Text.Json` performance improvements and new `JsonSerializerOptions.Web` defaults
- `HybridCache` as a built-in distributed/memory cache (could replace some custom caching)
- `Task.WhenEach` for better async enumeration of concurrent tasks
- Improved `HttpClientFactory` with keyed DI services
- AOT compilation improvements (relevant for single-file publishing)

**Note:** .NET 8 is an LTS release (supported until November 2026), while .NET 9 is STS (supported until May 2026). Depending on support timeline preferences, staying on .NET 8 may be intentional and reasonable. .NET 10 (the next LTS) ships November 2025.

---

## 18. Language and Framework Selection

**C# / .NET 8 — Appropriate.** For a cross-platform desktop application with plugin support, .NET is a solid choice. The alternatives each have significant tradeoffs:
- Rust would provide better binary size and memory safety but lacks Avalonia-equivalent GUI frameworks and has a much harder plugin story (no runtime reflection, ABI instability)
- Go has no mature cross-platform desktop GUI framework
- Electron/TypeScript would balloon binary size and memory usage for a mod manager

**Avalonia — Appropriate.** For cross-platform .NET desktop UI, Avalonia is the standard choice. MAUI has weaker Linux support; Uno Platform is less mature for desktop. Avalonia is well-suited here.

---

## Summary: Prioritized Recommendations

| Priority | Change | Effort | Impact |
|----------|--------|--------|--------|
| **High** | Consolidate three HTTP client implementations into `IHttpClientFactory` + Polly | Medium | Fixes socket exhaustion, eliminates ~1000 LOC, adds circuit breakers |
| **High** | Remove obsolete services, complete backend migration | Low | Eliminates dead code paths and the `#pragma warning disable` |
| **High** | Unify duplicate type names (`DownloadStatus`, `DownloadOptions`, `DownloadProgress`) | Low | Removes namespace ambiguity and clarifies ownership |
| **Medium** | Replace JSON flat-file stores with SQLite | Medium | ACID safety, indexed queries, single unified store |
| **Medium** | Fix blocking async in GUI DI | Low | Eliminates deadlock risk |
| **Medium** | Break up CLI god-class into separate command classes | Medium | Enables testing, eliminates duplication |
| **Medium** | Replace System.CommandLine beta with Spectre.Console.Cli | Medium | Stable dependency, less boilerplate, same ecosystem |
| **Medium** | Add data access abstractions (interfaces for DB and cache) | Low | Enables unit testing and future storage migration |
| **Medium** | Expand test coverage to CLI, GUI ViewModels, and download pipeline | Medium | Prevents regressions in the most complex components |
| **Low** | Adopt Options pattern for configuration | Low | Better validation, immutability |
| **Low** | Replace Makefile with Nuke Build | Medium | Cross-platform build system |
| **Low** | Clean up plugin system (remove unused MEF, add DI to plugins) | Low | Cleaner dependencies, more capable plugins |
| **Low** | Fix CancelKeyPress handler leak | Low | Correctness |
| **Low** | Rename PubGrubResolver or implement actual backtracking | Low | Accurate expectations or better resolution |
