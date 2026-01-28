# Reorganize, Sort, and Rename Guide

## Overview

The Modular tool now includes comprehensive mod organization features:

1. **Rename**: Converts numeric mod IDs to human-readable names
2. **Organize**: Optionally sorts mods into category folders
3. **Merge**: Automatically handles duplicate mods
4. **Auto-execute**: Runs automatically after downloads when configured

## Features

### 1. Simple Rename (Default)
Renames mod folders from IDs to names:
```
Before: ~/Games/Mods-Lists/stardewvalley/10021/
After:  ~/Games/Mods-Lists/stardewvalley/Stardew Valley Expanded/
```

### 2. Organize by Category
Creates category subdirectories:
```
After:  ~/Games/Mods-Lists/stardewvalley/
        ├── Gameplay/
        │   ├── Stardew Valley Expanded/
        │   └── Automate/
        ├── Graphics/
        │   ├── Seasonal Villager Outfits/
        │   └── Elle's Seasonal Buildings/
        └── UI/
            └── UI Info Suite/
```

### 3. Automatic Renaming
When `auto_rename` is enabled in config, mods are automatically renamed after downloading.

## Usage

### Method 1: Interactive Menu
```bash
./build/modular-cli
# Select option 3 (Rename)
# Choose whether to organize by category (y/n)
```

### Method 2: Automatic After Downloads
Enable in config.json:
```json
{
  "auto_rename": true
}
```

Then download as normal:
```bash
./build/modular-cli stardewvalley
```
Mods will be automatically renamed after downloading.

### Method 3: Manual Rename
From the menu, select option 3 to rename existing mods.

## Configuration

Edit `~/.config/Modular/config.json`:

```json
{
  "nexus_api_key": "your_api_key_here",
  "mods_directory": "/home/user/Games/Mods-Lists",
  "auto_rename": true,
  "gamebanana_user_id": "optional",
  "default_categories": ["main", "optional"],
  "auto_rename": true,
  "verify_downloads": false,
  "max_concurrent_downloads": 1,
  "verbose": false
}
```

## Smart Features

### Duplicate Handling
If a mod with the same name already exists, the tool will:
- Merge directory contents if both are directories
- Skip and warn if file types conflict

### Already Renamed
The tool automatically skips mods that have already been renamed (non-numeric folder names).

### Rate Limiting
Built-in 500ms delay between API calls to respect NexusMods rate limits.

### Error Recovery
- Continues processing other mods if one fails
- Provides detailed error messages
- Shows summary statistics

## Examples

### Rename Stardew Valley mods (simple)
```bash
./build/modular-cli
# Choose option 3
# Enter 'n' for no categories
```

### Rename with category organization
```bash
./build/modular-cli
# Choose option 3
# Enter 'y' for categories
```

### Download and auto-rename
First, enable auto-rename in config:
```bash
# Edit ~/.config/Modular/config.json and set "auto_rename": true
```

Then download:
```bash
./build/modular-cli stardewvalley
```

Mods will be downloaded and automatically renamed!

## Troubleshooting

**"NexusMods API key is not configured"**
- Set your API key in `~/.config/Modular/config.json`

**"Failed to fetch info for mod XXXXX"**
- Mod may have been deleted from NexusMods
- Check your API key is valid
- Verify internet connection

**"Destination already exists"**
- Two different mods have the same name (rare)
- Manually inspect and merge/delete as needed

## Performance

- Typical speed: ~2 seconds per mod (API rate limiting)
- 100 mods: ~3-4 minutes
- Progress is shown in real-time

## Summary Output

After completion, you'll see:
```
Successfully processed 45 mods in stardewvalley

=== Summary ===
Total mods processed: 45
```
