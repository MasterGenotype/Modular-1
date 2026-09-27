# Modular: Game Modules + ordered staging/deployment pipeline (revision 4)

> Revision 2 — refined with community install documentation for each supported
> game type (see [Research findings](#research-findings) and [Sources](#sources)).
> Changes from revision 1 are marked **[R2]**.
>
> Revision 3 — fixes four flaws found by checking revision 2 against the code
> (changeset paths, snapshot restore, the pre-install conflict check, installer
> backups) and adds a phased delivery order. Changes are marked **[R3]**.
> Revision 3.1 adds a choice of deployment method (hardlink, symbolic link or
> copy). It is described in §5a and also marked **[R3]**.
>
> Revision 4 folds in an external cross-check of install targets against Nexus
> Mods pages. It makes the destination a property of the **detected package
> type**, not just the game (§2a), adds anchors so FF7 Remake never matches
> FF7 Rebirth, protects "load-last" framework paks from order-prefixing, and
> records verified route tables for the BG3 and FF7 Rebirth modules that are
> still out of scope. Changes are marked **[R4]**.

## Problem

The install pipeline (`ModInstallationService` → `InstallerManager` → `IModInstaller`) writes each archive straight into the game directory. It has no notion of which game it is installing for, where mods belong, or what order they apply in, and game-specific knowledge is scattered across the installers. This work has two goals:

1. Refine the pipeline into detection → target-path resolution → staging → ordered deployment.
2. Introduce **Game Modules**, so installer backends plug in as self-contained modules, either built-in or supplied by plugins.

No new game support is added in this pass. The existing Cyberpunk 2077, FF7 Remake and Horizon Zero Dawn installers get wrapped as modules.

## Current state (key findings)

* `ModInstallationService.cs:103` calls `SelectInstallerAsync(archivePath)` **without a gameId**. As a result, `SupportedGameIds` scoping never applies in real installs.
* Staging is dead code. `StagingManager` cannot create a `StagingSession`.
* Bookkeeping is inconsistent: some installers record absolute paths and others relative ones. Backups use two suffixes (`.backup` and `.modular.bak`), but uninstall only restores `.backup`.
* `FomodInstaller.InstallAsync:174` uses `entry.Name`, which flattens every directory into the game root.
* Nothing writes the `detected_games` / `engine_detection` tables, so auto-snapshots never fire.
* Game resolution only does Steam substring matching. There is no walk-up to the game root, no Proton prefix and no engine info.
* Plugin installers are never registered into the `InstallerManager`.
* `ModProfile.LoadOrder`, `ResolutionResult.InstallOrder` and `FileConflictIndex` are populated but never consumed.
* **[R2]** `FF7RArchiveAnalyzer` routes `Engine.ini`/`Input.ini` to `<game>/Config/`. UE4 does not read user config from there. The real location is `Documents/My Games/FINAL FANTASY VII REMAKE/Saved/Config/WindowsNoEditor/`, so these mods currently install but have no effect. The `TargetRoot` design below fixes this.
* **[R3]** `ModInstallationService.cs:177` stores `installResult.InstalledFiles` in the changeset, and `UninstallAsync` deletes exactly those paths. Once installs are redirected into staging, these paths point into `~/.config/Modular/staging/…`. A changeset-based uninstall would delete the staged copies and leave the deployed links in the game folder.
* **[R3]** `SnapshotManager.RestoreSnapshotAsync` restores by diffing committed changesets, then calling `UninstallAsync` / `InstallAsync` once per mod. Under the new flow every install deploys, so a restore of N mods would purge and redeploy N times, and each intermediate deployment would be visible on disk.
* **[R3]** Step 5 of `InstallAsync` (`ModInstallationService.cs:139`) registers a "conflict" whenever a destination already exists in the game folder. After deployment, every file Modular deployed exists there, so each reinstall or redeploy would report conflicts with itself.
* **[R3]** The Cyberpunk, FF7R and HZD installers write `.modular.bak` backups, and LooseFile, BepInEx, UnrealPak and Steam write `.backup`, both next to their destination. With a staging target, the destination directory is empty, so this code never backs anything up. It must also not throw there.
* **[R2]** `HZDModInstaller` scopes to AppID 1151640 (Complete Edition), which is correct. Nothing yet stops a *path*-detected Horizon Zero Dawn **Remastered** install (AppID 2561580) from being treated as HZD, and Remastered has a completely different mod layout (see below).

## Research findings

### Two different notions of "order"

The biggest correction to revision 1 is that there are two separate orderings, and the games disagree on the second one:

| Concept | Meaning | Who decides |
|---|---|---|
| **File priority** | When two staged mods ship the *same relative path*, which physical file is deployed | Modular's `DeploymentPlanner` (last in list wins) |
| **Engine load order** | When two *different* files override the same game asset, which one the engine applies | The game: filename sort order or a manifest file |

| Game / engine | Engine load-order rule | Who wins a conflict | Ordering mechanism |
|---|---|---|---|
| Cyberpunk 2077 legacy `.archive` (`archive/pc/mod`) | ASCII/binary-alphabetical (`!`,`#` < `A`–`Z` < `_` < `a`–`z`) | **First loaded wins** | `archive/pc/mod/modlist.txt`, one archive filename per line, overrides alphabetical order |
| Cyberpunk 2077 REDmod (`mods/<name>/`) | Loads after all legacy archives; order is set at deploy time | Per-file, by REDmod order | `redMod.exe deploy -root=… -mod=A -mod=B …` or `-modlist=<file>`; game launched with `-modded` |
| UE4/UE5 paks (FF7R `End/Content/Paks/~mods`) | Mount order is alphabetical; `~mods` sorts last | **Last mounted wins** | Filename prefixes. Paks must end `_P.pak`, which gives patch-priority mounting |
| UE IoStore (`.pak` + `.utoc` + `.ucas`) | Same as above. The three files must share one stem | Last mounted wins | Rename all three together (not FF7R, which is UE4 pak-only, but relevant to `UnrealPakInstaller`) |
| HZD Complete Edition (Decima, `Packed_DX12/Patch*.bin`) | Alphabetical | Later patch wins (the community renames to `x_Patch_…` to raise priority) | Filename prefixes |
| HZD Remastered | No `Packed_DX12` workflow. Mods are root-level DLL loaders (`winhttp.dll` + `mod_config.ini`) | n/a | n/a |
| Bethesda (future) | `plugins.txt` in `%LOCALAPPDATA%/<Game>/`, `*` prefix = enabled | Last wins | Manifest file outside the game dir |
| BG3 (future) | Paks in `%LOCALAPPDATA%/Larian Studios/Baldur's Gate 3/Mods`, order in `PlayerProfiles/Public/modsettings.lsx` | Manifest order | Manifest file outside the game dir |

**Consequence:** Modular keeps one user-facing rule, "later in the order list wins." Each module then translates that list into the engine's own mechanism in `WriteLoadOrderAsync` / `MapDeployPath`. For Cyberpunk that means writing `modlist.txt` in **reverse**, because the first archive loaded wins.

### Runtime-generated and tool-modified files

Some files in the game directory are written by the game or by mod tooling after deployment:

* `r6/cache/final.redscripts`: redscript recompiles it at launch and keeps `final.redscripts.bk` as the vanilla copy.
* `r6/cache/modded/`: REDmod deploy output.
* Cyber Engine Tweaks writes per-mod `db.sqlite3`, logs and settings inside `bin/x64/plugins/cyber_engine_tweaks/mods/<mod>/`.
* HZD's `Patch_zzzzPrefetch.bin` is shipped by several mods and regenerated by tools. It is a common file-level conflict.

A naive purge/restore would either destroy user state or restore a stale vanilla file over a tool's output. Editors that save atomically (write a temp file, then rename) also silently **break hardlinks**.

### Proton / Linux loader requirements

Proxy-DLL loaders only load under Proton when a `WINEDLLOVERRIDES` native-first override is set:

* Cyberpunk (RED4ext / CET): `winmm,version=n,b`.
* BepInEx: `winhttp=n,b`.
* FF7R hooks (`dxgi`, `xinput1_3`, `dinput8`), HZD Remastered (`winhttp`) and DXVK wrappers (`d3d11`, `dxgi`) need the same kind of override.

Steam sometimes resets launch options after updates, and a missing override is the most common cause of "mod installed but does nothing" reports.

### Per-game layout rules worth enforcing

* Cyberpunk: `.archive` files must sit **directly** in `archive/pc/mod/`; subfolders are not loaded. A REDmod needs `mods/<name>/info.json`. Each CET mod lives in its own folder with `init.lua`. The manual install is "merge `archive/`, `bin/`, `r6/` (and `red4ext/`, `engine/`, `mods/`) into the game root."
* FF7R: nearly everything is a `_P.pak` into `End/Content/Paks/~mods/` (create the folder if missing). Hooks and 3DMigoto go to `End/Binaries/Win64/`. User INIs go to Documents.
* HZD CE: `.bin` patches go to `Packed_DX12/`. Uninstall means removing the patch files.

### GameBanana specifics

* Mod pages give free-form instructions, and the API exposes no install path, so archive-content detection (the existing analyzers) remains the source of truth.
* Many GameBanana ecosystems deploy into a **loader-managed folder**, not the game directory. For example, Reloaded-II keeps one folder per mod containing `ModConfig.json` under `<Reloaded>/Mods`, and a loader config enables it. This validates `TargetRoot` and the "folder-per-mod" mapping below for future modules.
* 1-Click installs use `https://gamebanana.com/mmdl/{fileId}` behind a manager-specific URL scheme. This is out of scope here, but the `game_mod` table should store the GameBanana file id so it can be supported later.

## Proposed design

### 1. SDK contract (`src/Modular.Sdk/Modules/`)

* `IGameModule` members:
  * `ModuleId`, `DisplayName`
  * `GameIds` (slugs + Steam AppIDs; **[R2]** plus GameBanana game ids), `SteamAppIds`
  * `Detect(string installPath) → GameModuleMatch?` (confidence + evidence, anchor-file based)
  * `ResolveModPaths(GameInstallation) → GameModPaths`
  * `GetInstallers() → IReadOnlyList<IModInstaller>`
* Optional hooks, each with a default no-op:
  * `MapDeployPath(DeployPathContext) → DeployTarget`. **[R2]** The context carries the relative path, the mod's order index and the enabled mod count. The result is `(RootName, RelativePath)`, so a module can both rename a file *and* route it to a non-game root.
  * `WriteLoadOrderAsync(GameInstallation, IReadOnlyList<DeployedMod>, CancellationToken)`
  * **[R2]** `ValidateStaged(GameInstallation, StagedMod) → IReadOnlyList<ModDiagnostic>`: layout checks run after staging (warnings, or errors that block deploy).
  * **[R2]** `GetGeneratedPaths() → IReadOnlyList<string>`: glob patterns (relative to a root) owned by the game or tools. They are excluded from vanilla backup/restore and conflict reports and never purged (for example `r6/cache/**`, `bin/x64/plugins/cyber_engine_tweaks/mods/*/db.sqlite3`, `*.log`).
  * **[R2]** `GetLaunchRequirements(GameInstallation, IReadOnlyList<DeployedMod>) → LaunchRequirements`: required launch arguments (such as `-modded`), `WINEDLLOVERRIDES` entries, and post-deploy tool actions. These are reported only; Modular does not edit Steam's `localconfig.vdf`.
* `GameInstallation`: game id, AppID, install root, store, Proton prefix path, engine family, module id, **[R2]** build id (from `appmanifest_<id>.acf` `buildid`).
* `GameModPaths`: named roots plus `DefaultRoot`. **[R2]** Standard root names are defined in the SDK:

  | Root name | Windows | Proton |
  |---|---|---|
  | `game` | install dir | install dir |
  | `localappdata` | `%LOCALAPPDATA%` | `<pfx>/drive_c/users/steamuser/AppData/Local` |
  | `documents` | `SpecialFolder.MyDocuments` | `<pfx>/drive_c/users/steamuser/Documents` |

  Modules add their own named roots on top, for example FF7R's `userconfig`.
* Additive, non-breaking extensions: `InstallContext.Game` / `InstallContext.ModPaths`, and `FileOperation.TargetRoot` (null = `game`).

### 2. Core module system (`src/Modular.Core/Modules/`)

* `GameModuleRegistry`:
  * Registers built-in modules and modules discovered from plugins (`PluginLoader` gains `IGameModule` discovery, exposed as `LoadedPlugin.Modules`).
  * Resolves by game id/AppID or by path detection.
* Built-in modules wrap the existing installers without changing their logic. Their anchors are refined as follows **[R2]**:
  * **`CyberpunkGameModule`**
    * Anchors: `bin/x64/Cyberpunk2077.exe`, `archive/pc/content`.
    * `WriteLoadOrderAsync` writes `archive/pc/mod/modlist.txt` listing every deployed `.archive` from **highest to lowest priority** (the reverse of Modular's order, because the first archive loaded wins). Archives Modular does not manage are appended after the managed ones in ASCII order, so the list is complete. A partial `modlist.txt` has ambiguous semantics.
    * REDmods: `GetLaunchRequirements` emits `-modded` plus a `redMod.exe deploy -root=<game> -mod=<a> -mod=<b>…` action in Modular order. On Windows the deployer runs it when `tools/redmod/bin/redMod.exe` exists. On Linux the command is printed, and running it through Proton is left for later.
    * `ValidateStaged` flags `.archive` files nested below `archive/pc/mod/<sub>/`, a REDmod without `info.json`, and a CET mod folder without `init.lua`.
    * Generated paths: `r6/cache/**`, CET `mods/*/db.sqlite3`, `*.log`.
    * On Proton, RED4ext/CET loaders produce the override `winmm,version=n,b`.
  * **`FF7RemakeGameModule`**
    * Anchors: `End/Binaries/Win64/ff7remake_.exe` and `End/Content/Paks`. **[R4]** `End/Content/Paks` alone is **not** sufficient. FF7 Rebirth (AppID 2909400) shares the `End/` layout but has a different mod ecosystem (IoStore bundles, Reunion's `End/Mods`). Detection requires the Remake executable and rejects directories containing the Rebirth executable, so Rebirth falls through to `GenericGameModule` until it has its own module.
    * Adds a `userconfig` root at `documents/My Games/FINAL FANTASY VII REMAKE/Saved/Config/WindowsNoEditor`. `MapDeployPath` routes the analyzer's `Config/*.ini` routes there, which fixes the dead `Config/` bug without touching the analyzer.
    * **Pak order-prefixing moves in scope.** The research confirms that UE mounts alphabetically and the last mount wins, which matches Modular's rule. For paks under `~mods/`, `MapDeployPath` renames `<name>.pak` to `<NNN>_<name>.pak`, where NNN is the zero-padded order index. The `_P` suffix is kept, and `.utoc`/`.ucas`/`.sig` siblings sharing the stem are renamed together.
    * **[R4] Load-last framework paks are exempt.** Loader paks rely on their own names to sort last (for example Rebirth's `ZGameInstanceLoader.pak`). Prefixing would move them into the ordered band and could break the loader. Modules declare these with a `PinnedLast` glob list: pinned paks keep their names and deploy after all numbered paks. The FF7 Remake list starts empty, the mechanism is generic, and a warning is emitted for any unprefixed `Z*`/`zz*` pak so the user can pin it.
    * `ValidateStaged` warns about a pak without the `_P` suffix and a pak outside `~mods`, and notes that 3DMigoto and DXVK require DX11.
    * On Proton, hooks produce overrides (`dxgi`, `xinput1_3`, `dinput8`, `d3d11` as present, `=n,b`).
  * **`HorizonZeroDawnGameModule`** (Complete Edition only, AppID 1151640)
    * Anchors: `HorizonZeroDawn.exe` + `Packed_DX12/`. The Remastered exe/AppID 2561580 is explicitly **not** matched, so it falls through to `GenericGameModule`, whose root-level DLL mods fit the loose-file installer.
    * `MapDeployPath` renames `Packed_DX12/Patch_<x>.bin` to `Packed_DX12/Patch_<NNN>_<x>.bin`. It keeps the `Patch_` prefix, because it is unverified whether the engine requires it. **This mapping is disabled by default** behind a module option until it is verified in-game.
    * `Patch_zzzzPrefetch.bin` is special-cased: the last writer still wins, but the conflict is reported as a warning ("regenerate prefetch").
  * **`GenericGameModule`**: fallback. The game root is the install dir, universal installers only, identity mapping. On Proton, proxy DLLs deployed next to any `.exe` produce override hints (see §5).
* `InstallerManager.SelectInstallerAsync` builds its candidates from the resolved module's installers, the universal installers and the plugin installers. `ModInstallationService` passes the resolved game through, which fixes the missing-gameId bug.

### 2a. Destination = game × package type **[R4]**

A game module selects the *detector set*. The detected **package type** selects the destination. One game routinely needs several destinations:
* FF7 Rebirth sends ordinary paks to `End/Content/Paks/~mods` and Reunion/Dresscode plugins to `End/Mods/<mod>`.
* BG3 sends ordinary paks to AppData `Mods`, and Script Extender to `<game>/bin`.

Routing is therefore resolved per file, in this order of precedence:

1. **Declared structure.** When archive paths already start at a recognised game-relative root, the tree is deployed as-is from that root and never rearranged. For Cyberpunk those roots are `archive/`, `bin/`, `r6/`, `red4ext/`, `engine/`, `mods/`. Nexus authors for Cyberpunk, CET and RED4ext all say "extract into the game root". Any wrapper folder is stripped, which the analyzers' existing `StrippedPrefix` already does.
2. **Package metadata.** Author- or loader-provided manifests outrank heuristics: REDmod `info.json`, FOMOD `ModuleConfig.xml`, and loader manifests such as Reunion plugin descriptors or BG3 native-loader configs. A `*.dll` alone never implies a destination, because in BG3 the required loader decides between `bin`, `bin/NativeMods` and AppData `Plugins`.
3. **Loose-file fallback.** Extension rules apply only to files not placed by rule 1 or 2. For Cyberpunk: `*.archive`/`*.xl` go to `archive/pc/mod/`, `*.reds` to `r6/scripts/<mod>/`, TweakXL `*.yaml` to `r6/tweaks/<mod>/`, a folder with `init.lua` to `bin/x64/plugins/cyber_engine_tweaks/mods/<mod>/`, a RED4ext plugin DLL folder to `red4ext/plugins/<plugin>/`, and a folder with `info.json` to `mods/<mod>/`.

**Contract changes:**
* `FileOperation` gains `PackageType` (a string id such as `cp77.legacy_archive`, `ue.pak_bundle`, `bg3.pak`) and a `RouteSource` (`Declared` | `Metadata` | `Heuristic`). Together with `TargetRoot` these are recorded in the staged manifest and shown by `install --dry-run`, so a mis-route can be traced to the rule that caused it.
* `IGameModule.GetPackageRoutes()` (default: empty) returns a declarative table of `PackageRoute(PackageType, Root, SubPath, Layout)`, where `Layout` is `Preserve` | `FlatFiles` | `FolderPerMod`. Future modules can then route by data through a generic table-driven installer, with no bespoke analyzer. The three existing modules keep their analyzers. Their `FileRoutes` already follow rules 1 → 3: `CyberpunkArchiveAnalyzer` anchors on `r6/scripts/`, `archive/pc/mod/` and `r6/tweaks/` before falling back to extension routing. They only need to label each route with its `PackageType` / `RouteSource`.
* **Root tokens:** routes use `$GAME`, `$LOCALAPPDATA`, `$DOCUMENTS` and module-defined roots (such as `$USERCONFIG`). The resolver translates these, through the Proton prefix on Linux. A module never contains a Steam-library or prefix path.
* **No invented folders:** `FlatFiles` layouts (BG3 `Mods/*.pak`, Cyberpunk `archive/pc/mod/*.archive`) must not get a `<modname>/` subfolder. `ValidateStaged` flags any extra folder level for these package types.

### 3. Game detection and target paths (`src/Modular.Core/GameDetection/GameInstallationResolver.cs`)

* `--game` accepts an AppID, NexusMods slug, **[R2]** GameBanana game id, display name or path.
* Lookup runs the Steam scan first (exact AppID, then module `GameIds` slug, then name match), then falls back to a manual path.
* For manual paths, the resolver walks up to 3 parents to find the directory a module's `Detect` anchors on. Pointing at `bin/x64` (Cyberpunk) or `End/Binaries/Win64` (FF7R) still resolves the real root.
* The resolver fills in the Proton prefix (`<library>/steamapps/compatdata/<appid>/pfx`), the engine (via `CompositeEngineDetector`) and **[R2]** `buildid`. It then upserts `detected_games` / `engine_detection`.
* `modular detect` and `detect-engine` also print the matched module, **[R2]** the resolved named roots, and whether a Proton prefix was found.

### 4. Per-mod staging

* `StagingManager` gets real sessions backed by persistent `~/.config/Modular/staging/<gameKey>/<modId>/` directories. **[R2]** Each staged mod is laid out as `<rootName>/<relative path>` (`game/…`, `userconfig/…`), so non-game roots survive restaging.
* Redirection is generic: the plan's `TargetDirectory` is rewritten to `stagingRoot/game/<relative(gameDir, plan.TargetDirectory)>`. That handles UnrealPak's absolute `~mods` target. Operations carrying a `TargetRoot` go to `stagingRoot/<TargetRoot>/…`.
* **[R3]** Paths are normalised in one step, before anything else looks at them. Installers emit paths relative to `plan.TargetDirectory`. FF7R pak-only plans and UnrealPak set that to `…/Content/Paks/~mods`, so their raw routes are relative to `~mods`, not the game root. The stager prefixes `relative(gameDir, plan.TargetDirectory)` to produce `(root, pathRelativeToRoot)` pairs. `MapDeployPath`, `ValidateStaged`, `GetGeneratedPaths` matching and the planner only ever see these normalised pairs.
* FOMOD's directory flattening is fixed. Staged files are recorded relative to their root.
* **[R3]** Installer-level backups (`.modular.bak` / `.backup`) become intentionally inert under staging, because the target folder is always empty. Vanilla backups are owned solely by the `Deployer`. The installer code is left untouched this pass, with a follow-up to delete it once legacy direct installs are removed. A test runs every built-in installer against an empty staging target and asserts that it succeeds and creates no backup files.
* **[R2]** `ValidateStaged` runs after staging. Errors abort the install before it registers or deploys, and warnings go into the install report.

### 5. Ordered deployment (`src/Modular.Core/Deployment/`)

* New DB table (schema v5) `game_mod` with columns: game_key, mod_id, module_id, installer_id, staging_path, order_index, enabled, installed_at, **[R2]** source (`nexus`/`gamebanana`/`local`), source_file_id, **[R3]** changeset_id.
* **[R3]** The v4 → v5 migration also adds `managed_by` (`legacy` | `deployment`, default `legacy`) and `game_key` columns to the changeset table. Existing rows stay `legacy`.
* Load order comes from, in priority order: `ModProfile.LoadOrder`, then the stored `order_index`, then install sequence. The rule "later wins" is documented as Modular's single user-facing rule.
* `DeploymentPlanner`:
  * Applies `MapDeployPath` per file.
  * Resolves last-writer-wins per `(root, relativePath)` and feeds overlaps into `FileConflictIndex`.
  * **[R2]** Skips `GetGeneratedPaths()` matches for conflicts.
  * **[R2]** Compares against engine-level conflicts it can see cheaply (same archive/pak basename in the same folder after mapping) and notes them in the report.
* `Deployer`:
  * **Purge.** Removes only files whose recorded identity still matches `deployment.json`, and restores the vanilla originals. How identity is recorded depends on the deployment method (§5a).
    * **[R2]** A deployed file whose identity changed was edited in place or had its link broken by an atomic save. It is **moved into `staging/<gameKey>/_overwrite/<root>/…`** instead of being deleted, and reported, so user edits are never lost.
    * **[R2]** Files under generated paths are left alone.
  * **Vanilla backup.** Vanilla files are backed up once to `~/.config/Modular/backups/<gameKey>/<buildId>/`. **[R2]** If the game's `buildid` changed since the last deployment, the old backups are stale: purge does *not* restore them over updated game files. The current files are re-snapshotted, and the user gets a warning to verify game files.
  * **Placing files.** Staged files are placed into each resolved root using the configured deployment method (§5a), which defaults to hardlink. **[R2]** Roots in the Proton prefix are usually on the same device as the library, so hardlinks work there. Documents folders on Windows can be OneDrive-redirected, and the fallback covers that case.
  * **Completion.** Writes `staging/<gameKey>/deployment.json` (root, rel path, mod, identity, build id), then calls `WriteLoadOrderAsync`, **[R2]** then collects `GetLaunchRequirements` and runs permitted post-deploy actions.
  * **[R2]** Manifest files Modular writes (`modlist.txt`, later `plugins.txt`/`modsettings.lsx`) have their pre-existing version backed up and restored on full purge.
* **[R2]** A generic proxy-DLL detector (`winmm`, `version`, `winhttp`, `dxgi`, `d3d11`, `dinput8`, `xinput1_3`, `dsound`) runs over deployed files next to a game executable. When the store is Steam on Linux, it adds a `WINEDLLOVERRIDES="<names>=n,b" %command%` hint to the report and to `modular deploy` output.
* **[R3] Changeset ownership:**
  * A staged install still creates a changeset, for history and telemetry, but marks it `managed_by = deployment` with its `game_key`. Its operations JSON records staged paths relative to the staging root, labelled as such, so they are never mistaken for game paths.
  * `UninstallAsync(changesetId)` branches on `managed_by`:
    * `deployment`: remove the `game_mod` row, redeploy the game, then delete the staging folder. The staging folder is deleted only after the redeploy succeeds, so a failed deploy leaves a recoverable state.
    * `legacy`: the existing path-deletion flow, which now also restores both backup suffixes.
  * `uninstall --game <g> --mod <id>` and `uninstall <changesetId>` therefore end up in the same code for managed mods.
* **[R3] Link safety (hardlink and symlink methods):** a deployed file shares its data with the staged copy, so any in-place write through the game path also modifies the staged file. For a symlink, even deleting the target through a tool that follows links would destroy the staged copy. No Modular code path may open a deployed path for writing or follow a link when deleting. The legacy uninstall and snapshot code must either delete the link itself or replace it by rename, never truncate or overwrite in place. Enforce this with tests that run a legacy uninstall and a snapshot restore over hardlinked and symlinked deployments and assert that the staged file is unchanged.

### 5a. Deployment method: hardlink, symbolic link or copy **[R3]**

The deployer places files through an `IDeploymentStrategy` with three implementations. The user picks one; the planner, purge, `deployment.json` and `_overwrite/` logic are shared.

| | **Hardlink** (default) | **Symbolic link** | **Copy** |
|---|---|---|---|
| Disk use | None extra | None extra | Full second copy of every deployed file |
| Deploy speed | Fast | Fast | Slow for large mods (GB-scale pak/archive mods) |
| Staging and game dir on different drives | ✗ fails, falls back | ✓ works | ✓ works |
| Windows requirements | NTFS, same volume | Developer Mode or admin (`SeCreateSymbolicLinkPrivilege`) | None |
| Linux / Proton | Same filesystem | Works; Wine follows host symlinks | Works everywhere |
| Visible as "modded" in a file manager | No | Yes (link arrow, `ls -l` shows target) | No |
| In-place edit through the game path | Changes the staged copy too | Changes the staged copy too | Staged copy untouched |
| Atomic save (temp + rename) by an editor/tool | Link silently replaced by a regular file | Link replaced by a regular file | File replaced (same as any edit) |
| Game/tool that rejects or re-resolves links | Not affected (indistinguishable from a normal file) | **Can break**: some games, anti-cheat and launchers canonicalize paths or refuse reparse points | Not affected |
| Steam "Verify integrity" on an overridden vanilla file | Replaces our link with vanilla; staged copy safe | May write *through* the link, overwriting the staged file if Steam opens in place; treat as a known risk | Replaces our file with vanilla |
| Staging folder deleted or moved | Game keeps working (data still referenced) | **Dangling links**: game sees missing files | Game keeps working |

**Implementation per method:**
* **Hardlink:** `link()` via P/Invoke on Unix and `CreateHardLinkW` on Windows (.NET 8 has no managed API). Identity recorded as device + inode + size + mtime. Drift means the inode changed (atomic save) or size/mtime changed (in-place edit).
* **Symbolic link:** the managed `File.CreateSymbolicLink` (available since .NET 6). On Windows, the Developer Mode check runs first so the failure message is actionable. Links use **absolute** targets, because relative targets break when roots live in different trees (the Proton `documents` root vs. the game dir). Identity recorded as "is a symlink" + target path, checked with `lstat` / `FileSystemInfo.LinkTarget` without following the link. Drift means the path is no longer a link or points elsewhere. Purge deletes the link itself, never the target.
* **Copy:** a plain file copy that preserves mtime. Identity recorded as size + mtime + content hash (xxHash64, computed while copying). Drift means the hash differs. Redeploy is **incremental**: a file whose target already matches the recorded hash and whose staged source is unchanged is left in place, so reordering a large mod list only rewrites files whose winner changed.

**Choosing and falling back:**
* Configuration, in order of precedence: `deploy --method hardlink|symlink|copy` for one run, then a per-game setting (`modular order` stores it alongside `game_mod` in a new `game_deploy_settings` row), then `deployment.method` in `config.json`, then the default `hardlink`.
* The fallback is configurable per method: `deployment.fallback = copy | fail` (default `copy`). Hardlink falls back when the error is cross-device (`EXDEV` / `ERROR_NOT_SAME_DEVICE`) or permission denied. Symlink falls back when the error is missing privilege (`ERROR_PRIVILEGE_NOT_HELD`) or the filesystem doesn't support symlinks (FAT/exFAT SD cards, some network shares). Copy has no fallback. Each fallback is recorded per file in `deployment.json` (`"method": "copy", "requested": "hardlink"`) and summarised in the deploy report, so mixed deployments are visible and purge uses the right identity check for each file.
* **Pre-flight probe:** before a deploy, the deployer tries the chosen method once per resolved root with a temp file and cleans up. When it fails, the report names the root (for example "`documents` is on a different drive; those files will be copied") before any game file is touched.
* **Changing method** for a game forces a full purge, then a fresh deploy, because identity records aren't comparable across methods.
* **[R3] Module constraints:** an optional `IGameModule.GetDeployConstraints()` hook (default: none) lets a module force `copy` for path globs where links are known to cause trouble, or mark `symlink` as unsupported for the whole game. No built-in module sets any constraint this pass: none has a verified link problem. The hook exists so a future anti-cheat-protected or path-canonicalizing game can opt out without core changes. Constraints override the user's choice file by file and are listed in the report.

**Recommendation to document for users:**
* **Hardlink:** the best default when staging and the game share a drive.
* **Symlink:** when they can't share a drive and disk space matters. Needs Developer Mode on Windows.
* **Copy:** for maximum compatibility, for games or tools that misbehave with links, or when the staging folder may be moved or deleted.

### 6. Service, CLI, and GUI wiring

* `ModInstallationService.InstallAsync` runs: resolve game → module → select installer → stage → **validate** → register `game_mod` → deploy (unless `DryRun` or `--no-deploy`).
* **[R3]** The step 5 pre-install conflict check is removed for staged installs. Overlaps between mods come from the `DeploymentPlanner`. For collisions with files Modular doesn't manage, the planner compares each target against `deployment.json`: an existing file that Modular did not deploy is reported as "will replace an unmanaged file (backed up)", not as a mod conflict. Legacy direct installs keep the old check.
* **[R3] Snapshots:**
  * Snapshots capture the `game_mod` state for the game: mod id, changeset id, order index, enabled, and a hash of the staging folder.
  * Restore rewrites `game_mod` to that state in one transaction. Mods whose staging folder is missing are re-staged from their archive (`--no-deploy`), and mods not in the snapshot are removed. Then exactly **one** deploy runs.
  * Legacy changesets in the snapshot keep the current changeset-diff restore.
  * The auto-snapshot moves from after install to **after a successful deploy**, so `install --no-deploy` never takes one.
* CLI:
  * `install` gains `--no-deploy`.
  * New `deploy --game <g> [--profile <json>] [--method hardlink|symlink|copy]`, `order list|set --game <g>`, `modules list`.
  * **[R3]** `config set deployment.method <m>` / `deployment.fallback <copy|fail>`, and `deploy --game <g> --method <m> --save` to persist a per-game method. `modular detect` prints which methods the pre-flight probe says will work for each resolved root.
  * `uninstall` gains `--game <g> --mod <id>`.
  * **[R2]** `deploy` and `install` print a "Launch requirements" panel: launch args, `WINEDLLOVERRIDES`, and pending tool actions such as REDmod deploy.
  * **[R2]** `order list` shows the effective engine order next to Modular's order (for example the reversed `modlist.txt` for Cyberpunk), so the translation is visible.
* GUI: `InstallViewModel` passes the selected game's AppID as `GameId`. **[R2]** Launch-requirement warnings are surfaced through the existing install status/log area. No other UI changes this pass.

## Validation

* New xunit tests:
  * Registry resolution (slug / AppID / **[R2]** GameBanana id / path).
  * **[R4]** Package-type routing:
    * A structured Cyberpunk zip (`Wrapper/archive/…`, `Wrapper/bin/…`, `Wrapper/r6/…`, `Wrapper/red4ext/…`) deploys with its tree unchanged apart from the stripped wrapper, and every route is labelled `Declared`.
    * A lone `.archive`, a lone `.reds`, a lone TweakXL `.yaml` and a bare CET folder with `init.lua` route to their fallback folders, labelled `Heuristic`.
    * A REDmod folder with `info.json` routes to `mods/<mod>/`, labelled `Metadata`.
    * A Rebirth-shaped directory does **not** match `FF7RemakeGameModule`.
    * A `PinnedLast` pak keeps its name and sorts after every numbered pak.
    * A test module using `GetPackageRoutes()` with `FlatFiles` rejects an invented `<modname>/` level.
    * `$LOCALAPPDATA` resolves inside the Proton prefix on Linux and to `%LOCALAPPDATA%` on Windows.
  * Resolver with a fake Steam root (appmanifest with `buildid` + compatdata + walk-up from `bin/x64` and `End/Binaries/Win64`).
  * **[R2]** HZD Remastered-shaped directory does *not* match the HZD module.
  * Staging redirection for a Cyberpunk-shaped zip and an UnrealPak zip.
  * Planner last-writer-wins and conflict reporting.
  * Deployer: same inode for hardlinks, copy fallback via a fake linker, purge restores originals, redeploy after reorder flips the winning file.
  * **[R3]** Deployment methods (the purge/redeploy/reorder tests run once per method as an xunit theory):
    * **Symlink:** the link points at the absolute staged path. Purge deletes the link and leaves the staged file. A link replaced by a regular file (simulated atomic save) goes to `_overwrite/`. Symlink tests skip on Windows runners without the privilege.
    * **Copy:** an in-place edit is detected by hash and moved to `_overwrite/`. An incremental redeploy after a reorder rewrites only the files whose winner changed (asserted via a counting fake file system).
    * **Fallback:** a fake strategy throwing a cross-device error falls back to copy under `fallback = copy`, and aborts before touching the game dir under `fail`. `deployment.json` records per-file `method` / `requested`.
    * **Pre-flight:** a failing probe for one root is reported before any file is placed.
    * **Method change:** switching hardlink → copy triggers a full purge and redeploy, and no hardlinks remain (link count 1).
    * **Module constraints:** a test-only module forcing `copy` for `*.dll` gets copies for DLLs and links for everything else.
* **[R2]** Module-level tests:
  * Cyberpunk: `modlist.txt` is written in reverse Modular order and includes unmanaged archives. A nested `archive/pc/mod/sub/x.archive` is flagged. REDmod launch requirements include `-modded` and ordered `-mod=` args.
  * FF7R: `Config/Engine.ini` lands in the Proton `documents` root. Paks become `000_a_P.pak`, `001_b_P.pak`, and a reorder swaps the prefixes. `.utoc`/`.ucas` siblings are renamed with their pak. A non-`_P` pak gets a warning.
  * HZD: the prefix mapping is off by default. The Prefetch conflict produces a warning.
  * Deployer: an atomically replaced file (inode changed) ends up in `_overwrite/` and is not deleted. Generated paths survive purge. A `buildid` change skips the stale-backup restore.
  * The proxy-DLL detector emits `winmm,version=n,b` for a RED4ext-shaped deployment when the store is Steam+Linux.
* **[R3]** Regression tests for the four flaws:
  * **Uninstall:** a staged install followed by `uninstall <changesetId>` removes the deployed link from the game folder, restores the vanilla file, and only then deletes staging. A simulated deploy failure leaves staging intact.
  * **Legacy changesets:** a legacy changeset (v4 row) still uninstalls through the old path and restores both `.backup` and `.modular.bak`.
  * **Snapshot restore:** restoring a 3-mod snapshot over a different 2-mod state calls the deployer exactly once (counted via a fake), and the final files match the snapshot order. An install with `--no-deploy` takes no auto-snapshot.
  * **Conflict check:** redeploying an unchanged set reports zero conflicts. A pre-existing unmanaged file is reported as "replaces unmanaged file" and is backed up.
  * **Installer backups:** every built-in installer succeeds against an empty staging target and writes no backup files.
  * **Link safety:** a legacy uninstall or snapshot restore over a hardlinked *or symlinked* deployment leaves the staged bytes unchanged.
  * **Paths:** an FF7R pak-only zip and an UnrealPak zip produce `game/End/Content/Paks/~mods/…` normalised paths before `MapDeployPath` runs.
* Fix up the existing `InstallerGameScopingTests` as needed.
* `make build` (warnings are errors) and `make test`.
* CLI smoke test against a temp fake game dir:
  * Install two overlapping archives, run `order set`, `deploy` and `uninstall`, and check file contents and link counts at each step.
  * **[R2]** Check the `modlist.txt` contents for a Cyberpunk-shaped fake dir.

## Delivery phases **[R3]**

Each phase builds and passes `make test` on its own and can merge independently.

1. **Game resolution and modules.**
   * SDK contracts, `GameModuleRegistry`, built-in modules (detection plus installers only; hooks stay default no-ops).
   * `GameInstallationResolver` with its DB upserts.
   * Passing the game id into `SelectInstallerAsync`.
   * This alone fixes the missing-gameId scoping bug and makes auto-snapshots fire.
2. **Staging.**
   * `StagingManager` sessions, path normalisation, the FOMOD fix, and the `managed_by` changeset split with the v5 migration.
   * The step 5 change, the installer-backup test, and the link-safety rule.
   * Installs are staged *and* copied straight into the game folder (a temporary single-mod deploy), so behaviour is unchanged for users until phase 3.
3. **Ordered deployment.**
   * `game_mod`, `DeploymentPlanner`, `Deployer`, `deployment.json` and `_overwrite/`.
   * `IDeploymentStrategy` with hardlink, symlink and copy, plus the fallback, pre-flight probe, per-game method setting and `--method`. The constraints hook is added as a no-op.
   * Build-id-aware backups, and the new uninstall branch.
   * Snapshot capture/restore of `game_mod` with a single deploy.
   * CLI commands `deploy`, `order`, `modules` and `uninstall --game/--mod`, plus the GUI `GameId` wiring.
4. **Per-game hooks.**
   * Cyberpunk `modlist.txt`, REDmod launch requirements and validation.
   * FF7R `userconfig` root and pak prefixing, **[R4]** with the `PinnedLast` exemption and the Rebirth-rejecting anchor.
   * **[R4]** `PackageType` / `RouteSource` labels on the three existing analyzers' routes, root tokens, the `GetPackageRoutes()` hook (empty for built-ins), and the `FlatFiles` extra-folder check.
   * HZD Remastered exclusion and the Prefetch warning.
   * Generated paths and the proxy-DLL / `WINEDLLOVERRIDES` detector.

## Verified routes for future modules **[R4]**

Recorded here so the SDK shape (§2a) is known to cover them. Both modules remain **out of scope** for this pass.

**Baldur's Gate 3** (AppID 1086940). Load order lives in `$LOCALAPPDATA/Larian Studios/Baldur's Gate 3/PlayerProfiles/Public/modsettings.lsx`, written by `WriteLoadOrderAsync`.

| Package type | Destination | Layout |
|---|---|---|
| `bg3.pak` (ordinary mod) | `$LOCALAPPDATA/Larian Studios/Baldur's Gate 3/Mods/` | `FlatFiles` (the `.pak` itself, no subfolder) |
| `bg3.script_extender` (`DWrite.dll`) | `$GAME/bin/` | `Preserve` |
| `bg3.native_loader` (Native Mod Loader) | `$GAME/bin/` | `Preserve` |
| `bg3.native_mod` | `$GAME/bin/NativeMods/` **or** `$LOCALAPPDATA/Larian Studios/Baldur's Gate 3/Plugins/`, depending on the installed loader | Metadata-driven only, never inferred from `*.dll` |

**FF7 Rebirth** (AppID 2909400):

| Package type | Destination | Layout |
|---|---|---|
| `ue.pak_bundle` (`.pak` + `.ucas` + `.utoc`) | `$GAME/End/Content/Paks/~mods/` | `FlatFiles`; the trio is renamed together if order-prefixed |
| `ff7rb.reunion_plugin` (Reunion / Dresscode and dependents) | `$GAME/End/Mods/<mod>/` | `FolderPerMod` |
| `ff7rb.gameinstance_loader` (`ZGameInstanceLoader.pak`) | `$GAME/End/Content/Paks/~mods/` | `PinnedLast` |

## Out of scope

* New game modules: UE generic, Bethesda (`plugins.txt`), BG3 (`modsettings.lsx`; routes above), **[R4]** FF7 Rebirth (routes above), HZD Remastered, BepInEx/MelonLoader as modules, Reloaded-II/GameBanana loader-managed games. **[R2]** The SDK roots (`localappdata`, `documents`), `TargetRoot` and `WriteLoadOrderAsync` are shaped to support these without contract changes.
* GUI load-order editor.
* **[R2]** Running `redMod.exe` under Proton, and editing Steam launch options automatically. Both are reported, not automated.
* **[R2]** GameBanana 1-Click (`/mmdl/{fileId}`) protocol handling (only the file id is stored now).
* Switch project integration.

## Sources

* Cyberpunk 2077: [Using Mods](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077), [Archive files Load Order](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077/load-order), [REDmod Usage](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077/redmod/usage), [REDmod deploy](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077/redmod/commands/deploy), [CDPR REDmod docs (PDF)](https://cdn-l-cyberpunk.cdprojektred.com/REDmod-docs.pdf), [Modding on Linux](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077/modding-on-linux), [CET on Proton](https://wiki.redmodding.org/cyber-engine-tweaks/getting-started/installing/linux-proton), [redscript (Nexus)](https://www.nexusmods.com/cyberpunk2077/mods/1511)
* Unreal / FF7R: [Pak patching (UE modding guide)](https://buckminsterfullerene02.github.io/dev-guide/Basis/PakPatching.html), [How to install mods, FF7R Nexus](https://www.nexusmods.com/finalfantasy7remake/videos/299), [Aggressive Companions (Nexus, `_P` priority notes)](https://www.nexusmods.com/finalfantasy7remake/mods/418?tab=posts), [Item Level Gaming FF7R guide](https://itemlevel.net/how-to-install-mods-in-final-fantasy-7-remake-intergrade/)
* Horizon Zero Dawn: [Better Quality Drops (Nexus, load order notes)](https://www.nexusmods.com/horizonzerodawn/mods/115?tab=posts), [Enhanced Focus (Nexus)](https://www.nexusmods.com/horizonzerodawn/mods/192?tab=posts), [Steam discussion on Packed_DX12](https://steamcommunity.com/app/1151640/discussions/0/3416556480585850033), [HZD Remastered Gameplay Tweaks (Nexus)](https://www.nexusmods.com/horizonzerodawnremastered/mods/19), [HZD Remastered Vortex extension](https://www.nexusmods.com/site/mods/1077)
* Proton loaders: [BepInEx under Proton/Wine](https://docs.bepinex.dev/articles/advanced/proton_wine.html)
* **[R4]** FF7 Rebirth: [Reunion Mod Loader (Nexus)](https://www.nexusmods.com/finalfantasy7rebirth/mods/1061?tab=posts), [Dresscode (Nexus)](https://www.nexusmods.com/finalfantasy7rebirth/mods/1062)
* **[R4]** BG3 loaders: [BG3 Script Extender (Nexus)](https://www.nexusmods.com/baldursgate3/mods/2172), [Installing Script Extender (BG3 community wiki)](https://wiki.bg3.community/en/Tutorials/Mod-Use/How-to-install-Script-Extender), [Native Mod Loader (Nexus)](https://www.nexusmods.com/baldursgate3/mods/944)
* Future modules: [BG3 mod types (community wiki)](https://wiki.bg3.community/en/Tutorials/Mod-Use/BG3-Mod-Types-and-how-to-install-them), [bg3.wiki Installing mods](https://bg3.wiki/wiki/Modding:Installing_mods), [Skyrim SE plugins.txt under Proton (Vortex on Linux gist)](https://gist.github.com/jameshibbard/62f039b6c8e9db4c9ef8e915a1b12f28)
* GameBanana: [Reloaded-II FAQ](https://reloaded-project.github.io/Reloaded-II/FAQ/), [1-Click Mod Installers wiki](https://gamebanana.com/wikis/1999), [Granblue Relink installing mods](https://nenkai.github.io/relink-modding/modding/installing_mods/)
