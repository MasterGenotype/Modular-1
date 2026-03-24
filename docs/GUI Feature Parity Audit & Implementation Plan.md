# GUI Feature Parity Audit
## Current State
The GUI (Avalonia MVVM) has 5 views: NexusMods, GameBanana, Downloads, Library, Settings. The CLI exposes significantly more functionality. The PluginsViewModel already exists but is NOT wired into navigation or DI.
## Missing Features (ordered by priority)
### 1. Plugins View — Quick Win
**PluginsViewModel already exists** with full discover/load/unload/toggle logic. Just needs wiring.
* Add `PluginLoader` and `PluginComposer` to DI in `Program.cs`
* Register `PluginsViewModel` as transient in DI
* Inject `PluginsViewModel` into `MainWindowViewModel`
* Add "Plugins" nav button in `MainWindow.axaml`
* Add `DataTemplate` mapping for `PluginsViewModel` → new `PluginsView.axaml`
* Create `PluginsView.axaml` — list plugins with name, version, status, load/unload toggle
* Add `"Plugins"` case to `NavigateTo()` switch
### 2. NexusMods SSO Login (CLI: `login`)
Settings already has API key input + test. Add SSO login button.
* Add `LoginCommand` relay in `SettingsViewModel` calling `NexusSsoClient.AuthenticateAsync()`
* Add "Login via Browser" button to NexusMods section of `SettingsView.axaml`
* Register `NexusSsoClient` in DI
* On success, populate API key field and save settings
### 3. Game Detection (CLI: `detect-games`, `detect-engine`)
Needed as a precursor for mod installation.
* Create `GameDetectionViewModel` — scans Steam libraries, lists games with AppID/name/path/engine
* Create `GameDetectionView.axaml` — DataGrid of detected games, "Scan" button, engine column
* Register `SteamGameScanner` in DI
* Add nav button + DataTemplate + route
### 4. Mod Installation (CLI: `install`)
Critical missing functionality — installing downloaded mods to game dirs.
* Create `InstallViewModel` — select archive, select target game (from detection), dry-run, install, show results
* Create `InstallView.axaml` — file picker for archive, game selector dropdown, options (force, no-backup, dry-run), progress, results
* Register `ModInstallationService` and `ModularDatabase` in DI
* Add nav button + DataTemplate + route
### 5. Installed Mods & Uninstall (CLI: `installed`, `uninstall`)
View and manage installed mods.
* Create `InstalledModsViewModel` — lists installed changesets, supports uninstall with confirmation
* Create `InstalledModsView.axaml` — DataGrid with changeset ID, mod, target, date; Uninstall button
* Wire into navigation, DI
### 6. Profile Management (CLI: `profile export/import/list`)
* Create `ProfilesViewModel` — list profiles, export current library, import profile
* Create `ProfilesView.axaml` — profile list, export button, import file picker
* Register `ProfileExporter` in DI
* Wire into navigation
### 7. Diagnostics (CLI: `diagnostics run`)
Lower priority — useful for troubleshooting.
* Add diagnostics panel to Settings view (or as separate view)
* Show health check results, system info, loaded plugins
* Register `DiagnosticService` in DI
### 8. Telemetry Management (CLI: `telemetry summary/export/clear`)
Lowest priority — add to Settings.
* Add telemetry section to `SettingsView.axaml`
* Show summary stats, export button, clear button
* Register `TelemetryService` in DI
## Implementation Approach
Prioritize items 1-5 as they cover the most impactful functionality. Items 6-8 can be integrated into Settings to avoid nav bloat. Item 1 is nearly zero-effort since the ViewModel already exists.
## Files to Modify
* `src/Modular.Gui/Program.cs` — DI registrations
* `src/Modular.Gui/Views/MainWindow.axaml` — Navigation buttons, DataTemplates
* `src/Modular.Gui/ViewModels/MainWindowViewModel.cs` — Child ViewModels, NavigateTo cases
* `src/Modular.Gui/ViewModels/SettingsViewModel.cs` — SSO login, diagnostics, telemetry
* `src/Modular.Gui/Views/SettingsView.axaml` — SSO button, diagnostics section, telemetry section
## Files to Create
* `src/Modular.Gui/Views/PluginsView.axaml` + `.axaml.cs`
* `src/Modular.Gui/Views/GameDetectionView.axaml` + `.axaml.cs`
* `src/Modular.Gui/ViewModels/GameDetectionViewModel.cs`
* `src/Modular.Gui/Views/InstallView.axaml` + `.axaml.cs`
* `src/Modular.Gui/ViewModels/InstallViewModel.cs`
* `src/Modular.Gui/Views/InstalledModsView.axaml` + `.axaml.cs`
* `src/Modular.Gui/ViewModels/InstalledModsViewModel.cs`
* `src/Modular.Gui/Views/ProfilesView.axaml` + `.axaml.cs`
* `src/Modular.Gui/ViewModels/ProfilesViewModel.cs`
