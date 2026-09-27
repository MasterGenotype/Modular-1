using Modular.Sdk.Archives;
using Modular.Sdk.Installers;

namespace Modular.Core.Installers.FF7Remake;

/// <summary>
/// Static analyzer that examines archive entry paths to determine which
/// <see cref="FF7RInstallType"/>s are present and how each file should be
/// routed to the game directory.
///
/// Detection uses a two-phase approach (borrowed from CyberpunkArchiveAnalyzer):
///   Phase 1 — Pre-scan for FF7R-specific anchors (End/Content/Paks, ~mods,
///             ff7remake_.exe references, End/Binaries/Win64, etc.).
///   Phase 2 — Classify each entry. Extension-only signals (.pak, .dll, .ini)
///             require an anchor to avoid mis-claiming archives from other games.
///
/// The analysis is purely structural — no files are extracted.
/// </summary>
public static class FF7RArchiveAnalyzer
{
    // ── Well-known directory prefixes (forward-slash normalized) ──────────

    private static readonly string[] PaksModsPrefixes =
        { "end/content/paks/~mods/" };

    /// <summary>
    /// Game-root-relative destination prefix for every pak mod.
    /// </summary>
    /// <remarks>
    /// Paks are routed from the game root rather than from <c>~mods</c> so that the
    /// destination does not depend on the archive's shape. Nothing downstream can
    /// recover the intent afterwards: for a multi-type plan the offset between the game
    /// root and the plan's target directory is empty, so a <c>~mods</c>-relative route
    /// is indistinguishable from a game-root one.
    /// </remarks>
    public const string PaksModsRoute = "End/Content/Paks/~mods/";

    private static readonly string[] PaksRootPrefixes =
        { "end/content/paks/" };

    private static readonly string[] BinariesPrefixes =
        { "end/binaries/win64/" };

    private static readonly string[] MigotoModsPrefixes =
        { "end/binaries/win64/mods/", "mods/" };

    // ── Anchor markers: files/paths unique to FF7R ──────────────────────

    /// <summary>
    /// Paths that are structurally unique to FF7R Intergrade and prove
    /// this archive targets FF7R rather than a generic UE4 game.
    /// </summary>
    private static readonly string[] FF7RAnchorPaths =
    {
        "end/content/paks/",
        "end/binaries/win64/",
        // FF7R-specific executable (note the underscore)
        "ff7remake_.exe",
    };

    /// <summary>
    /// Filename fragments that strongly suggest FF7R context when found
    /// alongside .pak files. Case-insensitive matching.
    /// </summary>
    /// <remarks>
    /// The bare fragment <c>"ff7"</c> matches <c>ff7rebirth_</c> as well as any Remake mod,
    /// and the structural anchors do not discriminate either, because Rebirth ships the same
    /// <c>End/Content/Paks/</c> and <c>End/Binaries/Win64/</c> layout. Rather than dropping
    /// the fragment — which would stop a legitimately named mod such as
    /// <c>CoolFF7Mod_P.pak</c> being recognised at all — Rebirth is excluded explicitly by
    /// <see cref="RebirthMarkers"/>. Module-scoped installer candidates normally keep the two
    /// games apart; this is defence in depth for the case where the analyzer is reached
    /// outside that scoping.
    /// </remarks>
    private static readonly string[] FF7RFilenameHints =
    {
        "ff7", "ffvii", "finalfantasy7", "finalfantasyvii",
        "remake", "intergrade", "midgar"
    };

    /// <summary>
    /// Fragments that prove an archive targets FF7 Rebirth, not Remake.
    /// </summary>
    private static readonly string[] RebirthMarkers =
    {
        "ff7rebirth", "rebirth_", "zgameinstanceloader", "end/mods/"
    };

    // ── 3DMigoto framework markers ──────────────────────────────────────

    private static readonly string[] MigotoFrameworkFiles =
    {
        "d3dx.ini",
        "shaderfixes/",
        "shaderfixes\\",
    };

    // ── DXVK markers ────────────────────────────────────────────────────

    private static readonly string[] DxvkMarkers =
    {
        "dxvk.conf",
        "dxvk-async",
    };

    /// <summary>
    /// Analyze the given archive entries and produce an <see cref="FF7RInstallLayout"/>.
    /// </summary>
    public static FF7RInstallLayout Analyze(IReadOnlyList<ArchiveEntry> entries)
    {
        var files = entries.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            return new FF7RInstallLayout
            {
                Types = FF7RInstallType.Unknown,
                Confidence = 0,
                Reason = "Archive is empty"
            };
        }

        // Normalize all paths to forward-slash lowercase for matching
        var normalized = files
            .Select(e => (Entry: e, Normalized: e.FullName.Replace('\\', '/')))
            .ToList();

        // Strip wrapper prefix if all entries share a single non-structural root
        var strippedPrefix = DetectStrippablePrefix(
            normalized.Select(n => n.Normalized).ToList());
        if (!string.IsNullOrEmpty(strippedPrefix))
        {
            normalized = normalized
                .Select(n => (n.Entry, Normalized: n.Normalized[strippedPrefix.Length..]))
                .ToList();
        }

        // ── Phase 1: Anchor detection ───────────────────────────────────
        var allLower = normalized.Select(n => n.Normalized.ToLowerInvariant()).ToList();

        // A Rebirth archive must never raise the Remake anchor: the two share the End/
        // layout, so without this the structural anchors alone would claim it.
        bool looksLikeRebirth = allLower.Any(p =>
            RebirthMarkers.Any(m => p.Contains(m, StringComparison.Ordinal)));

        bool hasFF7RAnchor =
            !looksLikeRebirth &&
            (
            // Structural anchors — paths unique to FF7R
            allLower.Any(p => FF7RAnchorPaths.Any(a =>
                p.Contains(a, StringComparison.Ordinal))) ||
            // ~mods folder (FF7R convention, also used by other UE4 games)
            allLower.Any(p => p.Contains("~mods/") || p.Contains("~mods\\")) ||
            // FF7R-specific filename fragments in any entry
            allLower.Any(p => FF7RFilenameHints.Any(h =>
                p.Contains(h, StringComparison.Ordinal))) ||
            // 3DMigoto markers (FF7R has one of the largest 3DMigoto modding scenes)
            allLower.Any(p => MigotoFrameworkFiles.Any(m =>
                p.Contains(m, StringComparison.Ordinal))) ||
            // DXVK markers
            allLower.Any(p => DxvkMarkers.Any(d =>
                p.Contains(d, StringComparison.Ordinal))));

        // ── Phase 2: Classify each entry ─────────────────────────────────

        var detectedTypes = FF7RInstallType.Unknown;
        var fileRoutes = new Dictionary<string, FileRoute>(StringComparer.OrdinalIgnoreCase);
        var signalConfidences = new List<double>();
        bool requiresDx11 = false;

        foreach (var (entry, path) in normalized)
        {
            var lower = path.ToLowerInvariant();

            // ── 1. Pak in ~mods (highest confidence pak signal) ──────
            if (ContainsAny(lower, PaksModsPrefixes))
            {
                detectedTypes |= FF7RInstallType.PakMod;

                // Game-root-relative, always. Routing paks relative to ~mods only
                // worked while the plan itself targeted ~mods, which it does for a
                // pak-only archive and not for a multi-type one — so a pak shipped
                // alongside an INI or a hook DLL landed in the game root and never
                // mounted.
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    PaksModsRoute + StripToAfter(path, "~mods/"), PackageTypes.UePak);
                signalConfidences.Add(0.98);
                continue;
            }

            // ── 2. Pak in Content/Paks root (without ~mods) ──────────
            if (ContainsAny(lower, PaksRootPrefixes) &&
                !lower.Contains("~mods") &&
                lower.EndsWith(".pak"))
            {
                detectedTypes |= FF7RInstallType.PakRoot;
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    "End/Content/Paks/" + Path.GetFileName(path), PackageTypes.UePak);
                signalConfidences.Add(0.90);
                continue;
            }

            // ── 3. 3DMigoto framework files ──────────────────────────
            if (MigotoFrameworkFiles.Any(m => lower.Contains(m)) &&
                !lower.EndsWith(".pak"))
            {
                detectedTypes |= FF7RInstallType.ThreeDMigoto;
                requiresDx11 = true;
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    RouteToWin64(path, lower), PackageTypes.UeMigoto);
                signalConfidences.Add(0.95);
                continue;
            }

            // ── 4. 3DMigoto sub-mods (Win64/Mods/ directory) ─────────
            if (ContainsAny(lower, MigotoModsPrefixes) &&
                !lower.EndsWith(".pak"))
            {
                detectedTypes |= FF7RInstallType.ThreeDMigotoMod;
                requiresDx11 = true;
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    lower.Contains("win64/mods/")
                        ? "End/Binaries/Win64/Mods/" + StripToAfter(path, "Mods/")
                        : "End/Binaries/Win64/Mods/" + path,
                    PackageTypes.UeMigoto);
                signalConfidences.Add(0.90);
                continue;
            }

            // ── 5. DXVK wrapper ──────────────────────────────────────
            if (DxvkMarkers.Any(d => lower.Contains(d)))
            {
                detectedTypes |= FF7RInstallType.DxvkWrapper;
                requiresDx11 = true;
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    RouteToWin64(path, lower), PackageTypes.Dxvk);
                signalConfidences.Add(0.92);
                continue;
            }

            // ── 6. DLL/ASI hook in Binaries path ─────────────────────
            if (ContainsAny(lower, BinariesPrefixes) &&
                (lower.EndsWith(".dll") || lower.EndsWith(".asi")))
            {
                detectedTypes |= FF7RInstallType.DllHook;
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    "End/Binaries/Win64/" + Path.GetFileName(path), PackageTypes.UeHook);
                signalConfidences.Add(0.95);
                continue;
            }

            // ── 7. INI/config files ──────────────────────────────────
            //    Path-anchored: files already in engine/config or similar
            if (lower.EndsWith(".ini") && ContainsAny(lower, BinariesPrefixes))
            {
                // INI next to EXE (e.g. d3dx.ini for 3DMigoto)
                detectedTypes |= FF7RInstallType.IniConfig;
                fileRoutes[entry.FullName] = FileRoute.Declared(
                    "End/Binaries/Win64/" + Path.GetFileName(path), PackageTypes.UeMigoto);
                signalConfidences.Add(0.85);
                continue;
            }

            // ── Extension-only signals (require FF7R anchor) ─────────

            // 8. Bare .pak file — most FF7R mods ship as flat .pak
            if (hasFF7RAnchor && lower.EndsWith(".pak"))
            {
                detectedTypes |= FF7RInstallType.PakMod;
                // Route to ~mods — the standard FF7R pak location — game-root-relative,
                // so the destination survives a multi-type archive.
                fileRoutes[entry.FullName] = FileRoute.Heuristic(
                    PaksModsRoute + Path.GetFileName(path), PackageTypes.UePak);
                signalConfidences.Add(0.88);
                continue;
            }

            // 9. Bare DLL — proxy DLL for hooking
            if (hasFF7RAnchor && lower.EndsWith(".dll") &&
                IsKnownProxyDll(lower))
            {
                detectedTypes |= FF7RInstallType.DllHook;
                fileRoutes[entry.FullName] = FileRoute.Heuristic(
                    "End/Binaries/Win64/" + Path.GetFileName(path), PackageTypes.UeHook);
                signalConfidences.Add(0.85);
                continue;
            }

            // 10. Bare .asi plugin
            if (hasFF7RAnchor && lower.EndsWith(".asi"))
            {
                detectedTypes |= FF7RInstallType.DllHook;
                fileRoutes[entry.FullName] = FileRoute.Heuristic(
                    "End/Binaries/Win64/" + Path.GetFileName(path), PackageTypes.UeHook);
                signalConfidences.Add(0.85);
                continue;
            }

            // 11. Bare .ini — standalone config file
            if (hasFF7RAnchor && lower.EndsWith(".ini") &&
                !lower.Contains("desktop.ini"))
            {
                detectedTypes |= FF7RInstallType.IniConfig;
                // A user INI belongs in the Documents config folder, not the game
                // directory: UE4 never reads user configuration from there. The route is
                // labelled and rooted so the module can point it at the real location.
                fileRoutes[entry.FullName] =
                    lower.Contains("engine") || lower.Contains("input")
                        ? FileRoute.Heuristic(
                            Path.GetFileName(path), PackageTypes.UeUserConfig)
                        : FileRoute.Heuristic(
                            "End/Binaries/Win64/" + Path.GetFileName(path),
                            PackageTypes.UeMigoto);
                signalConfidences.Add(0.70);
                continue;
            }

            // 12. DXVK d3d11.dll / dxgi.dll (no dxvk.conf companion — lower confidence)
            if (hasFF7RAnchor && IsDxvkCandidate(lower, allLower))
            {
                detectedTypes |= FF7RInstallType.DxvkWrapper;
                requiresDx11 = true;
                fileRoutes[entry.FullName] = FileRoute.Heuristic(
                    "End/Binaries/Win64/" + Path.GetFileName(path), PackageTypes.Dxvk);
                signalConfidences.Add(0.80);
                continue;
            }

            // ── Fallback: route to game root ─────────────────────────
            fileRoutes[entry.FullName] = FileRoute.Declared(path, PackageTypes.Unknown);
        }

        // ── Confidence calculation ───────────────────────────────────────
        double confidence;
        if (signalConfidences.Count == 0)
        {
            confidence = 0;
            detectedTypes = FF7RInstallType.Unknown;
        }
        else
        {
            confidence = signalConfidences.Max();
            var distinctTypes = CountSetBits((int)detectedTypes);
            if (distinctTypes > 1)
                confidence = Math.Min(1.0, confidence + 0.02 * (distinctTypes - 1));
        }

        // ── Reason string ────────────────────────────────────────────────
        var typeNames = Enum.GetValues<FF7RInstallType>()
            .Where(t => t != FF7RInstallType.Unknown && detectedTypes.HasFlag(t))
            .Select(t => t.ToString())
            .ToList();

        var reason = typeNames.Count switch
        {
            0 => "No recognized FF7 Remake mod structure detected",
            1 => $"Detected as {typeNames[0]}",
            _ => $"Multi-path mod: {string.Join(" + ", typeNames)}"
        };

        return new FF7RInstallLayout
        {
            Types = detectedTypes,
            Confidence = confidence,
            Reason = reason,
            FileRoutes = fileRoutes,
            StrippedPrefix = strippedPrefix,
            RequiresDx11 = requiresDx11
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Known proxy DLL names used by FF7R hooks (FFVIIHook, etc.).
    /// </summary>
    private static bool IsKnownProxyDll(string lowerFilename)
    {
        var name = Path.GetFileName(lowerFilename);
        return name is
            "xinput1_3.dll" or
            "dxgi.dll" or
            "d3d11.dll" or
            "d3d12.dll" or
            "dinput8.dll" or
            "x3daudio1_7.dll" or
            "winmm.dll" or
            "version.dll";
    }

    /// <summary>
    /// Detect if a DLL is likely a DXVK wrapper (d3d11.dll or dxgi.dll)
    /// when a companion DXVK marker exists in the archive.
    /// </summary>
    private static bool IsDxvkCandidate(string lowerPath, List<string> allLower)
    {
        var name = Path.GetFileName(lowerPath);
        if (name is not ("d3d11.dll" or "dxgi.dll"))
            return false;

        // Only flag as DXVK if there's a companion dxvk marker or both DLLs present
        return allLower.Any(p => DxvkMarkers.Any(d => p.Contains(d))) ||
               (allLower.Any(p => Path.GetFileName(p) == "d3d11.dll") &&
                allLower.Any(p => Path.GetFileName(p) == "dxgi.dll"));
    }

    /// <summary>
    /// Route a file to End/Binaries/Win64/, stripping any existing
    /// Binaries/Win64 prefix to avoid duplication.
    /// </summary>
    private static string RouteToWin64(string path, string lowerPath)
    {
        foreach (var prefix in BinariesPrefixes)
        {
            var idx = lowerPath.IndexOf(prefix, StringComparison.Ordinal);
            if (idx >= 0)
                return "End/Binaries/Win64/" + path[(idx + prefix.Length)..];
        }

        // Also handle bare "Win64/" prefix
        var win64Idx = lowerPath.IndexOf("win64/", StringComparison.Ordinal);
        if (win64Idx >= 0)
            return "End/Binaries/Win64/" + path[(win64Idx + "win64/".Length)..];

        return "End/Binaries/Win64/" + path;
    }

    /// <summary>
    /// Strip all path segments up to and including the given marker segment.
    /// E.g. StripToAfter("Foo/End/Content/Paks/~mods/bar.pak", "~mods/") => "bar.pak"
    /// </summary>
    private static string StripToAfter(string path, string marker)
    {
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return path[(idx + marker.Length)..];
        return path;
    }

    private static bool ContainsAny(string path, string[] fragments)
    {
        foreach (var f in fragments)
        {
            if (path.Contains(f, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string DetectStrippablePrefix(List<string> paths)
    {
        if (paths.Count == 0) return string.Empty;

        var firstSegments = paths
            .Select(p => p.Split('/').FirstOrDefault() ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (firstSegments.Count != 1)
            return string.Empty;

        var candidate = firstSegments[0] + "/";

        // Do NOT strip well-known FF7R game directories
        var wellKnown = new[]
        {
            "End", "Content", "Paks", "Binaries", "Win64",
            "Mods", "ShaderFixes", "fomod", "~mods"
        };

        if (wellKnown.Any(wk =>
            wk.Equals(firstSegments[0], StringComparison.OrdinalIgnoreCase)))
            return string.Empty;

        if (paths.All(p => p.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            return candidate;

        return string.Empty;
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
