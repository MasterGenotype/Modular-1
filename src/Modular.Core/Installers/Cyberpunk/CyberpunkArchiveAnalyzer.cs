using Modular.Sdk.Archives;

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

    // ── Framework root markers ───────────────────────────────────────────

    /// <summary>Files that indicate a framework/engine-level install.</summary>
    private static readonly (string path, string frameworkId, bool critical)[] FrameworkMarkers =
    {
        // RED4ext loader
        ("bin/x64/winmm.dll", "red4ext", true),
        ("bin\\x64\\winmm.dll", "red4ext", true),

        // CET core
        ("bin/x64/plugins/cyber_engine_tweaks.asi", "cet", true),
        ("bin\\x64\\plugins\\cyber_engine_tweaks.asi", "cet", true),
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
    public static CyberpunkInstallLayout Analyze(IReadOnlyList<ArchiveEntry> entries)
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

        // ── Signal accumulators ──────────────────────────────────────────

        var detectedTypes = CyberpunkInstallType.Unknown;
        var fileRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frameworkHints = new List<FrameworkFileHint>();
        var signalConfidences = new List<double>();

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
                    detectedTypes |= CyberpunkInstallType.FrameworkRoot;
                    fileRoutes[entry.FullName] = marker; // route to exact framework path
                    frameworkHints.Add(new FrameworkFileHint
                    {
                        SourceEntry = entry.FullName,
                        DestinationRelative = marker,
                        FrameworkId = fwId,
                        IsCritical = critical
                    });
                    signalConfidences.Add(0.98);
                    matchedFramework = true;
                    break;
                }
            }
            if (matchedFramework) continue;

            // ── 2. RED4ext plugin ────────────────────────────────────
            if (StartsWithAny(lowerPath, Red4ExtPluginPrefixes))
            {
                detectedTypes |= CyberpunkInstallType.Red4ExtPlugin;
                fileRoutes[entry.FullName] = EnsurePrefix("red4ext/plugins/", path, Red4ExtPluginPrefixes);
                signalConfidences.Add(0.95);
                continue;
            }

            // ── 3. CET mod ──────────────────────────────────────────
            if (StartsWithAny(lowerPath, CetModPrefixes))
            {
                detectedTypes |= CyberpunkInstallType.CetMod;
                fileRoutes[entry.FullName] = EnsurePrefix(
                    "bin/x64/plugins/cyber_engine_tweaks/mods/", path, CetModPrefixes);
                signalConfidences.Add(0.95);
                continue;
            }

            // ── 4. redscript mod ─────────────────────────────────────
            if (StartsWithAny(lowerPath, RedscriptPrefixes) &&
                (lowerPath.EndsWith(".reds") || lowerPath.EndsWith(".reds.bak")))
            {
                detectedTypes |= CyberpunkInstallType.RedscriptMod;
                fileRoutes[entry.FullName] = EnsurePrefix("r6/scripts/", path, RedscriptPrefixes);
                signalConfidences.Add(0.95);
                continue;
            }
            // Also match .reds files without the prefix (some mods ship flat)
            if (lowerPath.EndsWith(".reds") && !StartsWithAny(lowerPath, RedscriptPrefixes))
            {
                detectedTypes |= CyberpunkInstallType.RedscriptMod;
                fileRoutes[entry.FullName] = "r6/scripts/" + path;
                signalConfidences.Add(0.80);
                continue;
            }

            // ── 5. Legacy archive ────────────────────────────────────
            if (StartsWithAny(lowerPath, LegacyArchivePrefixes) ||
                (lowerPath.EndsWith(".archive") && !StartsWithAny(lowerPath, RedModPrefixes)))
            {
                detectedTypes |= CyberpunkInstallType.LegacyArchive;
                if (StartsWithAny(lowerPath, LegacyArchivePrefixes))
                    fileRoutes[entry.FullName] = EnsurePrefix("archive/pc/mod/", path, LegacyArchivePrefixes);
                else
                    fileRoutes[entry.FullName] = "archive/pc/mod/" + Path.GetFileName(path);
                signalConfidences.Add(0.90);

                // Handle companion .archive.xl files
                continue;
            }
            if (lowerPath.EndsWith(".archive.xl"))
            {
                detectedTypes |= CyberpunkInstallType.LegacyArchive;
                if (StartsWithAny(lowerPath, LegacyArchivePrefixes))
                    fileRoutes[entry.FullName] = EnsurePrefix("archive/pc/mod/", path, LegacyArchivePrefixes);
                else
                    fileRoutes[entry.FullName] = "archive/pc/mod/" + Path.GetFileName(path);
                signalConfidences.Add(0.90);
                continue;
            }

            // ── 6. REDmod ────────────────────────────────────────────
            if (StartsWithAny(lowerPath, RedModPrefixes) &&
                HasRedModStructure(lowerPath, normalized.Select(n => n.Normalized).ToList()))
            {
                detectedTypes |= CyberpunkInstallType.RedMod;
                fileRoutes[entry.FullName] = path; // already has mods/ prefix
                signalConfidences.Add(0.90);
                continue;
            }

            // ── 7. Tweak mod ─────────────────────────────────────────
            if (StartsWithAny(lowerPath, TweakPrefixes) ||
                lowerPath.EndsWith(".yaml") && path.Contains("tweak", StringComparison.OrdinalIgnoreCase))
            {
                detectedTypes |= CyberpunkInstallType.TweakMod;
                if (StartsWithAny(lowerPath, TweakPrefixes))
                    fileRoutes[entry.FullName] = EnsurePrefix("r6/tweaks/", path, TweakPrefixes);
                else
                    fileRoutes[entry.FullName] = "r6/tweaks/" + path;
                signalConfidences.Add(0.85);
                continue;
            }

            // ── 8. ini/config tweak ──────────────────────────────────
            if (StartsWithAny(lowerPath, IniConfigPrefixes) ||
                lowerPath.EndsWith(".ini"))
            {
                detectedTypes |= CyberpunkInstallType.IniTweak;
                if (StartsWithAny(lowerPath, IniConfigPrefixes))
                    fileRoutes[entry.FullName] = path;
                else
                    fileRoutes[entry.FullName] = "engine/config/platform/pc/" + Path.GetFileName(path);
                signalConfidences.Add(0.85);
                continue;
            }

            // ── 9. Input mapping XML ─────────────────────────────────
            if (StartsWithAny(lowerPath, InputMappingPrefixes) ||
                (lowerPath.EndsWith(".xml") && lowerPath.Contains("input")))
            {
                detectedTypes |= CyberpunkInstallType.InputMapping;
                if (StartsWithAny(lowerPath, InputMappingPrefixes))
                    fileRoutes[entry.FullName] = EnsurePrefix("r6/input/", path, InputMappingPrefixes);
                else
                    fileRoutes[entry.FullName] = "r6/input/" + Path.GetFileName(path);
                signalConfidences.Add(0.85);
                continue;
            }

            // ── 10. ASI plugin ───────────────────────────────────────
            if (lowerPath.EndsWith(".asi"))
            {
                detectedTypes |= CyberpunkInstallType.AsiPlugin;
                if (StartsWithAny(lowerPath, AsiPluginPrefixes))
                    fileRoutes[entry.FullName] = EnsurePrefix("bin/x64/plugins/", path, AsiPluginPrefixes);
                else
                    fileRoutes[entry.FullName] = "bin/x64/plugins/" + Path.GetFileName(path);
                signalConfidences.Add(0.90);
                continue;
            }

            // ── 11. Standalone exe ───────────────────────────────────
            if (lowerPath.EndsWith(".exe") && !StartsWithAny(lowerPath, new[] { "bin/", "engine/" }))
            {
                detectedTypes |= CyberpunkInstallType.StandaloneExe;
                fileRoutes[entry.FullName] = path; // deploy as-is
                signalConfidences.Add(0.70);
                continue;
            }

            // ── 12. redscript files in r6/scripts/ without .reds extension ──
            if (StartsWithAny(lowerPath, RedscriptPrefixes))
            {
                detectedTypes |= CyberpunkInstallType.RedscriptMod;
                fileRoutes[entry.FullName] = EnsurePrefix("r6/scripts/", path, RedscriptPrefixes);
                signalConfidences.Add(0.80);
                continue;
            }

            // ── Fallback: keep the path as-is, route to game root ────
            fileRoutes[entry.FullName] = path;
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

        return new CyberpunkInstallLayout
        {
            Types = detectedTypes,
            Confidence = confidence,
            Reason = reason,
            FileRoutes = fileRoutes,
            StrippedPrefix = strippedPrefix,
            FrameworkHints = frameworkHints
        };
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
