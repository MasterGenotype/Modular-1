# Modular-1 Architecture Analysis

An objective review of technology choices, design patterns, and areas where better alternatives exist.

---

## 1. Custom FluentHttp Library vs. Established Alternatives

**Current:** A hand-rolled `Modular.FluentHttp` library (7 files) wrapping `System.Net.Http.HttpClient` with fluent builder, retry logic, filters, and rate limiting.

**Problem:** This duplicates well-tested functionality available in mature libraries. The current implementation has specific issues:

- `FluentClient.Dispose()` is a no-op — doesn't dispose the underlying `HttpClient` (`FluentClient.cs:176-182`)
- `SetConnectionTimeout` and `SetRequestTimeout` both set the same `_httpClient.Timeout` property — they're identical methods (`FluentClient.cs:82-83`)
- Filter pipeline calls `filter.OnRequest(request)` synchronously despite being in an async context — no `async` filter support (`FluentClient.cs:124`)
- Rate limiter has a race condition: `WaitIfNeededAsync` and `ReserveRequest` are called as two separate operations without a single atomic check-and-reserve (`FluentClient.cs:99-103`)

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **Polly + IHttpClientFactory** (recommended) | Microsoft's built-in `IHttpClientFactory` handles `HttpClient` lifecycle correctly (pooling, DNS rotation). Polly provides battle-tested retry, circuit breaker, timeout, and rate limiting policies. This is the standard .NET approach. |
| **Refit** | Type-safe REST client generation from interfaces. Eliminates manual URL building and JSON deserialization boilerplate. Pairs with Polly for resilience. |
| **Flurl** | If a fluent API is specifically desired, Flurl is a mature, well-maintained fluent HTTP library that already does what `Modular.FluentHttp` attempts. |

**Impact:** Replacing FluentHttp with `IHttpClientFactory` + Polly would eliminate ~700 lines of custom code, fix the `HttpClient` lifecycle issues, and provide circuit breakers and bulkhead isolation the current code lacks.

---

## 2. JSON Flat-File Database vs. Embedded Database

**Current:** `DownloadDatabase` stores all records in a single JSON file (`DownloadDatabase.cs`). The entire file is loaded into a `List<DownloadRecord>` in memory, protected by a coarse `object _lock`. Queries are linear scans via LINQ.

**Problems:**

- **No atomicity**: A crash between download completion and `SaveAsync()` loses the record. There's no write-ahead log or journaling.
- **Full serialization on every save**: The entire database is re-serialized and written on each save, which scales poorly as history grows.
- **Linear search**: `FindRecord` does O(n) scans. With thousands of downloads, this becomes measurable.
- **No concurrent read/write**: The global `lock` means all readers block all writers and vice-versa.
- **Multiple separate JSON stores**: `DownloadDatabase`, `ModMetadataCache`, `HttpCache`, `RateLimitState`, and `DownloadHistoryService` all independently implement the same load-from-JSON/save-to-JSON pattern with their own locking — this is duplicated infrastructure repeated 5+ times.

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **SQLite via Microsoft.Data.Sqlite** (recommended) | Zero-config embedded database. ACID transactions protect against corruption. Indexed queries replace linear scans. WAL mode allows concurrent readers with a single writer. Single file, no server. The .NET ecosystem has first-class support. |
| **LiteDB** | If you want to stay document-oriented (no SQL), LiteDB is an embedded NoSQL database for .NET with BSON storage, ACID transactions, and indexing. Single DLL, no native dependencies. |

**Impact:** A single SQLite database would replace all 5+ JSON stores, provide crash safety, indexed lookups, and eliminate the duplicated serialization infrastructure across the codebase.

---

## 3. Duplicate HTTP Client Implementations

**Current:** There are **two** HTTP client abstractions that serve overlapping purposes:

1. `Modular.FluentHttp` — the custom fluent library (separate project)
2. `Modular.Core.Http.ModularHttpClient` — another wrapper with retry and rate limiting (`ModularHttpClient.cs`)

Both wrap `System.Net.Http.HttpClient`, both implement retry with exponential backoff, both integrate with `IRateLimiter`, and both handle timeouts. `DownloadEngine` uses raw `HttpClient` directly, bypassing both wrappers.

**Problem:** Three different HTTP approaches in one codebase means inconsistent behavior. Retry policies, timeouts, and error handling differ between them. A bug fixed in one may not be fixed in the other.

**Recommendation:** Consolidate to a single HTTP strategy. If adopting `IHttpClientFactory` + Polly (per recommendation #1), all three can be eliminated in favor of named/typed `HttpClient` instances with Polly policies attached via DI.

---

## 4. System.CommandLine (Beta) for CLI

**Current:** `System.CommandLine 2.0.0-beta4` for CLI argument parsing.

**Problem:** This package has been in beta/prerelease since 2019. While it's a Microsoft project, it has not shipped a stable release and its API has changed between preview versions. The `SetHandler` API used in `Program.cs` was already deprecated in later previews.

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **Cocona** | Builds on `Microsoft.Extensions.Hosting` (already used in the project). Define commands as simple methods with parameters. Much less boilerplate than System.CommandLine. |
| **CliFx** | Clean, attribute-based CLI framework. Stable releases. Strong typing for arguments and options. |
| **Spectre.Console.Cli** | Already a dependency (Spectre.Console is used for terminal UI). Adding `Spectre.Console.Cli` from the same ecosystem provides command parsing with the same styling, avoiding an extra dependency. |

**Impact:** The CLI `Program.cs` is 684 lines of procedural command setup and handler wiring. A framework like Spectre.Console.Cli or Cocona would reduce this significantly while providing better testability.

---

## 5. No CI/CD Pipeline

**Current:** No GitHub Actions, no CI configuration of any kind. Build orchestration is entirely via a local Makefile.

**Problem:** Without CI:
- No automated test runs on PRs — regressions can be merged silently
- No automated builds for release artifacts
- No linting or format enforcement
- Cross-platform publishing (`publish-linux`, `publish-windows`, `publish-macos`, `publish-macos-arm`) must be done manually

**Recommendation:** Add a GitHub Actions workflow with:
- `dotnet build` and `dotnet test` on push/PR
- Matrix build for multi-platform publish
- Release automation with artifact upload
- Consider using `dorny/test-reporter` for xUnit test result display in PRs

---

## 6. Obsolete Code Still in Active Use

**Current:** `GameBananaService` is marked `[Obsolete]` (`GameBananaService.cs:17`) but is still actively used by `RunGameBananaCommand()` in `Program.cs:363`. The CLI suppresses the warning with `#pragma warning disable CS0618` (`Program.cs:18`).

Similarly, `NexusModsService` (the pre-backend version) is still used in `RunCommandMode` for the default domain-based download path.

**Problem:** Two parallel implementations (legacy services + backend system) creates maintenance burden and confusion about which code path is active.

**Recommendation:** Complete the migration to the backend system and remove:
- `GameBananaService.cs`
- `NexusModsService.cs`
- The `gamebanana` subcommand (replace with `download --backend gamebanana`)
- The `#pragma warning disable` suppression

---

## 7. Blocking Async in DI Container

**Current:** The GUI's `Program.cs` calls `.GetAwaiter().GetResult()` on async operations during service registration (`Modular.Gui/Program.cs:66,74,84,125`):

```csharp
services.AddSingleton(sp =>
{
    var configService = sp.GetRequiredService<ConfigurationService>();
    return configService.LoadAsync().GetAwaiter().GetResult();
});
```

**Problem:** `.GetAwaiter().GetResult()` on async code risks deadlocks, especially in UI contexts. The comment in the code acknowledges this risk. While the current workaround (pre-resolving singletons before Avalonia starts, line 30-34) avoids the immediate deadlock, it's fragile — any change to service resolution order could reintroduce it.

**Better Alternatives:**

- Use `IHostedService` or `IStartupFilter` patterns to perform async initialization before the app starts
- Microsoft.Extensions.Hosting supports async startup natively — the GUI could adopt `IHost` the same way the CLI project could
- For Avalonia specifically, perform async initialization in `App.OnFrameworkInitializationCompleted` with proper `await`

---

## 8. Thread Safety via `lock` vs. `ConcurrentDictionary` / `ReaderWriterLockSlim`

**Current:** Nearly all thread-safe state uses `object _lock` with `lock()` blocks: `DownloadDatabase`, `ModMetadataCache`, `HttpCache`, `NexusRateLimiter`, `DependencyGraph`.

**Problem:** Using `lock` for read-heavy workloads serializes all access. The metadata cache and HTTP cache are overwhelmingly read operations with rare writes — a classic case for `ReaderWriterLockSlim` or `ConcurrentDictionary`.

**Recommendation:**
- `HttpCache`, `ModMetadataCache`: Replace `Dictionary` + `lock` with `ConcurrentDictionary<string, T>` — these are simple key-value stores with independent entries
- `NexusRateLimiter`: Keep `lock` (state is coupled between fields)
- `DownloadDatabase`: Would be resolved by migrating to SQLite

---

## 9. Plugin System: MEF vs. Direct Reflection

**Current:** The codebase references `System.Composition` (MEF) in `Modular.Core.csproj` and has a `PluginComposer` class, but `PluginLoader` does its own manual reflection-based discovery (`PluginLoader.cs:133-141`):

```csharp
var metadataTypes = assembly.GetTypes()
    .Where(t => typeof(IPluginMetadata).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
    .ToList();
```

**Problem:** Two plugin discovery mechanisms exist but the reflection-based one is what's actually used. The MEF dependency is dead weight. Additionally, plugins are instantiated via `Activator.CreateInstance` with no DI support — plugins can't receive services through their constructors.

**Recommendation:**
- Remove the `System.Composition` dependency if MEF isn't actually being used
- Add constructor injection support for plugins via `ActivatorUtilities.CreateInstance` (from `Microsoft.Extensions.DependencyInjection`) so plugins can receive `ILogger`, `AppSettings`, etc.
- Consider source generators for plugin discovery at compile time rather than runtime reflection

---

## 10. Makefile vs. .NET-Native Build Orchestration

**Current:** A 215-line GNU Makefile orchestrating all build, test, install, publish, and plugin targets.

**Problem:** Makefiles work but are not portable to Windows without additional tooling (requires WSL, Cygwin, or similar). Given this is a cross-platform .NET project targeting Windows/macOS/Linux, the build system itself isn't cross-platform.

**Better Alternatives:**

| Alternative | Why |
|---|---|
| **Nuke Build** (recommended) | .NET-native build automation. Write build logic in C#. Cross-platform. First-class `dotnet` integration. Can replace the entire Makefile with type-safe build steps. |
| **CAKE** | Similar to Nuke but uses a DSL. Also .NET-native and cross-platform. |
| **dotnet CLI scripts** | A simple PowerShell/bash script pair (`build.ps1`/`build.sh`) is the lightest-weight cross-platform option. |

---

## 11. Configuration Model Issues

**Current:** `AppSettings` is a mutable POCO with public setters on all properties (`AppSettings.cs`). Settings are mutated at runtime (e.g., `settings.Verbose = verbose` in `Program.cs:199`, `settings.DefaultCategories = categories.ToList()` in `Program.cs:197`).

**Problems:**
- Mutable settings shared across the entire app make it unclear what the "source of truth" configuration is at any point
- No validation on individual property values (e.g., `MaxConcurrentDownloads` could be set to 0 or -1)
- `DatabasePath` and `RateLimitStatePath` default to `string.Empty` but are required for operation — no fail-fast

**Recommendation:**
- Use the Options pattern (`IOptions<AppSettings>` / `IOptionsSnapshot<AppSettings>`) from `Microsoft.Extensions.Options` (already a dependency) instead of passing raw `AppSettings` instances
- Implement `IValidateOptions<AppSettings>` for startup validation
- Make runtime overrides (like `--verbose`) a separate concern from persistent configuration

---

## 12. Error Handling: String-Based Status Codes

**Current:** `DownloadRecord.Status` and download results use magic strings: `"success"`, `"verified"`, `"hash_mismatch"` (`DownloadDatabase.cs:105,127`).

**Problem:** String comparisons for status are fragile, not discoverable, and can't be enforced by the compiler.

**Recommendation:** Use an `enum DownloadStatus { Pending, Downloading, Success, Verified, HashMismatch, Failed }` with `[JsonConverter]` for serialization compatibility.

---

## 13. Language and Framework Selection

**C# / .NET 8 — Appropriate.** For a cross-platform desktop application with plugin support, .NET is a solid choice. The alternative (Rust, Go, TypeScript/Electron) each have significant tradeoffs for this use case:
- Rust would provide better binary size and memory safety but lacks Avalonia-equivalent GUI frameworks and has a steeper plugin story
- Go has no mature cross-platform desktop GUI framework
- Electron/TypeScript would balloon binary size and memory usage for a mod manager

**Avalonia — Appropriate.** For cross-platform .NET desktop UI, Avalonia is the standard choice. The alternatives (MAUI, Uno Platform) are either less mature on Linux or have different tradeoffs. Avalonia is well-suited here.

---

## Summary: Prioritized Recommendations

| Priority | Change | Effort | Impact |
|----------|--------|--------|--------|
| **High** | Replace FluentHttp + ModularHttpClient with `IHttpClientFactory` + Polly | Medium | Fixes lifecycle bugs, eliminates ~1000 LOC, adds circuit breakers |
| **High** | Add CI/CD (GitHub Actions) | Low | Prevents regressions, automates releases |
| **High** | Remove obsolete services, complete backend migration | Low | Eliminates dead code and confusion |
| **Medium** | Replace JSON flat-file stores with SQLite | Medium | ACID safety, indexed queries, single unified store |
| **Medium** | Fix blocking async in GUI DI | Low | Eliminates deadlock risk |
| **Medium** | Replace System.CommandLine beta with Spectre.Console.Cli | Medium | Stable dependency, less boilerplate, same ecosystem |
| **Low** | Adopt Options pattern for configuration | Low | Better validation, immutability |
| **Low** | Replace Makefile with Nuke Build | Medium | Cross-platform build system |
| **Low** | Use enums instead of magic strings for status | Low | Type safety |
| **Low** | Clean up plugin system (remove unused MEF dep, add DI) | Low | Cleaner dependencies |
