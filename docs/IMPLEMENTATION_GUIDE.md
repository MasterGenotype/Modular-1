# Implementation Guide

This document identifies areas of the Modular codebase that contain placeholder logic, simplified implementations, or incomplete integrations, and provides guidance on how each could be completed.

## Table of Contents

- [1. FOMOD Installer - UI Selection Integration](#1-fomod-installer---ui-selection-integration)
- [2. Legacy Service Migration](#2-legacy-service-migration)
- [3. IModVersionProvider - Backend Implementations](#3-imodversionprovider---backend-implementations)
- [4. Plugin Marketplace - Hosting and Index](#4-plugin-marketplace---hosting-and-index)
- [5. UI Extension Loading in GUI](#5-ui-extension-loading-in-gui)
- [6. Profile Export/Import - CLI and GUI Exposure](#6-profile-exportimport---cli-and-gui-exposure)
- [7. Diagnostics Service - User-Facing Integration](#7-diagnostics-service---user-facing-integration)
- [8. Telemetry Service - Workflow Integration](#8-telemetry-service---workflow-integration)
- [9. File Conflict Auto-Resolution](#9-file-conflict-auto-resolution)

---

## 1. FOMOD Installer - UI Selection Integration

**File:** `src/Modular.Core/Installers/FomodInstaller.cs`
**Status:** Basic implementation complete; user selection not wired up

### Current State

The FOMOD installer can detect FOMOD archives (by looking for `ModuleConfig.xml`), parse install steps/groups/plugins from the XML, and extract files. However, as noted at line 132:

> "Note: Full FOMOD install requires UI for user selections. This is a simplified implementation"

Currently, `AnalyzeAsync` parses the FOMOD config and stores it in `plan.Options["fomod_config"]`, but the install step always falls back to extracting all non-FOMOD files without presenting choices to the user. The `InstallAsync` method checks for `plan.Options["selected_files"]` but nothing populates that key.

### How to Implement

1. **Create a FOMOD selection dialog in the GUI** (`src/Modular.Gui/Views/`):
   - Parse the `install_steps` data from `plan.Options["fomod_config"]`
   - For each install step, display the groups and plugins as described by the FOMOD spec
   - Respect group types: `SelectExactlyOne`, `SelectAtLeastOne`, `SelectAtMostOne`, `SelectAll`, `SelectAny`
   - Collect user selections and map them to file source/destination pairs

2. **Create a CLI fallback** (`src/Modular.Cli/`):
   - Use Spectre.Console to present numbered menus for each install step
   - Support `--fomod-auto` flag to auto-select defaults or first option

3. **Wire selections into the install plan**:
   - After the UI collects selections, populate `plan.Options["selected_files"]` with the list of source paths
   - Map FOMOD `<file source="..." destination="..."/>` elements to `FileOperation` entries with correct destination paths
   - Pass the updated plan to `InstallAsync`

4. **Handle conditional flags**:
   - FOMOD supports `<conditionalFileInstalls>` with pattern-based conditions
   - Parse `<dependencies>` elements and evaluate them against selected plugins
   - This is the most complex part and can be deferred to a later iteration

### Key Files to Modify

- `src/Modular.Core/Installers/FomodInstaller.cs` - Add proper destination path mapping from FOMOD XML
- `src/Modular.Gui/Views/` - New `FomodInstallerDialog.axaml` view
- `src/Modular.Gui/ViewModels/` - New `FomodInstallerViewModel.cs`
- `src/Modular.Cli/Program.cs` - CLI prompts for FOMOD selections

---

## 2. Legacy Service Migration

**Files:**
- `src/Modular.Core/Services/NexusModsService.cs` (marked `[Obsolete]`)
- `src/Modular.Core/Services/GameBananaService.cs` (marked `[Obsolete]`)
- `src/Modular.Cli/Program.cs` (line 16: `TODO: Migrate remaining commands to use backend system`)

### Current State

The CLI's `RunCommandMode` method (direct domain argument invocation) still uses the legacy `NexusModsService` directly, bypassing the backend abstraction. The `RunInteractiveMode` and `RunDownloadCommand` methods already use the backend system correctly. The `RunGameBananaCommand` also uses the legacy `GameBananaService`.

The legacy services are functional but duplicate logic that now lives in the backend implementations (`NexusModsBackend`, `GameBananaBackend`).

### How to Implement

1. **Migrate `RunCommandMode`** to use `NexusModsBackend` via the registry:
   ```csharp
   // Replace:
   var nexusService = new NexusModsService(settings, rateLimiter, database, ...);
   await nexusService.DownloadFilesAsync(domain, ...);

   // With:
   var registry = InitializeBackends(settings, rateLimiter, database, metadataCache, verbose);
   var backend = registry.Get("nexusmods");
   await RunBackendDownload(backend, settings, domain, options);
   ```

2. **Migrate `RunGameBananaCommand`** similarly:
   ```csharp
   var registry = InitializeBackends(settings, rateLimiter, database, metadataCache, verbose);
   var backend = registry.Get("gamebanana");
   await RunBackendDownload(backend, settings, null, options);
   ```

3. **Remove the `#pragma warning disable CS0618`** at the top of `Program.cs`

4. **Verify feature parity**: Ensure the backend implementations cover all functionality from the legacy services before removing them. Check:
   - File category filtering
   - Progress callback signatures
   - Dry-run support
   - Force re-download behavior

5. **Remove or deprecate the legacy services** once migration is confirmed

### Key Files to Modify

- `src/Modular.Cli/Program.cs` - `RunCommandMode`, `RunGameBananaCommand`
- `src/Modular.Core/Services/NexusModsService.cs` - Remove after migration
- `src/Modular.Core/Services/GameBananaService.cs` - Remove after migration

---

## 3. IModVersionProvider - Backend Implementations

**File:** `src/Modular.Core/Dependencies/PubGrubResolver.cs`
**Status:** Resolver algorithm complete; no concrete `IModVersionProvider` implementations exist

### Current State

The `PubGrubResolver` implements a working dependency resolution algorithm that:
- Selects versions satisfying all constraints (preferring latest)
- Detects circular dependencies
- Reports incompatible mods
- Produces a topological install order

However, the `IModVersionProvider` interface it depends on has no implementations. This means the resolver cannot be used with actual mod data from NexusMods, GameBanana, or any other backend.

### How to Implement

1. **Create `NexusModsVersionProvider`**:
   ```csharp
   public class NexusModsVersionProvider : IModVersionProvider
   {
       private readonly NexusModsBackend _backend;

       public async Task<List<SemanticVersion>> GetAvailableVersionsAsync(
           string canonicalId, CancellationToken ct)
       {
           // Parse canonicalId to extract mod ID
           // Call NexusMods API: GET /v1/games/{domain}/mods/{id}/files.json
           // Extract version strings from file metadata
           // Parse into SemanticVersion objects
       }

       public async Task<List<ModDependency>> GetDependenciesAsync(
           string canonicalId, SemanticVersion version, CancellationToken ct)
       {
           // NexusMods API doesn't expose structured dependencies
           // Options:
           //   a) Parse FOMOD metadata for dependency info
           //   b) Use mod description/requirements text (heuristic)
           //   c) Return empty list (no dependency tracking)
       }
   }
   ```

2. **Create `GameBananaVersionProvider`**:
   - GameBanana API provides file version info via the `_aFiles` field
   - Dependencies can be extracted from the mod's `_aPrerequisites` field if available

3. **Register providers** in the backend implementations or via DI

4. **Expose resolution through CLI/GUI**:
   - Add a `modular resolve <mod-ids...>` CLI command
   - Add a dependency resolution panel in the GUI

### Key Files to Create

- `src/Modular.Core/Backends/NexusMods/NexusModsVersionProvider.cs`
- `src/Modular.Core/Backends/GameBanana/GameBananaVersionProvider.cs`

### Key Files to Modify

- `src/Modular.Core/Backends/NexusMods/NexusModsBackend.cs` - Expose provider
- `src/Modular.Core/Backends/GameBanana/GameBananaBackend.cs` - Expose provider

---

## 4. Plugin Marketplace - Hosting and Index

**File:** `src/Modular.Core/Plugins/PluginMarketplace.cs`
**Status:** Client-side infrastructure complete; no marketplace server or index exists

### Current State

The `PluginMarketplace` class can:
- Fetch a plugin index from a URL (`FetchIndexAsync`)
- Download, verify (SHA256), and install plugins (`InstallPluginAsync`)
- Check for updates by comparing installed vs. index versions (`CheckUpdatesAsync`)
- Uninstall plugins (`UninstallPluginAsync`)

What's missing is:
- An actual marketplace index URL (the `indexUrl` parameter is passed in but no default or documented URL exists)
- A server or static hosting for the plugin index JSON
- GUI/CLI integration to browse and install plugins

### How to Implement

1. **Create and host a plugin index**:
   - Define a JSON index file following the `PluginIndex` schema:
     ```json
     {
       "version": 1,
       "updated_at": "2025-01-01T00:00:00Z",
       "plugins": [
         {
           "id": "example-backend",
           "name": "Example Backend",
           "description": "Adds support for ExampleSite",
           "author": "AuthorName",
           "version": "1.0.0",
           "download_url": "https://example.com/plugins/example-backend-1.0.0.zip",
           "sha256": "abc123...",
           "tags": ["backend"]
         }
       ]
     }
     ```
   - Host on GitHub Releases, a static site, or a dedicated API

2. **Add a default index URL** to `AppSettings`:
   ```csharp
   public string PluginMarketplaceUrl { get; set; } = "https://raw.githubusercontent.com/MasterGenotype/modular-plugins/main/index.json";
   ```

3. **Wire into the CLI**:
   ```bash
   modular plugins list          # List available plugins from marketplace
   modular plugins install <id>  # Install a plugin
   modular plugins update        # Check and apply updates
   modular plugins remove <id>   # Uninstall a plugin
   ```

4. **Wire into the GUI**:
   - The `PluginsViewModel` already exists in `src/Modular.Gui/ViewModels/`
   - Add marketplace browsing, install buttons, and update notifications

### Key Files to Modify

- `src/Modular.Core/Configuration/AppSettings.cs` - Add marketplace URL setting
- `src/Modular.Cli/Program.cs` - Add `plugins` subcommand
- `src/Modular.Gui/ViewModels/PluginsViewModel.cs` - Integrate marketplace browsing

---

## 5. UI Extension Loading in GUI

**File:** `src/Modular.Sdk/UI/IUiExtension.cs`
**Status:** Interface defined; GUI does not load or render plugin UI extensions

### Current State

The `IUiExtension` interface defines a contract for plugins to contribute UI panels:
- `ExtensionId`, `DisplayName`, `Location` (MainTab, Sidebar, ToolsMenu, Settings, ModDetails, StatusBar)
- `CreateContent()` returns a framework-specific UI object
- Lifecycle methods: `OnActivatedAsync`, `OnDeactivatedAsync`

The `PluginLoader` discovers and exposes UI extensions via `GetAllUiExtensions()`. However, the GUI application does not query the plugin loader for UI extensions or render them anywhere.

### How to Implement

1. **Query extensions at startup** in `src/Modular.Gui/Program.cs` or `MainWindowViewModel.cs`:
   ```csharp
   var uiExtensions = pluginLoader.GetAllUiExtensions();
   foreach (var (location, extensions) in uiExtensions)
   {
       foreach (var ext in extensions)
       {
           // Register extension based on location
       }
   }
   ```

2. **Add extension containers in MainWindow**:
   - For `UiExtensionLocation.MainTab`: Add dynamic tabs to the main navigation
   - For `UiExtensionLocation.Sidebar`: Add a sidebar panel container
   - For `UiExtensionLocation.StatusBar`: Add a status bar extension area
   - For `UiExtensionLocation.Settings`: Add a plugin settings section

3. **Handle extension lifecycle**:
   - Call `OnActivatedAsync` when a tab/panel becomes visible
   - Call `OnDeactivatedAsync` when navigating away
   - Wrap in error boundary to prevent plugin UI crashes from taking down the app

4. **Type safety for Avalonia**:
   - `CreateContent()` returns `object` - cast to Avalonia `Control` at runtime
   - Consider adding an `IAvaloniaUiExtension` subinterface that returns `Control` directly
   - Add a `ContentPresenter` or `ContentControl` to host plugin content

### Key Files to Modify

- `src/Modular.Gui/ViewModels/MainWindowViewModel.cs` - Extension discovery
- `src/Modular.Gui/Views/MainWindow.axaml` - Extension containers
- `src/Modular.Gui/Program.cs` - Wire PluginLoader into DI

---

## 6. Profile Export/Import - CLI and GUI Exposure

**File:** `src/Modular.Core/Profiles/ProfileExporter.cs`
**Status:** Core logic complete; not exposed through CLI or GUI

### Current State

The `ProfileExporter` supports:
- Exporting profiles as JSON or ZIP archives (with optional README)
- Importing profiles from JSON or `.modpack`/`.zip` files
- Validation of imported profiles (version checks, required fields, lockfile integrity)

However, no CLI command or GUI view triggers these operations.

### How to Implement

1. **Add CLI commands**:
   ```bash
   modular profile export <name> --output <path> --format json|archive
   modular profile import <path> --resolve
   modular profile list
   ```

2. **Add GUI views**:
   - Export button in the Library view (select mods → export as profile)
   - Import button that opens a file picker for `.json`/`.zip`/`.modpack` files
   - Show validation warnings/errors after import

3. **Wire profile data to dependency resolution**:
   - On import, optionally run `PubGrubResolver` to check if all dependencies are available
   - Show missing mods and offer to download them

4. **Create `ModProfile` instances from user selections**:
   - The `ModProfile` class exists in `src/Modular.Core/Dependencies/ModProfile.cs`
   - Populate it from the mod library or from selected tracked mods

### Key Files to Modify

- `src/Modular.Cli/Program.cs` - Add `profile` subcommand group
- `src/Modular.Gui/ViewModels/LibraryViewModel.cs` - Export/import buttons
- `src/Modular.Gui/Views/LibraryView.axaml` - UI elements

---

## 7. Diagnostics Service - User-Facing Integration

**File:** `src/Modular.Core/Diagnostics/DiagnosticService.cs`
**Status:** Implementation complete; not exposed to users

### Current State

The `DiagnosticService` provides:
- System health checks (plugin system, plugin integrity, dependencies, disk space)
- Detailed diagnostic report generation (host version, runtime, platform, loaded plugins)
- Plugin manifest validation

None of these are accessible through the CLI or GUI.

### How to Implement

1. **Add CLI command**:
   ```bash
   modular diagnostics            # Run health check and show report
   modular diagnostics --json     # Output as JSON for automation
   modular diagnostics validate <plugin-path>  # Validate a plugin manifest
   ```

2. **Add GUI integration**:
   - Settings view → "Run Diagnostics" button
   - Display health status with colored indicators (green/yellow/red)
   - Show plugin details and dependency status

3. **Add startup health check** (optional):
   - Run quick health check on application startup
   - Show warning banner if any checks fail

### Key Files to Modify

- `src/Modular.Cli/Program.cs` - Add `diagnostics` subcommand
- `src/Modular.Gui/ViewModels/SettingsViewModel.cs` - Diagnostics button/panel

---

## 8. Telemetry Service - Workflow Integration

**File:** `src/Modular.Core/Telemetry/TelemetryService.cs`
**Status:** Complete implementation; not integrated into any workflow

### Current State

The `TelemetryService` supports:
- Privacy-respecting, opt-in telemetry with local-first storage
- Event recording (plugin crashes, installer results, download stats)
- Data anonymization (hashes session IDs, strips identifying fields)
- Summary generation and data export
- Date-partitioned JSON storage

However, no code in the application creates a `TelemetryService` instance or calls `RecordEvent`. The service exists but is unused.

### How to Implement

1. **Register in DI** (both CLI and GUI):
   ```csharp
   var telemetryPath = Path.Combine(configDir, "telemetry");
   var telemetryConfig = new TelemetryConfig { Enabled = settings.TelemetryEnabled };
   services.AddSingleton(new TelemetryService(telemetryPath, telemetryConfig, logger));
   ```

2. **Add configuration option** to `AppSettings`:
   ```csharp
   public bool TelemetryEnabled { get; set; } = false;  // Opt-in
   ```

3. **Instrument key workflows**:
   - After each download: `telemetry.RecordDownload(backend.Id, fileSize, duration, success)`
   - After installer execution: `telemetry.RecordInstallerResult(installerId, success, duration)`
   - On plugin crash (in `ErrorBoundary`): `telemetry.RecordPluginCrash(pluginId, exception)`

4. **Add CLI/GUI access to telemetry data**:
   ```bash
   modular telemetry summary     # Show 30-day summary
   modular telemetry export      # Export to JSON file
   modular telemetry clear       # Delete all telemetry data
   ```

5. **Settings UI**: Add telemetry opt-in toggle and "View Stats" button in Settings view

### Key Files to Modify

- `src/Modular.Core/Configuration/AppSettings.cs` - Add telemetry setting
- `src/Modular.Cli/Program.cs` - Register service, add subcommand
- `src/Modular.Gui/Program.cs` - Register service in DI
- `src/Modular.Core/ErrorHandling/ErrorBoundary.cs` - Record plugin crashes
- `src/Modular.Core/Downloads/DownloadEngine.cs` - Record download stats

---

## 9. File Conflict Auto-Resolution

**Files:**
- `src/Modular.Core/Dependencies/FileConflictIndex.cs`
- `src/Modular.Core/Dependencies/ConflictResolver.cs`

### Current State

The conflict detection system can identify when multiple mods install files to the same path. `ConflictResolver` implements resolution strategies, but the system has limited auto-resolution capabilities and is not integrated into the installation workflow.

### How to Implement

1. **Build conflict index during installation**:
   - After each mod's `AnalyzeAsync`, register its file operations in `FileConflictIndex`
   - Before `InstallAsync`, query the index for conflicts

2. **Implement resolution strategies**:
   - **Last-wins**: Default behavior, later mods overwrite earlier ones (document load order)
   - **Priority-based**: Use mod priority/load order to determine winner
   - **User-prompted**: Show conflicts in GUI and let user choose per-file
   - **Backup**: Create `.modular-backup` copies of overwritten files for rollback

3. **Add conflict report to install plan**:
   - `InstallPlan` should include a `Conflicts` list
   - GUI shows conflicts before installation begins
   - CLI prompts for confirmation when conflicts exist

4. **Rollback support**:
   - Track original files that were overwritten
   - Support `modular uninstall <mod>` that restores previous state

### Key Files to Modify

- `src/Modular.Core/Installers/InstallerManager.cs` - Integrate conflict checking
- `src/Modular.Core/Dependencies/ConflictResolver.cs` - Add resolution strategies
- `src/Modular.Gui/ViewModels/` - Conflict resolution dialog

---

## Priority Recommendations

| Priority | Item | Rationale |
|----------|------|-----------|
| High | Legacy Service Migration (#2) | Removes code duplication and the `[Obsolete]` warning suppression |
| High | FOMOD UI Integration (#1) | FOMOD is the most common mod format; simplified install limits usability |
| Medium | Diagnostics CLI (#7) | Low effort, high value for troubleshooting |
| Medium | Profile Export/Import (#6) | Core feature for mod sharing; implementation is complete but hidden |
| Medium | Plugin Marketplace (#4) | Enables community plugin ecosystem |
| Low | IModVersionProvider (#3) | Dependency resolution is useful but most mod platforms lack structured dependency data |
| Low | UI Extensions (#5) | Useful for plugin authors but requires plugin ecosystem to exist first |
| Low | Telemetry Integration (#8) | Nice-to-have for development insights |
| Low | Conflict Auto-Resolution (#9) | Advanced feature; manual resolution works for most cases |
