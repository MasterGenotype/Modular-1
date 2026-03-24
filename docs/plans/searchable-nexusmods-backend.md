# Plan: Searchable NexusMods Backend + Mod Collections

## Background & Constraints

**The core problem:** NexusMods v1 REST API (`api.nexusmods.com/v1`) has no search endpoint. It only exposes the authenticated user's tracked mods. All current peer projects work around this in the same ways:

| Project | Approach |
|---|---|
| **Vortex** | Browser extension + `nxm://` protocol links; Collections share mod IDs |
| **Wabbajack** | Pre-compiled `.wabbajack` manifest files; no live search of NexusMods |
| **Mod Organizer 2** | `nxm://` link capture from browser; no API search |
| **Jackify** | GraphQL v2 API for metadata; links captured from browser |

**The solution:** Use two mechanisms in tandem:
1. **NexusMods v2 GraphQL API** (`api.nexusmods.com/v2/graphql`) — supports real full-text search, filtering, sorting, pagination. Same API that powers the NexusMods website.
2. **v1 Discovery Endpoints** — trending, latest added, recently updated (no auth search, but good for browsing).

---

## Phase 1 — SDK Contract Extensions

**File:** `src/Modular.Sdk/Backends/ISearchableBackend.cs` *(new)*

Add a separate opt-in interface rather than bloating `IModBackend`. Backends declare search support via `BackendCapabilities`.

```csharp
public interface ISearchableBackend
{
    Task<ModSearchResult> SearchModsAsync(ModSearchQuery query, CancellationToken ct = default);
}

public record ModSearchQuery
{
    public required string Terms { get; init; }
    public string? GameDomain { get; init; }       // "skyrimspecialedition"
    public int? CategoryId { get; init; }
    public ModSortOrder SortBy { get; init; } = ModSortOrder.Relevance;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;       // max 20 per request (rate limit friendly)
    public bool AdultContent { get; init; } = false;
}

public enum ModSortOrder { Relevance, Endorsements, Downloads, Updated, Added }

public record ModSearchResult
{
    public required List<BackendMod> Mods { get; init; }
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasNextPage => Page * PageSize < TotalCount;
}
```

**File:** `src/Modular.Sdk/Backends/BackendCapabilities.cs`

Add `Search = 0x40` flag.

**File:** `src/Modular.Sdk/Backends/Common/BackendMod.cs`

Add missing fields needed by search results:
- `int? EndorsementCount`
- `long? DownloadCount`
- `string? Version`
- `bool IsAdult`

---

## Phase 2 — NexusMods GraphQL Client

**File:** `src/Modular.Core/Backends/NexusMods/NexusModsGraphQlClient.cs` *(new)*

A thin wrapper that POSTs GraphQL queries to `https://api.nexusmods.com/v2/graphql`.

Key query: `mods(filter: { gameDomainName: $game }, query: $terms, sort: [...], limit: $n, offset: $o)`

```graphql
query SearchMods($terms: String!, $game: String!, $limit: Int!, $offset: Int!, $sort: [ModsSortInput!]) {
  mods(
    filter: { gameDomainName: { value: $game } }
    query: $terms
    sort: $sort
    limit: $limit
    offset: $offset
  ) {
    nodes {
      modId
      name
      summary
      author
      version
      categoryId
      endorsementCount
      downloadCount
      createdAt
      updatedAt
      pictureUrl
      adultContent
      game { domainName }
    }
    totalCount
    pageInfo { hasNextPage }
  }
}
```

Rate limit impact: GraphQL calls count against the same `x-rl-*` headers. Each search page = 1 API call. Handled by the existing `NexusRateLimiter`.

**File:** `src/Modular.Core/Backends/NexusMods/NexusModsModels.cs` *(new)*

Typed C# records for GraphQL responses:
```csharp
record NexusGraphQlMod(int ModId, string Name, string? Summary, string? Author,
    string? Version, int? CategoryId, int EndorsementCount, long DownloadCount,
    long CreatedAt, long UpdatedAt, string? PictureUrl, bool AdultContent,
    NexusGraphQlGame Game);
record NexusGraphQlGame(string DomainName);
record NexusGraphQlPageInfo(bool HasNextPage, int TotalCount);
```

---

## Phase 3 — NexusMods Backend: Search + Discovery

**File:** `src/Modular.Core/Backends/NexusMods/NexusModsBackend.cs` *(modify)*

1. Implement `ISearchableBackend`:

```csharp
public class NexusModsBackend : IModBackend, ISearchableBackend
{
    public async Task<ModSearchResult> SearchModsAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        // POST to /v2/graphql via NexusModsGraphQlClient
        // Map NexusGraphQlMod[] → BackendMod[]
        // Return paginated ModSearchResult
    }
}
```

2. Add `Capabilities |= BackendCapabilities.Search`

3. Add discovery methods (v1 endpoints, no extra auth cost):
   - `GetTrendingModsAsync(string gameDomain, int limit, CancellationToken ct)`
     → `GET /v1/games/{domain}/mods/trending.json`
   - `GetLatestAddedModsAsync(string gameDomain, int limit, CancellationToken ct)`
     → `GET /v1/games/{domain}/mods/latest_added.json`
   - `GetRecentlyUpdatedModsAsync(string gameDomain, string period, CancellationToken ct)`
     → `GET /v1/games/{domain}/mods/updated.json?period=1w` (1d / 1w / 1m)

---

## Phase 4 — Mod Collection Feature

Inspired by Wabbajack's `.wabbajack` manifest format and Vortex Collections.

**File:** `src/Modular.Sdk/Collections/IModCollectionRepository.cs` *(new)*

```csharp
public interface IModCollectionRepository
{
    Task<ModCollection> CreateAsync(string name, string gameId, CancellationToken ct = default);
    Task<ModCollection?> LoadAsync(string path, CancellationToken ct = default);
    Task SaveAsync(ModCollection collection, string path, CancellationToken ct = default);
    Task<List<ModCollectionEntry>> ListAsync(CancellationToken ct = default);
}
```

**File:** `src/Modular.Sdk/Collections/ModCollection.cs` *(new)*

```csharp
public class ModCollection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string GameId { get; set; }       // "skyrimspecialedition"
    public string BackendId { get; init; } = "nexusmods";
    public string SchemaVersion { get; init; } = "1.0";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ModCollectionEntry> Entries { get; set; } = [];
}

public class ModCollectionEntry
{
    public required string ModId { get; set; }
    public required string Name { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }      // pinned version string
    public string? FileId { get; set; }       // pinned file ID
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? Md5 { get; set; }          // integrity check (Wabbajack-style)
    public string? Url { get; set; }          // NexusMods page URL
    public string? Notes { get; set; }        // user notes per mod
    public bool IsOptional { get; set; }
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
}
```

Storage: JSON at `~/.config/Modular/collections/{name}.collection.json`

**File:** `src/Modular.Core/Collections/ModCollectionService.cs` *(new)*

Core service responsibilities:
- CRUD on collections
- `AddModAsync(collection, BackendMod, fileId?)` — resolves latest file if fileId not pinned
- `RemoveModAsync(collection, modId)`
- `DownloadCollectionAsync(collection, outputDir, options, progress, ct)` — bulk download all entries
- `VerifyCollectionAsync(collection, outputDir, ct)` — MD5 verify all downloaded files
- `ExportAsync(collection, path)` / `ImportAsync(path)` — portable JSON sharing
- `CheckUpdatesAsync(collection, ct)` — compare pinned fileIds to current latest

---

## Phase 5 — CLI Commands

**File:** `src/Modular.Cli/Commands/SearchCommand.cs` *(new)*

```
modular search <terms> [--game skyrimspecialedition] [--sort endorsements] [--page 1] [--limit 20] [--backend nexusmods]
modular search "SKSE64" --game skyrimspecialedition
modular search "armor retexture" --sort downloads --limit 10
```

Output: Spectre.Console table with columns: `ModId | Name | Author | Downloads | Endorsements | Updated`

**File:** `src/Modular.Cli/Commands/BrowseCommand.cs` *(new)*

```
modular browse trending --game skyrimspecialedition
modular browse latest --game skyrimspecialedition
modular browse updated --game skyrimspecialedition --period 1w
```

**File:** `src/Modular.Cli/Commands/CollectionCommand.cs` *(new)*

Sub-commands grouped under `collection`:
```
modular collection create "My Skyrim Build" --game skyrimspecialedition
modular collection list
modular collection show <name>
modular collection add <name> <modId> [--file-id <fileId>] [--optional]
modular collection remove <name> <modId>
modular collection download <name> [--output ~/Mods] [--dry-run] [--verify]
modular collection verify <name> [--output ~/Mods]
modular collection export <name> [--output ./export.json]
modular collection import <path>
modular collection check-updates <name>
```

---

## Phase 6 — GUI Changes

**File:** `src/Modular.Gui/ViewModels/NexusSearchViewModel.cs` *(new)*

New ViewModel for the NexusMods search tab:
- Properties: `SearchText`, `SelectedGame`, `SortOrder`, `IsLoading`
- `SearchResults` (ObservableCollection\<ModDisplayModel\>)
- `CurrentPage`, `TotalResults`, `HasNextPage`
- Commands: `SearchCommand`, `NextPageCommand`, `PrevPageCommand`
- `AddToCollectionCommand(mod)` — opens a dialog to pick or create a collection

**File:** `src/Modular.Gui/ViewModels/ModListViewModel.cs` *(modify)*

Replace the current "filter subscribed mods locally" search with actual API search:
- When `SearchText` is non-empty and >= 3 chars, debounce 400ms then call `SearchModsAsync`
- Keep existing tracked mods view accessible via a `ShowTrackedOnly` toggle

**File:** `src/Modular.Gui/ViewModels/CollectionViewModel.cs` *(new)*

- `Collections` (ObservableCollection)
- `SelectedCollection`, `CollectionEntries`
- Commands: `CreateCollectionCommand`, `DeleteCollectionCommand`, `AddModCommand`, `RemoveModCommand`, `DownloadCollectionCommand`, `ExportCommand`, `ImportCommand`

**Views** (Avalonia `.axaml`):
- `NexusSearchView.axaml` — search bar + results grid + pagination
- `CollectionView.axaml` — split panel: left = collection list, right = entries table
- `AddToCollectionDialog.axaml` — pick existing or create new

---

## Phase 7 — Rate Limiting & Caching Strategy

NexusMods GraphQL search costs the same as a v1 call. With 500 hourly / 20K daily limits:

- **Search debouncing**: 400ms delay before firing (prevents rapid keystroke queries)
- **Result caching**: Cache search results keyed by `(terms, game, sort, page)` with 5-min TTL in `ModMetadataCache` or a new `SearchResultCache`
- **Discovery feed caching**: Cache trending/latest for 15 minutes
- **Collection download**: Reuse existing `DownloadModsAsync` pipeline, feed collection entries instead of tracked mods

---

## File Change Summary

| File | Action |
|---|---|
| `src/Modular.Sdk/Backends/ISearchableBackend.cs` | **New** |
| `src/Modular.Sdk/Backends/BackendCapabilities.cs` | **Modify** — add `Search` flag |
| `src/Modular.Sdk/Backends/Common/BackendMod.cs` | **Modify** — add endorsement/download/version fields |
| `src/Modular.Sdk/Collections/ModCollection.cs` | **New** |
| `src/Modular.Sdk/Collections/IModCollectionRepository.cs` | **New** |
| `src/Modular.Core/Backends/NexusMods/NexusModsBackend.cs` | **Modify** — implement `ISearchableBackend`, add discovery |
| `src/Modular.Core/Backends/NexusMods/NexusModsGraphQlClient.cs` | **New** |
| `src/Modular.Core/Backends/NexusMods/NexusModsModels.cs` | **New** |
| `src/Modular.Core/Collections/ModCollectionService.cs` | **New** |
| `src/Modular.Core/Collections/ModCollectionRepository.cs` | **New** |
| `src/Modular.Cli/Commands/SearchCommand.cs` | **New** |
| `src/Modular.Cli/Commands/BrowseCommand.cs` | **New** |
| `src/Modular.Cli/Commands/CollectionCommand.cs` | **New** |
| `src/Modular.Gui/ViewModels/NexusSearchViewModel.cs` | **New** |
| `src/Modular.Gui/ViewModels/CollectionViewModel.cs` | **New** |
| `src/Modular.Gui/ViewModels/ModListViewModel.cs` | **Modify** — real search, tracked toggle |
| `src/Modular.Gui/Views/NexusSearchView.axaml` | **New** |
| `src/Modular.Gui/Views/CollectionView.axaml` | **New** |
| `tests/Modular.Core.Tests/NexusModsBackendSearchTests.cs` | **New** |
| `tests/Modular.Core.Tests/ModCollectionServiceTests.cs` | **New** |

---

## Implementation Order

1. **SDK contracts** — `ISearchableBackend`, `ModCollection`, updated `BackendMod` (no dependencies)
2. **GraphQL client + models** — isolated, testable without touching existing backend
3. **`NexusModsBackend` search** — wire GraphQL client, implement discovery endpoints, add `Search` capability
4. **`ModCollectionService`** — depends on backend but not on CLI/GUI
5. **CLI commands** — thin wrappers; search + collection commands
6. **GUI ViewModels** — `NexusSearchViewModel`, `CollectionViewModel`, update `ModListViewModel`
7. **GUI Views** — Avalonia AXAML for search and collections
8. **Tests** — unit tests for GraphQL parsing, collection CRUD, search result mapping
