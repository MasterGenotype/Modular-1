Dependency-Aware Steam Game Mod Installer AI Agent Prompt

Objective:
Build a robust, dependency-aware system for installing game mods to Steam-based game installations on Linux.

Reference:
Use the research documents within the docs/ directory for
"Dependency-Sorted File Operations Across Multiple Archives Using Database Record Abstractions and Staging" "~/.gitrepos/Modular-1/docs/dependency_sorted_file_operations.md"
"Building a C# Steam Game Catalog Constructor with Engine Detection and Database Storage" "~/.gitrepos/Modular-1/docs/Steam-Client-Game-Operations.md"

Requirements:
1. Mod metadata management (database of mod info, dependencies, target files, checksum).
2. Dependency resolution (topological sorting / DAG; detect circular dependencies).
3. Staging area for installation (temporary directories for extraction and overlay before committing).
4. Atomic commit to game directory.
5. Rollback & logging (backup replaced files, log operations and conflicts).
6. Steam integration on Linux (detect libraries, handle permissions).
7. User interface (CLI/GUI for mod selection, dependency visualization, batch installs).
8. Optional enhancements (checksum validation, parallel installation, mod update/uninstall).

Deliverables:
- Modular Linux tool/script for dependency-aware Steam game mod installation.
- Documentation for database schema, staging/commit procedures, rollback, and Steam integration.

Instructions for AI Agent:
- Generate scaffolding code in Python or Linux shell.
- Include functions for database handling, staging, dependency resolution, atomic commit, and rollback.
- Provide example usage scenarios for installing Steam game mods.
