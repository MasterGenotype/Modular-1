# Prioritized Downloads Implementation

## Overview
Implemented web-validated prioritized downloading for NexusMods. The system now downloads mods in priority order based on web scraper validation, skipping API-only mods that aren't validated by the web tracking center.

## Download Priority

When `validate_tracking` is enabled in config:

1. **Priority 1 (Matched)**: Mods present in BOTH API tracked list AND web tracking center
2. **Priority 2 (Web-only)**: Mods only in web tracking center
3. **Skipped**: Mods only in API that aren't validated by web scraper

## Example (Stardew Valley)

```
API tracked mods: 79
Web scraped mods: 195
Matched mods: 46
Web-only mods: 149
API-only mods: 33 (SKIPPED)

Total downloaded: 195 mods (46 + 149)
```

## Implementation Details

### Changes Made

1. **TrackingValidator.h**:
   - Added `matched_mod_ids` field to `ValidationResult` to store mod IDs present in both sources

2. **TrackingValidator.cpp**:
   - Store matched mod IDs in validation result

3. **main.cpp**:
   - Added validation result caching per domain to avoid duplicate web scraping
   - Modified Pass 1 (Scanning) to count files only for validated mods
   - Modified Pass 2 (Downloading) to download validated mods in priority order
   - Skip API-only mods with informational message

### Performance Optimization

- Validation results are cached per domain to avoid scraping the web tracking center twice
- Web scraping happens once during the scanning pass
- Download pass reuses cached validation results

### Behavior

**When validation is enabled** (`validate_tracking: true`):
- Downloads 195 mods (matched + web-only)
- Skips 33 API-only mods
- Logs: `[INFO] Skipping 33 API-only mods (not validated by web scraper)`

**When validation is disabled** (`validate_tracking: false`):
- Falls back to original behavior
- Downloads all API tracked mods (79 mods)

## False Positive Detection

The web scraper correctly identifies false positives:
- Game ID 1303 was extracted by regex `/mods/(\d+)` from HTML
- System correctly identifies it as NOT in tracked list and skips it
- Log shows: `WARNING: Mod 1303 is NOT in tracked list. Skipping.`

## Configuration

No new config options needed. Uses existing:
```json
{
  "validate_tracking": true,
  "cookie_file": "~/Documents/cookies.txt"
}
```

## Benefits

1. **More accurate mod list**: Uses web tracking center (195 mods) as the authoritative source
2. **Prioritized downloads**: Matched mods downloaded first for reliability
3. **Automatic filtering**: Skips API-only mods that may be stale or incorrectly tracked
4. **Performance**: Single web scrape per domain per run via caching
