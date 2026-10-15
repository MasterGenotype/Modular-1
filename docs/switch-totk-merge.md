# Tears of the Kingdom: the merge installer

Most Switch games in Modular install each mod into its own LayeredFS folder under the
emulator's `load/<TitleID>/<ModName>/`, and the emulator layers them; when two mods ship
the same file, one simply overwrites the other.

Tears of the Kingdom (TitleID `0100F2C0115B6000`) installs the way
[TKMM](https://github.com/TKMM-Team/Tkmm) does instead. Each mod is imported into a
managed store and diffed against your own copy of the game (a *changelog*), and the
enabled mods of a profile are merged, in priority order, into **one** output folder. Two
mods that edit different parts of the same file both take effect, and the resource size
table (RESTBL) is regenerated. The emulator sees that output as a single mod,
`load/0100F2C0115B6000/TKMM`.

This is a port of **TKMM v2.0.0-rc1** on **TkSharp**; see [Version pin](#version-pin).
Every other TitleID keeps the per-folder installer unchanged.

## Setup

Merging needs your own game dump and keys, as TKMM does.

| Setting | Key | What it is |
|---|---|---|
| Keys | `keys` | Folder with `prod.keys` / `title.keys` (e.g. `~/.local/share/yuzu/keys`) |
| Base game | `base-game` | The base game `.xci` / `.nsp` (or split folder) |
| Update | `update` | The update `.nsp` |
| NAND | `nand` | An emulator NAND folder with the update installed (instead of an update file) |
| RomFS dump | `dump` | An extracted RomFS folder (instead of the packaged game) |
| SD card | `sd-card` | Root of an SD card or emuMMC with the game installed |
| Version | `version` | `Auto`, `1.4.2`, `1.4.1`, `1.4.0`, `1.2.1`, `1.2.0`, `1.1.2`, `1.1.1`, `1.1.0` |
| Language | `language` | MALS language, e.g. `USen`, `EUen`, `JPja` |
| Merge output | `merge-output` | Where merges are written (default below) |
| romfslite | `romfslite` | `true` to rename `romfs` to `romfslite` after merging (Atmosphère 20+ with the TotK Optimizer) |
| Emulator | `emulator` | The emulator's executable: Ryujinx, or a Yuzu-family emulator |
| 7-Zip | `7z` | 7-Zip executable for faster `.7z` installs (found on `PATH` if unset) |

The game needs a base game plus an update (1.1.0 or later), from any combination of the
sources above. With an emulator set and the version on `Auto`, the emulator's own game
is preferred: the update selected for TotK in Ryujinx, or the update on a Yuzu-family
emulator's NAND.

```bash
modular switch totk config keys ~/.local/share/yuzu/keys
modular switch totk config base-game ~/Switch/TOTK.xci
modular switch totk config nand ~/.local/share/yuzu/nand
modular switch totk config --check          # "Game found (version 121)"
```

List settings (`base-game`, `update`, `dump`, `nand`) are replaced by a new value; add
to them with `--add`. Any setting is unset with `--clear`. Settings live in the
`switch_totk` section of `~/.config/Modular/config.json`.

In the GUI, choose *Tears of the Kingdom* in the Switch panel's game selector; the same
settings are under *Game dump, keys and merge settings*.

## Commands

```bash
modular switch totk install ./MyMod.zip       # folder, .zip, .rar, .7z or .tkcl
modular switch totk install ./MyMod --merge   # and merge straight away
modular switch totk list [--all]              # current profile, 1 = highest priority
modular switch totk order MyMod 1             # move to the top
modular switch totk enable|disable MyMod
modular switch totk option MyMod                         # show options
modular switch totk option MyMod Colour Blue --on        # choose one
modular switch totk merge [--dry-run]
modular switch totk deploy [--target <dir>]
modular switch totk export-sd /run/media/me/SWITCH [--yes]
modular switch totk uninstall MyMod           # from every profile, then merge again
modular switch totk profile list|create <name> [--select]|select <name>|add <mod>|remove <mod>
```

Mods are named by name (case-insensitive) or by the ID `list` shows. A new install goes
to the bottom of the current profile (lowest priority), as in TKMM. Selecting a profile
merges its cheats and exefs straight away, as TKMM does; run `merge` for the rest.

The generic Switch commands route TotK here too:

| Command | For TotK |
|---|---|
| `switch install --game 0100F2C0115B6000` | Imports the scanned mods and merges. A re-run replaces each mod in place and skips unchanged ones. |
| `switch remove --game 0100F2C0115B6000 --mods A,B` / `--all` | Uninstalls them, then merges once. |
| `switch status --game 0100F2C0115B6000` | Profile, merge output, rollback snapshot, deploy target. |
| `switch rollback --game 0100F2C0115B6000` | Puts the merge output back as it was before the latest merge. |

## Output layout

```
~/.config/Modular/switch/totk/
├── data/                    TkSharp's store: state.db, contents/<mod id>/ (changelogs)
├── merged/                  default merge output
│   ├── romfs/               merged game files, incl. the regenerated RESTBL
│   ├── exefs/               merged code patches (IPS) and subsdk
│   ├── cheats/
│   └── romfs_metadata.bin
└── snapshots/merge-output/  the merge targets as they were before the latest merge

<emulator load dir>/0100F2C0115B6000/TKMM  ->  merge output (symlink)
```

**What a merge replaces.** Only `romfs`, `romfslite`, `exefs`, `cheats` and
`romfs_metadata.bin` in the merge output are deleted and rewritten; anything else in the
folder is left alone. They are hardlinked into the snapshot first (copied when the
snapshot is on another filesystem), so `switch rollback` can restore them.

**Changing the merge output** only creates the new folder. Neither the old nor the new
folder is moved, deleted or overwritten; the next merge writes to the new one. (TKMM
releases after rc1 move the old output on change; Modular deliberately does not.)

**Deploy** links the emulator's TotK mod folder to the merge output. The folder is the
configured emulator's (Ryujinx's `mods/contents/0100f2c0115b6000/TKMM`, or a
Yuzu-family emulator's `load_directory` from its `qt-config.ini`), else Yuzu's
`~/.local/share/yuzu/load/0100F2C0115B6000/TKMM`.

- An existing link to the merge output counts as deployed; a link elsewhere is replaced
  (removing a link deletes nothing it points at).
- An empty folder is replaced. **A folder with files in it is refused** and left alone —
  for example a `TKMM` folder written by TKMM itself. Move it aside to deploy.
- Where symlinks can't be created (Windows without Developer Mode), the output is
  copied instead, with a warning, and marked with `.modular-totk-copy`. Deploy again
  after each merge to refresh the copy; only the merge targets inside it are replaced.

**SD card export** (`export-sd`) merges with IPS patches written to
`<drive>/atmosphere/exefs_patches/TKMM`, then asks before deleting `romfs`, `romfslite`,
`exefs` and `romfs_metadata.bin` from `<drive>/atmosphere/contents/0100F2C0115B6000` and
copying the merge output there. The drive must already have an `atmosphere` folder.

## Version pin

Modular follows **TKMM v2.0.0-rc1** only, and uses TkSharp at the commit rc1 pins,
`797e76ec19bd6ce10af451a13909cffe8ddb21cd`. Nothing from rc2 or later is backported.

`lib/TkSharp` is a git submodule tracking
[MasterGenotype/TkSharp](https://github.com/MasterGenotype/TkSharp), branch
`modular/rc1-sharpcompress-0.50`: 797e76ec plus two patches, both for SharpCompress.

1. **Build against SharpCompress 0.50.4.** rc1's TkSharp targets SharpCompress 0.40.0,
   which carries advisory GHSA-6c8g-7p36-r338 (NU1902), so Modular uses 0.50.4. 0.50
   renamed the factories TkSharp calls (`ArchiveFactory.Open` and `SevenZipArchive.Open`
   to `OpenArchive`, `ZipReader.Open` to `OpenReader`); unpatched, every archive install
   fails with `MissingMethodException`. The patch bumps the package and renames the three
   calls.
2. **Buffer a `.tkcl` read from inside an archive.** SharpCompress 0.50's entry stream
   fails part-way under the small reads TkSharp's pack reader makes ("Unexpected end of
   stream while reading ZIP archive"); the patch copies such a stream to a temporary file
   first.

Clone with `git clone --recurse-submodules`, or run `git submodule update --init` in an
existing clone.

What is not ported from rc1: the TotK Optimizer, GameBanana browsing and install,
thumbnails, the legacy `.data` upgrade, the auto-updater and the Switch-native build.

### Behaviour kept from rc1

Ported as rc1 has it, including:

- `export-sd` merges and writes IPS patches to the card *before* asking to clear it, and
  its clear list leaves out `cheats`.
- Checking whether a `.7z` can use 7-Zip starts the 7-Zip executable once with no
  arguments and doesn't wait for it (rc1's `External7zHelper.CanUseExternal`).
- The copy helper refuses an output whose path *starts with* the source path, so a copy
  from `merged` into a sibling `merged2` is refused.

### TkSharp and threads

TkSharp keeps process-wide state (the changelog builder's ROM provider, profile events,
its log) and decompresses its pack-file lookup through a single static, non-thread-safe
zstd decompressor. Use one `TotkMergeService` per process and one operation at a time;
the GUI runs TotK operations one at a time, and the tests that touch TkSharp run
serially.

## Tests

`tests/Modular.Switch.Tests` covers routing by TitleID, merge cleanup (only the five
targets), snapshots and rollback, changing the merge output, deploy (link, re-deploy,
empty and non-empty targets, copy fallback) and reading `.zip`, `.7z`, `.rar` and `.tkcl`
mods through TkSharp's readers.

The integration tests merge real mods against the real game and are skipped unless these
are set:

```bash
export TOTK_KEYS_DIR=~/.local/share/yuzu/keys
export TOTK_BASE_GAME=~/Switch/TOTK.xci
export TOTK_NAND=~/.local/share/yuzu/nand      # or TOTK_UPDATE=<update .nsp>
make test-switch
```

They install two mods that change different messages in the same file (the language's
MALS archive) and check the merged output has both changes and a regenerated RESTBL,
then export to a fake SD card.
