# GameBanana API Guide for Modular Module Development

This guide documents the GameBanana API and provides step-by-step instructions for building a Modular module that interfaces with it. It covers API versions, endpoints, authentication, response parsing, and concrete implementation patterns using Modular's `FluentHttp` client and `IModBackend` interface.

---

## Table of Contents

1. [GameBanana API Overview](#gamebanana-api-overview)
2. [API Versions](#api-versions)
3. [Core Endpoints Reference](#core-endpoints-reference)
4. [Authentication](#authentication)
5. [Response Formats and Parsing](#response-formats-and-parsing)
6. [Rate Limiting](#rate-limiting)
7. [Building a Modular GameBanana Module](#building-a-modular-gamebanana-module)
8. [Step-by-Step Implementation](#step-by-step-implementation)
9. [Testing Your Module](#testing-your-module)
10. [Common Pitfalls](#common-pitfalls)
11. [API Reference Quick Sheet](#api-reference-quick-sheet)

---

## GameBanana API Overview

[GameBanana](https://gamebanana.com) is a game modding community hosting mods, skins, maps, tools, and other user-generated content for hundreds of games. It exposes multiple API surfaces that allow programmatic access to submissions, user profiles, file downloads, and search functionality.

Key characteristics of the GameBanana API:

- **No API key required** for read-only access to public data
- **No strict rate limiting** documented, but be respectful (see [Rate Limiting](#rate-limiting))
- **JSON responses** from all modern endpoints
- **Multiple API versions** coexist (legacy, v6, v10, v11)
- **Direct download URLs** are provided inline with file metadata (no separate resolution step)

### Official Resources

| Resource | URL |
|----------|-----|
| API Portal | `https://api.gamebanana.com/` |
| API Wiki | `https://gamebanana.com/wikis/1899` |
| Direct Access Wiki | `https://gamebanana.com/wikis/1898` |
| API Forum Threads | `https://gamebanana.com/threads/cats/3892` |
| GameBanana Developers (GitHub) | `https://github.com/banana-org` |

---

## API Versions

GameBanana has evolved through several API versions. Understanding which to use and when is critical.

### Legacy API (`api.gamebanana.com`)

The original REST API, still operational but older. Uses a function-call style with explicit field selection.

```
GET https://api.gamebanana.com/Core/Item/Data
    ?itemtype=Mod
    &itemid=350538
    &fields=name,description,Url().sDownloadUrl()
```

**Key features:**
- Multicall enabled: pass arrays of parameters to batch requests
- Append `&help` to any call to see available fields and documentation
- Supports `format=json` (default) or `format=xml`
- Returns arrays of values in field order (not key-value objects)

**Supported item types:** `App`, `Article`, `Blog`, `Bug`, `Club`, `Contest`, `Event`, `File`, `Game`, `Member`, `Mod`, `ModCategory`, `Model`, `News`, `Poll`, `Project`, `Question`, `Review`, `Skin`, `Sound`, `Spray`, `Studio`, `Thread`, `Tool`, `Ware`, `Wip`

**Example response:**
```json
["Cloudrip", "Bair Force 1", "https://gamebanana.com/mods/download/350538"]
```

**Discovering fields:** Append `&help` to any request:
```
GET https://api.gamebanana.com/Core/Item/Data
    ?itemtype=Mod&itemid=350538
    &fields=name
    &help
```

### Other Legacy Endpoints

| Endpoint | Purpose |
|----------|---------|
| `Core/Item/Data` | Get fields for a single item by type and ID |
| `Core/List/Section` | List items by section with sorting and pagination |
| `Core/List/Like` | Search items matching a field prefix |
| `Core/Member/Identify` | Look up a member ID by username |
| `Core/Member/Authenticate` | Authenticate and get a session token |

### APIv6 (`gamebanana.com/apiv6/`)

An intermediate version using property-based queries. Used by several mod managers.

```
GET https://gamebanana.com/apiv6/Mod/Multi
    ?_csvProperties=_sName,_aFiles,_aPreviewMedia,_aSubmitter
    &_csvRowIds=12345,67890
```

**Parameters:**
- `_csvProperties` - comma-separated field names to return
- `_csvRowIds` - comma-separated item IDs to fetch

**Common field names:**
| Field | Type | Description |
|-------|------|-------------|
| `_sName` | string | Submission name |
| `_aFiles` | array | File objects with download info |
| `_aPreviewMedia` | array | Preview images and videos |
| `_aSubmitter` | object | Submitter user info |
| `_bHasUpdates` | bool | Whether updates are available |
| `_aLatestUpdates` | array | Recent update entries |
| `_aAlternateFileSources` | array | Mirror download URLs |
| `_idRow` | int | Row/item ID |

### APIv10 (`gamebanana.com/apiv10/`) - **Recommended for Modular**

The version currently used by Modular's `GameBananaService`. Provides a cleaner REST-style interface.

```
GET https://gamebanana.com/apiv10/Member/{userId}/Submissions
    ?_aFilters[Generic_SubscriptionCount]=>0

GET https://gamebanana.com/apiv10/Mod/{modId}/Files
```

**Key patterns:**
- Resource-based URLs: `/{EntityType}/{id}/{SubResource}`
- Filter support via `_aFilters[FilterName]=value`
- JSON responses with `_aRecords` arrays and `_sName`/`_idRow` patterns
- Pagination via `_nPage` and `_nPerpage` parameters

### APIv11 (`gamebanana.com/apiv11/`) - **Latest**

The newest version powering the GameBanana website itself. All dynamic data on the site loads from v11 endpoints.

```
GET https://gamebanana.com/apiv11/Mod/Index
    ?_aFilters[Generic_Category]=3490
    &_nPage=1
    &_nPerpage=15
```

**Discovery method:** Open browser DevTools on any GameBanana page and observe network requests to `gamebanana.com/apiv11/` to see all available endpoints and their parameters.

**Known v11 endpoints:**

| Endpoint | Purpose |
|----------|---------|
| `Mod/Index` | List/index mods with filters |
| `Mod/{id}/ProfilePage` | Full mod profile data |
| `Mod/{id}/DownloadPage` | Download page data with file URLs |
| `Game/{id}/Subfeed` | Game-specific submission feed |
| `Member/{id}/ProfilePage` | User profile data |

---

## Core Endpoints Reference

These are the endpoints most relevant for a Modular GameBanana module, organized by use case.

### Fetching User Subscriptions

Retrieve mods a user has subscribed to:

```
GET /apiv10/Member/{userId}/Submissions
    ?_aFilters[Generic_SubscriptionCount]=>0
```

**Response structure:**
```json
{
  "_aRecords": [
    {
      "_idRow": 123456,
      "_sName": "My Cool Mod",
      "_sModelName": "Mod",
      "_sProfileUrl": "/mods/123456",
      "_aSubmitter": {
        "_idRow": 789,
        "_sName": "AuthorName"
      },
      "_tsDateAdded": 1700000000,
      "_tsDateModified": 1700100000,
      "_aPreviewMedia": { ... },
      "_nSubscriptionCount": 42
    }
  ],
  "_nRecordCount": 1
}
```

### Fetching Mod Details

Get metadata for a specific mod:

```
# Legacy API (explicit field selection)
GET https://api.gamebanana.com/Core/Item/Data
    ?itemtype=Mod
    &itemid=123456
    &fields=name,description,Files().aFiles(),Preview().sStructuredDataFullsizeUrl(),Credits().aAuthors()

# APIv10
GET https://gamebanana.com/apiv10/Mod/123456/ProfilePage

# APIv11
GET https://gamebanana.com/apiv11/Mod/123456/ProfilePage
```

### Fetching Mod Files

Get downloadable files for a mod:

```
GET https://gamebanana.com/apiv10/Mod/{modId}/Files
```

**Response structure:**
```json
{
  "_aFiles": [
    {
      "_idRow": 999,
      "_sFile": "my_mod_v1.2.zip",
      "_nFilesize": 1048576,
      "_sDownloadUrl": "https://files.gamebanana.com/mods/my_mod_v1.2.zip",
      "_sMd5Checksum": "",
      "_sDescription": "Main file",
      "_tsDateAdded": 1700000000,
      "_nDownloadCount": 150
    }
  ]
}
```

**Important notes:**
- `_sDownloadUrl` provides a direct download link (no resolution step needed)
- `_sMd5Checksum` exists but is often empty for most records
- `_nFilesize` is in bytes

### Searching for Mods

```
# Legacy List API
GET https://api.gamebanana.com/Core/List/Like
    ?itemtype=Mod
    &field=name
    &query=searchterm

# APIv11 Index with game filter
GET https://gamebanana.com/apiv11/Mod/Index
    ?_aFilters[Generic_Game]=8694
    &_sSearchString=my+search
    &_nPage=1
    &_nPerpage=15
```

### Fetching Game Information

```
# Get game categories
GET https://gamebanana.com/apiv10/Game/{gameId}/Categories

# Get game mod feed
GET https://gamebanana.com/apiv11/Game/{gameId}/Subfeed
    ?_nPage=1
    &_nPerpage=15
```

### Member Lookup

```
# By ID
GET https://gamebanana.com/apiv10/Member/{userId}/ProfilePage

# By username (Legacy API)
GET https://api.gamebanana.com/Core/Member/Identify
    ?username=SomeUser
```

---

## Authentication

GameBanana authentication is **optional** for most read operations. It is only required for:
- Submitting content
- Managing subscriptions programmatically
- Accessing private/restricted data

### Unauthenticated Access (Read-Only)

No headers or tokens needed. Simply make GET requests:

```csharp
var response = await _client.GetAsync($"Mod/{modId}/Files")
    .AsJsonAsync();
```

### Authenticated Access

For operations requiring authentication, GameBanana uses a token-based system:

1. **Register an app** on GameBanana to get an App ID
2. **Authenticate** using the legacy API:
   ```
   GET https://api.gamebanana.com/Core/Member/Authenticate
       ?userid={userId}
       &appid={appId}
       &password={apiPassword}
   ```
3. **Use the returned token** in subsequent requests

For Modular's purposes (downloading subscribed mods), authentication is **not needed**. The user ID alone is sufficient to query public subscription lists.

---

## Response Formats and Parsing

### APIv10/v11 JSON Conventions

GameBanana uses a Hungarian notation-style naming convention for JSON fields:

| Prefix | Type | Example |
|--------|------|---------|
| `_s` | string | `_sName`, `_sFile`, `_sDownloadUrl` |
| `_n` | number | `_nFilesize`, `_nDownloadCount`, `_nPage` |
| `_b` | boolean | `_bHasUpdates`, `_bIsPrivate` |
| `_a` | array/object | `_aFiles`, `_aRecords`, `_aSubmitter` |
| `_id` | ID (int) | `_idRow`, `_idGameRow` |
| `_ts` | timestamp | `_tsDateAdded`, `_tsDateModified` |

### Parsing with System.Text.Json

Since field names use underscores and Hungarian notation, use `JsonElement` for dynamic parsing rather than strongly-typed deserialization:

```csharp
var response = await _client.GetAsync($"Mod/{modId}/Files").AsJsonAsync();

if (response.RootElement.TryGetProperty("_aFiles", out var files))
{
    foreach (var file in files.EnumerateArray())
    {
        var name = file.GetProperty("_sFile").GetString();
        var size = file.GetProperty("_nFilesize").GetInt64();
        var url  = file.GetProperty("_sDownloadUrl").GetString();
    }
}
```

For well-known response structures, you can create DTOs with `JsonPropertyName`:

```csharp
public class GameBananaFile
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sFile")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("_nFilesize")]
    public long FileSize { get; set; }

    [JsonPropertyName("_sDownloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("_sMd5Checksum")]
    public string? Md5Checksum { get; set; }

    [JsonPropertyName("_sDescription")]
    public string? Description { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long DateAddedTimestamp { get; set; }

    [JsonPropertyName("_nDownloadCount")]
    public int DownloadCount { get; set; }
}

public class GameBananaModRecord
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("_sModelName")]
    public string? ModelName { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long DateAddedTimestamp { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public long DateModifiedTimestamp { get; set; }

    [JsonPropertyName("_nSubscriptionCount")]
    public int SubscriptionCount { get; set; }
}
```

### Legacy API Response Format

The legacy `Core/Item/Data` endpoint returns a flat JSON array of values in the order fields were requested:

```
Request:  ?fields=name,description,Url().sDownloadUrl()
Response: ["ModName", "ModDescription", "https://..."]
```

This is positional, not keyed. Parse by index:

```csharp
var values = await response.AsArrayAsync<JsonElement>();
var name = values[0].GetString();
var description = values[1].GetString();
var downloadUrl = values[2].GetString();
```

---

## Rate Limiting

GameBanana does **not publicly document** strict rate limits, unlike NexusMods. However, best practices apply:

| Guideline | Recommendation |
|-----------|----------------|
| Request frequency | Max 1-2 requests per second for sustained use |
| Burst allowance | Short bursts of 5-10 rapid requests are tolerated |
| User-Agent | Always set a descriptive User-Agent header |
| Caching | Cache mod metadata locally to reduce redundant calls |
| Retry strategy | Exponential backoff on HTTP 429 or 5xx responses |

### Implementation in Modular

Since GameBanana has no strict rate limiting, the `BackendCapabilities` for a GameBanana module should **not** include the `RateLimited` flag. However, implement courtesy delays:

```csharp
public BackendCapabilities Capabilities => BackendCapabilities.None;
// No rate limiting, no auth required, no MD5 verification
```

If you want to add optional courtesy throttling:

```csharp
private readonly SemaphoreSlim _throttle = new(1, 1);
private DateTime _lastRequest = DateTime.MinValue;

private async Task ThrottleAsync()
{
    await _throttle.WaitAsync();
    try
    {
        var elapsed = DateTime.UtcNow - _lastRequest;
        if (elapsed < TimeSpan.FromMilliseconds(500))
            await Task.Delay(TimeSpan.FromMilliseconds(500) - elapsed);
        _lastRequest = DateTime.UtcNow;
    }
    finally
    {
        _throttle.Release();
    }
}
```

---

## Building a Modular GameBanana Module

This section walks through building a complete GameBanana module for Modular following the `IModBackend` pattern documented in [API-BACKENDS-GUIDE.md](./API-BACKENDS-GUIDE.md).

### Prerequisites

Before you begin, ensure you understand:

1. **Modular's three-layer architecture:** CLI -> Core -> FluentHttp
2. **The `IModBackend` interface** (see `API-BACKENDS-GUIDE.md`)
3. **The FluentHttp client** (see `docs/fluent/`)
4. **GameBanana API basics** (covered above)

### Module File Structure

```
src/Modular.Core/
├── Backends/
│   └── GameBanana/
│       ├── GameBananaBackend.cs      # IModBackend implementation
│       ├── GameBananaConfig.cs       # Backend-specific configuration
│       └── GameBananaModels.cs       # API response DTOs
├── Services/
│   └── GameBananaService.cs          # (existing) low-level API client
└── Configuration/
    └── AppSettings.cs                # (update) add GameBanana config keys
```

---

## Step-by-Step Implementation

### Step 1: Define Configuration

Add any new configuration keys needed in `AppSettings.cs`:

```csharp
// Already exists:
[JsonPropertyName("gamebanana_user_id")]
public string GameBananaUserId { get; set; } = string.Empty;

// Add if needed for extended features:
[JsonPropertyName("gamebanana_game_ids")]
public List<int> GameBananaGameIds { get; set; } = [];

[JsonPropertyName("gamebanana_download_dir")]
public string GameBananaDownloadDir { get; set; } = "gamebanana";
```

Config file entry (`~/.config/Modular/config.json`):

```json
{
  "gamebanana_user_id": "12345",
  "gamebanana_game_ids": [8694, 5942],
  "gamebanana_download_dir": "gamebanana",
  "enabled_backends": ["nexusmods", "gamebanana"]
}
```

### Step 2: Create API Response Models

Create `GameBananaModels.cs` with strongly-typed DTOs:

```csharp
// src/Modular.Core/Backends/GameBanana/GameBananaModels.cs
using System.Text.Json.Serialization;

namespace Modular.Core.Backends.GameBanana;

/// <summary>
/// Response wrapper for paginated record listings.
/// </summary>
public class GameBananaRecordResponse
{
    [JsonPropertyName("_aRecords")]
    public List<GameBananaRecord> Records { get; set; } = [];

    [JsonPropertyName("_nRecordCount")]
    public int RecordCount { get; set; }
}

/// <summary>
/// A single submission record (mod, skin, tool, etc.).
/// </summary>
public class GameBananaRecord
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("_sModelName")]
    public string? ModelName { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long? DateAddedTimestamp { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public long? DateModifiedTimestamp { get; set; }

    [JsonPropertyName("_nSubscriptionCount")]
    public int SubscriptionCount { get; set; }

    [JsonPropertyName("_aSubmitter")]
    public GameBananaSubmitter? Submitter { get; set; }

    [JsonPropertyName("_aGame")]
    public GameBananaGameRef? Game { get; set; }
}

/// <summary>
/// Submitter (author) information nested within records.
/// </summary>
public class GameBananaSubmitter
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; set; }
}

/// <summary>
/// Game reference nested within records.
/// </summary>
public class GameBananaGameRef
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sName")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Response wrapper for mod file listings.
/// </summary>
public class GameBananaFilesResponse
{
    [JsonPropertyName("_aFiles")]
    public List<GameBananaFileEntry> Files { get; set; } = [];
}

/// <summary>
/// A single downloadable file entry.
/// </summary>
public class GameBananaFileEntry
{
    [JsonPropertyName("_idRow")]
    public int Id { get; set; }

    [JsonPropertyName("_sFile")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("_nFilesize")]
    public long FileSize { get; set; }

    [JsonPropertyName("_sDownloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("_sMd5Checksum")]
    public string? Md5Checksum { get; set; }

    [JsonPropertyName("_sDescription")]
    public string? Description { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public long DateAddedTimestamp { get; set; }

    [JsonPropertyName("_nDownloadCount")]
    public int DownloadCount { get; set; }
}
```

### Step 3: Implement the Backend

Create `GameBananaBackend.cs` implementing `IModBackend`:

```csharp
// src/Modular.Core/Backends/GameBanana/GameBananaBackend.cs
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Backends.Common;
using Modular.Core.Configuration;
using Modular.Core.Utilities;
using Modular.FluentHttp.Implementation;
using Modular.FluentHttp.Interfaces;

namespace Modular.Core.Backends.GameBanana;

public class GameBananaBackend : IModBackend
{
    private const string BaseUrl = "https://gamebanana.com/apiv10";
    private readonly AppSettings _settings;
    private readonly IFluentClient _client;
    private readonly ILogger<GameBananaBackend>? _logger;

    public string Id => "gamebanana";
    public string DisplayName => "GameBanana";

    // GameBanana: no game domains, no file categories, no rate limiting,
    // no mandatory auth, no reliable MD5 verification
    public BackendCapabilities Capabilities => BackendCapabilities.None;

    public GameBananaBackend(AppSettings settings,
        ILogger<GameBananaBackend>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _client = FluentClientFactory.Create(BaseUrl);
        _client.SetUserAgent("Modular/1.0");
    }

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(_settings.GameBananaUserId))
            errors.Add("GameBanana user ID is not configured (gamebanana_user_id)");
        return errors;
    }

    /// <summary>
    /// Fetches the user's subscribed mods from GameBanana.
    /// </summary>
    public async Task<List<BackendMod>> GetUserModsAsync(
        string? gameDomain = null, CancellationToken ct = default)
    {
        var mods = new List<BackendMod>();

        try
        {
            var response = await _client
                .GetAsync($"Member/{_settings.GameBananaUserId}/Submissions")
                .WithArgument("_aFilters[Generic_SubscriptionCount]", ">0")
                .AsJsonAsync();

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

            _logger?.LogInformation(
                "Fetched {Count} subscribed mods from GameBanana", mods.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to fetch subscribed mods for user {UserId}",
                _settings.GameBananaUserId);
        }

        return mods;
    }

    /// <summary>
    /// Fetches downloadable files for a specific mod.
    /// GameBanana provides direct download URLs inline.
    /// </summary>
    public async Task<List<BackendModFile>> GetModFilesAsync(
        string modId, string? gameDomain = null,
        FileFilter? filter = null, CancellationToken ct = default)
    {
        var files = new List<BackendModFile>();

        try
        {
            var response = await _client
                .GetAsync($"Mod/{modId}/Files")
                .AsJsonAsync();

            if (response.RootElement.TryGetProperty("_aFiles", out var filesEl))
            {
                foreach (var file in filesEl.EnumerateArray())
                {
                    if (!file.TryGetProperty("_sDownloadUrl", out var urlProp))
                        continue;

                    var url = urlProp.GetString();
                    if (string.IsNullOrEmpty(url))
                        continue;

                    var modFile = new BackendModFile
                    {
                        DirectDownloadUrl = url,
                        FileName = Path.GetFileName(new Uri(url).LocalPath)
                    };

                    // Extract optional metadata
                    if (file.TryGetProperty("_idRow", out var idEl))
                        modFile.FileId = idEl.ToString();
                    if (file.TryGetProperty("_nFilesize", out var sizeEl))
                        modFile.SizeBytes = sizeEl.GetInt64();
                    if (file.TryGetProperty("_sFile", out var nameEl))
                        modFile.FileName = nameEl.GetString() ?? modFile.FileName;
                    if (file.TryGetProperty("_sDescription", out var descEl))
                        modFile.DisplayName = descEl.GetString();
                    if (file.TryGetProperty("_sMd5Checksum", out var md5El))
                    {
                        var md5 = md5El.GetString();
                        if (!string.IsNullOrEmpty(md5))
                            modFile.Md5 = md5;
                    }
                    if (file.TryGetProperty("_tsDateAdded", out var tsEl))
                    {
                        modFile.UploadedAt = DateTimeOffset
                            .FromUnixTimeSeconds(tsEl.GetInt64()).DateTime;
                    }

                    files.Add(modFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to fetch files for mod {ModId}", modId);
        }

        return files;
    }

    /// <summary>
    /// No-op for GameBanana: download URLs are provided inline
    /// via GetModFilesAsync (DirectDownloadUrl is pre-populated).
    /// </summary>
    public Task<string?> ResolveDownloadUrlAsync(
        string modId, string fileId,
        string? gameDomain = null, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Downloads all subscribed mods to the specified directory.
    /// </summary>
    public async Task DownloadModsAsync(
        string outputDirectory, string? gameDomain = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var mods = await GetUserModsAsync(gameDomain, ct);
        var completed = 0;
        var total = mods.Count;

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new DownloadProgress
            {
                Status = $"Processing {mod.Name}",
                Completed = completed,
                Total = total,
                CurrentFile = mod.Name
            });

            if (options?.DryRun == true)
            {
                _logger?.LogInformation("[DRY RUN] Would download: {Name}", mod.Name);
                completed++;
                continue;
            }

            var files = await GetModFilesAsync(mod.ModId, gameDomain, null, ct);
            var modDir = Path.Combine(outputDirectory,
                FileUtils.SanitizeDirectoryName(mod.Name));
            FileUtils.EnsureDirectoryExists(modDir);

            foreach (var file in files)
            {
                var url = file.DirectDownloadUrl;
                if (string.IsNullOrEmpty(url)) continue;

                var outputPath = Path.Combine(modDir,
                    FileUtils.SanitizeFilename(file.FileName));

                if (!options?.Force == true && File.Exists(outputPath))
                {
                    _logger?.LogDebug("Skipping existing: {File}", outputPath);
                    continue;
                }

                try
                {
                    await _client.GetAsync(url).DownloadToAsync(outputPath, null, ct);
                    _logger?.LogInformation("Downloaded: {File}", outputPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to download {Url}", url);
                }
            }

            completed++;
        }

        progress?.Report(new DownloadProgress
        {
            Status = "Done",
            Completed = total,
            Total = total
        });
    }
}
```

### Step 4: Register the Backend

In `Program.cs`, register the GameBanana backend in the initialization method:

```csharp
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

### Step 5: Wire Up CLI Commands

Add the backend to Modular's command structure:

```csharp
// Generic download command (replaces backend-specific commands)
var downloadCommand = new Command("download", "Download mods from a backend");
var backendOption = new Option<string?>("--backend", "Backend ID to use");
downloadCommand.AddOption(backendOption);

downloadCommand.SetHandler(async (backendId) =>
{
    var registry = InitializeBackends(settings, rateLimiter, database);

    IModBackend backend;
    if (backendId != null)
    {
        backend = registry.Get(backendId)
            ?? throw new ConfigException($"Unknown backend: {backendId}");
    }
    else
    {
        // Fall back to interactive selection
        backend = PromptBackendSelection(registry);
    }

    var errors = backend.ValidateConfiguration();
    if (errors.Count > 0)
    {
        Console.Error.WriteLine(
            $"Configuration errors: {string.Join(", ", errors)}");
        return;
    }

    await backend.DownloadModsAsync(settings.ModsDirectory, null, options, progress);
}, backendOption);
```

---

## Testing Your Module

### Unit Tests

Create tests in `tests/Modular.Core.Tests/Backends/GameBananaBackendTests.cs`:

```csharp
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;

namespace Modular.Core.Tests.Backends;

public class GameBananaBackendTests
{
    [Fact]
    public void ValidateConfiguration_MissingUserId_ReturnsError()
    {
        var settings = new AppSettings { GameBananaUserId = "" };
        var backend = new GameBananaBackend(settings);
        var errors = backend.ValidateConfiguration();
        Assert.Single(errors);
        Assert.Contains("user ID", errors[0]);
    }

    [Fact]
    public void ValidateConfiguration_ValidSettings_ReturnsEmpty()
    {
        var settings = new AppSettings { GameBananaUserId = "12345" };
        var backend = new GameBananaBackend(settings);
        var errors = backend.ValidateConfiguration();
        Assert.Empty(errors);
    }

    [Fact]
    public void Capabilities_ShouldBeNone()
    {
        var settings = new AppSettings { GameBananaUserId = "12345" };
        var backend = new GameBananaBackend(settings);
        Assert.Equal(BackendCapabilities.None, backend.Capabilities);
    }

    [Fact]
    public void Id_ShouldBeGamebanana()
    {
        var settings = new AppSettings { GameBananaUserId = "12345" };
        var backend = new GameBananaBackend(settings);
        Assert.Equal("gamebanana", backend.Id);
    }

    [Fact]
    public async Task ResolveDownloadUrl_ReturnsNull()
    {
        // GameBanana provides URLs inline, no resolution needed
        var settings = new AppSettings { GameBananaUserId = "12345" };
        var backend = new GameBananaBackend(settings);
        var result = await backend.ResolveDownloadUrlAsync("123", "456");
        Assert.Null(result);
    }
}
```

### Integration Tests (Manual / Optional)

```csharp
[Fact(Skip = "Requires network access")]
public async Task FetchSubscribedMods_Integration()
{
    var settings = new AppSettings
    {
        GameBananaUserId = Environment.GetEnvironmentVariable("GB_USER_ID") ?? ""
    };
    var backend = new GameBananaBackend(settings);
    var mods = await backend.GetUserModsAsync();
    // Verify response structure is parsed correctly
    Assert.NotNull(mods);
}
```

### Discovering API Responses for Test Fixtures

Use curl to capture real responses for mocking:

```bash
# Fetch subscribed mods for a user
curl -s "https://gamebanana.com/apiv10/Member/12345/Submissions?_aFilters[Generic_SubscriptionCount]=>0" \
  | jq . > test_fixtures/gb_subscriptions.json

# Fetch mod files
curl -s "https://gamebanana.com/apiv10/Mod/350538/Files" \
  | jq . > test_fixtures/gb_mod_files.json

# Legacy API with help
curl -s "https://api.gamebanana.com/Core/Item/Data?itemtype=Mod&itemid=350538&fields=name&help" \
  | jq . > test_fixtures/gb_legacy_help.json
```

---

## Common Pitfalls

### 1. Empty MD5 Checksums

The `_sMd5Checksum` field exists in file entries but is empty for most records. Do **not** rely on it for download verification. If you need integrity checks, compute MD5 locally after download and compare against a previously known hash.

```csharp
// Do NOT assume MD5 is available
if (!string.IsNullOrEmpty(file.Md5))
{
    var actualMd5 = await Md5Calculator.CalculateMd5Async(outputPath, ct);
    if (actualMd5 != file.Md5)
        _logger?.LogWarning("MD5 mismatch for {File}", file.FileName);
}
```

### 2. URL Encoding in File Names

GameBanana file URLs may contain special characters. Always use `Uri` for parsing rather than string manipulation:

```csharp
// Correct
var filename = Path.GetFileName(new Uri(url).LocalPath);

// Incorrect - may break on encoded characters
var filename = url.Split('/').Last();
```

### 3. Pagination

List endpoints return paginated results. If a user has many subscriptions, you must paginate:

```csharp
var page = 1;
var allRecords = new List<GameBananaRecord>();

while (true)
{
    var response = await _client
        .GetAsync($"Member/{userId}/Submissions")
        .WithArgument("_aFilters[Generic_SubscriptionCount]", ">0")
        .WithArgument("_nPage", page.ToString())
        .WithArgument("_nPerpage", "50")
        .AsJsonAsync();

    if (!response.RootElement.TryGetProperty("_aRecords", out var records))
        break;

    var batch = records.EnumerateArray().ToList();
    if (batch.Count == 0)
        break;

    // Parse and add to allRecords...
    page++;
}
```

### 4. API Version Differences

Do not mix API versions in a single request chain. If using `apiv10` as the base URL, all requests should target v10 endpoints. The response formats differ between versions.

### 5. File Overwrites

Always check if a file already exists before downloading, unless `--force` is specified. GameBanana mods may have large files and re-downloading wastes bandwidth:

```csharp
if (!options.Force && File.Exists(outputPath))
{
    _logger?.LogDebug("Skipping existing file: {Path}", outputPath);
    continue;
}
```

### 6. Timestamp Handling

GameBanana timestamps (`_tsDateAdded`, `_tsDateModified`) are Unix epoch seconds (not milliseconds):

```csharp
// Correct
var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;

// Incorrect - would be off by 1000x
var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
```

---

## API Reference Quick Sheet

### Base URLs

| Version | Base URL | Status |
|---------|----------|--------|
| Legacy | `https://api.gamebanana.com/` | Active (oldest) |
| v6 | `https://gamebanana.com/apiv6/` | Active |
| v10 | `https://gamebanana.com/apiv10/` | Active (recommended) |
| v11 | `https://gamebanana.com/apiv11/` | Active (newest) |

### Common APIv10 Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `Member/{id}/Submissions` | User's submissions |
| GET | `Mod/{id}/Files` | Mod's downloadable files |
| GET | `Mod/{id}/ProfilePage` | Full mod profile |
| GET | `Game/{id}/Categories` | Game's mod categories |

### Common Query Parameters

| Parameter | Example | Description |
|-----------|---------|-------------|
| `_aFilters[FilterName]` | `_aFilters[Generic_Game]=8694` | Filter results |
| `_nPage` | `1` | Page number (1-indexed) |
| `_nPerpage` | `15` | Results per page |
| `_sSort` | `_nDateModified` | Sort field |
| `_sDirection` | `desc` | Sort direction |

### Common Response Fields

| Field | Type | Description |
|-------|------|-------------|
| `_idRow` | int | Unique record ID |
| `_sName` | string | Display name |
| `_sFile` | string | Filename on disk |
| `_nFilesize` | long | File size in bytes |
| `_sDownloadUrl` | string | Direct download URL |
| `_sMd5Checksum` | string | MD5 hash (often empty) |
| `_tsDateAdded` | long | Unix timestamp (seconds) |
| `_tsDateModified` | long | Unix timestamp (seconds) |
| `_aRecords` | array | Result records array |
| `_nRecordCount` | int | Total record count |
| `_aSubmitter` | object | Author info |
| `_aPreviewMedia` | array | Preview images |

### HTTP Headers

| Header | Value | When |
|--------|-------|------|
| `User-Agent` | `Modular/1.0` | Always |
| `Accept` | `application/json` | Optional (default is JSON) |

### Community API Wrappers

| Language | Package | Repository |
|----------|---------|------------|
| Node.js | `gamebanana` | [SpikeHD/gamebanana](https://github.com/SpikeHD/gamebanana) |
| Node.js | `gamebanana` | [UPL123/gamebanana](https://github.com/UPL123/gamebanana) |
| Python | `pybanana` | [robinxoxo/pybanana](https://github.com/robinxoxo/pybanana) |
| .NET | GB.Net | [banana-org (GitHub)](https://github.com/banana-org) |
