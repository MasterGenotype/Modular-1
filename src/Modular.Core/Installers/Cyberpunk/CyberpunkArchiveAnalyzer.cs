using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.Cyberpunk;

/// <summary>
/// Static analyzer that examines archive entry paths and file extensions to
/// determine which <see cref="CyberpunkInstallType"/>s are present and how
/// each file should be routed to the game directory.
///
/// The analysis is purely structural — it reads nothing from disk beyond the
/// archive's table of contents. This keeps detection fast and side-effect free.
/// </summary>
public static class CyberpunkArchiveAnalyzer
{
    // ── Well-known directory prefixes (case-insensitive matching) ─────────

    private static readonly string[] Red4ExtPluginPrefixes =
        { "red4ext/plugins/", "red4ext\\plugins\\" };

    private static readonly string[] CetModPrefixes =
        { "bin/x64/plugins/cyber_engine_tweaks/mods/", "bin\\x64\\plugins\\cyber_engine_tweaks\\mods\\" };

    private static readonly string[] RedscriptPrefixes =
        { "r6/scripts/", "r6\\scripts\\" };

    private static readonly string[] LegacyArchivePrefixes =
        { "archive/pc/mod/", "archive\\pc\\mod\\" };

    private static readonly string[] RedModPrefixes =
        { "mods/", "mods\\" };

    private static readonly string[] TweakPrefixes =
        { "r6/tweaks/", "r6\\tweaks\\" };

    private static readonly string[] IniConfigPrefixes =
        { "engine/config/platform/pc/", "engine\\config\\platform\\pc\\",
          "engine/config/", "engine\\config\\" };

    private static readonly string[] InputMappingPrefixes =
        { "r6/input/", "r6\\input\\" };

    private static readonly string[] AsiPluginPrefixes =
        { "bin/x64/plugins/", "bin\\x64\\plugins\\" };

    /// <summary>
    /// Top-level folders of the game directory. A file under one of them already says
    /// where it goes, so no extension rule may move it.
    /// </summary>
    private static readonly HashSet<string> GameRootFolders =
        new(StringComparer.OrdinalIgnoreCase) { "archive", "bin", "engine", "mods", "r6", "red4ext", "tools" };

    /// <summary>Subfolders of a REDmod next to its <c>info.json</c>.</summary>
    private static readonly string[] RedModContentFolders =
        { "archives/", "customsounds/", "scripts/", "tweaks/" };

    private const string CetModsRoot = "bin/x64/plugins/cyber_engine_tweaks/mods/";

    /// <summary>
    /// Where ACU (Appearance Change Unlocker) reads character presets from, one folder
    /// per body: <c>female/</c> and <c>male/</c>.
    /// </summary>
    public const string AcuPresetsRoot = CetModsRoot + "AppearanceChangeUnlocker/character-presets/";

    // ── Framework root markers ───────────────────────────────────────────

    /// <summary>Files that indicate a framework/engine-level install.</summary>
    private static readonly (string path, string frameworkId, bool critical)[] FrameworkMarkers =
    {
        // RED4ext loader
        ("bin/x64/winmm.dll", "red4ext", true),
        ("bin\\x64\\winmm.dll", "red4ext", true),

        // CET core (current releases load through version.dll, older ones through d3d11.dll)
        ("bin/x64/plugins/cyber_engine_tweaks.asi", "cet", true),
        ("bin\\x64\\plugins\\cyber_engine_tweaks.asi", "cet", true),
        ("bin/x64/version.dll", "cet", true),
        ("bin\\x64\\version.dll", "cet", true),
        ("bin/x64/d3d11.dll", "cet", true),
        ("bin\\x64\\d3d11.dll", "cet", true),

        // redscript compiler
        ("engine/tools/scc.exe", "redscript", true),
        ("engine\\tools\\scc.exe", "redscript", true),
        ("engine/config/base/scripts.ini", "redscript", false),
        ("engine\\config\\base\\scripts.ini", "redscript", false),
    };

    /// <summary>
    /// Analyze the given archive entries and produce a <see cref="CyberpunkInstallLayout"/>
    /// describing every detected installation type and per-file routing.
    /// </summary>
    /// <param name="entries">Archive entries (from <see cref="IArchiveReader.Entries"/>).</param>
    /// <param name="modName">
    /// Name to give a mod folder the archive does not name itself, such as a CET mod whose
    /// <c>init.lua</c> sits at the archive root. Usually the archive's file name.
    /// </param>
    /// <param name="isCyberpunk">
    /// Whether the archive is known to be installed into Cyberpunk 2077. Loose layouts —
    /// a bare <c>.archive</c>, a folder with an <c>init.lua</c>, a plugin DLL — are only
    /// routed when this is set or the archive holds a path unique to Cyberpunk, because
    /// on their own they could belong to any game.
    /// </param>
    public static CyberpunkInstallLayout Analyze(
        IReadOnlyList<ArchiveEntry> entries,
        string? modName = null,
        bool isCyberpunk = false)
    {
        var files = entries.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            return new CyberpunkInstallLayout
            {
                Types = CyberpunkInstallType.Unknown,
                Confidence = 0,
                Reason = "Archive is empty"
            };
        }

        // Normalize all paths to forward-slash for uniform matching
        var normalized = files
            .Select(e => (Entry: e, Normalized: e.FullName.Replace('\\', '/')))
            .ToList();

        // Detect and strip a common wrapper prefix.
        // Many CP2077 mod archives wrap everything inside a single top-level folder
        // like "CyberEngineTweaks/" — we strip that to expose the real structure.
        var strippedPrefix = DetectStrippablePrefix(normalized.Select(n => n.Normalized).ToList());
        if (!string.IsNullOrEmpty(strippedPrefix))
        {
            normalized = normalized
                .Select(n => (n.Entry, Normalized: n.Normalized[strippedPrefix.Length..]))
                .ToList();
        }

        var wrapperName = strippedPrefix.Length > 0 ? strippedPrefix.TrimEnd('/') : null;
        var folderName = wrapperName ?? NullIfBlank(modName);

        // ── Phase 1: pre-scan for Cyberpunk-specific anchors ─────────────
        //
        // Extension-only signals (.ini, .reds, .archive, .asi, .exe, .xml,
        // .archive.xl) are ambiguous — many games use the same extensions.
        // We only allow those broad matches when the archive already contains
        // at least one structurally unambiguous Cyberpunk 2077 path (an
        // "anchor").  Anchors are directory prefixes that are unique to
        // Cyberpunk's modding layout:
        //   r6/*, red4ext/*, archive/pc/mod/*, bin/x64/plugins/cyber_engine_tweaks/*,
        //   engine/config/*, engine/tools/*, mods/*/info.json, known framework DLLs.
        // When the caller knows the game is Cyberpunk, no anchor is needed.

        var allPaths = normalized.Select(n => n.Normalized).ToList();
        var allLower = allPaths.Select(p => p.ToLowerInvariant()).ToList();
        bool hasCyberpunkAnchor = allLower.Any(p =>
            StartsWithAny(p, Red4ExtPluginPrefixes) ||
            StartsWithAny(p, CetModPrefixes) ||
            StartsWithAny(p, RedscriptPrefixes) ||
            StartsWithAny(p, LegacyArchivePrefixes) ||
            StartsWithAny(p, TweakPrefixes) ||
            StartsWithAny(p, InputMappingPrefixes) ||
            StartsWithAny(p, IniConfigPrefixes) ||
            p.EndsWith(".archive.xl") ||
            FrameworkMarkers.Any(m =>
                p == m.path.ToLowerInvariant() ||
                p.EndsWith("/" + m.path.ToLowerInvariant())) ||
            // REDmod: mods/{name}/info.json
            (StartsWithAny(p, RedModPrefixes) && HasRedModStructure(p, allPaths)));

        var lenient = hasCyberpunkAnchor || isCyberpunk;

        // ── Phase 2: find the mod folders of loose layouts ───────────────
        //
        // A file outside the game's own top-level folders carries no destination, so
        // it is routed by the mod folder it sits in: the nearest folder holding a
        // REDmod info.json, a CET init.lua, or a RED4ext plugin DLL. "" is the archive
        // root once any wrapper folder has been stripped.

        var loosePaths = allLower.Where(p => !IsUnderGameRootFolder(p)).ToList();
        var redModRoots = lenient ? FindRedModRoots(loosePaths) : new List<string>();
        var cetRoots = lenient
            ? FoldersContaining(loosePaths, p => FileName(p) == "init.lua")
            : new List<string>();

        // A loose .exe is a program, and the DLLs in its folder and below are its own:
        // a setup tool with a bundled runtime is not a RED4ext plugin. Nor is a folder of
        // Windows or .NET runtime DLLs, wherever it sits.
        var programRoots = FoldersContaining(loosePaths, p => p.EndsWith(".exe"));
        var red4ExtRoots = lenient
            ? FoldersContaining(loosePaths, p =>
                    p.EndsWith(".dll") &&
                    !IsRuntimeDll(FileName(p)) &&
                    FindRoot(p, programRoots) == null)
            : new List<string>();

        var rootDlls = loosePaths.Where(p => !p.Contains('/') && p.EndsWith(".dll")).ToList();
        var rootDllName = rootDlls.Count == 1
            ? Path.GetFileNameWithoutExtension(allPaths[allLower.IndexOf(rootDlls[0])])
            : null;

        // ── Phase 3: classify each entry ─────────────────────────────────

        var detectedTypes = CyberpunkInstallType.Unknown;
        var fileRoutes = new Dictionary<string, FileRoute>(StringComparer.OrdinalIgnoreCase);
        var frameworkHints = new List<FrameworkFileHint>();
        var signalConfidences = new List<double>();
        var skipped = new List<string>();

        void Route(ArchiveEntry entry, CyberpunkInstallType type, FileRoute route, double confidence)
        {
            detectedTypes |= type;
            fileRoutes[entry.FullName] = route;
            signalConfidences.Add(confidence);
        }

        foreach (var (entry, path) in normalized)
        {
            var lowerPath = path.ToLowerInvariant();

            // ── 1. Framework root markers (highest priority) ─────────
            var matchedFramework = false;
            foreach (var (marker, fwId, critical) in FrameworkMarkers)
            {
                if (lowerPath == marker.ToLowerInvariant() ||
                    lowerPath.EndsWith("/" + marker.ToLowerInvariant()))
                {
                    Route(entry, CyberpunkInstallType.FrameworkRoot,
                        FileRoute.Declared(marker, PackageTypes.Cp77Framework), 0.98);
                    frameworkHints.Add(new FrameworkFileHint
                    {
                        SourceEntry = entry.FullName,
                        DestinationRelative = marker,
                        FrameworkId = fwId,
                        IsCritical = critical
                    });
                    matchedFramework = true;
                    break;
                }
            }
            if (matchedFramework) continue;

            // ── 2. Loose files: routed by mod folder or extension ────
            if (!IsUnderGameRootFolder(lowerPath))
            {
                if (TryRouteLoose(entry, path, lowerPath) is { } loose)
                    Route(entry, loose.Type, loose.Route, loose.Confidence);
                else if (lenient)
                    // Nothing in Cyberpunk reads it from where it would land (a readme, an
                    // item code list, a modder's source file): left out, not dropped at
                    // the game root.
                    skipped.Add(entry.FullName);
                else
                    fileRoutes[entry.FullName] = FileRoute.Declared(path, PackageTypes.Unknown);
                continue;
            }

            // ── 3. RED4ext plugin ────────────────────────────────────
            if (StartsWithAny(lowerPath, Red4ExtPluginPrefixes))
            {
                Route(entry, CyberpunkInstallType.Red4ExtPlugin, FileRoute.Declared(
                    EnsurePrefix("red4ext/plugins/", path, Red4ExtPluginPrefixes),
                    PackageTypes.Cp77Red4ExtPlugin), 0.95);
                continue;
            }

            // ── 4. CET mod ──────────────────────────────────────────
            if (StartsWithAny(lowerPath, CetModPrefixes))
            {
                Route(entry, CyberpunkInstallType.CetMod, FileRoute.Declared(
                    EnsurePrefix(CetModsRoot, path, CetModPrefixes),
                    PackageTypes.Cp77CetMod), 0.95);
                continue;
            }

            // ── 5. redscript mod ─────────────────────────────────────
            if (StartsWithAny(lowerPath, RedscriptPrefixes))
            {
                Route(entry, CyberpunkInstallType.RedscriptMod, FileRoute.Declared(
                    EnsurePrefix("r6/scripts/", path, RedscriptPrefixes),
                    PackageTypes.Cp77Redscript), lowerPath.EndsWith(".reds") ? 0.95 : 0.80);
                continue;
            }

            // ── 6. Legacy archive ────────────────────────────────────
            if (StartsWithAny(lowerPath, LegacyArchivePrefixes))
            {
                Route(entry, CyberpunkInstallType.LegacyArchive, FileRoute.Declared(
                    EnsurePrefix("archive/pc/mod/", path, LegacyArchivePrefixes),
                    PackageTypes.Cp77LegacyArchive), 0.90);
                continue;
            }

            // ── 7. REDmod ────────────────────────────────────────────
            if (StartsWithAny(lowerPath, RedModPrefixes) && HasRedModStructure(lowerPath, allPaths))
            {
                Route(entry, CyberpunkInstallType.RedMod,
                    FileRoute.FromMetadata(path, PackageTypes.Cp77RedMod), 0.90);
                continue;
            }

            // ── 8. Tweak mod ─────────────────────────────────────────
            if (StartsWithAny(lowerPath, TweakPrefixes))
            {
                Route(entry, CyberpunkInstallType.TweakMod, FileRoute.Declared(
                    EnsurePrefix("r6/tweaks/", path, TweakPrefixes),
                    PackageTypes.Cp77Tweak), 0.85);
                continue;
            }

            // ── 9. ini/config tweak ──────────────────────────────────
            if (StartsWithAny(lowerPath, IniConfigPrefixes))
            {
                Route(entry, CyberpunkInstallType.IniTweak,
                    FileRoute.Declared(path, PackageTypes.Cp77IniTweak), 0.85);
                continue;
            }

            // ── 10. Input mapping XML ────────────────────────────────
            if (StartsWithAny(lowerPath, InputMappingPrefixes))
            {
                Route(entry, CyberpunkInstallType.InputMapping, FileRoute.Declared(
                    EnsurePrefix("r6/input/", path, InputMappingPrefixes),
                    PackageTypes.Cp77InputMapping), 0.85);
                continue;
            }

            // ── 11. ASI plugin ───────────────────────────────────────
            if (lowerPath.EndsWith(".asi") && StartsWithAny(lowerPath, AsiPluginPrefixes))
            {
                Route(entry, CyberpunkInstallType.AsiPlugin, FileRoute.Declared(
                    EnsurePrefix("bin/x64/plugins/", path, AsiPluginPrefixes),
                    PackageTypes.Cp77AsiPlugin), 0.90);
                continue;
            }

            // ── Fallback: keep the path as-is, route to game root ────
            fileRoutes[entry.FullName] = FileRoute.Declared(path, PackageTypes.Unknown);
        }

        // ── Confidence calculation ───────────────────────────────────────
        double confidence;
        if (signalConfidences.Count == 0)
        {
            confidence = 0;
            detectedTypes = CyberpunkInstallType.Unknown;
        }
        else
        {
            // Base = highest individual signal; boost for multiple corroborating types
            confidence = signalConfidences.Max();
            var distinctTypes = CountSetBits((int)detectedTypes);
            if (distinctTypes > 1)
                confidence = Math.Min(1.0, confidence + 0.02 * (distinctTypes - 1));
        }

        // ── Build reason string ──────────────────────────────────────────
        var typeNames = Enum.GetValues<CyberpunkInstallType>()
            .Where(t => t != CyberpunkInstallType.Unknown && detectedTypes.HasFlag(t))
            .Select(t => t.ToString())
            .ToList();

        var reason = typeNames.Count switch
        {
            0 => "No recognized Cyberpunk 2077 mod structure detected",
            1 => $"Detected as {typeNames[0]}",
            _ => $"Multi-path mod: {string.Join(" + ", typeNames)}"
        };

        // Nothing but a program and its own files: a tool to run, not a mod to install.
        // Copied into the game it would load nothing, and its DLLs could be mistaken for
        // the game's.
        string? rejection = null;
        if (detectedTypes == CyberpunkInstallType.StandaloneExe)
        {
            // Shallowest first, so the program itself is named before its helpers.
            var programs = allPaths.Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Count(c => c == '/'))
                .Select(Path.GetFileName)
                .ToList();
            var named = string.Join(", ", programs.Take(2)) +
                        (programs.Count > 2 ? $" and {programs.Count - 2} more" : string.Empty);
            rejection =
                $"This archive is a Windows program ({named}), not a mod: " +
                "it holds nothing the game loads. Run it with Proton or Wine if it is an installer, " +
                "or download the mod's own files instead.";
            reason = "Standalone Windows program, not a mod";
        }
        else if (lenient && detectedTypes == CyberpunkInstallType.Unknown &&
                 !allLower.Any(IsUnderGameRootFolder))
        {
            // Nothing the game loads, and nothing laid out from the game folder: modders'
            // resources (WolvenKit sources, templates, reference images) or documentation.
            // Copied into the game they would only clutter it.
            rejection =
                "This archive holds nothing Cyberpunk 2077 loads: no .archive, script, " +
                "plugin or config file, nor anything laid out from the game folder. It looks " +
                "like a resource for mod authors (source files, templates, reference images); " +
                "keep it in the library and open it from there.";
            reason = "Nothing the game loads";
        }

        return new CyberpunkInstallLayout
        {
            Types = detectedTypes,
            Confidence = confidence,
            Reason = reason,
            FileRoutes = fileRoutes,
            StrippedPrefix = strippedPrefix,
            FrameworkHints = frameworkHints,
            Rejection = rejection,
            SkippedFiles = rejection == null ? skipped : []
        };

        // Routes a file that is not under any of the game's top-level folders, or
        // returns null to leave it at the game root as-is.
        (CyberpunkInstallType Type, FileRoute Route, double Confidence)? TryRouteLoose(
            ArchiveEntry entry, string path, string lowerPath)
        {
            // Beside a program, files are the program's own: its runtime, and templates it
            // writes mods from (Lethal Curves' rig builder carries .xl and .yaml files).
            // Routed by extension they would install into the game as if they were a mod.
            if (FindRoot(lowerPath, programRoots) != null)
            {
                return lenient && lowerPath.EndsWith(".exe")
                    ? (CyberpunkInstallType.StandaloneExe, FileRoute.Declared(path), 0.70)
                    : null;
            }

            // .archive.xl is Cyberpunk-specific (ArchiveXL format) — always accepted.
            if (lowerPath.EndsWith(".archive.xl") && FindRoot(lowerPath, redModRoots) == null)
            {
                return (CyberpunkInstallType.LegacyArchive, FileRoute.Heuristic(
                    "archive/pc/mod/" + Path.GetFileName(path), PackageTypes.Cp77LegacyArchive), 0.90);
            }

            if (!lenient)
                return null;

            // A REDmod folder: info.json beside archives/, scripts/, tweaks/ or customSounds/.
            if (FindRoot(lowerPath, redModRoots) is { } redModRoot &&
                RootFolderName(path, redModRoot, folderName) is { } redModName)
            {
                return (CyberpunkInstallType.RedMod, FileRoute.FromMetadata(
                    $"mods/{redModName}/{RelativeTo(path, redModRoot)}", PackageTypes.Cp77RedMod), 0.85);
            }

            // A CET mod folder: anything beside an init.lua.
            if (FindRoot(lowerPath, cetRoots) is { } cetRoot &&
                RootFolderName(path, cetRoot, folderName) is { } cetName)
            {
                return (CyberpunkInstallType.CetMod, FileRoute.Heuristic(
                    $"{CetModsRoot}{cetName}/{RelativeTo(path, cetRoot)}", PackageTypes.Cp77CetMod), 0.85);
            }

            // A character preset, which ACU reads from its own folder. Which body it is for
            // is not in the file; the installer asks, female to begin with.
            if (lowerPath.EndsWith(".preset"))
            {
                return (CyberpunkInstallType.CetMod, FileRoute.Heuristic(
                    AcuPresetsRoot + "female/" + Path.GetFileName(path), PackageTypes.Cp77CetMod), 0.80);
            }

            // .archive and .xl: the game and ArchiveXL only read archive/pc/mod directly.
            if (lowerPath.EndsWith(".archive") || lowerPath.EndsWith(".xl"))
            {
                return (CyberpunkInstallType.LegacyArchive, FileRoute.Heuristic(
                    "archive/pc/mod/" + Path.GetFileName(path), PackageTypes.Cp77LegacyArchive), 0.85);
            }

            if (lowerPath.EndsWith(".reds"))
            {
                return (CyberpunkInstallType.RedscriptMod, FileRoute.Heuristic(
                    "r6/scripts/" + InModFolder(path, folderName), PackageTypes.Cp77Redscript), 0.80);
            }

            if (lowerPath.EndsWith(".tweak") || lowerPath.EndsWith(".yaml") || lowerPath.EndsWith(".yml"))
            {
                return (CyberpunkInstallType.TweakMod, FileRoute.Heuristic(
                    "r6/tweaks/" + InModFolder(path, folderName), PackageTypes.Cp77Tweak), 0.80);
            }

            if (lowerPath.EndsWith(".ini"))
            {
                return (CyberpunkInstallType.IniTweak, FileRoute.Heuristic(
                    "engine/config/platform/pc/" + Path.GetFileName(path), PackageTypes.Cp77IniTweak), 0.70);
            }

            if (lowerPath.EndsWith(".xml") && lowerPath.Contains("input"))
            {
                return (CyberpunkInstallType.InputMapping, FileRoute.Heuristic(
                    "r6/input/" + Path.GetFileName(path), PackageTypes.Cp77InputMapping), 0.70);
            }

            if (lowerPath.EndsWith(".asi"))
            {
                return (CyberpunkInstallType.AsiPlugin, FileRoute.Heuristic(
                    "bin/x64/plugins/" + Path.GetFileName(path), PackageTypes.Cp77AsiPlugin), 0.75);
            }

            // A RED4ext plugin folder: anything beside a plugin DLL. At the archive root
            // the DLL's own name is a better folder name than the archive's.
            if (FindRoot(lowerPath, red4ExtRoots) is { } pluginRoot &&
                RootFolderName(path, pluginRoot, wrapperName ?? rootDllName ?? folderName) is { } pluginName)
            {
                return (CyberpunkInstallType.Red4ExtPlugin, FileRoute.Heuristic(
                    $"red4ext/plugins/{pluginName}/{RelativeTo(path, pluginRoot)}",
                    PackageTypes.Cp77Red4ExtPlugin), 0.75);
            }

            // Standalone tools are extracted as-is.
            if (lowerPath.EndsWith(".exe"))
                return (CyberpunkInstallType.StandaloneExe, FileRoute.Declared(path), 0.70);

            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Detect a single top-level wrapper folder that contains all archive entries.
    /// For example, if every file starts with "MyMod/", we strip "MyMod/" so the
    /// remaining paths can be matched against known directory structures.
    /// </summary>
    private static string DetectStrippablePrefix(List<string> paths)
    {
        if (paths.Count == 0) return string.Empty;

        // Split on / and check if all entries share a common first segment
        var firstSegments = paths
            .Select(p => p.Split('/').FirstOrDefault() ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (firstSegments.Count != 1)
            return string.Empty;

        var candidate = firstSegments[0] + "/";

        // Only strip if the segment is NOT a well-known game directory
        var wellKnown = new[]
        {
            "bin", "engine", "r6", "red4ext", "archive", "mods",
            "BepInEx", "fomod"
        };

        if (wellKnown.Any(wk => wk.Equals(firstSegments[0], StringComparison.OrdinalIgnoreCase)))
            return string.Empty;

        // Confirm all entries start with this prefix
        if (paths.All(p => p.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            return candidate;

        return string.Empty;
    }

    /// <summary>
    /// Check if a path within the mods/ directory has a valid REDmod structure:
    /// must contain info.json sibling at the mod root level.
    /// </summary>
    private static bool HasRedModStructure(string lowerPath, List<string> allPaths)
    {
        // Extract the mod folder name under mods/ (e.g. "mods/MyMod/...")
        var afterMods = lowerPath;
        foreach (var prefix in RedModPrefixes)
        {
            if (lowerPath.StartsWith(prefix.ToLowerInvariant()))
            {
                afterMods = lowerPath[prefix.Length..];
                break;
            }
        }

        var parts = afterMods.Split('/');
        if (parts.Length < 2)
            return false;

        var modFolder = parts[0];
        var expectedInfoJson = $"mods/{modFolder}/info.json";

        return allPaths.Any(p =>
            p.Replace('\\', '/').Equals(expectedInfoJson, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a path lies under one of the game directory's own top-level folders.
    /// </summary>
    private static bool IsUnderGameRootFolder(string path)
    {
        var slash = path.IndexOf('/');
        if (slash <= 0 || !GameRootFolders.Contains(path[..slash]))
            return false;

        // The game reads archive/pc/ only. "archive/base/..." is a WolvenKit project's
        // unpacked source, which a modders' resource ships and the game never loads.
        return !path.StartsWith("archive/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("archive/pc/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Folders, outside the game's top-level folders, that hold a REDmod: an
    /// <c>info.json</c> with at least one REDmod content folder beside it.
    /// </summary>
    /// <param name="loosePaths">Lower-case paths not under a game root folder.</param>
    private static List<string> FindRedModRoots(List<string> loosePaths) =>
        FoldersContaining(loosePaths, p => FileName(p) == "info.json")
            .Where(root =>
            {
                var prefix = root.Length == 0 ? string.Empty : root + "/";
                return loosePaths.Any(p =>
                    p.StartsWith(prefix) &&
                    RedModContentFolders.Any(folder => p[prefix.Length..].StartsWith(folder)));
            })
            .ToList();

    /// <summary>
    /// The distinct folders holding a file that matches <paramref name="isMarker"/>.
    /// </summary>
    private static List<string> FoldersContaining(List<string> paths, Func<string, bool> isMarker) =>
        paths
            .Where(isMarker)
            .Select(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : string.Empty)
            .Distinct()
            .ToList();

    /// <summary>
    /// The deepest of <paramref name="roots"/> that contains <paramref name="lowerPath"/>,
    /// or null when none does.
    /// </summary>
    private static string? FindRoot(string lowerPath, List<string> roots) =>
        roots
            .Where(root => root.Length == 0 || lowerPath.StartsWith(root + "/"))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

    /// <summary>
    /// The name of a mod folder: its own name, or for the archive root the
    /// <paramref name="fallback"/>. Null when the root has no name to give.
    /// </summary>
    /// <param name="path">Original-case path of a file under the root.</param>
    /// <param name="root">Lower-case root folder, "" for the archive root.</param>
    /// <param name="fallback">Name for the archive root.</param>
    private static string? RootFolderName(string path, string root, string? fallback)
    {
        if (root.Length == 0)
            return fallback;

        var original = path[..root.Length];
        return original.Contains('/') ? original[(original.LastIndexOf('/') + 1)..] : original;
    }

    /// <summary>The part of <paramref name="path"/> below <paramref name="root"/>.</summary>
    private static string RelativeTo(string path, string root) =>
        root.Length == 0 ? path : path[(root.Length + 1)..];

    /// <summary>
    /// A file at the archive root gets its own folder, so its mod's files stay together;
    /// a file already in a folder keeps it.
    /// </summary>
    private static string InModFolder(string path, string? folderName) =>
        path.Contains('/') || folderName == null ? path : $"{folderName}/{path}";

    private static string FileName(string path) =>
        path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    /// <summary>
    /// Whether a lower-case DLL name belongs to the Windows C runtime, the Visual C++
    /// runtime or .NET, which programs bundle and no RED4ext plugin is named after.
    /// </summary>
    private static bool IsRuntimeDll(string fileName) =>
        fileName.StartsWith("api-ms-win-") ||
        fileName.StartsWith("system.") ||
        fileName.StartsWith("microsoft.") ||
        fileName.StartsWith("vcruntime") ||
        fileName.StartsWith("msvcp") ||
        fileName is "ucrtbase.dll" or "concrt140.dll" or "hostfxr.dll" or "hostpolicy.dll" or
            "coreclr.dll" or "clrjit.dll" or "mscorlib.dll" or "netstandard.dll" or
            "mscordaccore.dll" or "mscordbi.dll" or "windowsbase.dll";

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool StartsWithAny(string path, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Ensure a file path carries the expected canonical prefix.
    /// If it already does, return as-is. Otherwise prepend it.
    /// </summary>
    private static string EnsurePrefix(string canonicalPrefix, string path, string[] knownPrefixes)
    {
        var lower = path.ToLowerInvariant();
        foreach (var prefix in knownPrefixes)
        {
            if (lower.StartsWith(prefix.ToLowerInvariant()))
                return canonicalPrefix + path[prefix.Length..];
        }
        return canonicalPrefix + path;
    }

    private static int CountSetBits(int value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }
}
