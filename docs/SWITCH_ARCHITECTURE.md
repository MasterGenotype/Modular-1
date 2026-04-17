# Switch Module Architecture

```
Modular.Switch/
├── Models/
│   ├── SwitchTitleId.cs        — Validated 16-char hex TitleID value type
│   ├── SwitchModCategory.cs    — RomFs / ExeFs / Cheats / Content enum + YuzuSubPath()
│   ├── SwitchMod.cs            — Canonical mod record (source, hash, deps, install state)
│   ├── SwitchModManifest.cs    — Optional manifest.json deserialization
│   └── SwitchInstallState.cs   — JSON state file (~/.config/Modular/switch_state.json)
│
├── Scanner/
│   └── SwitchModScanner.cs     — Discovers mods from .zip/.7z/.rar/folders; 4-tier
│                                  heuristic: manifest → path segments → name → entries
│
├── DependencyResolver/
│   └── SwitchDependencyGraph.cs — Thread-safe directed graph; Kahn's topological sort;
│                                   cycle, conflict, and missing-dep detection
│
├── Installer/
│   ├── YuzuPaths.cs            — Resolves ~/.local/share/yuzu/load/<ID>/; supports
│                                  native, Flatpak, and Snap Yuzu layouts; path-traversal guard
│   └── SwitchModInstaller.cs   — Transactional install (snapshot → extract/copy → validate);
│                                  idempotency via source hash; rollback via snapshot restore
│
└── Lutris/
    └── LutrisHookWriter.cs     — Generates prelaunch shell script; auto-injects into
                                   Lutris YAML when TitleID + "yuzu" are found in config

Modular.Cli/Commands/Switch/
├── SwitchCommand.cs            — Branch root (prints help when invoked bare)
├── SwitchScanCommand.cs        — modular switch scan
├── SwitchResolveCommand.cs     — modular switch resolve
├── SwitchInstallCommand.cs     — modular switch install (+ Lutris hook wiring)
├── SwitchRemoveCommand.cs      — modular switch remove
├── SwitchRollbackCommand.cs    — modular switch rollback
└── SwitchStatusCommand.cs      — modular switch status
```

## Data flow

```
[Local archives / folders]
         │
         ▼
  SwitchModScanner            (scan)
  ↳ detect TitleID, category, parse manifest
  ↳ compute SHA-256 hash
         │
         ▼
  SwitchInstallState.json     (persisted)
         │
         ▼
  SwitchDependencyGraph       (resolve)
  ↳ BFS closure
  ↳ conflict detection
  ↳ Kahn topological sort → InstallOrder[]
         │
         ▼
  SwitchModInstaller          (install)
  ↳ idempotency check (hash == installed_hash → skip)
  ↳ snapshot existing slot
  ↳ streaming extract / copy into Yuzu load dir
  ↳ path-traversal validation
  ↳ update state → save
         │
         ▼
  LutrisHookWriter            (--runner lutris)
  ↳ write ~/.config/Modular/switch_hooks/<ID>_prelaunch.sh
  ↳ auto-inject into ~/.config/lutris/games/*.yml
```

## Idempotency

Every mod carries a `source_hash` (SHA-256 of archive / directory tree).
On install the hash is written as `installed_hash`.
Subsequent installs of the same mod are **skipped** unless:
- `--force` is passed (clears `installed_hash` before the run), or
- the source file changed (new hash ≠ recorded hash).

## Rollback

Before writing any files, the installer copies the current slot directory
to `<slot>.snapshot_<timestamp>/`.  On success the snapshot is retained
(available for explicit `modular switch rollback`).  On failure during
install the snapshot is automatically restored.

## Safety boundaries

`YuzuPaths.AssertInsideLoadDir()` validates every resolved destination
path with `Path.GetFullPath` before any write.  Path traversal entries
(e.g. `../../etc/passwd`) are silently skipped and logged.
