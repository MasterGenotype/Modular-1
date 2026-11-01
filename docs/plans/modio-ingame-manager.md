# Plan: Track/Untrack (NexusMods + mod.io) and BG3 In-Game Mod Manager Compatibility

## Background & Constraints

Modular can **read** NexusMods tracked mods (`GET /v1/user/tracked_mods.json`) and mod.io
subscriptions (`GET /me/subscribed`), and `modular download` pulls from those lists. It cannot
**change** them. Both services expose the endpoints to do so.

For Baldur's Gate 3 there is a second consumer of mod.io subscriptions: **the game's built-in mod
manager downloads every subscribed mod on launch**. Modular has to manage those mods without causing
a second copy to be downloaded.

### Verified API behaviour

| | NexusMods (v1, from the published swagger) | mod.io (from the REST docs) |
|---|---|---|
| Track | `POST /v1/user/tracked_mods.json?domain_name=<d>`, form `mod_id` → **201** tracked, **200** already tracked | `POST /games/{g}/mods/{m}/subscribe` → **201** + Mod object, **200** already subscribed |
| Untrack | `DELETE` same URL and form → **200**, **404** `Missing` | `DELETE` same path → **204** (empty body), **400** error_ref 15005 if not subscribed |
| Auth | Existing `apikey` header | OAuth token with **write** scope. An API key is read-only (11003) |
| Errors | — | 401/11005 expired token; 403/11006 inactive account, 15000 DMCA, 15001 hidden |
| Gotchas | Domain is required; ids are ints | Every non-GET request needs `Content-Type: application/x-www-form-urlencoded`, even with no body, or it fails with 415. `include_dependencies` is undocumented, so don't rely on it |

### Verified BG3 in-game manager behaviour (from files on disk)

1. **Storage.** It extracts the pak from the live modfile zip and saves it as
   `Mods/<modfile filename stem>.pak`. Example: the zip holds `AlfiraJoinsTheParty_<uuid>.pak`, but
   it lands as `alfirajoinstheparty_3539eba9-6-6gki.pak`. The suffix changes with each upload.
2. **Identity.** mod.io paks embed their identity in `meta.lsx`:
   - `PublishHandle` is the mod.io mod id (Alfira = 4345659).
   - `MD5` is a per-upload id. It is **not** the md5 of the pak, the zip or the LSPK header.
   - `BG3ModSettings` already copies both fields into `modsettings.lsx`.
3. **No other state.** The Proton prefix has no separate mod.io SDK data folder. Locally there is only
   the pak and its `modsettings.lsx` entry.
4. **Duplicate cause (very likely).** `BaldursGate3GameModule.MapDeployPath` renames every pak to
   `<band>-<order>_<name>.pak`. A Modular-deployed copy therefore never has the name the manager
   expects, so the manager downloads its own.

### Unverified (Phase 0 settles these)

- Does the manager detect "installed" by **file name**, or by `PublishHandle`/`MD5`?
- Does it update files **in place** (same inode)? If so, hardlinked deploys would corrupt staging.
- Does it **delete** files and/or `modsettings.lsx` entries of unsubscribed mods?
- Does it silently replace an older upload with the live one? This is what broke a save on 2026-10-28.

---

## Design Principles

1. **One copy per mod.io mod**, at the manager's path `Mods/<live modfile stem>.pak`. Modular never
   deploys a second, band-prefixed copy of a mod the manager owns.
2. **Hand off by default.** `track` on a mod.io BG3 mod subscribes and lets the in-game manager
   download it. Modular adopts the file afterwards.
3. **Pre-place only when certain.** Modular writes a mod.io pak itself only when every certainty check
   passes (see Phase 6c). Otherwise it falls back to hand-off without asking. Pre-placing is only a
   speed-up; nothing depends on it.
4. **Repeats are not errors.** Tracking something already tracked, or untracking something that
   isn't, succeeds, so scripts and bulk runs can repeat safely.
5. **Never write while the game runs.** All writes to `Mods/` and `modsettings.lsx` are refused while
   `bg3.exe` or `bg3_dx11.exe` is running.

---

## Phase 0 — Experiments (needs user consent and two game launches)

Use small throwaway mod.io mods that aren't in the current loadout. Snapshot `Mods/` before and after
each launch: name, inode, mtime, md5, and `meta.lsx` `PublishHandle`/`MD5`. Also snapshot
`modsettings.lsx` and the subscription list. The user launches, waits at the main menu until
downloads finish, and quits.

| # | Launch | Setup | Question |
|---|---|---|---|
| E1a | 1 | Subscribed; exact replica pre-placed (name, bytes, modsettings entry) | Accepted, or re-downloaded? |
| E1b | 1 | Subscribed; exact replica pre-placed, **no** modsettings entry | Is a file without an entry treated as foreign? |
| E2 | 1 | Subscribed; copy only under Modular's band-prefixed name | Is a duplicate downloaded? Confirms the duplicate cause. Also: is removing Modular's copy before hand-off enough? |
| E3 | 1 | Subscribed; **older** upload under the live name | Is it updated? |
| E5 | 1 | E3's file after launch | Updated in place (same inode) or replaced? Decides whether hardlinks are safe |
| E4 | 2 | Unsubscribed; file still present | Are the file and/or the entry deleted? |
| E6 | 2 | Unsubscribed; file renamed to Modular naming | Left alone? Decides whether `untrack --keep` works |

Afterwards: unsubscribe from all test mods and remove their files and entries.

**Outcome rules**
- If E1a or E1b causes any re-download or second copy, `ModIo.Bg3PrePlace` stays permanently off and
  Phase 6c is dropped.
- If E5 shows in-place writes, game-owned files are always deployed by copy. This is the plan's
  default anyway.
- If E6 shows the file was removed, `untrack --keep` moves the pak into Modular staging and redeploys
  it as a normal Modular-owned file.

---

## Phase 1 — SDK Contract

**File:** `src/Modular.Sdk/Backends/ITrackingBackend.cs` *(new)*

```csharp
public interface ITrackingBackend
{
    TrackingTerms Terms { get; }   // Nexus: Track/Tracked, mod.io: Subscribe/Subscribed
    Task<TrackResult> TrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default);
    Task<TrackResult> UntrackAsync(string modId, string? gameDomain = null, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetTrackedIdsAsync(string? gameDomain = null, CancellationToken ct = default);
}

public enum TrackOutcome { Tracked, AlreadyTracked, Untracked, NotTracked }
public record TrackResult(TrackOutcome Outcome, BackendMod? Mod = null);

public enum TrackingFailure { NotAuthenticated, MissingWriteScope, ModNotFound, Unavailable, RateLimited, Other }
public sealed class TrackingException(TrackingFailure reason, string message) : Exception(message)
{
    public TrackingFailure Reason { get; } = reason;
}
```

**File:** `src/Modular.Sdk/Backends/BackendCapabilities.cs` — add `Tracking = 1 << 8`.

## Phase 2 — NexusMods

**File:** `src/Modular.Core/Backends/NexusMods/NexusModsBackend.cs`

- Implement `ITrackingBackend` with `PostAsync`/`DeleteAsync` + `WithFormBody`, going through the
  existing rate limiter.
- Update `_trackedModsCache` after each change, so `IsModTrackedAsync` stays correct.
- Check whether FluentHttp sends a form body on `DELETE`. If not, add the extension.

**Tests:** `tests/Modular.Core.Tests/Backends/NexusTrackingTests.cs`. They cover the mapping of
201/200/404, that domain, `mod_id` and `apikey` are sent, and the cache update.

## Phase 3 — mod.io client writes

**Files:** `ModIoClient.cs`, `ModIoBackend.cs`

- `SendAsync` takes the HTTP method and an optional form body, and always sets the form content type
  on non-GET requests.
- Treat 204 / empty body as success. Today `JsonDocument.Parse` throws on it.
- Never append `api_key` on writes. With no access token, throw `TrackingException(NotAuthenticated)`
  before sending anything.
- Map `ModIoException` error_refs to `TrackingFailure`: 11003 → MissingWriteScope,
  11005 → NotAuthenticated, 15000/15001 → Unavailable, 15005 → NotTracked outcome.
- Resolve a mod's game from `_gameOfMod`, then from `ModIoGameIds`, otherwise require `--game`.
- Downloads record the mod.io **file id, filename and md5** in the `.modular.json` sidecar.

**Tests:** extend `ModIoBackendTests.cs` (FakeModIo). They cover 204 without a crash, the read-only
token message, that no request goes out with only an API key, and that the content type is set.

## Phase 4 — Schema v8: ownership and source

**Migration** (`user_version` 7 → 8) on `game_mod`:

| Column | Values | Notes |
|---|---|---|
| `file_owner` | `modular` \| `game` | Default `modular` |
| `source` | `nexusmods` \| `modio` \| `local` | Exists, but is always `local` today. Fill from the sidecar on install; backfill from sidecars where possible |
| `remote_file_id` | mod.io / Nexus file id | — |
| `publish_handle` | int | From `meta.lsx` |
| `upload_md5` | string | `meta.lsx` `MD5`; detects updates made by the game |
| `track_state` | `none` \| `pending_download` \| `installed` | `pending_download` = subscribed, waiting for the in-game manager |

`InstallCommand` records the source from the sidecar.

## Phase 5 — BG3 module: game-owned files and hand-off

**File:** `src/Modular.Core/Modules/BaldursGate3GameModule.cs`

| | Modular-owned | Game-owned |
|---|---|---|
| File name | `<band>-<order>_<name>.pak` | Manager's exact name; `MapDeployPath` override skips the band prefix |
| Placement | Hardlink from staging | **Copy**; never hardlinked |
| Deploy / undeploy | Modular adds and removes | Never renamed or deleted, except on untrack |
| Load order / enable | Modular | Modular (`PublishHandle`/`MD5` from meta) |

- **Running-game guard** for every write to `Mods/` and `modsettings.lsx`.
- **`track` (mod.io, BG3) = hand-off:**
  1. **Pre-check:** find installed paks with the same module UUID or `PublishHandle`. If Modular owns
     a copy (Nexus/local), undeploy it from `Mods/` and `modsettings.lsx` and keep it in staging for
     rollback. Prompt first, unless `--prefer modio|nexus` is given. Modular never renames its own
     copy to look like the manager's, because that is pre-placing.
  2. Subscribe through the API, after confirmation (skip with `--yes`).
  3. Create the record: `file_owner=game`, `track_state=pending_download`, expected `publish_handle`
     and `remote_file_id`. **Nothing is written to `Mods/` or `modsettings.lsx`.**
- **`untrack`:**
  - `pending_download`: unsubscribe and drop the record.
  - `installed`: unsubscribe, then remove the file and its `modsettings.lsx` entry right away.
  - `--keep` pins the current version: unsubscribe, move the pak into staging, and redeploy it as
    Modular-owned under Modular naming, so the manager stops updating it.

## Phase 6 — Reconcile, duplicate guards, optional pre-place

### 6a. `modular sync --game 1086940`

Runs explicitly, and also before `deploy`, `installed` and a GUI refresh. It compares the
subscription list with a `Mods/` scan (`meta.lsx` UUID, `PublishHandle`, `MD5`) and the DB.

| Observation | Action |
|---|---|
| `pending_download` mod; matching pak now present | Adopt: `track_state=installed`, store name and `upload_md5`. If the manager placed it somewhere unexpected in `modsettings.lsx`, move it to the configured position and report |
| Unknown manager-named pak with `PublishHandle` ≠ 0 | Adopt as game-owned |
| Game-owned pak whose `MD5` changed | Record the new version; report "updated by in-game manager: X a → b" |
| Subscribed with no file | Report "downloads on next launch" |
| Game-owned file, no longer subscribed | Report as an orphan (the manager may delete it) |
| Two paks with the same module UUID | Report the duplicate; offer to keep one |

### 6b. Cross-backend duplicate guards

- **Nexus install of a module that is a subscribed mod.io mod** (by UUID, or by `PublishHandle` when
  present): stop. `--prefer modio` (default, matching the 2026-10-16 dedupe) skips the Nexus copy;
  `--prefer nexus` unsubscribes the mod.io one.
- **mod.io track of a mod already installed** (by `PublishHandle`): warn, then apply the Phase 5
  pre-check.

### 6c. Pre-place (optional; built only if Phase 0 E1a and E1b pass)

Behind `ModIo.Bg3PrePlace`, which is **off by default**. A mod is pre-placed only if **all** of these
hold. Otherwise it is handed off without asking:

| Check | Why |
|---|---|
| Phase 0 E1a/E1b showed the manager accepts an exact replica | The basis for certainty |
| Name == live modfile filename with `.zip` → `.pak`, same casing | The name may be how the manager detects it |
| Pak bytes == the pak inside the live modfile zip; zip verified against the mod.io md5 | No repack and no stale upload |
| `meta.lsx` `PublishHandle` == mod id, and `MD5` matches the live upload | No stale upload under the right name |
| Zip contains exactly one pak | Multi-pak naming wasn't tested |
| The placement pattern was covered in Phase 0 (with or without a modsettings entry) | Untested cases hand off |
| Game not running; subscription confirmed | Avoids racing the manager |

## Phase 7 — CLI and GUI

```
modular track   <mod|url>... [--backend nexusmods|modio] [--game] [--with-requirements] [--installed] [--prefer modio|nexus] [--yes] [--dry-run]
modular untrack <mod|url>... [--backend] [--game] [--keep] [--dry-run]
modular tracked [--backend] [--game]
modular sync    --game <id>
```

- `ModTrackingService` (`Core/Services/`) is shared by the CLI and GUI. It handles link parsing,
  batches (one result per mod, continues after a failure), `--with-requirements` (same-site only,
  skips external links) and `--installed` (source from sidecars).
- GUI:
  - Track/Untrack in NexusMods search (including "Track selected").
  - Untrack/Unsubscribe in the Tracked and Subscriptions tabs.
  - Subscribe in mod.io search, with a confirmation explaining the in-game download.
  - Badges: Tracked, Game-owned, Updated by in-game manager, Downloads on next launch.
  - Bulk untrack asks for confirmation.

## Phase 8 — Tests, docs, package

- Snapshot-based `sync` scenarios built from the Phase 0 results.
- Guard tests: duplicate by UUID, running game, hand-off writes nothing.
- Pre-place certainty tests: each failing check falls back to hand-off.
- Docs: `README.md`, `API-BACKENDS-GUIDE.md`, `MODIO-API-GUIDE.md`.
- Package: `makepkg` in `pkg/`, then `sudo deploytix update <abs path>.pkg.tar.zst` and reboot.

## Live checks (each needs user OK; they change the user's accounts)

- NexusMods: track a small mod, list it, untrack it.
- mod.io: covered by Phase 0. This also confirms whether the current token has write scope; if not,
  create a read+write token at mod.io/me/access.
