# Implementing API Backends as Optional Modules

This guide describes how to add new mod repository backends (e.g., NexusMods, GameBanana, CurseForge, ModDB) to Modular as optional, pluggable modules. It covers the architectural changes needed and walks through a concrete implementation example.

---

## Table of Contents

1. [Current Architecture](#current-architecture)
2. [Target Architecture](#target-architecture)
3. [Step 1: Define the IModBackend Interface](#step-1-define-the-imodbackend-interface)
4. [Step 2: Define Common Models](#step-2-define-common-models)
5. [Step 3: Implement Backend-Specific Adapters](#step-3-implement-backend-specific-adapters)
6. [Step 4: Create a Backend Registry](#step-4-create-a-backend-registry)
7. [Step 5: Update Configuration](#step-5-update-configuration)
8. [Step 6: Update the CLI Layer](#step-6-update-the-cli-layer)
9. [Step 7: Handle Backend-Specific Features](#step-7-handle-backend-specific-features)
10. [Step 8: Testing Strategy](#step-8-testing-strategy)
11. [Example: Adding a CurseForge Backend](#example-adding-a-curseforge-backend)
12. [Checklist for New Backends](#checklist-for-new-backends)

---

## Current Architecture

The codebase currently has two concrete service classes with no shared interface:

```
Modular.Core/Services/
├── NexusModsService.cs    ← directly uses NexusMods API
├── GameBananaService.cs   ← directly uses GameBanana API
├── RenameService.cs
└── TrackingValidatorService.cs
```

Each service is instantiated directly in `Program.cs`:

```csharp
// Program.cs:119
var nexusService = new NexusModsService(settings, rateLimiter, database, logger);

// Program.cs:282
var gbService = new GameBananaService(settings, logger);
```

**Problems with the current approach:**
- No shared contract — each backend has its own method signatures
- CLI code has backend-specific branches (`RunCommandMode`, `RunGameBananaCommand`)
- Adding a new backend requires modifying `Program.cs` with new commands and wiring
- No way to iterate over all configured backends generically

---

## Target Architecture

```
Modular.Core/
├── Backends/
│   ├── IModBackend.cs              ← shared interface
│   ├── BackendCapabilities.cs      ← feature flags
│   ├── BackendRegistry.cs          ← discovers and manages backends
│   ├── NexusMods/
│   │   ├── NexusModsBackend.cs     ← implements IModBackend
│   │   ├── NexusModsConfig.cs      ← backend-specific settings
│   │   └── NexusModsModels.cs      ← API-specific DTOs (existing models)
│   ├── GameBanana/
│   │   ├── GameBananaBackend.cs
│   │   ├── GameBananaConfig.cs
│   │   └── GameBananaModels.cs
│   └── Common/
│       ├── BackendMod.cs           ← unified mod representation
│       ├── BackendModFile.cs       ← unified file representation
│       └── DownloadRequest.cs      ← unified download descriptor
```

---

## Step 1: Define the IModBackend Interface

Create `Modular.Core/Backends/IModBackend.cs`. This is the core abstraction that every backend implements.

```csharp
namespace Modular.Core.Backends;

/// <summary>
/// Common interface for all mod repository backends.
/// Each backend (NexusMods, GameBanana, CurseForge, etc.) implements this.
/// </summary>
public interface IModBackend
{
    /// <summary>
    /// Unique identifier for this backend (e.g., "nexusmods", "gamebanana").
    /// Used as config keys, CLI subcommand names, and directory names.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable display name (e.g., "NexusMods", "GameBanana").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Declares what this backend supports.
    /// </summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// Validates that the backend is properly configured (API keys, user IDs, etc.).
    /// Returns a list of validation errors, or an empty list if valid.
    /// </summary>
    IReadOnlyList<string> ValidateConfiguration();

    /// <summary>
    /// Fetches the list of mods the user has tracked/subscribed to.
    /// </summary>
    Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches downloadable files for a specific mod.
    /// </summary>
    Task<List<BackendModFile>> GetModFilesAsync(
        string modId,
        string? gameDomain = null,
        FileFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a mod file to a direct download URL.
    /// Some APIs (NexusMods) require a separate call; others include URLs in file listings.
    /// </summary>
    Task<string?> ResolveDownloadUrlAsync(
        string modId,
        string fileId,
        string? gameDomain = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads files for tracked/subscribed mods.
    /// </summary>
    Task DownloadModsAsync(
        string outputDirectory,
        string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}
```

### Supporting types

```csharp
namespace Modular.Core.Backends;

/// <summary>
/// Feature flags declaring what a backend supports.
/// </summary>
[Flags]
public enum BackendCapabilities
{
    None            = 0,
    GameDomains     = 1 << 0,  // Supports game domain filtering (NexusMods)
    FileCategories  = 1 << 1,  // Supports file category filtering (main/optional)
    Md5Verification = 1 << 2,  // Provides MD5 hashes for verification
    RateLimited     = 1 << 3,  // Requires rate limiting
    Authentication  = 1 << 4,  // Requires API key or login
    ModCategories   = 1 << 5,  // Supports mod category organization
}

public class FileFilter
{
    public List<string>? Categories { get; set; }  // "main", "optional", etc.
}

public class DownloadOptions
{
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public FileFilter? Filter { get; set; }
    public Action<string>? StatusCallback { get; set; }
}

public class DownloadProgress
{
    public string Status { get; set; } = string.Empty;
    public int Completed { get; set; }
    public int Total { get; set; }
    public string? CurrentFile { get; set; }
}
```

---

## Step 2: Define Common Models

Create unified models in `Modular.Core/Backends/Common/` that all backends map their API responses into.

```csharp
namespace Modular.Core.Backends.Common;

/// <summary>
/// Unified representation of a mod across all backends.
/// </summary>
public class BackendMod
{
    /// <summary>Backend-specific mod ID (string to support both int and string IDs).</summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>Human-readable mod name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Game domain or game identifier (backend-specific).</summary>
    public string? GameDomain { get; set; }

    /// <summary>Mod category ID, if the backend supports categories.</summary>
    public int? CategoryId { get; set; }

    /// <summary>Which backend this mod came from.</summary>
    public string BackendId { get; set; } = string.Empty;
}

/// <summary>
/// Unified representation of a downloadable file.
/// </summary>
public class BackendModFile
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public long? SizeBytes { get; set; }
    public string? Md5 { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }         // "main", "optional", etc.
    public string? DirectDownloadUrl { get; set; } // Pre-resolved URL if available
    public DateTime? UploadedAt { get; set; }
}
```

**Key design decision:** Use `string` for IDs everywhere. NexusMods uses `int`, GameBanana uses `string`. A `string` accommodates both without conversion headaches. Backend implementations convert internally.

---

## Step 3: Implement Backend-Specific Adapters

### NexusMods Backend

Wrap the existing `NexusModsService` logic into the `IModBackend` interface.

```csharp
namespace Modular.Core.Backends.NexusMods;

public class NexusModsBackend : IModBackend
{
    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly DownloadDatabase _database;
    private readonly IRateLimiter _rateLimiter;
    private readonly ILogger<NexusModsBackend>? _logger;

    public string Id => "nexusmods";
    public string DisplayName => "NexusMods";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.GameDomains |
        BackendCapabilities.FileCategories |
        BackendCapabilities.Md5Verification |
        BackendCapabilities.RateLimited |
        BackendCapabilities.Authentication |
        BackendCapabilities.ModCategories;

    public NexusModsBackend(
        AppSettings settings,
        IRateLimiter rateLimiter,
        DownloadDatabase database,
        ILogger<NexusModsBackend>? logger = null)
    {
        _settings = settings;
        _rateLimiter = rateLimiter;
        _database = database;
        _logger = logger;
        _client = FluentClientFactory.Create("https://api.nexusmods.com",
            new RateLimiterAdapter(rateLimiter), logger);
        _client.SetUserAgent("Modular/1.0");
    }

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(_settings.NexusApiKey))
            errors.Add("NexusMods API key is not configured (nexus_api_key)");
        return errors;
    }

    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null, CancellationToken ct = default)
    {
        var response = await _client.GetAsync("v1/user/tracked_mods.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<TrackedMod>();

        var mods = response.Select(m => new BackendMod
        {
            ModId = m.ModId.ToString(),
            Name = m.Name,
            GameDomain = m.DomainName,
            BackendId = Id
        }).ToList();

        if (gameDomain != null)
            mods = mods.Where(m => m.GameDomain == gameDomain).ToList();

        return mods;
    }

    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId, string? gameDomain = null,
        FileFilter? filter = null, CancellationToken ct = default)
    {
        var filesResponse = await _client
            .GetAsync($"v1/games/{gameDomain}/mods/{modId}/files.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsAsync<ModFilesResponse>();

        var files = filesResponse.Files.Select(f => new BackendModFile
        {
            FileId = f.FileId.ToString(),
            FileName = f.FileName,
            DisplayName = f.Name,
            SizeBytes = f.SizeKb * 1024,
            Md5 = f.Md5,
            Version = f.Version,
            Category = GetCategoryName(f.CategoryId),
            UploadedAt = DateTimeOffset
                .FromUnixTimeSeconds(f.UploadedTimestamp).DateTime
        }).ToList();

        // Apply category filter
        if (filter?.Categories is { Count: > 0 } cats)
        {
            var catSet = cats.Select(c => c.ToLowerInvariant()).ToHashSet();
            files = files.Where(f =>
                catSet.Contains(f.Category?.ToLowerInvariant() ?? "")).ToList();
        }

        return files;
    }

    public async Task<string?> ResolveDownloadUrlAsync(
        string modId, string fileId,
        string? gameDomain = null, CancellationToken ct = default)
    {
        var links = await _client
            .GetAsync($"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json")
            .WithHeader("apikey", _settings.NexusApiKey)
            .WithHeader("accept", "application/json")
            .AsArrayAsync<DownloadLink>();

        return links.Count > 0 ? links[0].Uri : null;
    }

    public async Task DownloadModsAsync(
        string outputDirectory, string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Reuse existing DownloadFilesAsync logic, adapted to
        // use BackendMod/BackendModFile and report DownloadProgress.
        // See NexusModsService.DownloadFilesAsync for reference.
        // ...
    }

    // (private helpers: GetCategoryName, RateLimiterAdapter — same as current)
}
```

### GameBanana Backend

```csharp
namespace Modular.Core.Backends.GameBanana;

public class GameBananaBackend : IModBackend
{
    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly ILogger<GameBananaBackend>? _logger;

    public string Id => "gamebanana";
    public string DisplayName => "GameBanana";

    public BackendCapabilities Capabilities => BackendCapabilities.None;
    // GameBanana: no game domains, no file categories, no rate limiting,
    // no auth required, no MD5 verification

    public GameBananaBackend(AppSettings settings,
        ILogger<GameBananaBackend>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _client = FluentClientFactory.Create("https://gamebanana.com/apiv10");
        _client.SetUserAgent("Modular/1.0");
    }

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(_settings.GameBananaUserId))
            errors.Add("GameBanana user ID is not configured (gamebanana_user_id)");
        return errors;
    }

    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null, CancellationToken ct = default)
    {
        var response = await _client
            .GetAsync($"Member/{_settings.GameBananaUserId}/Submissions")
            .WithArgument("_aFilters[Generic_SubscriptionCount]", ">0")
            .AsJsonAsync();

        var mods = new List<BackendMod>();
        if (response.RootElement.TryGetProperty("_aRecords", out var records))
        {
            foreach (var record in records.EnumerateArray())
            {
                if (record.TryGetProperty("_idRow", out var idProp) &&
                    record.TryGetProperty("_sName", out var nameProp))
                {
                    mods.Add(new BackendMod
                    {
                        ModId = idProp.ToString(),
                        Name = nameProp.GetString() ?? "Unknown",
                        BackendId = Id
                    });
                }
            }
        }
        return mods;
    }

    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId, string? gameDomain = null,
        FileFilter? filter = null, CancellationToken ct = default)
    {
        var response = await _client.GetAsync($"Mod/{modId}/Files").AsJsonAsync();
        var files = new List<BackendModFile>();

        if (response.RootElement.TryGetProperty("_aFiles", out var filesEl))
        {
            foreach (var file in filesEl.EnumerateArray())
            {
                if (file.TryGetProperty("_sDownloadUrl", out var urlProp))
                {
                    var url = urlProp.GetString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        files.Add(new BackendModFile
                        {
                            FileId = url.GetHashCode().ToString(),
                            FileName = Path.GetFileName(new Uri(url).LocalPath),
                            DirectDownloadUrl = url
                        });
                    }
                }
            }
        }
        return files;
    }

    public Task<string?> ResolveDownloadUrlAsync(
        string modId, string fileId,
        string? gameDomain = null, CancellationToken ct = default)
    {
        // GameBanana provides URLs directly in GetModFilesAsync,
        // so this is a no-op passthrough.
        return Task.FromResult<string?>(null);
    }

    public async Task DownloadModsAsync(
        string outputDirectory, string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Reuse existing DownloadAllSubscribedModsAsync logic.
        // ...
    }
}
```

---

## Step 4: Create a Backend Registry

The registry discovers, holds, and provides access to all configured backends.

```csharp
namespace Modular.Core.Backends;

/// <summary>
/// Registry of available mod backends. Backends register themselves
/// and consumers can iterate or look up by ID.
/// </summary>
public class BackendRegistry
{
    private readonly Dictionary<string, IModBackend> _backends = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a backend. Call during application startup.
    /// </summary>
    public void Register(IModBackend backend)
    {
        _backends[backend.Id] = backend;
    }

    /// <summary>
    /// Get a backend by ID. Returns null if not registered.
    /// </summary>
    public IModBackend? Get(string id)
    {
        return _backends.TryGetValue(id, out var backend) ? backend : null;
    }

    /// <summary>
    /// Get all registered backends.
    /// </summary>
    public IReadOnlyList<IModBackend> GetAll() => _backends.Values.ToList();

    /// <summary>
    /// Get all backends that are properly configured (validation passes).
    /// </summary>
    public IReadOnlyList<IModBackend> GetConfigured()
    {
        return _backends.Values
            .Where(b => b.ValidateConfiguration().Count == 0)
            .ToList();
    }

    /// <summary>
    /// Get backends that support a specific capability.
    /// </summary>
    public IReadOnlyList<IModBackend> GetWithCapability(BackendCapabilities cap)
    {
        return _backends.Values
            .Where(b => b.Capabilities.HasFlag(cap))
            .ToList();
    }
}
```

---

## Step 5: Update Configuration

### Add backend-level enable/disable in `AppSettings`

```csharp
// In AppSettings.cs, add:

[JsonPropertyName("enabled_backends")]
public List<string> EnabledBackends { get; set; } = ["nexusmods", "gamebanana"];

// Backend-specific config sections can be added as nested objects
// or kept as top-level keys for backward compatibility:
//   nexus_api_key       → used by NexusModsBackend
//   gamebanana_user_id  → used by GameBananaBackend
//   curseforge_api_key  → used by a future CurseForgeBackend
```

### Config file example (`~/.config/Modular/config.json`)

```json
{
  "enabled_backends": ["nexusmods", "gamebanana"],
  "nexus_api_key": "YOUR_KEY",
  "gamebanana_user_id": "12345",
  "mods_directory": "~/Games/Mods-Lists",
  "default_categories": ["main", "optional"]
}
```

Each backend reads only the settings it needs from `AppSettings`. If a backend needs many settings, create a dedicated config class:

```csharp
public class NexusModsConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public List<string> DefaultCategories { get; set; } = ["main", "optional"];
    public bool VerifyDownloads { get; set; } = false;
    public bool ValidateTracking { get; set; } = false;
}
```

---

## Step 6: Update the CLI Layer

### Startup: register backends

```csharp
// In Program.cs, replace per-backend initialization with:

static BackendRegistry InitializeBackends(
    AppSettings settings,
    NexusRateLimiter rateLimiter,
    DownloadDatabase database)
{
    var registry = new BackendRegistry();

    if (settings.EnabledBackends.Contains("nexusmods"))
        registry.Register(new NexusModsBackend(settings, rateLimiter, database));

    if (settings.EnabledBackends.Contains("gamebanana"))
        registry.Register(new GameBananaBackend(settings));

    return registry;
}
```

### Generic download command

Replace backend-specific commands with a unified `download` command:

```csharp
// modular download --backend nexusmods --domain skyrimspecialedition
// modular download --backend gamebanana
// modular download --all   ← downloads from all configured backends

var downloadCommand = new Command("download", "Download mods from a backend");
var backendOption = new Option<string?>("--backend", "Backend to use");
var allOption = new Option<bool>("--all", "Download from all configured backends");

downloadCommand.SetHandler(async (backendId, all, domain, ...) =>
{
    var registry = InitializeBackends(settings, rateLimiter, database);

    IEnumerable<IModBackend> backends;
    if (all)
    {
        backends = registry.GetConfigured();
    }
    else if (backendId != null)
    {
        var b = registry.Get(backendId)
            ?? throw new ConfigException($"Unknown backend: {backendId}");
        backends = [b];
    }
    else
    {
        // Default: prompt user via interactive menu
        backends = [PromptBackendSelection(registry)];
    }

    foreach (var backend in backends)
    {
        var errors = backend.ValidateConfiguration();
        if (errors.Count > 0)
        {
            LiveProgressDisplay.ShowWarning(
                $"Skipping {backend.DisplayName}: {string.Join(", ", errors)}");
            continue;
        }

        var gameDom = backend.Capabilities.HasFlag(BackendCapabilities.GameDomains)
            ? domain : null;

        await backend.DownloadModsAsync(
            settings.ModsDirectory, gameDom, options, progress, ct);
    }
});
```

### Interactive mode

```csharp
static async Task RunInteractiveMode()
{
    var registry = InitializeBackends(...);
    var configured = registry.GetConfigured();

    // Build menu dynamically from registered backends
    var menuItems = configured
        .Select(b => b.DisplayName)
        .Append("Rename")
        .ToArray();

    var choice = LiveProgressDisplay.ShowNumberedMenu("Modular", menuItems);
    if (choice == 0) return;
    if (choice <= configured.Count)
    {
        var backend = configured[choice - 1];
        // prompt for game domain if backend supports it
        // then call backend.DownloadModsAsync(...)
    }
}
```

---

## Step 7: Handle Backend-Specific Features

Use the `Capabilities` flags to conditionally apply features:

```csharp
// Rate limiting — only if backend declares it
if (backend.Capabilities.HasFlag(BackendCapabilities.RateLimited))
{
    await rateLimiter.WaitIfNeededAsync(ct);
}

// MD5 verification — only if backend provides hashes
if (backend.Capabilities.HasFlag(BackendCapabilities.Md5Verification)
    && settings.VerifyDownloads)
{
    var actualMd5 = await Md5Calculator.CalculateMd5Async(outputPath, ct);
    // compare with file.Md5
}

// Category filtering — only if backend supports it
if (backend.Capabilities.HasFlag(BackendCapabilities.FileCategories))
{
    filter = new FileFilter { Categories = settings.DefaultCategories };
}

// Game domain prompting — only if backend uses domains
if (backend.Capabilities.HasFlag(BackendCapabilities.GameDomains))
{
    gameDomain = LiveProgressDisplay.AskString("Enter game domain:");
}
```

### Download URL resolution differences

NexusMods requires a separate API call to get download URLs. GameBanana provides them inline. The `IModBackend` interface handles this via two patterns:

1. **Inline URLs:** `GetModFilesAsync` returns files with `DirectDownloadUrl` populated. The download orchestrator uses the URL directly.
2. **Separate resolution:** `DirectDownloadUrl` is null. The orchestrator calls `ResolveDownloadUrlAsync` before downloading.

```csharp
foreach (var file in files)
{
    var url = file.DirectDownloadUrl
        ?? await backend.ResolveDownloadUrlAsync(mod.ModId, file.FileId, gameDomain, ct);

    if (url == null) continue;
    await client.GetAsync(url).DownloadToAsync(outputPath, null, ct);
}
```

---

## Step 8: Testing Strategy

### Unit tests per backend

Each backend should have tests that mock the HTTP layer:

```csharp
// tests/Modular.Core.Tests/Backends/NexusModsBackendTests.cs
public class NexusModsBackendTests
{
    [Fact]
    public async Task GetUserMods_ReturnsTrackedMods()
    {
        // Arrange: mock IFluentClient to return canned JSON
        // Act: call backend.GetUserModsAsync("skyrimspecialedition")
        // Assert: verify BackendMod list is correctly mapped
    }

    [Fact]
    public void ValidateConfiguration_MissingApiKey_ReturnsError()
    {
        var settings = new AppSettings { NexusApiKey = "" };
        var backend = new NexusModsBackend(settings, ...);
        var errors = backend.ValidateConfiguration();
        Assert.Contains(errors, e => e.Contains("API key"));
    }
}
```

### Integration tests

Test against the real API with recorded responses (use a tool like WireMock.Net or record/replay HTTP fixtures):

```csharp
[Fact(Skip = "Requires API key")]
public async Task NexusMods_Integration_FetchTrackedMods()
{
    var settings = new AppSettings
    {
        NexusApiKey = Environment.GetEnvironmentVariable("NEXUS_API_KEY") ?? ""
    };
    var backend = new NexusModsBackend(settings, rateLimiter, database);
    var mods = await backend.GetUserModsAsync();
    Assert.NotEmpty(mods);
}
```

### Registry tests

```csharp
[Fact]
public void GetConfigured_ExcludesBackendsWithMissingConfig()
{
    var registry = new BackendRegistry();
    registry.Register(new NexusModsBackend(new AppSettings { NexusApiKey = "" }, ...));
    registry.Register(new GameBananaBackend(new AppSettings { GameBananaUserId = "123" }));

    var configured = registry.GetConfigured();
    Assert.Single(configured);
    Assert.Equal("gamebanana", configured[0].Id);
}
```

---

## Example: Adding a CurseForge Backend

Here is a step-by-step walkthrough for adding a hypothetical CurseForge backend.

### 1. Add config key to `AppSettings.cs`

```csharp
[JsonPropertyName("curseforge_api_key")]
public string CurseForgeApiKey { get; set; } = string.Empty;
```

### 2. Create the backend class

```csharp
// Modular.Core/Backends/CurseForge/CurseForgeBackend.cs
namespace Modular.Core.Backends.CurseForge;

public class CurseForgeBackend : IModBackend
{
    private const string BaseUrl = "https://api.curseforge.com";
    private readonly AppSettings _settings;
    private readonly IFluentClient _client;

    public string Id => "curseforge";
    public string DisplayName => "CurseForge";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.GameDomains |
        BackendCapabilities.Authentication;

    public CurseForgeBackend(AppSettings settings,
        ILogger<CurseForgeBackend>? logger = null)
    {
        _settings = settings;
        _client = FluentClientFactory.Create(BaseUrl);
        _client.SetUserAgent("Modular/1.0");
    }

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(_settings.CurseForgeApiKey))
            errors.Add("CurseForge API key not configured (curseforge_api_key)");
        return errors;
    }

    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null, CancellationToken ct = default)
    {
        // CurseForge uses game IDs (integers), not domain strings.
        // Map gameDomain to game ID internally if needed.
        var response = await _client.GetAsync("v1/mods/search")
            .WithHeader("x-api-key", _settings.CurseForgeApiKey)
            .WithArgument("gameId", MapGameDomain(gameDomain))
            .AsJsonAsync();

        // Parse response and map to BackendMod...
        return new List<BackendMod>();
    }

    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId, string? gameDomain = null,
        FileFilter? filter = null, CancellationToken ct = default)
    {
        var response = await _client.GetAsync($"v1/mods/{modId}/files")
            .WithHeader("x-api-key", _settings.CurseForgeApiKey)
            .AsJsonAsync();

        // Parse and map to BackendModFile...
        // CurseForge provides download URLs inline.
        return new List<BackendModFile>();
    }

    public Task<string?> ResolveDownloadUrlAsync(
        string modId, string fileId,
        string? gameDomain = null, CancellationToken ct = default)
    {
        // CurseForge includes URLs in file listings
        return Task.FromResult<string?>(null);
    }

    public async Task DownloadModsAsync(
        string outputDirectory, string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Orchestrate: GetUserMods → GetModFiles → download each
    }

    private static string MapGameDomain(string? domain) => domain switch
    {
        "skyrimspecialedition" => "1704",
        "stardewvalley" => "669",
        _ => domain ?? ""
    };
}
```

### 3. Register in startup

```csharp
if (settings.EnabledBackends.Contains("curseforge"))
    registry.Register(new CurseForgeBackend(settings));
```

### 4. Update `enabled_backends` default

```csharp
public List<string> EnabledBackends { get; set; } =
    ["nexusmods", "gamebanana", "curseforge"];
```

That's it. No changes to CLI command structure, no new subcommands, no new branches in `Program.cs`.

---

## Checklist for New Backends

When adding a new backend, verify each of these:

- [ ] **Create backend class** implementing `IModBackend`
- [ ] **Set `Id` and `DisplayName`** — `Id` must be lowercase, no spaces
- [ ] **Declare `Capabilities`** — set only the flags that apply
- [ ] **Implement `ValidateConfiguration`** — check all required config keys
- [ ] **Implement `GetUserModsAsync`** — map API response to `BackendMod`
- [ ] **Implement `GetModFilesAsync`** — map to `BackendModFile`, populate `DirectDownloadUrl` if available
- [ ] **Implement `ResolveDownloadUrlAsync`** — only needed if URLs require a separate API call
- [ ] **Implement `DownloadModsAsync`** — full download orchestration
- [ ] **Add config key(s)** to `AppSettings.cs`
- [ ] **Register in `InitializeBackends`** in `Program.cs`
- [ ] **Add to `enabled_backends` default** list
- [ ] **Write unit tests** for model mapping and config validation
- [ ] **Document the backend** — required config, API quirks, rate limits
- [ ] **Handle rate limiting** — if the API requires it, implement or reuse `IRateLimiter`
- [ ] **Handle authentication** — pass API keys/tokens via headers or query params

---

## Migration Path from Current Code

To adopt this pattern incrementally:

1. **Create the interface and common models first** — no changes to existing code
2. **Wrap `NexusModsService` in `NexusModsBackend`** — the backend can delegate to the existing service internally, then gradually inline the logic
3. **Wrap `GameBananaService` in `GameBananaBackend`** — same approach
4. **Add `BackendRegistry`** and wire it into `Program.cs` alongside existing code
5. **Switch CLI commands** to use the registry — replace `RunGameBananaCommand` etc. with generic handlers
6. **Remove old direct service instantiation** once all backends go through the registry
7. **Delete the old service classes** when the backend classes fully replace them
