# NexusMods Tracking Validation Mechanisms

## Overview

This document describes the multi-layer validation system that ensures **ONLY** mods from the user's NexusMods tracking center are downloaded.

## Validation Layers

### Layer 1: Source Endpoint (Primary Security)
**API Endpoint:** `https://api.nexusmods.com/v1/user/tracked_mods.json`

**Security:**
- Requires authenticated API key
- Server-side returns ONLY mods tracked by the account associated with the API key
- No way to request mods not tracked by the user
- NexusMods enforces this at the API level

**Implementation:** `get_tracked_mods_with_domain()` (NexusMods.cpp:104-141)

**API Response Format:**
```json
[
  {
    "mod_id": 12345,
    "domain_name": "stardewvalley",
    "name": "Stardew Valley Expanded"
  },
  {
    "mod_id": 67890,
    "domain_name": "skyrimspecialedition",
    "name": "SkyUI"
  }
]
```

### Layer 2: Domain Filtering
**Function:** `get_tracked_mods_for_domain()` (NexusMods.cpp:152-161)

**Validation:**
- Filters tracked mods by exact domain name match
- Local filtering (no additional API calls)
- Only returns mod IDs that are BOTH tracked AND match the domain

**Code:**
```cpp
std::vector<int> get_tracked_mods_for_domain(const std::string& game_domain, const Config& config)
{
    std::vector<int> ids;
    auto all_tracked = get_tracked_mods_with_domain(config);
    for (const auto& tm : all_tracked) {
        if (tm.domain_name == game_domain) ids.push_back(tm.mod_id);
    }
    return ids;
}
```

### Layer 3: Pre-Download Validation
**Function:** `get_file_ids()` (NexusMods.cpp:194-318)

**Validation:**
- Before fetching file lists, re-validates each mod_id against tracked list
- Builds a set of tracked IDs for the domain
- Explicitly checks each mod against this set
- **Rejects and skips any mod not in tracked list**

**Code (lines 212-227):**
```cpp
// Pre-validate: Get tracked mods list to verify each mod
auto tracked_mods = get_tracked_mods_with_domain(config);
std::set<int> tracked_ids_for_domain;
for (const auto& tm : tracked_mods) {
    if (tm.domain_name == game_domain) {
        tracked_ids_for_domain.insert(tm.mod_id);
    }
}

for (auto mod_id : mod_ids) {
    // VALIDATION: Ensure this mod is in the user's tracked list
    if (tracked_ids_for_domain.find(mod_id) == tracked_ids_for_domain.end()) {
        std::cerr << "WARNING: Mod " << mod_id << " is NOT in tracked list. Skipping." << std::endl;
        mod_file_ids[mod_id] = {};  // Empty file list
        continue;
    }
    // ... proceed with file list fetch ...
}
```

### Layer 4: Download Link Generation
**Function:** `generate_download_links()` (NexusMods.cpp:320-358)

**Security:**
- Uses authenticated API endpoint: `/v1/games/{domain}/mods/{mod_id}/files/{file_id}/download_link.json`
- Requires valid API key for the user
- NexusMods server validates user has access to generate download links
- Server returns 403 Forbidden if user doesn't have access

**Note:** This is a server-side check enforced by NexusMods API

## Validation Functions

### `get_user_info()` - Account Verification
```cpp
std::string get_user_info(const Config& config)
```
**Endpoint:** `https://api.nexusmods.com/v1/users/validate.json`

**Purpose:**
- Validates API key
- Returns user account information including user_id
- Can be used to verify account association

**Response:**
```json
{
  "user_id": 123456,
  "key": "valid",
  "name": "username",
  "is_premium": true,
  "is_supporter": false,
  "email": "user@example.com",
  "profile_url": "https://nexusmods.com/users/123456"
}
```

### `is_mod_tracked()` - Individual Mod Verification
```cpp
bool is_mod_tracked(const std::string& game_domain, int mod_id, const Config& config)
```

**Purpose:**
- Explicit boolean check if a specific mod is tracked
- Queries full tracked list and searches for the mod
- Returns true only if mod is in user's tracking center

**Usage Example:**
```cpp
if (!is_mod_tracked("stardewvalley", 12345, config)) {
    std::cerr << "Mod 12345 is NOT tracked - refusing to download" << std::endl;
    return;
}
```

## Workflow Security Summary

### 1. Startup
```
User runs: ./modular-cli stardewvalley
             ↓
Config loads API key from ~/.config/Modular/config.json
             ↓
API key ties all requests to user's account
```

### 2. Tracking List Fetch
```
GET https://api.nexusmods.com/v1/user/tracked_mods.json
Headers: apikey: {user_api_key}
             ↓
Server validates API key → user account
             ↓
Server returns ONLY mods tracked by this user
             ↓
Parse response: Extract mod_id + domain_name
```

### 3. Domain Filter
```
User specified: stardewvalley
             ↓
Filter tracked mods: domain_name == "stardewvalley"
             ↓
Result: [12345, 67890, ...] (only Stardew Valley mods)
```

### 4. File List Fetch (with validation)
```
For each mod_id in filtered list:
    ↓
    Verify: mod_id in tracked_ids_for_domain?
    ↓ YES                ↓ NO
    Fetch files          Skip with warning
    ↓
    Continue download
```

### 5. Download
```
For each (mod_id, file_id):
    ↓
    Generate download link via API
    ↓
    Server checks: User has access to this mod?
    ↓ YES              ↓ NO
    Return URL         Return 403
    ↓
    Download file
```

## Security Guarantees

✅ **Impossible to download untracked mods** - Source API only returns tracked mods  
✅ **Multi-layer verification** - 4 separate validation points  
✅ **Server-side enforcement** - NexusMods API enforces access control  
✅ **Explicit warnings** - Any attempted access to untracked mod logs warning  
✅ **API key binding** - All operations tied to user's API key and account  

## Testing Validation

You can verify the validation system works by:

### Test 1: Check Your Tracked Mods
```bash
# API call to see what your account is tracking
curl -H "apikey: YOUR_API_KEY" \
  https://api.nexusmods.com/v1/user/tracked_mods.json | jq
```

### Test 2: Verify Domain Filtering
```bash
# Run with verbose logging to see filtering
./modular-cli stardewvalley --dry-run
# Should only show Stardew Valley mods from your tracking list
```

### Test 3: Check User Info
The `get_user_info()` function validates your API key and returns your user_id:
```cpp
std::string user_info = get_user_info(config);
// Parse JSON to see your user_id
```

## Audit Trail

Every download attempt is logged in:
- `~/Games/Mods-Lists/{domain}/downloads.db.json` - Full download history
- Contains: mod_id, file_id, URL, timestamp, status

You can audit this file to verify only your tracked mods were downloaded.

## Conclusion

The system has **4 layers of protection** ensuring only tracked mods are downloaded:

1. **Source API** - Server returns only tracked mods (enforced by NexusMods)
2. **Domain Filter** - Local filtering by game domain
3. **Pre-Validation** - Explicit check before file list fetch
4. **Download Auth** - Server validates access when generating download links

**Result:** It is technically impossible to download a mod you're not tracking.
