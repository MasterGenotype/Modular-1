# mod.io backend

`ModIoBackend` (`src/Modular.Core/Backends/ModIo/`) reads mods from [mod.io](https://mod.io), the hub
behind the in-game mod managers of Baldur's Gate 3 and other games. Its backend id is `modio`.

## Credentials

Both are created at <https://mod.io/me/access>.

| Setting | Env var | Gives |
|---------|---------|-------|
| `modio_access_token` | `MODIO_ACCESS_TOKEN` | Everything, including **your subscriptions**. Sent as `Authorization: Bearer`. |
| `modio_api_key` | `MODIO_API_KEY` | Public data only: search, mod info, files, dependencies. Sent as `?api_key=`. |

With a token the key is not needed. Subscriptions always need the token.

## Games

A mod.io game is a number, not a name: Baldur's Gate 3 is `6715`. Wherever another backend
takes a game domain, mod.io takes this id. With none given, `modio_game_ids` is used.

Every request goes to the game's own host, `https://g-{gameId}.modapi.io/v1`. The old shared
host `api.mod.io` is deprecated and answers 404 to every request.

## Files

A mod has many uploads but one **live** file, the one the game installs. The backend reports
the live file as category `main` and every other upload as `old_version`, so the default
`main, optional` filter downloads exactly what the game would.

Download links (`binary_url`) can carry a verification hash that expires. The backend drops
links that have expired, and the downloader asks for a fresh one when a transfer is retried.

## Usage

```bash
modular download --backend modio            # subscriptions of every game in modio_game_ids
modular download --backend modio 6715       # subscriptions for Baldur's Gate 3
modular search -b modio -g 6715 "party limit"
modular track --backend modio 4345659       # subscribe (BG3: the game downloads it)
modular track https://mod.io/g/baldursgate3/m/alfira-joins-the-party
modular untrack --backend modio 4345659
modular tracked --backend modio
modular sync                                # BG3: record what the game downloaded
```

Downloads land in `{mods_directory}/modio/{game}/{mod name}/`, where `{game}` is the game's name
reduced to lower-case letters and digits (`baldursgate3`). See "Mods directory layout" in the README.

In the GUI, the **mod.io** page has a Subscriptions tab and a Search tab; credentials and
game ids are under Settings → mod.io. The Search tab picks its game by name: type part of it
and choose from the list (`ModIoBackend.SearchGamesAsync`, the `/games?_q=` endpoint, which
every game's host serves). Typing an id still works.

## Subscribing and unsubscribing

`ModIoBackend` implements `ITrackingBackend` (`modular track` / `untrack --backend modio`):

| Request | Answers |
|---|---|
| `POST /games/{g}/mods/{m}/subscribe` | **201** + the mod, or **200** if already subscribed |
| `DELETE /games/{g}/mods/{m}/subscribe` | **204** with an empty body, or **400** `error_ref` 15005 if not subscribed |

Writes need the access token, with **write** scope; an API key is read-only (11003). Every
write is sent `Content-Type: application/x-www-form-urlencoded`, even with no body, or mod.io
answers 415. Refusals map to `TrackingFailure`: 11003 read-only token, 11005 expired token,
11006 inactive account, 15000 taken down, 15001 hidden.

## Baldur's Gate 3's in-game mod manager

**The game downloads every subscription itself on its next launch.** Modular therefore never
installs a subscribed BG3 mod as well: a copy the game does not recognise as its own makes it
download the mod a second time. Verified on the game's files:

- The manager takes the pak out of the live upload's zip **byte for byte** and saves it in the
  user `Mods` folder under the zip's own name with `.pak` for `.zip`
  (`alfirajoinstheparty_3539eba9-6-6gki.zip` → `alfirajoinstheparty_3539eba9-6-6gki.pak`). The
  name changes with every upload. It keeps no other state on disk.
- A mod.io pak names its mod in its own `meta.lsx`: `PublishHandle` is the mod.io mod id.
  The game copies it into `PlayerProfiles/Public/modsettings.lsx`, so any non-zero
  `PublishHandle` is a mod id this backend can fetch.
- The `MD5` in a mod.io pak's `meta.lsx` is **not** a hash of the pak, the zip or its
  header, and two different mods' uploads can share one. Never tell uploads apart by it alone.
- Modular deploys BG3 paks as `<band>-<order>_<name>.pak`, which never matches the game's
  name. That is how Modular-installed copies of subscribed mods were downloaded twice.

So for BG3 (`InGameModService`, `src/Modular.Core/InGame/`):

- **track** subscribes and records the mod as *waiting for the game's download*
  (`game_owned_mod`, schema v8). Nothing is written to the game's folders. If Modular already
  deploys the same module, it stops; `--prefer modio` disables Modular's copy (staging kept),
  `--prefer nexus` skips the subscription.
- **sync** (also on the GUI's Subscriptions refresh) records what the game downloaded and
  reports uploads the game swapped in, subscriptions still waiting, game files for mods no
  longer subscribed, Modular copies of subscribed mods, and modules held by two files. It
  writes only Modular's own records.
- **untrack** unsubscribes and removes the game's file and its `modsettings.lsx` entry at once.
- Game-owned mods never enter `game_mod`, so the deployer can't rename, hardlink or delete them.
- Writes to the game's folders are refused while `bg3.exe` / `bg3_dx11.exe` runs.

Placing a file for the game ahead of its download ("pre-placing") is not done. It is only
considered once launches have shown the manager accepts an exact copy as its own; see
`docs/plans/modio-ingame-manager.md`.

## File history and the platform header

With `X-Modio-Platform` set, `/mods/{m}/files` lists only the live file. Without it, every
upload is listed (Alfira: 22). Old uploads, for rollback or pinning, have to be fetched
without the header.

## Rate limits

mod.io allows about 60 requests a minute for an API key. The client spaces requests one
second apart and, on HTTP 429, waits for `Retry-After` before retrying (twice at most).
