# Archived Documentation

This directory contains historical documentation that is no longer actively maintained but preserved for reference. These files were written for the original C++ codebase before the migration to C#/.NET 8.0.

## C++ to C# Migration (Completed)

Task lists and implementation notes from the migration:

- `CSHARP_MIGRATION_INSTRUCTIONS.md` - Migration instructions
- `WEEK1_TASKLIST.md` through `WEEK7_TASKLIST.md` - Weekly task lists for each migration phase
- `WEEK1_IMPLEMENTATION.md`, `WEEK2_IMPLEMENTATION.md` - Implementation notes

## C++ Era Documentation

These files reference the original C++ codebase (`.cpp` files, CURL, CMake, etc.) and do not apply to the current C# project:

- `IMPROVEMENTS_CPP.md` - Improvement instructions for the C++ version
- `TASKS_CPP.md` - Task list for the C++ version
- `NEXUSMODS_DISCREPANCIES_CPP.md` - NexusMods API discrepancy analysis (references `NexusMods.cpp`)
- `RATE_LIMITING_FIXES_SUMMARY_CPP.md` - Rate limiting fixes (references `src/core/NexusMods.cpp`)
- `PRIORITIZED_DOWNLOADS_CPP.md` - Prioritized downloads (references C++ `TrackingValidator`)
- `TEST_RESULTS_CPP.md` - Test results (references `g++` compilation)
- `NEXUSMODS_TRACKING_ANALYSIS_CPP.md` - Tracking analysis (references `NexusMods.cpp`)
- `MODULAR_INTEGRATION_COMPARISON_CPP.md` - Fluent HTTP client comparison (C++ vs C# integration plan)

## Superseded Documentation

- `TRACKING_VALIDATION_SUMMARY.md` - Superseded by [`../TRACKING_VALIDATION.md`](../TRACKING_VALIDATION.md)

## Note

For current documentation, see the parent [`docs/`](../) directory or the project [README](../../README.md).
