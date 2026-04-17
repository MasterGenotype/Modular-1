# Switch Module — Modular-1 Integration Guide

This document describes the exact changes needed to wire the Switch module
into the existing Modular-1 solution.

---

## 1. Add the project to the solution

From the repo root:

```bash
dotnet sln Modular.sln add src/Modular.Switch/Modular.Switch.csproj
```

---

## 2. Reference Modular.Switch from Modular.Cli

Add to **`src/Modular.Cli/Modular.Cli.csproj`** inside `<ItemGroup>`:

```xml
<ProjectReference Include="../Modular.Switch/Modular.Switch.csproj" />
```

---

## 3. Register CLI commands in Program.cs

In **`src/Modular.Cli/Program.cs`**, locate the `app.Configure(config => { ... })` block
and add the following branch **alongside** the existing commands:

```csharp
using Modular.Cli.Commands.Switch;

// Inside config => { ... }:
config.AddBranch<SwitchCommand.Settings>("switch", switchCmd =>
{
    switchCmd.SetDescription("Manage Nintendo Switch mods for Yuzu-emulated games");

    switchCmd.AddCommand<SwitchScanCommand>("scan");
    switchCmd.AddCommand<SwitchResolveCommand>("resolve");
    switchCmd.AddCommand<SwitchInstallCommand>("install");
    switchCmd.AddCommand<SwitchRemoveCommand>("remove");
    switchCmd.AddCommand<SwitchRollbackCommand>("rollback");
    switchCmd.AddCommand<SwitchStatusCommand>("status");
});
```

---

## 4. NuGet dependency

`Modular.Switch.csproj` already references **SharpCompress** (same version used by
`Modular.Core`).  No additional NuGet restore steps are needed if you restore from the
solution root.

---

## 5. State file location

Switch state is persisted at:

```
~/.config/Modular/switch_state.json
```

This mirrors the existing `~/.config/Modular/modular.db` convention.

---

## 6. Yuzu mod directory layout (reference)

```
~/.local/share/yuzu/load/
└── <TITLE_ID>/                  ← one folder per game
    └── <ModName>/               ← one folder per mod (the "slot")
        ├── romfs/               ← RomFS patch files
        ├── exefs/               ← ExeFS IPS patches / stubs
        └── cheats/              ← *.txt cheat files
```

LayeredFS loads slots in alphabetical order.  Use `load_order` in `manifest.json`
to rename slots with a numeric prefix (e.g. `01_MyMod`) for deterministic ordering
— the installer handles this automatically when `LoadOrder > 0`.

---

## 7. Lutris hook

After running:

```bash
modular switch install --game <TITLE_ID> --runner lutris
```

A script is generated at:

```
~/.config/Modular/switch_hooks/<TITLE_ID>_prelaunch.sh
```

Set this as the **Pre-launch script** in Lutris → right-click game → Configure →
System options → Pre-launch script.

If the Lutris YAML for the game is auto-detected (it must contain both "yuzu" and
the TitleID), the hook is injected automatically.

---

## 8. Manifest format (optional)

Place a `manifest.json` in the root of an archive or folder to provide explicit metadata:

```json
{
  "name": "HD Texture Pack",
  "version": "2.1.0",
  "title_id": "0100F2C0115B6000",
  "category": "romfs",
  "load_order": 10,
  "dependencies": [],
  "conflicts": ["0100F2C0115B6000/RomFs/LowRes_Texture_Pack"]
}
```

All fields are optional — the scanner applies heuristics for any missing values.
