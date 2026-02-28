# Codebase Architectural & Logical Errors Analysis

**Date:** February 28, 2026
**Scope:** Full codebase review of Modular-1 (C# / .NET 8.0)
**Projects Reviewed:** Modular.Core, Modular.Cli, Modular.Gui, Modular.FluentHttp, Modular.Sdk, tests/

---

## Table of Contents

1. [Critical Issues](#1-critical-issues)
2. [Thread Safety & Concurrency](#2-thread-safety--concurrency)
3. [Resource Management & Memory Leaks](#3-resource-management--memory-leaks)
4. [Logical Errors](#4-logical-errors)
5. [Exception Handling Anti-patterns](#5-exception-handling-anti-patterns)
6. [Architectural Violations](#6-architectural-violations)
7. [Security Issues](#7-security-issues)
8. [Test Quality Issues](#8-test-quality-issues)
9. [Performance Issues](#9-performance-issues)
10. [Summary Table](#10-summary-table)

---

## 1. Critical Issues

### 1.1 Lock Inside Async Method — Deadlock Risk
**File:** `src/Modular.Core/RateLimiting/RateLimitScheduler.cs` (~line 207)
**Severity: CRITICAL**

```csharp
public async Task AcquireConcurrentSlotAsync(CancellationToken ct)
{
    await _concurrentSemaphore.WaitAsync(ct);  // async resumption may be on thread-pool thread
    lock (_lock)                               // lock after await — deadlock risk
    {
        ConcurrentActive++;
    }
}
```

Using `lock` after an `await` is dangerous. The continuation may resume on a different thread-pool thread, and if the lock is already held by the original thread, a deadlock can result. **Fix:** Replace the `lock` block with a second `SemaphoreSlim`, or use `Interlocked.Increment` for the counter.

---

### 1.2 Double-Checked Locking Anti-pattern — Race Condition on Queue Start
**File:** `src/Modular.Core/Downloads/DownloadQueue.cs` (~line 148)
**Severity: CRITICAL**

```csharp
// This check is OUTSIDE any lock:
if (_processingCts != null && !_processingCts.IsCancellationRequested)
{
    _logger?.LogWarning("Queue processing already started");
    return;
}
// Race window: another thread can enter here simultaneously
_processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
```

Two concurrent callers can both pass the null-check and both start queue processing. **Fix:** Move the guard and assignment inside the `await _processingLock.WaitAsync(ct)` block.

---

### 1.3 Unguarded `int.Parse` on External API Data
**File:** `src/Modular.Core/Backends/NexusMods/NexusModsBackend.cs` (~line 310)
**Severity: HIGH**

```csharp
var modIdInt  = int.Parse(mod.ModId);   // throws FormatException on invalid input
var fileIdInt = int.Parse(file.FileId);
```

`ModId` and `FileId` are strings from an external REST API. Malformed responses will crash the download pipeline. **Fix:** Use `int.TryParse` and handle the failure case gracefully.

---

### 1.4 Timer Not Disposed — Memory Leak in MainWindowViewModel
**File:** `src/Modular.Gui/ViewModels/MainWindowViewModel.cs` (~line 84)
**Severity: HIGH**

```csharp
_rateLimitTimer = new System.Timers.Timer(5000);
_rateLimitTimer.Elapsed += OnRateLimitTimerElapsed;
_rateLimitTimer.Start();
// No IDisposable implementation, timer fires indefinitely
```

`System.Timers.Timer` holds a GC root; without explicit disposal the ViewModel is never collected and the callback fires forever. **Fix:** Implement `IDisposable`, stop and dispose the timer inside `Dispose()`.

---

### 1.5 VersionRange OR Semantics Not Implemented
**File:** `src/Modular.Core/Versioning/VersionRange.cs` (~line 35)
**Severity: HIGH**

```csharp
var orParts = range.Split("||", ...);
foreach (var orPart in orParts)
{
    var andParts = orPart.Split(' ', ...);
    foreach (var constraint in andParts)
    {
        result._constraints.Add(parsed!);  // all constraints go into ONE list
    }
}
```

All constraints from all OR branches are appended to the same list and evaluated with AND semantics. A range like `">=1.0 || >=2.0"` is evaluated as `>=1.0 AND >=2.0` instead of as a union. **Fix:** Model OR branches as separate constraint groups and evaluate each group independently.

---

## 2. Thread Safety & Concurrency

### 2.1 Race Condition in NexusRateLimiter
**File:** `src/Modular.Core/RateLimiting/NexusRateLimiter.cs` (~line 189)
**Severity: HIGH**

The `WaitIfNeededAsync` method releases the lock at the end of the `lock (_lock)` block, then waits asynchronously with `await Task.Delay(...)`. Another thread can call `ReserveRequest()` and exhaust limits during that delay, making the original check stale. **Fix:** Re-acquire the lock and re-evaluate `CanMakeRequest()` after the delay returns.

### 2.2 Inconsistent Timezone in Rate Limit Reset
**File:** `src/Modular.Core/RateLimiting/NexusRateLimiter.cs` (~line 162)
**Severity: MEDIUM**

```csharp
_dailyReset = now.Date.AddDays(1);   // .Date drops offset info
```

`now` is `DateTimeOffset.UtcNow`, but `.Date` returns a `DateTime` (Kind=Unspecified), losing timezone context. The comparison `now >= _dailyReset` may be off by hours depending on the host timezone. **Fix:** Use `now.UtcDateTime.Date.AddDays(1)` and store as `DateTime` (UTC) consistently.

### 2.3 Non-Thread-Safe BackendRegistry.Register
**File:** `src/Modular.Core/Backends/BackendRegistry.cs` (~line 18)
**Severity: MEDIUM**

```csharp
public void Register(IModBackend backend)
{
    _backends[backend.Id] = backend;  // Dictionary not thread-safe
}
```

If two backends are registered concurrently the dictionary could become corrupt. **Fix:** Use `ConcurrentDictionary<string, IModBackend>` or guard with a lock.

### 2.4 Non-Thread-Safe Single Connection in ModularDatabase
**File:** `src/Modular.Core/Database/ModularDatabase.cs` (~line 37)
**Severity: MEDIUM**

```csharp
if (_connection is { State: ConnectionState.Open })
    return _connection;     // no lock — two threads can both read null and both create a connection
_connection = new SqliteConnection(_connectionString);
await _connection.OpenAsync();
```

**Fix:** Use a `SemaphoreSlim(1,1)` guard around the check-and-create, or use a connection pool.

### 2.5 HttpClient.Timeout Set Without Synchronization
**File:** `src/Modular.FluentHttp/Implementation/FluentClient.cs` (~line 88)
**Severity: MEDIUM**

```csharp
public IFluentClient SetTimeout(TimeSpan timeout) { _httpClient.Timeout = timeout; return this; }
```

`HttpClient.Timeout` is not thread-safe. Concurrent callers writing different values produce unpredictable behavior. **Fix:** Do not mutate a shared `HttpClient`; configure timeout per-request via `CancellationTokenSource` or create a dedicated client per configuration.

### 2.6 Race Condition in DownloadQueueViewModel.ProcessQueueAsync
**File:** `src/Modular.Gui/ViewModels/DownloadQueueViewModel.cs` (~line 169)
**Severity: MEDIUM**

```csharp
_downloadCts = new CancellationTokenSource();
IsDownloading = true;
// ... processing ...
finally {
    IsDownloading = false;
    _downloadCts = null;   // another concurrent invocation sees null and starts again
}
```

**Fix:** Add a `SemaphoreSlim(1,1)` guard to ensure only one invocation runs at a time.

---

## 3. Resource Management & Memory Leaks

### 3.1 SqliteDownloadRepository Does Not Implement IDisposable
**File:** `src/Modular.Core/Database/SqliteDownloadRepository.cs`
**Severity: HIGH**

The repository takes a `ModularDatabase` (which wraps a `SqliteConnection`) but never disposes it, leaving the connection open indefinitely. **Fix:** Implement `IDisposable` and dispose `_db` on teardown.

### 3.2 HttpClient Instantiated Ad-hoc — Socket Exhaustion Risk
**Files:**
- `src/Modular.Gui/ViewModels/DownloadQueueViewModel.cs` (~line 100)
- `src/Modular.Gui/ViewModels/SettingsViewModel.cs` (~line 327)
- `src/Modular.Cli/Commands/Plugins/PluginsInstallCommand.cs` (~line 37)
- `src/Modular.Cli/Commands/Plugins/PluginsUpdateCommand.cs` (~line 31)
- `src/Modular.Cli/Commands/Plugins/PluginsListCommand.cs` (~line 59)

**Severity: MEDIUM**

Multiple `new HttpClient()` instances are created in short-lived contexts. HttpClient is intended to be long-lived; frequent creation exhausts socket descriptors and ignores DNS changes. **Fix:** Inject `IHttpClientFactory` (already available via `Microsoft.Extensions.Http`) or share a single static instance.

### 3.3 Async-over-Sync Blocking in FluentResponse — Thread-Pool Starvation
**File:** `src/Modular.FluentHttp/Implementation/FluentResponse.cs` (~line 50)
**Severity: HIGH**

```csharp
public string AsString()
{
    _cachedBody = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); // blocks thread
    return _cachedBody;
}
```

Blocking an async operation inside a synchronous wrapper on ASP.NET or other synchronization-context hosts can cause deadlocks. **Fix:** Make `AsStringAsync()` the primary API; expose `AsString()` only where callers have confirmed there is no synchronization context.

### 3.4 PluginComposer Leaves _compositionHost Null on Failure
**File:** `src/Modular.Core/Plugins/PluginComposer.cs` (~line 62)
**Severity: MEDIUM**

If `configuration.CreateContainer()` throws, `_compositionHost` remains null. Subsequent `GetExports<T>()` calls silently return empty collections, making the error invisible. **Fix:** Either propagate the exception or set a `_isInitialized` flag and throw on subsequent calls.

### 3.5 DownloadQueue Events Not Unsubscribable — Potential Leak
**File:** `src/Modular.Core/Downloads/DownloadQueue.cs` (~line 28)
**Severity: LOW-MEDIUM**

```csharp
public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;
public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;
```

Subscribers that forget to unsubscribe keep the queue alive. The queue class should implement `IDisposable` and clear its event invocation lists on disposal, or use `WeakEventManager`.

---

## 4. Logical Errors

### 4.1 Retry Count Off-by-One — Extra Attempt Executed
**Files:**
- `src/Modular.Core/Downloads/DownloadQueue.cs` (~line 279)
- `src/Modular.FluentHttp/Implementation/FluentClient.cs` (~line 99)

**Severity: MEDIUM**

In DownloadQueue.cs, `RetryCount` is incremented **before** the `>= MaxRetries` check, so with `MaxRetries=3` the loop runs 4 attempts. In FluentClient.cs, the loop is `for (attempt = 0; attempt <= maxRetries; ...)`, also producing `maxRetries+1` iterations. Both should be reconciled to match documented behavior.

### 4.2 ConflictResolver.Result.Success — Inconsistent State
**File:** `src/Modular.Core/Dependencies/ConflictResolver.cs` (~line 39, 55)
**Severity: MEDIUM**

```csharp
result.Success = true;                         // line 39 — optimistic default
// ...
if (strategy == Automatic && result.Suggestions.Count > 0)
    result.Success = result.AppliedSuggestions.Count > 0;  // line 55 — may overwrite to false
```

When strategy is not `Automatic`, `Success` stays `true` even though no suggestions were applied. The caller cannot distinguish "no conflict" from "conflict present but manual action needed". **Fix:** Set `Success` to `true` only when conflicts are actually absent.

### 4.3 GreedyDependencyResolver — Unguarded Dictionary Access
**File:** `src/Modular.Core/Dependencies/GreedyDependencyResolver.cs` (~line 95)
**Severity: MEDIUM**

```csharp
var modConstraints = constraints[modId];  // KeyNotFoundException if modId missing
```

If a dependency was enqueued but its entry was never initialised in `constraints`, this throws. **Fix:** Use `constraints.GetValueOrDefault(modId)` and handle the null case.

### 4.4 FileConflictIndex — Accepts Empty Game Path
**File:** `src/Modular.Core/Dependencies/FileConflictIndex.cs` (~line 20)
**Severity: MEDIUM**

`RegisterFile` does not validate that `gamePath` is non-empty. An empty path normalises to an empty string key, causing all empty-path registrations to appear as the same file and producing spurious conflicts. **Fix:** Throw `ArgumentException` when `gamePath` is null or whitespace.

### 4.5 GameBananaBackend.Capabilities Declares None Despite Exposing API Methods
**File:** `src/Modular.Core/Backends/GameBanana/GameBananaBackend.cs` (~line 32)
**Severity: MEDIUM**

```csharp
public BackendCapabilities Capabilities => BackendCapabilities.None;
```

Code in other layers may short-circuit based on `Capabilities == None`, never calling the backend's actual methods even though they are functional. This is a Liskov Substitution Principle violation: the type claims no capabilities but the implementation has them. **Fix:** Expose the actual capabilities the backend supports.

### 4.6 DependencyEdge — Unused `EdgeType` Enum
**File:** `src/Modular.Core/Dependencies/DependencyEdge.cs` (~line 78)
**Severity: LOW**

An `EdgeType` enum is defined but never referenced; the code uses `DependencyType` from a different namespace instead. This indicates either dead code or an incomplete refactor. **Fix:** Remove `EdgeType` if not needed, or replace `DependencyType` usages with it.

---

## 5. Exception Handling Anti-patterns

### 5.1 Silent Return on Credential Load Failure
**File:** `src/Modular.Core/Security/ConfigCredentialStore.cs` (~line 111)
**Severity: HIGH**

```csharp
catch (Exception ex)
{
    _logger?.LogError(ex, "Failed to load credentials from {Path}", _credentialsPath);
    // method returns without indicating failure; callers get an empty store
}
```

Callers proceed as if credentials were loaded, producing confusing downstream API-authentication failures. **Fix:** Expose a `bool IsLoaded` property, or throw a `ConfigurationException`.

### 5.2 Bare `catch` Swallows OutOfMemoryException and StackOverflowException
**Files:**
- `src/Modular.Core/ErrorHandling/ErrorBoundary.cs` (~line 114)
- `src/Modular.Core/Http/HttpCache.cs` (~line 149)
- `src/Modular.Gui/Services/DownloadHistoryService.cs` (~lines 133, 159)

**Severity: MEDIUM**

```csharp
catch
{
    return default;   // catches EVERYTHING including fatal CLR exceptions
}
```

`OutOfMemoryException`, `StackOverflowException`, `ThreadAbortException` should never be caught and silently ignored. **Fix:** Replace `catch` with `catch (Exception ex)` and re-throw if `ex` is a fatal CLR exception (e.g., check `ex is OutOfMemoryException or StackOverflowException`), or catch only specific recoverable types.

### 5.3 GameBananaBackend Returns Empty List on API Error
**File:** `src/Modular.Core/Backends/GameBanana/GameBananaBackend.cs` (~line 146)
**Severity: MEDIUM**

```csharp
catch (Exception ex)
{
    _logger?.LogError(ex, "Failed to fetch subscribed mods...");
}
// returns empty list — callers cannot distinguish "no mods" from "error"
```

**Fix:** Either propagate the exception, or return a `Result<List<BackendMod>>` discriminated union that carries error state.

### 5.4 PluginLoader Catches All Exceptions During Manifest Read
**File:** `src/Modular.Core/Plugins/PluginLoader.cs` (~line 94)
**Severity: MEDIUM**

`catch (Exception)` catches both recoverable IO errors and unrecoverable runtime errors. **Fix:** Catch `IOException`, `JsonException`, and `UnauthorizedAccessException` individually; let others propagate.

---

## 6. Architectural Violations

### 6.1 DownloadEngine Violates Single Responsibility Principle
**File:** `src/Modular.Core/Downloads/DownloadEngine.cs`
**Severity: MEDIUM**

A single class handles HTTP transport, file I/O, hash computation, progress reporting, and resume logic. This makes the class hard to test and extend. **Fix:** Extract `HashCalculator`, `ProgressReporter`, and `ResumeHandler` into separate collaborators.

### 6.2 PluginLoader Uses Permissive ErrorBoundary — Silent Discovery Failures
**File:** `src/Modular.Core/Plugins/PluginLoader.cs` (~line 36)
**Severity: MEDIUM**

```csharp
_errorBoundary = new ErrorBoundary(ErrorBoundaryPolicy.Permissive, logger);
// ...
var result = _errorBoundary.Execute(..., () => { /* discover installers */ },
    new List<IModInstaller>()).Value ?? new List<IModInstaller>();
```

A failed discovery returns an empty list, which is indistinguishable from a plugin that exposes no installers. **Fix:** Use a `Result<T>` return type so callers can distinguish the two cases.

### 6.3 PluginLoader Instantiates Plugins With Activator.CreateInstance — No Constructor Injection
**File:** `src/Modular.Core/Plugins/PluginLoader.cs` (~line 143)
**Severity: MEDIUM**

```csharp
var metadataInstance = Activator.CreateInstance(metadataTypes[0]) as IPluginMetadata;
```

Plugins requiring constructor arguments will silently fail at runtime. **Fix:** Require plugins to have a parameterless constructor (and document it), or pass a service-locator/factory into the plugin assembly load context.

### 6.4 FluentResponse Mixes Sync and Async APIs — Broken HTTP Abstraction
**File:** `src/Modular.FluentHttp/Implementation/FluentResponse.cs`
**Severity: MEDIUM**

The `IFluentResponse` interface exposes both synchronous (`AsString()`) and asynchronous (`AsStringAsync()`) members. The sync implementation blocks an async operation. The entire response model should be async-first.

### 6.5 MVVM Violation — Synchronous File I/O on UI Thread in LibraryViewModel
**File:** `src/Modular.Gui/ViewModels/LibraryViewModel.cs` (~line 127)
**Severity: MEDIUM**

```csharp
partial void OnSelectedDomainChanged(GameDomainItem? value)
{
    LoadModFolders(value);  // synchronous directory enumeration on the UI thread
}
```

Large mod directories will freeze the UI. **Fix:** Make `OnSelectedDomainChanged` an async handler (or fire a command) and run directory enumeration on a background thread.

### 6.6 Inconsistent Error Strategy Between Repositories
**Severity: MEDIUM**

`DownloadDatabase` throws `ParseException` on JSON parse failure; `ModMetadataCache` silently resets its data. Callers must handle the two sibling classes differently, violating the Principle of Least Surprise. **Fix:** Standardise on throwing (or returning a `Result`) so callers have a uniform contract.

### 6.7 CLI Commands Instantiate HttpClient Directly — No DI
**Files:** Multiple `src/Modular.Cli/Commands/Plugins/` files
**Severity: MEDIUM**

CLI commands create `new HttpClient()` inline, bypassing the dependency-injection container that provides rate-limiting, retry, and authentication middleware. **Fix:** Inject `IModularHttpClient` or use `IHttpClientFactory`.

---

## 7. Security Issues

### 7.1 Credentials Stored in Plaintext; Windows File Permissions Unset
**File:** `src/Modular.Core/Security/ConfigCredentialStore.cs` (~line 134)
**Severity: HIGH**

- Credentials are serialised to a JSON file without encryption.
- Unix restrictive permissions (`UserRead|UserWrite`) are set inside a bare `catch` block that silently ignores failures.
- Windows receives no permission hardening at all.

**Fix:** On Windows, use DPAPI (`ProtectedData.Protect`). On Unix, apply permissions before writing. Propagate or at least log permission errors.

### 7.2 String-Interpolated SQL in PRAGMA Statement
**File:** `src/Modular.Core/Database/ModularDatabase.cs` (~line 80)
**Severity: LOW-MEDIUM**

```csharp
cmd.CommandText = $"PRAGMA user_version = {version};";
```

Although `version` is currently an `int`, the pattern should use parameterised queries uniformly to prevent future accidental injection. **Fix:** Use `$"PRAGMA user_version = @v"` with a bound parameter, or cast `version` explicitly and document why interpolation is safe here.

### 7.3 NexusApiKey Stored in Plain AppSettings Without Validation
**File:** `src/Modular.Core/Configuration/AppSettings.cs` (~line 13)
**Severity: LOW-MEDIUM**

No format validation for the API key. An empty string or a key of incorrect length is accepted silently, producing confusing HTTP 401 errors later. **Fix:** Validate key format on assignment or during configuration binding.

---

## 8. Test Quality Issues

### 8.1 UtilityTests Ignores Expected Output — Not a Real Test
**File:** `tests/Modular.Core.Tests/UtilityTests.cs` (~line 10)
**Severity: LOW**

```csharp
[Theory]
[InlineData("~/Documents", "/home")]
[InlineData("/absolute/path", "/absolute/path")]
[InlineData("", "")]
public void FileUtils_ExpandPath_HandlesVariousPaths(string input, string _)  // expected ignored
{
    var result = FileUtils.ExpandPath(input);
    result.Should().NotBeNull();   // only checks non-null, not correctness
}
```

Regressions in path-expansion logic will not be caught. **Fix:** Assert `result.Should().Be(expected)`.

### 8.2 DatabaseTests Missing Edge Cases
**File:** `tests/Modular.Core.Tests/DatabaseTests.cs`
**Severity: LOW**

Missing coverage for:
- `RemoveRecord` with a non-existent record
- `GetRecordsByDomain` with an empty domain string
- `UpdateVerification` on a non-existent record
- Concurrent access patterns
- Corrupted database file recovery

### 8.3 Mock Backends Return Only Happy-Path Data
**File:** `tests/Modular.Core.Tests/Backends/BackendRegistryTests.cs` (~line 169)
**Severity: LOW**

The `MockBackend` always returns empty lists and never simulates errors, validation failures, or partial data. Tests using it cannot verify error-handling paths.

---

## 9. Performance Issues

### 9.1 FilteredMods Re-enumerated on Every SelectAll Call
**File:** `src/Modular.Gui/ViewModels/ModListViewModel.cs` (~line 195)
**Severity: LOW**

`FilteredMods` is an `IEnumerable` that recalculates on each enumeration. `SelectAll()` may enumerate it multiple times (once for iteration and once for `UpdateSelectedCount`), causing quadratic work with large lists. **Fix:** Materialise `FilteredMods` into a `List<T>` before iterating.

### 9.2 Metadata Lookup Called Per Directory — O(n) API Calls
**File:** `src/Modular.Core/Utilities/FileUtils.cs` (~line 108)
**Severity: LOW-MEDIUM**

`metadataLookup` is invoked for every directory and every sub-directory during scan. With thousands of mods this can result in tens of thousands of lookup calls. **Fix:** Batch the lookups or build a lookup dictionary before scanning.

---

## 10. Summary Table

| # | File | Issue | Severity |
|---|------|-------|----------|
| 1.1 | RateLimitScheduler.cs | `lock` inside async after `await` — deadlock | **CRITICAL** |
| 1.2 | DownloadQueue.cs | Double-check locking outside lock — race | **CRITICAL** |
| 1.3 | NexusModsBackend.cs | `int.Parse` on external data — crash | **HIGH** |
| 1.4 | MainWindowViewModel.cs | Timer never disposed — memory leak | **HIGH** |
| 1.5 | VersionRange.cs | OR semantics not implemented | **HIGH** |
| 2.1 | NexusRateLimiter.cs | Rate limit check stale after async delay | **HIGH** |
| 2.2 | NexusRateLimiter.cs | `.Date` strips timezone — reset offset wrong | **MEDIUM** |
| 2.3 | BackendRegistry.cs | `Register` not thread-safe | **MEDIUM** |
| 2.4 | ModularDatabase.cs | Single connection not thread-safe | **MEDIUM** |
| 2.5 | FluentClient.cs | `HttpClient.Timeout` written concurrently | **MEDIUM** |
| 2.6 | DownloadQueueViewModel.cs | Concurrent `ProcessQueueAsync` race | **MEDIUM** |
| 3.1 | SqliteDownloadRepository.cs | No `IDisposable` — connection leak | **HIGH** |
| 3.2 | Multiple files | `new HttpClient()` ad-hoc — socket exhaustion | **MEDIUM** |
| 3.3 | FluentResponse.cs | `.GetAwaiter().GetResult()` — starvation/deadlock | **HIGH** |
| 3.4 | PluginComposer.cs | Null `_compositionHost` silent on failure | **MEDIUM** |
| 3.5 | DownloadQueue.cs | Events not clearable — leak | **LOW** |
| 4.1 | DownloadQueue.cs / FluentClient.cs | Retry off-by-one | **MEDIUM** |
| 4.2 | ConflictResolver.cs | `Success` inconsistent without auto-resolve | **MEDIUM** |
| 4.3 | GreedyDependencyResolver.cs | Unguarded dictionary key access | **MEDIUM** |
| 4.4 | FileConflictIndex.cs | Empty `gamePath` accepted — false conflicts | **MEDIUM** |
| 4.5 | GameBananaBackend.cs | `Capabilities = None` despite functional API | **MEDIUM** |
| 4.6 | DependencyEdge.cs | Unused `EdgeType` enum — dead code | **LOW** |
| 5.1 | ConfigCredentialStore.cs | Silent failure on credential load | **HIGH** |
| 5.2 | ErrorBoundary.cs et al. | Bare `catch` swallows fatal exceptions | **MEDIUM** |
| 5.3 | GameBananaBackend.cs | Empty list on error hides failure | **MEDIUM** |
| 5.4 | PluginLoader.cs | `catch (Exception)` too broad | **MEDIUM** |
| 6.1 | DownloadEngine.cs | SRP violation — too many concerns | **MEDIUM** |
| 6.2 | PluginLoader.cs | Silent discovery failure via `ErrorBoundary` | **MEDIUM** |
| 6.3 | PluginLoader.cs | `Activator.CreateInstance` — no DI | **MEDIUM** |
| 6.4 | FluentResponse.cs | Sync/async API mismatch | **MEDIUM** |
| 6.5 | LibraryViewModel.cs | Sync file I/O on UI thread | **MEDIUM** |
| 6.6 | Repositories | Inconsistent error strategies | **MEDIUM** |
| 6.7 | CLI commands | `new HttpClient()` bypasses DI | **MEDIUM** |
| 7.1 | ConfigCredentialStore.cs | Plaintext credentials; Windows unprotected | **HIGH** |
| 7.2 | ModularDatabase.cs | String-interpolated SQL (PRAGMA) | **LOW** |
| 7.3 | AppSettings.cs | API key not validated | **LOW** |
| 8.1 | UtilityTests.cs | Expected value ignored in test | **LOW** |
| 8.2 | DatabaseTests.cs | Missing edge-case coverage | **LOW** |
| 8.3 | BackendRegistryTests.cs | Mock only returns happy-path | **LOW** |
| 9.1 | ModListViewModel.cs | `FilteredMods` re-enumerated | **LOW** |
| 9.2 | FileUtils.cs | Per-directory metadata lookup | **LOW** |

---

## Recommended Fix Priority

1. **Immediate (CRITICAL / HIGH):**
   - Fix `lock` inside async in `RateLimitScheduler.cs`
   - Fix double-check locking race in `DownloadQueue.cs`
   - Replace `int.Parse` with `int.TryParse` in `NexusModsBackend.cs`
   - Implement `IDisposable` in `MainWindowViewModel.cs` and `SqliteDownloadRepository.cs`
   - Fix OR semantics in `VersionRange.cs`
   - Fix `FluentResponse` blocking async calls
   - Encrypt or protect credential storage in `ConfigCredentialStore.cs`
   - Fix `ConfigCredentialStore` to propagate load failure

2. **Short-term (MEDIUM):**
   - Thread-safe `BackendRegistry`, `ModularDatabase`, `DownloadQueueViewModel`
   - Consistent exception strategies across repositories
   - Replace ad-hoc `HttpClient` with injected factory
   - Fix `GameBananaBackend.Capabilities` to reflect reality
   - Guard `GreedyDependencyResolver` dictionary access
   - Validate `gamePath` in `FileConflictIndex`
   - Fix `ConflictResolver.Success` semantics

3. **Long-term (LOW / Refactoring):**
   - Decompose `DownloadEngine` per SRP
   - Make `PluginLoader` DI-aware
   - Standardise retry count semantics
   - Add edge-case tests and fix test assertions
   - Remove `EdgeType` dead code
