# GUI Implementation Notes

Outstanding GUI improvements and fixes to address.

## Issues to Fix

1. **GameBanana Backend Placement** - GameBanana Backend is present in the NexusMods Panel. Relocate to the GameBanana Panel and set it as the default backend there.

2. **GameBanana Search** - Search for GameBanana currently displays placeholders. Implement search that fuzzy-matches entries in the same manner as the NexusMods implementation. Searching should filter results based on GameDomain and mod name.

3. **Profiles Panel** - Currently has no usable functionality. Should be a sub-menu item within Settings that opens a new page with a back button. Within this page, provide create/remove actions for per-game profiles (e.g., different loadouts for single-player vs. multiplayer).

4. **Collections Panel** - Currently unusable. Requirements:
   - Query for collections online per backend selection
   - Download collections and install collection mods into per-game directories
   - Simulate installation to Steam game directories for collection record storage
   - Up to 3 collections per game for record storage
   - Collections are primarily a NexusMods feature, so move them to a NexusMods Panel tab instead of the Backups Panel

5. **File Picker for Profile Import** - Use a native file picker dialog for browsing to exported profiles during import.

6. **Plugins Panel** - Plugins is a developer-facing component. Move it to a sub-section within the Settings panel.

7. **NexusMods Collections API** - Use the NexusMods API to search and list available collections by GameDomain ID. Display name, description, total mod count, and dependency/requirement list. Provide an option to download selected collections.
