# Modular: Game Modules + ordered staging/deployment pipeline (revision 4.2)
Source document: `~/Downloads/GAME_MODULES_DEPLOYMENT_PLAN(2).md` (revision 4). Markers:
* **[R2]**: community install documentation for each supported game type.
* **[R3]**: revision 3/3.1. Fixes four flaws found by checking revision 2 against the code (changeset paths, snapshot restore, the pre-install conflict check, installer backups), adds a choice of deployment method (§5a) and a phased delivery order.
* **[R3.2]**: an FF7R pak-routing bug verified in the code that revision 4 does not cover. Retained.
* **[R4]**: revision 4. An external cross-check of install targets against Nexus Mods pages. Destination becomes a property of the detected **package type**, not just the game (§2a); FF7 Remake gains anchors so it never matches FF7 Rebirth; "load-last" framework paks are protected from order-prefixing; verified route tables are recorded for the still-out-of-scope BG3 and FF7 Rebirth modules.
* **[R4.2]**: code checks of the revision 4 claims. Confirms the Rebirth collision reaches deeper than detection, and pins down what the `PackageType` / `RouteSource` labelling actually costs.
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
* **[R2]** `FF7RArchiveAnalyzer` routes `Engine.ini`/`Input.ini` to `<game>/Config/` (verified at `FF7RArchiveAnalyzer.cs:256-257`). UE4 does not read user config from there. The real location is `Documents/My Games/FINAL FANTASY VII REMAKE/Saved/Config/WindowsNoEditor/`, so these mods currently install but have no effect. The `TargetRoot` design below fixes this.
* **[R2]** `HZDModInstaller` scopes to AppID 1151640 (Complete Edition), which is correct. Nothing yet stops a *path*-detected Horizon Zero Dawn **Remastered** install (AppID 2561580) from being treated as HZD, and Remastered has a completely different mod layout.
* **[R3]** `ModInstallationService.cs:180-186` stores `installResult.InstalledFiles` in the changeset, and `UninstallAsync` deletes exactly those paths. Once installs are redirected into staging, these paths point into `~/.config/Modular/staging/…`. A changeset-based uninstall would delete the staged copies and leave the deployed links in the game folder.
* **[R3]** `SnapshotManager.RestoreSnapshotAsync` (lines 318-428) restores by diffing committed changesets, then calling `UninstallAsync` / `InstallAsync` once per mod. Under the new flow every install deploys, so a restore of N mods would purge and redeploy N times, and each intermediate deployment would be visible on disk.
* **[R3]** Step 5 of `InstallAsync` (`ModInstallationService.cs:138-147`) registers a "conflict" whenever a destination already exists in the game folder. After deployment, every file Modular deployed exists there, so each reinstall or redeploy would report conflicts with itself.
* **[R3]** The Cyberpunk, FF7R and HZD installers write `.modular.bak` backups, and LooseFile, BepInEx, UnrealPak and Steam write `.backup`, both next to their destination. With a staging target, the destination directory is empty, so this code never backs anything up. It must also not throw there.
* **[R3.2]** FF7R pak routing depends on archive shape.
    * `FF7RArchiveAnalyzer` routes `~mods` paks (line 140) and bare paks (line 224) to a path relative to `~mods`.
    * `FF7RModInstaller.AnalyzeAsync:115-117` only targets `End/Content/Paks/~mods` when the archive is a *single-type* PakMod.
    * Multi-type archives (for example a pak plus an INI or a hook DLL) target the game root, so their paks land in `<game>/<name>_P.pak` and never load.
    * The §4 path normalisation cannot fix this: `relative(gameDir, plan.TargetDirectory)` is empty for multi-type plans, so the information is already lost.
* **[R4]** `FF7RemakeGameModule` anchoring on `End/Content/Paks` alone is not enough. FF7 Rebirth (AppID 2909400) ships the same `End/` layout but a different ecosystem (IoStore bundles, Reunion's `End/Mods`), so a path-detected Rebirth install would be treated as Remake.
* **[R4.2]** The collision is not only in game detection: `FF7RArchiveAnalyzer` claims Rebirth archives too. Its structural anchors are `end/content/paks/` and `end/binaries/win64/` (`FF7RArchiveAnalyzer.cs:40-46`), which Rebirth shares, and its filename hints include the bare fragment `"ff7"` (line 54), which matches `ff7rebirth_`. Only the executable hint `ff7remake_.exe` actually discriminates. Module-scoped installer candidates (§2) are therefore what keeps the two apart, which is why the Rebirth-rejecting anchor is load-bearing rather than cosmetic.
* **[R4.2]** All three analyzers expose routes as `Dictionary<string, string>` (entry → relative destination): `CyberpunkInstallLayout.cs:32`, `FF7RInstallLayout.cs:31`, and the HZD equivalent. Labelling routes with `PackageType` / `RouteSource` (§2a) means widening that dictionary's value to a small route record, which touches every analyzer and its tests. `FileOperation` (`IModInstaller.cs:164-200`) is a plain mutable class, so adding `TargetRoot`, `PackageType` and `RouteSource` there is genuinely additive.
* **[R4.2]** `CyberpunkArchiveAnalyzer` already implements the §2a precedence. Declared paths win (`Red4ExtPluginPrefixes`, `CetModPrefixes`, `RedscriptPrefixes`, `LegacyArchivePrefixes`, `TweakPrefixes`, `InputMappingPrefixes`, `IniConfigPrefixes` at lines 164-276), REDmod `info.json` structure is checked before the `mods/` fallback (line 231), and extension-only rules are gated behind `hasCyberpunkAnchor`. The R4 work on this analyzer is labelling, not restructuring.
## Research findings
### Two different notions of "order"
The biggest correction to revision 1 is that there are two separate orderings, and the games disagree on the second one:
* **File priority.** When two staged mods ship the *same relative path*, this decides which physical file is deployed. Modular's `DeploymentPlanner` decides: last in the list wins.
* **Engine load order.** When two *different* files override the same game asset, this decides which one the engine applies. The game decides, through filename sort order or a manifest file.
Engine load-order rules per game:
* **Cyberpunk 2077, legacy `.archive`** (`archive/pc/mod`)
    * Rule: ASCII/binary-alphabetical (`!`,`#` < `A`–`Z` < `_` < `a`–`z`).
    * Conflict: **first loaded wins**.
    * Mechanism: `archive/pc/mod/modlist.txt`, one archive filename per line, overrides alphabetical order.
* **Cyberpunk 2077, REDmod** (`mods/<name>/`)
    * Rule: loads after all legacy archives; order is set at deploy time.
    * Conflict: per file, by REDmod order.
    * Mechanism: `redMod.exe deploy -root=… -mod=A -mod=B …` or `-modlist=<file>`; the game is launched with `-modded`.
* **UE4/UE5 paks** (FF7R `End/Content/Paks/~mods`)
    * Rule: mount order is alphabetical; `~mods` sorts last.
    * Conflict: **last mounted wins**.
    * Mechanism: filename prefixes. Paks must end `_P.pak`, which gives patch-priority mounting.
* **UE IoStore** (`.pak` + `.utoc` + `.ucas`)
    * Same rule and conflict behaviour as UE paks, but the three files must share one stem, so all three are renamed together.
    * Not used by FF7R (UE4 pak-only), but relevant to `UnrealPakInstaller`.
* **HZD Complete Edition** (Decima, `Packed_DX12/Patch*.bin`)
    * Rule: alphabetical.
    * Conflict: the later patch wins; the community renames files to `x_Patch_…` to raise priority.
    * Mechanism: filename prefixes.
* **HZD Remastered**: there is no `Packed_DX12` workflow. Mods are root-level DLL loaders (`winhttp.dll` + `mod_config.ini`).
* **Bethesda (future)**: `plugins.txt` in `%LOCALAPPDATA%/<Game>/`, where a `*` prefix means enabled. Last wins. It is a manifest file outside the game dir.
* **BG3 (future)**: paks live in `%LOCALAPPDATA%/Larian Studios/Baldur's Gate 3/Mods`, and order is set in `PlayerProfiles/Public/modsettings.lsx`. It is a manifest file outside the game dir.
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
* Many GameBanana ecosystems deploy into a **loader-managed folder**, not the game directory. For example, Reloaded-II keeps one folder per mod containing `ModConfig.json` under `<Reloaded>/Mods`, and a loader config enables it. This validates `TargetRoot` and the folder-per-mod mapping below for future modules.
* 1-Click installs use `https://gamebanana.com/mmdl/{fileId}` behind a manager-specific URL scheme. This is out of scope here, but the `game_mod` table stores the GameBanana file id so it can be supported later.
## Proposed design
### 1. SDK contract (`src/Modular.Sdk/Modules/`)
* `IGameModule` members:
    * `ModuleId`, `DisplayName`
    * `GameIds` (slugs + Steam AppIDs; **[R2]** plus GameBanana game ids), `SteamAppIds`
    * `Detect(string installPath) → GameModuleMatch?` (confidence + evidence, anchor-file based)
    * `ResolveModPaths(GameInstallation) → GameModPaths`
    * `GetInstallers() → IReadOnlyList<IModInstaller>`
* Optional hooks, each with a default no-op:
    * `MapDeployPath(DeployPathContext) → DeployTarget`. **[R2]** The context carries the root name, the relative path, the mod's order index and the enabled mod count. The result is `(RootName, RelativePath)`, so a module can both rename a file *and* route it to a non-game root.
    * `WriteLoadOrderAsync(GameInstallation, IReadOnlyList<DeployedMod>, CancellationToken)`
    * **[R2]** `ValidateStaged(GameInstallation, StagedMod) → IReadOnlyList<ModDiagnostic>`: layout checks run after staging (warnings, or errors that block deploy).
    * **[R2]** `GetGeneratedPaths() → IReadOnlyList<string>`: glob patterns (relative to a root) owned by the game or tools. They are excluded from vanilla backup/restore and conflict reports and never purged (for example `r6/cache/**`, `bin/x64/plugins/cyber_engine_tweaks/mods/*/db.sqlite3`, `*.log`).
    * **[R2]** `GetLaunchRequirements(GameInstallation, IReadOnlyList<DeployedMod>) → LaunchRequirements`: required launch arguments (such as `-modded`), `WINEDLLOVERRIDES` entries, and post-deploy tool actions. These are reported only; Modular does not edit Steam's `localconfig.vdf`.
* `GameInstallation`: game id, AppID, install root, store, Proton prefix path, engine family, module id, **[R2]** build id (from `appmanifest_<id>.acf` `buildid`).
* `GameModPaths`: named roots plus `DefaultRoot`. **[R2]** Standard root names are defined in the SDK:
    * `game`: the install dir, on both Windows and Proton.
    * `localappdata`: `%LOCALAPPDATA%` on Windows; `<pfx>/drive_c/users/steamuser/AppData/Local` under Proton.
    * `documents`: `SpecialFolder.MyDocuments` on Windows; `<pfx>/drive_c/users/steamuser/Documents` under Proton.
    * Modules add their own named roots on top, for example FF7R's `userconfig`.
* Additive, non-breaking extensions: `InstallContext.Game` / `InstallContext.ModPaths`, and `FileOperation.TargetRoot` (null = `game`). **[R4]** `FileOperation` also gains `PackageType` and `RouteSource` (§2a).
* **[R4]** `GetPackageRoutes() → IReadOnlyList<PackageRoute>` (default: empty), the declarative routing table described in §2a.
### 2. Core module system (`src/Modular.Core/Modules/`)
* `GameModuleRegistry`:
    * Registers built-in modules and modules discovered from plugins (`PluginLoader` gains `IGameModule` discovery, exposed as `LoadedPlugin.Modules`).
    * Resolves by game id/AppID or by path detection.
* Built-in modules wrap the existing installers with minimal logic changes. Their anchors are refined as follows **[R2]**:
    * **`CyberpunkGameModule`**
        * Anchors: `bin/x64/Cyberpunk2077.exe`, `archive/pc/content`.
        * `WriteLoadOrderAsync` writes `archive/pc/mod/modlist.txt` listing every deployed `.archive` from **highest to lowest priority** (the reverse of Modular's order, because the first archive loaded wins). Archives Modular does not manage are appended after the managed ones in ASCII order, so the list is complete. A partial `modlist.txt` has ambiguous semantics.
        * REDmods: `GetLaunchRequirements` emits `-modded` plus a `redMod.exe deploy -root=<game> -mod=<a> -mod=<b>…` action in Modular order. On Windows the deployer runs it when `tools/redmod/bin/redMod.exe` exists. On Linux the command is printed, and running it through Proton is left for later.
        * `ValidateStaged` flags `.archive` files nested below `archive/pc/mod/<sub>/`, a REDmod without `info.json`, and a CET mod folder without `init.lua`.
        * Generated paths: `r6/cache/**`, CET `mods/*/db.sqlite3`, `*.log`.
        * On Proton, RED4ext/CET loaders produce the override `winmm,version=n,b`.
    * **`FF7RemakeGameModule`**
        * Anchors: `End/Binaries/Win64/ff7remake_.exe` and `End/Content/Paks`. **[R4]** `End/Content/Paks` alone is **not** sufficient, because FF7 Rebirth shares it. Detection requires the Remake executable and explicitly rejects a directory containing `ff7rebirth_.exe`, so Rebirth falls through to `GenericGameModule` until it gets its own module.
        * **[R4.2]** The same discrimination is added to `FF7RArchiveAnalyzer`: the `"ff7"` filename hint is narrowed so it does not fire on `ff7rebirth`, and a Rebirth executable reference in the archive suppresses the FF7R anchor. This is defence in depth for the case where the analyzer is reached outside module scoping.
        * Adds a `userconfig` root at `documents/My Games/FINAL FANTASY VII REMAKE/Saved/Config/WindowsNoEditor`. `MapDeployPath` routes the analyzer's `Config/*.ini` routes there, which fixes the dead `Config/` bug without touching the analyzer.
        * **[R3.2]** Fix pak routing so every pak reaches `~mods`, whatever the archive's shape. `FF7RArchiveAnalyzer` pak routes (lines 140 and 224) become game-root-relative (`End/Content/Paks/~mods/<name>`), and `FF7RModInstaller.AnalyzeAsync` always targets the game root, removing the single-type special case at lines 115-117. This is the only logic change inside an existing installer. It is needed because neither the module mapping nor path normalisation can recover the intent afterwards. Existing FF7R analyzer expectations in tests are updated to match.
        * **Pak order-prefixing moves in scope.** The research confirms that UE mounts alphabetically and the last mount wins, which matches Modular's rule. For paks under `~mods/`, `MapDeployPath` renames `<name>.pak` to `<NNN>_<name>.pak`, where NNN is the zero-padded order index. The `_P` suffix is kept, and `.utoc`/`.ucas`/`.sig` siblings sharing the stem are renamed together.
        * **[R4] Load-last framework paks are exempt.** Loader paks rely on their own names to sort last (Rebirth's `ZGameInstanceLoader.pak` is the reference case). Prefixing would drag them into the ordered band and could break the loader. Modules declare these through a `PinnedLast` glob list: pinned paks keep their names and deploy after every numbered pak. The FF7 Remake list starts empty, the mechanism is generic, and any unprefixed `Z*` / `zz*` pak raises a warning so the user can pin it.
        * `ValidateStaged` warns about a pak without the `_P` suffix and a pak outside `~mods`, and notes that 3DMigoto and DXVK require DX11.
        * On Proton, hooks produce overrides (`dxgi`, `xinput1_3`, `dinput8`, `d3d11` as present, `=n,b`).
    * **`HorizonZeroDawnGameModule`** (Complete Edition only, AppID 1151640)
        * Anchors: `HorizonZeroDawn.exe` + `Packed_DX12/`. The Remastered exe/AppID 2561580 is explicitly **not** matched, so it falls through to `GenericGameModule`, whose root-level DLL mods fit the loose-file installer.
        * `MapDeployPath` renames `Packed_DX12/Patch_<x>.bin` to `Packed_DX12/Patch_<NNN>_<x>.bin`. It keeps the `Patch_` prefix, because it is unverified whether the engine requires it. **This mapping is disabled by default** behind a module option until it is verified in-game.
        * `Patch_zzzzPrefetch.bin` is special-cased: the last writer still wins, but the conflict is reported as a warning ("regenerate prefetch").
    * **`GenericGameModule`**: fallback. The game root is the install dir, universal installers only, identity mapping. On Proton, proxy DLLs deployed next to any `.exe` produce override hints (see §5).
* `InstallerManager.SelectInstallerAsync` builds its candidates from the resolved module's installers, the universal installers and the plugin installers. `ModInstallationService` passes the resolved game through, which fixes the missing-gameId bug.
### 2a. Destination = game × package type **[R4]**
A game module selects the *detector set*. The detected **package type** selects the destination. One game routinely needs several destinations: FF7 Rebirth sends ordinary paks to `End/Content/Paks/~mods` but Reunion/Dresscode plugins to `End/Mods/<mod>`, and BG3 sends ordinary paks to AppData `Mods` but Script Extender to `<game>/bin`.
Routing is resolved per file, in this order of precedence:
1. **Declared structure.** When archive paths already start at a recognised game-relative root, the tree deploys as-is from that root and is never rearranged. For Cyberpunk those roots are `archive/`, `bin/`, `r6/`, `red4ext/`, `engine/` and `mods/`; Nexus authors for Cyberpunk, CET and RED4ext all say "extract into the game root". Any wrapper folder is stripped, which the analyzers' existing `StrippedPrefix` already does.
2. **Package metadata.** Author- or loader-provided manifests outrank heuristics: REDmod `info.json`, FOMOD `ModuleConfig.xml`, and loader manifests such as Reunion plugin descriptors or BG3 native-loader configs. A bare `*.dll` never implies a destination, because in BG3 the installed loader decides between `bin`, `bin/NativeMods` and AppData `Plugins`.
3. **Loose-file fallback.** Extension rules apply only to files not placed by rule 1 or 2. For Cyberpunk: `*.archive` / `*.xl` to `archive/pc/mod/`, `*.reds` to `r6/scripts/<mod>/`, TweakXL `*.yaml` to `r6/tweaks/<mod>/`, a folder with `init.lua` to `bin/x64/plugins/cyber_engine_tweaks/mods/<mod>/`, a RED4ext plugin DLL folder to `red4ext/plugins/<plugin>/`, and a folder with `info.json` to `mods/<mod>/`.
Contract changes:
* `FileOperation` gains `PackageType` (a string id such as `cp77.legacy_archive`, `ue.pak_bundle`, `bg3.pak`) and `RouteSource` (`Declared` | `Metadata` | `Heuristic`). With `TargetRoot` these are recorded in the staged manifest and shown by `install --dry-run`, so a mis-route traces back to the rule that caused it.
* **[R4.2]** Carrying the labels from analyzer to operation requires widening each layout's `FileRoutes` from `Dictionary<string, string>` to `Dictionary<string, FileRoute>`, where `FileRoute` is `(Destination, PackageType, RouteSource, Root)`. All three analyzers and their existing tests are updated in the same change. This is the one non-additive part of R4.
* `IGameModule.GetPackageRoutes()` (default: empty) returns a declarative table of `PackageRoute(PackageType, Root, SubPath, Layout)`, where `Layout` is `Preserve` | `FlatFiles` | `FolderPerMod`. Future modules then route by data through a generic table-driven installer, with no bespoke analyzer. The three existing modules keep their analyzers and return an empty table; they only label their routes.
* **Root tokens:** routes use `$GAME`, `$LOCALAPPDATA`, `$DOCUMENTS` and module-defined roots such as `$USERCONFIG`. The resolver translates these, through the Proton prefix on Linux. A module never contains a Steam-library or prefix path.
* **No invented folders:** `FlatFiles` layouts (BG3 `Mods/*.pak`, Cyberpunk `archive/pc/mod/*.archive`) must not gain a `<modname>/` subfolder. `ValidateStaged` flags any extra folder level for these package types, which is the same check that already catches nested `archive/pc/mod/<sub>/`.
### 3. Game detection and target paths (`src/Modular.Core/GameDetection/GameInstallationResolver.cs`)
* `--game` accepts an AppID, NexusMods slug, **[R2]** GameBanana game id, display name or path.
* Lookup runs the Steam scan first (exact AppID, then module `GameIds` slug, then name match), then falls back to a manual path.
* For manual paths, the resolver walks up to 3 parents to find the directory a module's `Detect` anchors on. Pointing at `bin/x64` (Cyberpunk) or `End/Binaries/Win64` (FF7R) still resolves the real root.
* The resolver fills in the Proton prefix (`<library>/steamapps/compatdata/<appid>/pfx`), the engine (via `CompositeEngineDetector`) and **[R2]** `buildid`. It then upserts `detected_games` / `engine_detection`.
* `modular detect` and `detect-engine` also print the matched module, **[R2]** the resolved named roots, and whether a Proton prefix was found.
### 4. Per-mod staging
* `StagingManager` gets real sessions backed by persistent `~/.config/Modular/staging/<gameKey>/<modId>/` directories. **[R2]** Each staged mod is laid out as `<rootName>/<relative path>` (`game/…`, `userconfig/…`), so non-game roots survive restaging.
* Redirection is generic: the plan's `TargetDirectory` is rewritten to `stagingRoot/game/<relative(gameDir, plan.TargetDirectory)>`. That handles UnrealPak's absolute `~mods` target. Operations carrying a `TargetRoot` go to `stagingRoot/<TargetRoot>/…`.
* **[R3]** Paths are normalised in one step, before anything else looks at them.
    * Installers emit paths relative to `plan.TargetDirectory`. UnrealPak (and, before the R3.2 fix, FF7R pak-only plans) sets that to `…/Content/Paks/~mods`, so raw routes are relative to `~mods`, not the game root.
    * The stager prefixes `relative(gameDir, plan.TargetDirectory)` to produce `(root, pathRelativeToRoot)` pairs.
    * `MapDeployPath`, `ValidateStaged`, `GetGeneratedPaths` matching and the planner only ever see these normalised pairs.
* FOMOD's directory flattening is fixed. Staged files are recorded relative to their root.
* **[R3]** Installer-level backups (`.modular.bak` / `.backup`) become intentionally inert under staging, because the target folder is always empty.
    * Vanilla backups are owned solely by the `Deployer`.
    * The installer code is left untouched this pass. A follow-up deletes it once legacy direct installs are removed.
    * A test runs every built-in installer against an empty staging target and asserts that it succeeds and creates no backup files.
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
    * **Placing files.** Staged files are placed into each resolved root with the configured deployment method (§5a), which defaults to hardlink. **[R2]** Roots in the Proton prefix are usually on the same device as the library, so hardlinks work there. Documents folders on Windows can be OneDrive-redirected, and the fallback covers that case.
    * **Completion.** Writes `staging/<gameKey>/deployment.json` (root, rel path, mod, identity, method, build id), then calls `WriteLoadOrderAsync`, **[R2]** then collects `GetLaunchRequirements` and runs permitted post-deploy actions.
    * **[R2]** Manifest files Modular writes (`modlist.txt`, later `plugins.txt`/`modsettings.lsx`) have their pre-existing version backed up and restored on full purge.
* **[R2]** A generic proxy-DLL detector (`winmm`, `version`, `winhttp`, `dxgi`, `d3d11`, `dinput8`, `xinput1_3`, `dsound`) runs over deployed files next to a game executable. When the store is Steam on Linux, it adds a `WINEDLLOVERRIDES="<names>=n,b" %command%` hint to the report and to `modular deploy` output.
* **[R3] Changeset ownership:**
    * A staged install still creates a changeset, for history and telemetry, but marks it `managed_by = deployment` with its `game_key`. Its operations JSON records staged paths relative to the staging root, labelled as such, so they are never mistaken for game paths.
    * `UninstallAsync(changesetId)` branches on `managed_by`:
        * `deployment`: remove the `game_mod` row, redeploy the game, then delete the staging folder. The staging folder is deleted only after the redeploy succeeds, so a failed deploy leaves a recoverable state.
        * `legacy`: the existing path-deletion flow, which now also restores both backup suffixes.
    * `uninstall --game <g> --mod <id>` and `uninstall <changesetId>` therefore end up in the same code for managed mods.
* **[R3] Link safety (hardlink and symlink methods):**
    * A deployed file shares its data with the staged copy, so any in-place write through the game path also modifies the staged file. For a symlink, even a delete by a tool that follows links would destroy the staged copy.
    * No Modular code path may open a deployed path for writing or follow a link when deleting. The legacy uninstall and snapshot code must either delete the link itself or replace it by rename, never truncate or overwrite in place.
    * Tests enforce this: a legacy uninstall and a snapshot restore run over hardlinked and symlinked deployments, and the staged file must be unchanged.
### 5a. Deployment method: hardlink, symbolic link or copy **[R3]**
The deployer places files through an `IDeploymentStrategy` with three implementations. The user picks one; the planner, purge, `deployment.json` and `_overwrite/` logic are shared.
Trade-offs:
* **Hardlink** (default)
    * Disk and speed: no extra disk use; fast.
    * Requirements: staging and target on the same filesystem (NTFS, same volume on Windows). Fails across drives, then falls back.
    * Looks like a normal file. Games, anti-cheat and launchers can't tell it apart.
    * Edits: an in-place edit through the game path changes the staged copy too. An atomic save silently replaces the link with a regular file.
    * Steam "Verify integrity" replaces our link with vanilla; the staged copy is safe.
    * If the staging folder is deleted or moved, the game keeps working, because the data is still referenced.
* **Symbolic link**
    * Disk and speed: no extra disk use; fast.
    * Works across drives. Windows needs Developer Mode or admin (`SeCreateSymbolicLinkPrivilege`). On Linux/Proton, Wine follows host symlinks.
    * Visible as modded: a file manager shows a link arrow, and `ls -l` shows the target.
    * Edits: an in-place edit changes the staged copy too. An atomic save replaces the link with a regular file.
    * **Can break** games, anti-cheat or launchers that canonicalize paths or refuse reparse points.
    * Steam "Verify integrity" may write *through* the link and overwrite the staged file if Steam opens it in place. This is a known risk.
    * If the staging folder is deleted or moved, links dangle and the game sees missing files.
* **Copy**
    * Disk and speed: a full second copy of every deployed file; slow for GB-scale pak/archive mods.
    * Works everywhere, with no special requirements.
    * Looks like a normal file, so games and tools are not affected.
    * Edits leave the staged copy untouched. An atomic save replaces the file, like any edit.
    * Steam "Verify integrity" replaces our file with vanilla.
    * The game keeps working if the staging folder is deleted or moved.
Implementation per method:
* **Hardlink:** `link()` via P/Invoke on Unix and `CreateHardLinkW` on Windows (.NET 8 has no managed API). Identity is recorded as device + inode + size + mtime. Drift means the inode changed (atomic save) or size/mtime changed (in-place edit).
* **Symbolic link:**
    * Uses the managed `File.CreateSymbolicLink` (available since .NET 6). On Windows, the Developer Mode check runs first so the failure message is actionable.
    * Links use **absolute** targets, because relative targets break when roots live in different trees (the Proton `documents` root vs. the game dir).
    * Identity is recorded as "is a symlink" + target path, checked with `lstat` / `FileSystemInfo.LinkTarget` without following the link. Drift means the path is no longer a link or points elsewhere.
    * Purge deletes the link itself, never the target.
* **Copy:**
    * A plain file copy that preserves mtime. Identity is recorded as size + mtime + content hash (xxHash64, computed while copying). Drift means the hash differs.
    * Redeploy is **incremental**: a file whose target already matches the recorded hash and whose staged source is unchanged is left in place, so reordering a large mod list only rewrites files whose winner changed.
Choosing and falling back:
* Configuration, in order of precedence:
    1. `deploy --method hardlink|symlink|copy` for one run.
    2. A per-game setting, stored in a new `game_deploy_settings` row alongside `game_mod`.
    3. `deployment.method` in `config.json`.
    4. The default, `hardlink`.
* Fallback is configurable: `deployment.fallback = copy | fail` (default `copy`).
    * Hardlink falls back on cross-device (`EXDEV` / `ERROR_NOT_SAME_DEVICE`) or permission-denied errors.
    * Symlink falls back on missing privilege (`ERROR_PRIVILEGE_NOT_HELD`) or a filesystem without symlink support (FAT/exFAT SD cards, some network shares).
    * Copy has no fallback.
    * Each fallback is recorded per file in `deployment.json` (`"method": "copy", "requested": "hardlink"`) and summarised in the deploy report. Mixed deployments are therefore visible, and purge uses the right identity check for each file.
* **Pre-flight probe:** before a deploy, the deployer tries the chosen method once per resolved root with a temp file, then cleans up. When the probe fails, the report names the root (for example "`documents` is on a different drive; those files will be copied") before any game file is touched.
* **Changing method** for a game forces a full purge, then a fresh deploy, because identity records aren't comparable across methods.
* **Module constraints:** an optional `IGameModule.GetDeployConstraints()` hook (default: none) lets a module force `copy` for path globs where links are known to cause trouble, or mark `symlink` as unsupported for the whole game.
    * No built-in module sets a constraint this pass, because none has a verified link problem. The hook lets a future anti-cheat-protected or path-canonicalizing game opt out without core changes.
    * Constraints override the user's choice file by file and are listed in the report.
Recommendations to document for users:
* **Hardlink:** the best default when staging and the game share a drive.
* **Symlink:** when they can't share a drive and disk space matters. Needs Developer Mode on Windows.
* **Copy:** for maximum compatibility, for games or tools that misbehave with links, or when the staging folder may be moved or deleted.
### 6. Service, CLI, and GUI wiring
* `ModInstallationService.InstallAsync` runs: resolve game → module → select installer → stage → **validate** → register `game_mod` → deploy (unless `DryRun` or `--no-deploy`).
* **[R3]** The step 5 pre-install conflict check is removed for staged installs.
    * Overlaps between mods come from the `DeploymentPlanner`.
    * For collisions with files Modular doesn't manage, the planner compares each target against `deployment.json`. An existing file that Modular did not deploy is reported as "will replace an unmanaged file (backed up)", not as a mod conflict.
    * Legacy direct installs keep the old check.
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
        * A Rebirth-shaped directory does **not** match `FF7RemakeGameModule`, and **[R4.2]** a Rebirth-shaped *archive* does not raise the FF7R anchor in `FF7RArchiveAnalyzer`.
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
    * FF7R: `Config/Engine.ini` lands in the Proton `documents` root. Paks become `000_a_P.pak`, `001_b_P.pak`, and a reorder swaps the prefixes. `.utoc`/`.ucas` siblings are renamed with their pak. A non-`_P` pak gets a warning. **[R3.2]** A multi-type archive (pak + `Engine.ini` + `dxgi.dll`) stages its pak under `game/End/Content/Paks/~mods/`, not the game root.
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
    * **[R3.2]** The FF7R pak-routing fix in `FF7RArchiveAnalyzer` / `FF7RModInstaller`, together with its multi-type test.
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
    * FF7R `userconfig` root and pak prefixing, **[R4]** with the `PinnedLast` exemption and the Rebirth-rejecting anchor (**[R4.2]** in both the module and the analyzer).
    * **[R4]** `PackageType` / `RouteSource` labels on the three existing analyzers' routes (**[R4.2]** via the `FileRoute` widening), root tokens, the `GetPackageRoutes()` hook (empty for built-ins), and the `FlatFiles` extra-folder check.
    * HZD Remastered exclusion and the Prefetch warning.
    * Generated paths and the proxy-DLL / `WINEDLLOVERRIDES` detector.
## Verified routes for future modules **[R4]**
Recorded so the §2a SDK shape is known to cover them. Both modules remain **out of scope** for this pass.
**Baldur's Gate 3** (AppID 1086940). Load order lives in `$LOCALAPPDATA/Larian Studios/Baldur's Gate 3/PlayerProfiles/Public/modsettings.lsx`, written by `WriteLoadOrderAsync`.
* `bg3.pak` (ordinary mod) → `$LOCALAPPDATA/Larian Studios/Baldur's Gate 3/Mods/`, layout `FlatFiles` (the `.pak` itself, no subfolder).
* `bg3.script_extender` (`DWrite.dll`) → `$GAME/bin/`, layout `Preserve`.
* `bg3.native_loader` (Native Mod Loader) → `$GAME/bin/`, layout `Preserve`.
* `bg3.native_mod` → `$GAME/bin/NativeMods/` **or** `$LOCALAPPDATA/Larian Studios/Baldur's Gate 3/Plugins/`, depending on the installed loader. Metadata-driven only, never inferred from `*.dll`.
**FF7 Rebirth** (AppID 2909400):
* `ue.pak_bundle` (`.pak` + `.ucas` + `.utoc`) → `$GAME/End/Content/Paks/~mods/`, layout `FlatFiles`; the trio is renamed together when order-prefixed.
* `ff7rb.reunion_plugin` (Reunion / Dresscode and dependents) → `$GAME/End/Mods/<mod>/`, layout `FolderPerMod`.
* `ff7rb.gameinstance_loader` (`ZGameInstanceLoader.pak`) → `$GAME/End/Content/Paks/~mods/`, layout `PinnedLast`.
## Out of scope
* New game modules: UE generic, Bethesda (`plugins.txt`), BG3 (`modsettings.lsx`; routes above), **[R4]** FF7 Rebirth (routes above), HZD Remastered, BepInEx/MelonLoader as modules, Reloaded-II/GameBanana loader-managed games. **[R2]** The SDK roots (`localappdata`, `documents`), `TargetRoot` and `WriteLoadOrderAsync` are shaped to support these without contract changes.
* GUI load-order editor.
* **[R2]** Running `redMod.exe` under Proton, and editing Steam launch options automatically. Both are reported, not automated.
* **[R2]** GameBanana 1-Click (`/mmdl/{fileId}`) protocol handling (only the file id is stored now).
* Switch project integration.
## Sources
The full source list is in the Sources section of `~/Downloads/GAME_MODULES_DEPLOYMENT_PLAN(2).md`: the REDmodding wiki (using mods, archive load order, REDmod usage and deploy, CDPR REDmod PDF, modding on Linux, CET on Proton), the UE pak-patching guide, Nexus mod pages for FF7R and HZD, the BepInEx Proton/Wine docs, **[R4]** the FF7 Rebirth Reunion Mod Loader and Dresscode pages, **[R4]** the BG3 Script Extender and Native Mod Loader pages plus the BG3 community wiki and bg3.wiki install guides, the Skyrim SE `plugins.txt`-under-Proton gist, and the Reloaded-II FAQ / GameBanana 1-Click wiki / Granblue Relink install guide.