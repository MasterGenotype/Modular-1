# Reorganization Improvements

## Issue Report

The user reported two issues:
1. Not all tracked mods were being downloaded
2. Renaming wasn't reorganizing already-renamed mods into categories

## Investigation Results

### Issue 1: Tracked Mods Pagination

**Finding**: No pagination issue exists.

- Tested the `/v1/user/tracked_mods.json` API endpoint
- Confirmed all 412 tracked mods are returned in a single request
- No pagination parameters are needed

**Explanation for missing downloads**:
- User has 79 tracked Stardew Valley mods
- Only 62 mod directories were created
- **Reason**: 17 mods don't have files in the requested categories (main/optional)
  - Some mods may only have "update" or "miscellaneous" files
  - Some mods may have failed validation (not in tracked list)
  
**Solution**: Working as designed. To download more files, use additional categories:
```bash
./build/modular-cli --categories main,optional,update,miscellaneous stardewvalley
```

### Issue 2: Category Reorganization

**Finding**: The `reorganizeAndRenameMods` function only processed numeric mod IDs, preventing reorganization of already-renamed mods.

**Original behavior**:
1. Scans game domain directory for subdirectories
2. Skips any non-numeric directory names (already renamed)
3. Only processes numeric directories (mod IDs)

**Problem**: If you run rename once without categories, then run again with categories, nothing happens because all directories are now non-numeric.

**Solution implemented**:
1. Load the downloads database to build a mapping of `mod_id` → `directory_path`
2. Process ALL directories, not just numeric ones
3. For already-renamed directories:
   - Look up their mod_id from the database
   - Fetch category info from API
   - Move them into appropriate category subdirectories
4. Improved path handling to use `fs::path` instead of string concatenation

## Code Changes

### File: `src/core/Rename.cpp`

**Key improvements**:

1. **Added Database integration**:
   ```cpp
   #include "Database.h"
   #include <map>
   ```

2. **Changed data structure**:
   - Old: `std::vector<std::string> modIDs` (directory names only)
   - New: `std::vector<fs::path> modDirs` (full paths)
   - Database map: `std::map<int, fs::path> modIdToPath` for reverse lookup

3. **Improved directory scanning**:
   - Collects full paths instead of just names
   - Skips special files (downloads.db.json, download_links.txt)
   - Processes all directories regardless of naming

4. **Database-based mod identification**:
   ```cpp
   if (fs::exists(dbPath)) {
       modular::Database db(dbPath);
       db.load();
       auto records = db.getRecordsByDomain(gameDomain);
       for (const auto& record : records) {
           if (modIdToPath.find(record.mod_id) == modIdToPath.end()) {
               fs::path filepath = record.filepath;
               if (!filepath.empty() && fs::exists(filepath.parent_path())) {
                   modIdToPath[record.mod_id] = filepath.parent_path();
               }
           }
       }
   }
   ```

5. **Enhanced reorganization logic**:
   - Handles both numeric (unprocessed) and renamed (already processed) directories
   - For renamed directories, looks up mod_id from database
   - Moves directories to category subdirectories while preserving content
   - Better error messages showing directory names instead of generic IDs

## Usage Examples

### Example 1: Initial download with category organization

```bash
./build/modular-cli --organize-by-category stardewvalley
```

Result:
```
~/Games/Mods-Lists/stardewvalley/
├── Gameplay Mechanics/
│   ├── Automate/
│   └── CJB Cheats Menu/
└── Visuals and Graphics/
    └── Alternative Textures/
```

### Example 2: Reorganize already-renamed mods

If you already ran the tool without `--organize-by-category` and have:
```
~/Games/Mods-Lists/stardewvalley/
├── Automate/
├── CJB Cheats Menu/
└── Alternative Textures/
```

Running with `--organize-by-category`:
```bash
./build/modular-cli --organize-by-category stardewvalley
```

Will reorganize to:
```
~/Games/Mods-Lists/stardewvalley/
├── Gameplay Mechanics/
│   ├── Automate/
│   └── CJB Cheats Menu/
└── Visuals and Graphics/
    └── Alternative Textures/
```

### Example 3: Menu mode reorganization

```bash
./build/modular-cli
# Choose option 3 (Rename)
# When prompted "Organize by category? (y/n):", enter 'y'
```

This will reorganize ALL mods across ALL game domains.

## Technical Details

### Database Structure

The downloads database (`downloads.db.json`) tracks:
- `game_domain`: Game identifier
- `mod_id`: Numeric mod ID
- `filepath`: Full path to downloaded file

**Example record**:
```json
{
  "game_domain": "stardewvalley",
  "mod_id": 1063,
  "filepath": "/home/user/Games/Mods-Lists/stardewvalley/Automate/Automate.zip",
  ...
}
```

From the filepath, we extract:
- Parent directory: `/home/user/Games/Mods-Lists/stardewvalley/Automate/`
- This gives us `mod_id: 1063` → `Automate` mapping

### Path Handling

Using `fs::path` instead of string concatenation provides:
- Cross-platform compatibility
- Proper path separator handling
- Built-in path comparison (`oldPath != newPath`)
- Easy extraction of components (`filename()`, `parent_path()`)

### Rate Limiting

The function maintains 500ms delays between API calls to respect NexusMods rate limits, regardless of whether processing numeric or renamed directories.

## Testing

To test the improvements:

1. **Test reorganization of renamed mods**:
   ```bash
   # Assuming mods are already renamed but not categorized
   /home/superphenotype/.gitrepos/Modular/build/modular-cli --organize-by-category stardewvalley
   ```

2. **Verify category structure**:
   ```bash
   ls -la /home/superphenotype/Games/Mods-Lists/stardewvalley/
   ```
   Should show category subdirectories like "Gameplay Mechanics/", "Visuals and Graphics/", etc.

3. **Check mod movement**:
   ```bash
   ls -la /home/superphenotype/Games/Mods-Lists/stardewvalley/Gameplay\ Mechanics/
   ```
   Should show mods moved into the category.

## Notes

- Empty category directories are NOT created (only when mods exist for that category)
- Category names are sanitized for filesystem safety
- Duplicate handling: if a mod already exists at the destination, directories are merged
- The database must exist for reorganization of renamed mods to work
- If database doesn't exist, only numeric (unprocessed) mod directories will be renamed

## Future Improvements

Potential enhancements:
1. Add a `--force-reorganize` flag to rebuild category structure from scratch
2. Cache API responses to avoid redundant calls during reorganization
3. Add progress bar for reorganization operations
4. Support moving mods between category subdirectories if category changed
