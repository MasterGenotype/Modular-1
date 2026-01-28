# Category Organization Feature

## Overview

The Modular application now supports organizing downloaded NexusMods files by their NexusMods categories. When enabled, mods will be automatically sorted into category subdirectories (e.g., "Gameplay Mechanics/", "Visuals and Graphics/", "UI/") based on the category metadata from NexusMods.

## Configuration

### Config File

Add `organize_by_category` to `~/.config/Modular/config.json`:

```json
{
  "auto_rename": true,
  "organize_by_category": true,
  ...
}
```

- **Default**: `false` (disabled)
- **When enabled**: Mods are organized into category subdirectories during auto-rename
- **When disabled**: Mods are renamed but remain in the game domain root directory

### CLI Flag

Use the `--organize-by-category` flag to enable category organization for a single run:

```bash
./build/modular-cli --organize-by-category stardewvalley
```

This overrides the config file setting for that execution only.

## How It Works

1. **Download Phase**: Mods are downloaded to `~/Games/Mods-Lists/{game_domain}/{mod_id}/`
2. **Auto-Rename Phase** (if `auto_rename = true`):
   - Fetches mod info from NexusMods API (includes category_id and category name)
   - Renames directory from numeric ID to human-readable name
   - **If `organize_by_category = true`**:
     - Creates category subdirectory (e.g., "Gameplay Mechanics/")
     - Moves renamed mod into category subdirectory
     - Handles duplicates by merging directory contents

3. **Result Structure**:
   ```
   ~/Games/Mods-Lists/stardewvalley/
   ├── Gameplay Mechanics/
   │   ├── Automate/
   │   ├── CJB Cheats Menu/
   │   └── Destroyable Bushes/
   ├── Visuals and Graphics/
   │   ├── Alternative Textures/
   │   └── Dynamic Reflections/
   ├── UI/
   │   ├── Better Crafting/
   │   └── Convenient Inventory/
   └── Characters/
       ├── Abigail Lewd Dialogue/
       └── Anime Style Skimpy Portraits for Non-marriageable NPCs/
   ```

## Examples

### Example 1: Enable in config, download with auto-rename

```bash
# Edit config to set organize_by_category = true
./build/modular-cli stardewvalley
```

Mods will be automatically organized by category after download.

### Example 2: CLI flag override

```bash
# Even if config has organize_by_category = false
./build/modular-cli --organize-by-category stardewvalley
```

Mods will be organized by category for this run only.

### Example 3: Manual reorganization (menu mode)

```bash
./build/modular-cli
# Choose option 3 (Rename)
# When prompted "Organize by category? (y/n):", enter 'y'
```

This will reorganize all existing mods across all game domains.

### Example 4: Download without category organization

```bash
# If config has organize_by_category = false (default)
./build/modular-cli stardewvalley
```

Mods will be renamed but NOT organized into categories.

## Category Examples (Stardew Valley)

NexusMods categories vary by game. For Stardew Valley, common categories include:

- Audio
- Buildings
- Characters
- Cheats
- Crafting
- Clothing
- Crops
- Dialogue
- Events
- Expansions
- Fishing
- Furniture
- Gameplay Mechanics
- Interiors
- Items
- Livestock and Animals
- Locations
- Maps
- Miscellaneous
- Modding Tools
- New Characters
- Pets / Horses
- Player
- Portraits
- User Interface
- Visuals and Graphics

## Technical Details

### API Integration

The category information comes from the NexusMods API response when fetching mod info:

```json
{
  "name": "Automate",
  "category_id": 1,
  "category": {
    "name": "Gameplay Mechanics"
  }
}
```

### Rate Limiting

The rename operation includes 500ms delays between API calls to respect NexusMods rate limits.

### Duplicate Handling

If a mod directory already exists in the target location:
- Contents are merged (files from source are moved to destination)
- Source directory is removed after merge
- No data is lost

### Skipping Already-Renamed Mods

The system skips directories that don't have numeric names, assuming they've already been renamed/organized.

## Configuration Priority

1. **CLI flag** (`--organize-by-category`) - highest priority
2. **Config file** (`config.json`) - fallback
3. **Interactive menu** - manual override for menu mode

## Verification

To verify the feature is working:

1. Check config: `cat ~/.config/Modular/config.json | grep organize_by_category`
2. Run with dry-run: `./build/modular-cli --dry-run --organize-by-category stardewvalley`
3. Check help: `./build/modular-cli --help` (should list `--organize-by-category`)
4. Inspect directory structure after download to see category subdirectories

## Notes

- The feature respects the existing `auto_rename` setting (must be `true` for automatic organization)
- Category organization can be applied retroactively using menu option 3
- Empty category directories are not created (only when mods exist for that category)
- Category names are sanitized for filesystem safety (special characters removed)
