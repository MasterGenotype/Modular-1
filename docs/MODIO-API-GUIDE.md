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
```

Downloads land in `{mods_directory}/modio/{game}/{mod name}/`, where `{game}` is the game's name
reduced to lower-case letters and digits (`baldursgate3`). See "Mods directory layout" in the README.

In the GUI, the **mod.io** page has a Subscriptions tab and a Search tab; credentials and
game ids are under Settings → mod.io.

## Baldur's Gate 3's in-game mod manager

The game records the mod.io id of each mod it installed as `PublishHandle` in
`PlayerProfiles/Public/modsettings.lsx`. Any non-zero `PublishHandle` is a mod id this backend
can fetch.

## Rate limits

mod.io allows about 60 requests a minute for an API key. The client spaces requests one
second apart and, on HTTP 429, waits for `Retry-After` before retrying (twice at most).
