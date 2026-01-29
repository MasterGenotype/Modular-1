# C# .NET Migration Instructions for Modular

This document provides instructions for an AI coding agent to rewrite the Modular C++ codebase in C# .NET 8+.

---

## Project Overview

**Modular** is a CLI application that automates downloading, organizing, and managing game modifications from NexusMods and GameBanana. The C++ codebase uses libcurl, nlohmann/json, and OpenSSL.

**Key Features to Preserve:**
- Fluent HTTP client with middleware/filter pipeline
- NexusMods and GameBanana API integrations
- Rate limiting with state persistence
- JSON-based download history database
- Real-time terminal progress UI
- MD5 verification of downloads
- Mod folder renaming and category organization

---

## 1. Project Structure

Create a solution with the following structure:

```
Modular/
├── Modular.sln
├── src/
│   ├── Modular.Core/              # Core library (class library)
│   │   ├── Configuration/
│   │   │   ├── AppSettings.cs
│   │   │   └── ConfigurationService.cs
│   │   ├── Database/
│   │   │   ├── DownloadRecord.cs
│   │   │   └── DownloadDatabase.cs
│   │   ├── Http/
│   │   │   ├── ModularHttpClient.cs
│   │   │   └── RetryPolicy.cs
│   │   ├── RateLimiting/
│   │   │   ├── IRateLimiter.cs
│   │   │   └── NexusRateLimiter.cs
│   │   ├── Services/
│   │   │   ├── NexusModsService.cs
│   │   │   ├── GameBananaService.cs
│   │   │   ├── RenameService.cs
│   │   │   └── TrackingValidatorService.cs
│   │   ├── Models/
│   │   │   ├── TrackedMod.cs
│   │   │   ├── ModFile.cs
│   │   │   └── ValidationResult.cs
│   │   ├── Exceptions/
│   │   │   ├── ModularException.cs
│   │   │   ├── RateLimitException.cs
│   │   │   ├── ApiException.cs
│   │   │   └── NetworkException.cs
│   │   └── Utilities/
│   │       ├── FileUtils.cs
│   │       └── Md5Calculator.cs
│   │
│   ├── Modular.FluentHttp/        # Fluent HTTP client (class library)
│   │   ├── Interfaces/
│   │   │   ├── IFluentClient.cs
│   │   │   ├── IRequest.cs
│   │   │   ├── IResponse.cs
│   │   │   └── IHttpFilter.cs
│   │   ├── Implementation/
│   │   │   ├── FluentClient.cs
│   │   │   ├── FluentRequest.cs
│   │   │   ├── FluentResponse.cs
│   │   │   └── RequestOptions.cs
│   │   ├── Filters/
│   │   │   ├── AuthenticationFilter.cs
│   │   │   ├── RateLimitFilter.cs
│   │   │   ├── LoggingFilter.cs
│   │   │   └── RetryFilter.cs
│   │   ├── Retry/
│   │   │   ├── IRetryConfig.cs
│   │   │   └── RetryCoordinator.cs
│   │   └── Factory.cs
│   │
│   └── Modular.Cli/               # CLI executable (console app)
│       ├── Program.cs
│       ├── Commands/
│       │   ├── NexusCommand.cs
│       │   └── GameBananaCommand.cs
│       └── UI/
│           └── LiveProgressDisplay.cs
│
└── tests/
    ├── Modular.Core.Tests/
    │   ├── ConfigurationTests.cs
    │   ├── DatabaseTests.cs
    │   └── UtilityTests.cs
    └── Modular.FluentHttp.Tests/
        └── FluentClientTests.cs
```

---

## 2. Dependencies (NuGet Packages)

### Modular.Core
```xml
<PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Options" Version="8.0.0" />
<PackageReference Include="System.Text.Json" Version="8.0.0" />
```

### Modular.FluentHttp
```xml
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
<PackageReference Include="System.Text.Json" Version="8.0.0" />
```

### Modular.Cli
```xml
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
<PackageReference Include="Spectre.Console" Version="0.49.1" />
<PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
```

### Test Projects
```xml
<PackageReference Include="xunit" Version="2.6.6" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.5.6" />
<PackageReference Include="FluentAssertions" Version="6.12.0" />
<PackageReference Include="Moq" Version="4.20.70" />
```

---

## 3. Core Component Mappings

### 3.1 Configuration (`Config` → `AppSettings`)

**C++ Source:** `include/core/Config.h`, `src/core/Config.cpp`

```csharp
// AppSettings.cs
public class AppSettings
{
    public string NexusApiKey { get; set; } = string.Empty;
    public string GameBananaUserId { get; set; } = string.Empty;
    public string ModsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Games", "Mods-Lists");
    public List<string> DefaultCategories { get; set; } = ["main", "optional"];
    public bool AutoRename { get; set; } = true;
    public bool OrganizeByCategory { get; set; } = true;
    public bool VerifyDownloads { get; set; } = false;
    public bool ValidateTracking { get; set; } = false;
    public int MaxConcurrentDownloads { get; set; } = 1;
    public bool Verbose { get; set; } = false;
    public string CookieFile { get; set; } = "~/Documents/cookies.txt";
}
```

**Key Behavior:**
- Config file location: `~/.config/Modular/config.json`
- Environment variables override config file values
- `NEXUS_API_KEY` → `NexusApiKey`
- `GB_USER_ID` → `GameBananaUserId`

### 3.2 HTTP Client (`HttpClient` → `ModularHttpClient`)

**C++ Source:** `include/core/HttpClient.h`, `src/core/HttpClient.cpp`

```csharp
public class ModularHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IRateLimiter _rateLimiter;
    private readonly ILogger<ModularHttpClient> _logger;
    private RetryPolicy _retryPolicy = new();

    public async Task<HttpResponseMessage> GetAsync(string url,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);

    public async Task<bool> DownloadFileAsync(string url, string outputPath,
        Dictionary<string, string>? headers = null,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default);

    public void SetRetryPolicy(RetryPolicy policy);
}

public class RetryPolicy
{
    public int MaxRetries { get; set; } = 3;
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 16000;
    public bool ExponentialBackoff { get; set; } = true;
}
```

**Key Behavior:**
- Use `HttpClient` with `IHttpClientFactory` for connection pooling
- Progress callbacks throttled to ~10 updates/second
- Retry on 5xx and 429 errors, NOT on 4xx (except 429)
- Parse rate limit headers from responses

### 3.3 Rate Limiter (`RateLimiter` → `NexusRateLimiter`)

**C++ Source:** `include/core/RateLimiter.h`, `src/core/RateLimiter.cpp`

```csharp
public interface IRateLimiter
{
    void UpdateFromHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers);
    bool CanMakeRequest();
    Task WaitIfNeededAsync(CancellationToken cancellationToken = default);
    Task SaveStateAsync(string path);
    Task LoadStateAsync(string path);
}
```

**NexusMods Rate Limits:**
- Daily: 20,000 requests/24 hours (resets at 00:00 GMT)
- Hourly: 500 requests/hour (resets on the hour)

**Headers to Parse:**
- `x-rl-daily-remaining`
- `x-rl-daily-reset` (Unix timestamp)
- `x-rl-hourly-remaining`
- `x-rl-hourly-reset` (Unix timestamp)

**Critical:** Store reset timestamps, not just remaining counts.

### 3.4 Database (`Database` → `DownloadDatabase`)

**C++ Source:** `include/core/Database.h`, `src/core/Database.cpp`

```csharp
public class DownloadRecord
{
    public string GameDomain { get; set; } = string.Empty;
    public int ModId { get; set; }
    public int FileId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string Filepath { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Md5Expected { get; set; } = string.Empty;
    public string Md5Actual { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime DownloadTime { get; set; }
    public string Status { get; set; } = string.Empty; // "success", "verified", "failed"
    public string? ErrorMessage { get; set; }
}

public class DownloadDatabase
{
    public void AddRecord(DownloadRecord record);
    public DownloadRecord? FindRecord(string gameDomain, int modId, int fileId);
    public IEnumerable<DownloadRecord> GetRecordsByDomain(string gameDomain);
    public bool IsDownloaded(string gameDomain, int modId, int fileId);
    public void UpdateVerification(string gameDomain, int modId, int fileId,
        string md5Actual, bool verified);
    public Task SaveAsync();
    public Task LoadAsync();
}
```

**Storage Format:** Human-readable JSON file at configurable path.

### 3.5 NexusMods Service

**C++ Source:** `include/core/NexusMods.h`, `src/core/NexusMods.cpp`

```csharp
public class NexusModsService
{
    public async Task<List<TrackedMod>> GetTrackedModsAsync(CancellationToken ct = default);
    public async Task<Dictionary<int, List<int>>> GetFileIdsAsync(
        IEnumerable<int> modIds, string gameDomain,
        string? filterCategories = null, CancellationToken ct = default);
    public async Task<Dictionary<(int modId, int fileId), string>> GenerateDownloadLinksAsync(
        Dictionary<int, List<int>> fileIds, string gameDomain, CancellationToken ct = default);
    public async Task DownloadFilesAsync(string gameDomain,
        IProgress<(string status, int completed, int total)>? progress = null,
        bool dryRun = false, bool force = false, CancellationToken ct = default);
}
```

**API Endpoints:**
| Endpoint | Purpose |
|----------|---------|
| `/v1/users/validate.json` | Validate API key |
| `/v1/user/tracked_mods.json` | Get tracked mods |
| `/v1/games/{domain}/mods/{id}/files` | Get mod files |
| `/v1/games/{domain}/mods/{id}/files/{fileId}/download_link.json` | Generate download link |
| `/v1/games/{domain}.json` | Get game info (includes categories array) |

**Headers:** `apikey: {API_KEY}`, `accept: application/json`

### 3.6 GameBanana Service

**C++ Source:** `include/core/GameBanana.h`, `src/core/GameBanana.cpp`

```csharp
public class GameBananaService
{
    public async Task<List<(string modId, string modName)>> FetchSubscribedModsAsync(
        string userId, CancellationToken ct = default);
    public async Task<List<string>> FetchModFileUrlsAsync(
        string modId, CancellationToken ct = default);
    public async Task DownloadModFilesAsync(string modId, string modName, string baseDir,
        IProgress<(string status, int completed, int total)>? progress = null,
        CancellationToken ct = default);
}
```

**API Endpoints:**
- `/apiv10/User/{userId}/Submissions` - Get subscribed mods
- `/apiv10/ModProfile/{modId}/Files` - Get mod files

### 3.7 Rename Service

**C++ Source:** `include/core/Rename.h`, `src/core/Rename.cpp`

```csharp
public class RenameService
{
    public IEnumerable<string> GetGameDomainNames(string modsDir);
    public IEnumerable<string> GetModIds(string gameDomainPath);
    public async Task<string> FetchModNameAsync(string gameDomain, string modId, CancellationToken ct = default);
    public async Task<int> ReorganizeAndRenameModsAsync(string gameDomainPath,
        bool organizeByCategory = true, CancellationToken ct = default);
    public async Task<Dictionary<int, string>> FetchGameCategoriesAsync(
        string gameDomain, CancellationToken ct = default);
    public async Task<int> RenameCategoryFoldersAsync(string gameDomainPath, CancellationToken ct = default);
}
```

**Output Structure:**
```
~/Mods/
├── skyrimspecialedition/
│   ├── Weapons/
│   │   └── Better_Swords/
│   │       └── Better Swords-1234-1-0.zip
│   └── Armor/
│       └── Steel_Plate_Redux/
│           └── Steel Plate Redux-9012-1-5.zip
```

---

## 4. Fluent HTTP Client

**C++ Source:** `include/fluent/`, `src/fluent/`

This is a custom fluent API wrapper around HTTP operations. Translate the full interface.

### 4.1 Core Interfaces

```csharp
public interface IFluentClient : IDisposable
{
    // HTTP methods
    IRequest GetAsync(string resource);
    IRequest PostAsync(string resource);
    IRequest PutAsync(string resource);
    IRequest PatchAsync(string resource);
    IRequest DeleteAsync(string resource);
    IRequest HeadAsync(string resource);
    IRequest SendAsync(HttpMethod method, string resource);

    // Configuration
    IFluentClient SetBaseUrl(string baseUrl);
    IFluentClient SetOptions(RequestOptions options);
    IFluentClient SetUserAgent(string userAgent);

    // Authentication
    IFluentClient SetAuthentication(string scheme, string parameter);
    IFluentClient SetBearerAuth(string token);
    IFluentClient SetBasicAuth(string username, string password);
    IFluentClient ClearAuthentication();

    // Filters (middleware)
    FilterCollection Filters { get; }
    IFluentClient AddFilter(IHttpFilter filter);

    // Retry
    IFluentClient SetRetryPolicy(int maxRetries, int initialDelayMs = 1000,
        int maxDelayMs = 16000, bool exponentialBackoff = true);
    IFluentClient DisableRetries();

    // Rate limiting
    IFluentClient SetRateLimiter(IRateLimiter rateLimiter);

    // Timeouts
    IFluentClient SetConnectionTimeout(TimeSpan timeout);
    IFluentClient SetRequestTimeout(TimeSpan timeout);

    // Logging
    IFluentClient SetLogger(ILogger logger);
}

public interface IRequest
{
    // URL parameters
    IRequest WithArgument(string key, string value);
    IRequest WithArguments(IEnumerable<KeyValuePair<string, string>> args);

    // Headers
    IRequest WithHeader(string key, string value);
    IRequest WithHeaders(IDictionary<string, string> headers);
    IRequest WithoutHeader(string key);

    // Authentication
    IRequest WithAuthentication(string scheme, string parameter);
    IRequest WithBearerAuth(string token);
    IRequest WithBasicAuth(string username, string password);

    // Body
    IRequest WithBody(Func<IBodyBuilder, RequestBody> builder);
    IRequest WithBody(RequestBody body);
    IRequest WithJsonBody<T>(T value);
    IRequest WithFormBody(IEnumerable<KeyValuePair<string, string>> fields);

    // Options
    IRequest WithOptions(RequestOptions options);
    IRequest WithIgnoreHttpErrors(bool ignore = true);
    IRequest WithTimeout(TimeSpan timeout);
    IRequest WithCancellation(CancellationToken token);

    // Filters
    IRequest WithFilter(IHttpFilter filter);
    IRequest WithoutFilter(IHttpFilter filter);
    IRequest WithRetryConfig(IRetryConfig config);
    IRequest WithNoRetry();

    // Execution (sync wrappers call async internally)
    IResponse AsResponse();
    string AsString();
    JsonDocument AsJson();
    void DownloadTo(string path, IProgress<(long downloaded, long total)>? progress = null);

    // Async execution
    Task<IResponse> AsResponseAsync();
    Task<string> AsStringAsync();
    Task<JsonDocument> AsJsonAsync();
    Task DownloadToAsync(string path, IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IResponse
{
    // Status
    bool IsSuccessStatusCode { get; }
    int StatusCode { get; }
    string StatusReason { get; }

    // Headers
    IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }
    string? GetHeader(string name);
    bool HasHeader(string name);
    string? ContentType { get; }
    long? ContentLength { get; }

    // Body access
    string AsString();
    byte[] AsByteArray();
    JsonDocument AsJson();
    T As<T>();
    List<T> AsArray<T>();

    // Async body access
    Task<string> AsStringAsync();
    Task<byte[]> AsByteArrayAsync();
    Task<JsonDocument> AsJsonAsync();

    // File operations
    void SaveToFile(string path, IProgress<(long downloaded, long total)>? progress = null);
    Task SaveToFileAsync(string path, IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default);

    // Metadata
    string EffectiveUrl { get; }
    TimeSpan Elapsed { get; }
    bool WasRedirected { get; }
}

public interface IHttpFilter
{
    void OnRequest(IRequest request);
    void OnResponse(IResponse response, bool httpErrorAsException);
    string Name { get; }
    int Priority { get; }
}
```

### 4.2 Built-in Filters

Implement these filters using `DelegatingHandler` pattern:
- `AuthenticationFilter` - Adds API key header
- `RateLimitFilter` - Enforces rate limits, blocks if needed
- `LoggingFilter` - Logs requests/responses
- `RetryFilter` - Implements retry with exponential backoff

### 4.3 Factory

```csharp
public static class FluentClientFactory
{
    public static IFluentClient Create(string? baseUrl = null);
    public static IFluentClient Create(string baseUrl, IRateLimiter rateLimiter,
        ILogger? logger = null);
}
```

---

## 5. Exception Hierarchy

**C++ Source:** `include/core/Exceptions.h`

```csharp
public class ModularException : Exception
{
    public string? Url { get; set; }
    public string? Context { get; set; }
    public string? ResponseSnippet { get; set; }
}

public class NetworkException : ModularException
{
    public int? CurlCode { get; set; } // Map to HttpRequestException codes
}

public class ApiException : ModularException
{
    public int StatusCode { get; set; }
    public string? RequestId { get; set; }
}

public class RateLimitException : ApiException
{
    public int? RetryAfterSeconds { get; set; }
}

public class AuthException : ApiException { }

public class ParseException : ModularException
{
    public string? JsonSnippet { get; set; }
}

public class FileSystemException : ModularException { }

public class ConfigException : ModularException { }
```

---

## 6. CLI Application

**C++ Source:** `src/cli/main.cpp`, `src/cli/LiveUI.cpp`

### 6.1 Entry Point

Use `System.CommandLine` for argument parsing:

```csharp
var rootCommand = new RootCommand("Modular - Game mod download manager");

var domainArg = new Argument<string?>("domain", () => null, "Game domain (e.g., skyrimspecialedition)");
var categoriesOption = new Option<string[]>("--categories", "Filter by categories");
var dryRunOption = new Option<bool>("--dry-run", "Show what would be downloaded");
var forceOption = new Option<bool>("--force", "Re-download existing files");
var organizeOption = new Option<bool>("--organize-by-category", "Create category subdirectories");

rootCommand.AddArgument(domainArg);
rootCommand.AddOption(categoriesOption);
// ... etc
```

### 6.2 Interactive Menu

```
=== Modular ===
1. GameBanana
2. NexusMods
3. Rename
0. Exit
Choose: _
```

### 6.3 Progress Display

Use `Spectre.Console` for terminal UI:

```csharp
await AnsiConsole.Progress()
    .Columns(new ProgressColumn[]
    {
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new RemainingTimeColumn(),
        new SpinnerColumn()
    })
    .StartAsync(async ctx =>
    {
        var task = ctx.AddTask("[green]Downloading mods[/]");
        // Update task.Value and task.MaxValue
    });
```

---

## 7. Testing

Use xUnit with FluentAssertions and Moq.

### 7.1 Configuration Tests

```csharp
public class ConfigurationTests
{
    [Fact]
    public void SaveAndLoad_PreservesValues()
    {
        // Arrange
        var config = new AppSettings { NexusApiKey = "test_key" };

        // Act
        await configService.SaveAsync(config, testPath);
        var loaded = await configService.LoadAsync(testPath);

        // Assert
        loaded.NexusApiKey.Should().Be("test_key");
    }

    [Fact]
    public void EnvironmentVariables_TakePrecedence()
    {
        Environment.SetEnvironmentVariable("NEXUS_API_KEY", "env_key");
        // Test that env var overrides config file
    }
}
```

### 7.2 Database Tests

```csharp
public class DatabaseTests
{
    [Fact]
    public void AddRecord_CanBeRetrieved()
    {
        var db = new DownloadDatabase(testPath);
        var record = new DownloadRecord { GameDomain = "skyrim", ModId = 123 };

        db.AddRecord(record);
        var found = db.FindRecord("skyrim", 123, 456);

        found.Should().NotBeNull();
    }

    [Fact]
    public async Task PersistsToDisk_AndReloads()
    {
        var db1 = new DownloadDatabase(testPath);
        db1.AddRecord(new DownloadRecord { ModId = 1 });
        await db1.SaveAsync();

        var db2 = new DownloadDatabase(testPath);
        await db2.LoadAsync();

        db2.GetRecordCount().Should().Be(1);
    }
}
```

### 7.3 Fluent Client Tests

```csharp
public class FluentClientTests
{
    [Fact]
    public async Task GetAsync_BuildsCorrectUrl()
    {
        var client = FluentClientFactory.Create("https://api.example.com");

        var request = client.GetAsync("/users")
            .WithArgument("page", "1")
            .WithArgument("limit", "10");

        // Verify URL is "https://api.example.com/users?page=1&limit=10"
    }

    [Fact]
    public async Task WithBearerAuth_AddsHeader()
    {
        var client = FluentClientFactory.Create();
        client.SetBearerAuth("my_token");

        // Verify Authorization header is "Bearer my_token"
    }
}
```

---

## 8. Key Implementation Notes

### 8.1 Async Throughout

Use `async/await` consistently. The C++ code uses `std::future` in places - convert all to `Task<T>`.

### 8.2 Dependency Injection

Register services in `Program.cs`:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("Modular"));

builder.Services.AddHttpClient<ModularHttpClient>();
builder.Services.AddSingleton<IRateLimiter, NexusRateLimiter>();
builder.Services.AddSingleton<DownloadDatabase>();
builder.Services.AddScoped<NexusModsService>();
builder.Services.AddScoped<GameBananaService>();
builder.Services.AddScoped<RenameService>();
```

### 8.3 JSON Serialization

Use `System.Text.Json` with snake_case naming for API compatibility:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true
};
```

### 8.4 MD5 Calculation

```csharp
public static string CalculateMd5(string filePath)
{
    using var md5 = MD5.Create();
    using var stream = File.OpenRead(filePath);
    var hash = md5.ComputeHash(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}
```

### 8.5 File Path Handling

- Use `Path.Combine()` for cross-platform paths
- Expand `~` to user home directory
- Use `Directory.CreateDirectory()` which handles existing dirs

### 8.6 Filename Sanitization

```csharp
public static string SanitizeFilename(string filename)
{
    var invalid = Path.GetInvalidFileNameChars();
    return string.Concat(filename.Select(c => invalid.Contains(c) ? '_' : c));
}
```

### 8.7 Rate Limit State Persistence

Store as JSON with Unix timestamps:

```json
{
  "daily_remaining": 19500,
  "daily_reset": 1706486400,
  "hourly_remaining": 450,
  "hourly_reset": 1706400000
}
```

---

## 9. API Response Models

Define models matching the NexusMods API responses:

```csharp
public class TrackedMod
{
    public int ModId { get; set; }
    public string DomainName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class ModFile
{
    public int FileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeKb { get; set; }
    public string? Md5 { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
}

public class DownloadLink
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
}
```

---

## 10. Migration Checklist

- [ ] Create solution structure with 3 projects + 2 test projects
- [ ] Implement `AppSettings` and configuration loading
- [ ] Implement `DownloadDatabase` with JSON persistence
- [ ] Implement `IRateLimiter` and `NexusRateLimiter`
- [ ] Implement `ModularHttpClient` with retry logic
- [ ] Implement full `IFluentClient` interface and implementation
- [ ] Implement all HTTP filters
- [ ] Implement `NexusModsService` with all API calls
- [ ] Implement `GameBananaService`
- [ ] Implement `RenameService` for folder organization
- [ ] Implement `TrackingValidatorService`
- [ ] Implement custom exception hierarchy
- [ ] Implement CLI with System.CommandLine
- [ ] Implement progress UI with Spectre.Console
- [ ] Write unit tests for all components
- [ ] Test end-to-end workflows

---

## 11. Quality Requirements

1. **Null Safety:** Enable nullable reference types (`<Nullable>enable</Nullable>`)
2. **Async:** All I/O operations must be async
3. **Cancellation:** Support `CancellationToken` throughout
4. **Logging:** Use `ILogger<T>` from Microsoft.Extensions.Logging
5. **Error Handling:** Preserve the detailed exception hierarchy
6. **Thread Safety:** `RateLimiter` and `Database` must be thread-safe
7. **Disposable:** Implement `IDisposable` where appropriate
8. **Tests:** Minimum 80% code coverage on core logic

---

## 12. Reference Files

When implementing, refer to these C++ source files:

| Component | C++ Header | C++ Implementation |
|-----------|------------|-------------------|
| Config | `include/core/Config.h` | `src/core/Config.cpp` |
| Database | `include/core/Database.h` | `src/core/Database.cpp` |
| HttpClient | `include/core/HttpClient.h` | `src/core/HttpClient.cpp` |
| RateLimiter | `include/core/RateLimiter.h` | `src/core/RateLimiter.cpp` |
| NexusMods | `include/core/NexusMods.h` | `src/core/NexusMods.cpp` |
| GameBanana | `include/core/GameBanana.h` | `src/core/GameBanana.cpp` |
| Rename | `include/core/Rename.h` | `src/core/Rename.cpp` |
| TrackingValidator | `include/core/TrackingValidator.h` | `src/core/TrackingValidator.cpp` |
| Utils | `include/core/Utils.h` | `src/core/Utils.cpp` |
| Exceptions | `include/core/Exceptions.h` | - |
| LiveUI | `include/cli/LiveUI.h` | `src/cli/LiveUI.cpp` |
| FluentClient | `include/fluent/*.h` | `src/fluent/*.cpp` |
