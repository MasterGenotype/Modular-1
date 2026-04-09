using Modular.Sdk.Archives;

namespace Modular.Core.Installers.HorizonZeroDawn;

/// <summary>
/// Static analyzer that examines archive entry paths to determine which
/// <see cref="HZDInstallType"/>s are present and route each file to the
/// correct game subdirectory.
///
/// Two-phase detection:
///   Phase 1 — Pre-scan for HZD-specific anchors (Packed_DX12/, HorizonZeroDawn.exe
///             references, Patch_*.bin naming, known HZD DLL names).
///   Phase 2 — Classify each entry. Extension-only signals (.bin, .dll, .ini)
///             require an anchor to avoid mis-claiming archives from other games.
///
/// HZD's Decima engine loads .bin patch files from Packed_DX12/ in alphabetical
/// order. The analyzer does NOT enforce load order — that's a deployment concern,
/// not an archive classification concern.
/// </summary>
public static class HZDArchiveAnalyzer
{
    // ── Well-known directory prefixes ────────────────────────────────────

    private static readonly string[] PackedDx12Prefixes =
        { "packed_dx12/" };

    // ── HZD-specific anchor signals ─────────────────────────────────────

    /// <summary>
    /// Paths/fragments that are structurally unique to Horizon Zero Dawn.
    /// </summary>
    private static readonly string[] HZDAnchorPaths =
    {
        "packed_dx12/",
        "horizonzerodawn.exe",
        "horizonzerodawn",
    };

    /// <summary>
    /// Filename patterns strongly associated with HZD mods.
    /// The Patch_*.bin convention is overwhelmingly HZD-specific in practice.
    /// </summary>
    private static readonly string[] HZDFilenameHints =
    {
        "patch_", // Decima patch convention
        "aloy",   // protagonist name
        "hzd",
        "horizon",
        "zerodawn",
        "oo2core_3_win64", // Oodle decompressor from HZD
    };

    // ── GPU utility markers ─────────────────────────────────────────────

    private static readonly string[] GpuUtilityMarkers =
    {
        "enablesignatureoverride.reg",
        "disablesignatureoverride.reg",
        "nvngx.dll",
        "nvngx.ini",
        "ffx_fsr2_api",
    };

    /// <summary>
    /// Analyze archive entries and produce an <see cref="HZDInstallLayout"/>.
    /// </summary>
    public static HZDInstallLayout Analyze(IReadOnlyList<ArchiveEntry> entries)
    {
        var files = entries.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            return new HZDInstallLayout
            {
                Types = HZDInstallType.Unknown,
                Confidence = 0,
                Reason = "Archive is empty"
            };
        }

        // Normalize paths
        var normalized = files
            .Select(e => (Entry: e, Normalized: e.FullName.Replace('\\', '/')))
            .ToList();

        // Strip non-structural wrapper prefix
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

        bool hasHZDAnchor =
            // Packed_DX12 path (the defining HZD mod directory)
            allLower.Any(p => p.Contains("packed_dx12/")) ||
            // HZD executable reference
            allLower.Any(p => p.Contains("horizonzerodawn")) ||
            // Patch_*.bin naming convention (very strong HZD signal)
            allLower.Any(p =>
                Path.GetFileName(p).StartsWith("patch_") &&
                p.EndsWith(".bin")) ||
            // HZD-specific filename fragments
            allLower.Any(p => HZDFilenameHints.Any(h =>
                p.Contains(h, StringComparison.Ordinal))) ||
            // GPU utility markers (registry patches for DLSS/FSR)
            allLower.Any(p => GpuUtilityMarkers.Any(g =>
                p.Contains(g, StringComparison.Ordinal)));

        // ── Phase 2: Classify each entry ─────────────────────────────────

        var detectedTypes = HZDInstallType.Unknown;
        var fileRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var signalConfidences = new List<double>();

        foreach (var (entry, path) in normalized)
        {
            var lower = path.ToLowerInvariant();
            var fileName = Path.GetFileName(lower);

            // ── 1. .bin file in Packed_DX12/ path ────────────────────
            if (lower.Contains("packed_dx12/") && lower.EndsWith(".bin"))
            {
                detectedTypes |= HZDInstallType.DecimaPatch;
                fileRoutes[entry.FullName] = "Packed_DX12/" +
                    StripToAfter(path, "Packed_DX12/", "packed_dx12/");
                signalConfidences.Add(0.98);
                continue;
            }

            // ── 2. Patch_*.bin naming convention (bare, no path) ─────
            if (fileName.StartsWith("patch_") && lower.EndsWith(".bin"))
            {
                detectedTypes |= HZDInstallType.DecimaPatch;
                fileRoutes[entry.FullName] = "Packed_DX12/" + Path.GetFileName(path);
                signalConfidences.Add(0.95);
                continue;
            }

            // ── 3. Bare .bin file with HZD anchor ────────────────────
            if (hasHZDAnchor && lower.EndsWith(".bin") &&
                !fileName.StartsWith("patch_") &&
                !lower.Contains("packed_dx12/"))
            {
                detectedTypes |= HZDInstallType.DecimaPatch;
                fileRoutes[entry.FullName] = "Packed_DX12/" + Path.GetFileName(path);
                signalConfidences.Add(0.85);
                continue;
            }

            // ── 4. GPU utility files (.reg, nvngx, fsr2 DLLs) ───────
            if (GpuUtilityMarkers.Any(g => lower.Contains(g)))
            {
                detectedTypes |= HZDInstallType.GpuUtility;
                fileRoutes[entry.FullName] = Path.GetFileName(path);
                signalConfidences.Add(0.92);
                continue;
            }

            // ── 5. DLL hook — known proxy DLLs ──────────────────────
            if (hasHZDAnchor && lower.EndsWith(".dll") && IsKnownProxyDll(fileName))
            {
                // Distinguish GPU utility DLLs from hook DLLs
                if (fileName is "nvngx.dll" || fileName.StartsWith("ffx_fsr2"))
                {
                    detectedTypes |= HZDInstallType.GpuUtility;
                }
                else
                {
                    detectedTypes |= HZDInstallType.DllHook;
                }
                fileRoutes[entry.FullName] = Path.GetFileName(path);
                signalConfidences.Add(0.88);
                continue;
            }

            // ── 6. ASI loader/plugin ─────────────────────────────────
            if (hasHZDAnchor && lower.EndsWith(".asi"))
            {
                detectedTypes |= HZDInstallType.GpuUtility;
                fileRoutes[entry.FullName] = Path.GetFileName(path);
                signalConfidences.Add(0.85);
                continue;
            }

            // ── 7. Config files (.toml, .ini) ────────────────────────
            if (hasHZDAnchor && (lower.EndsWith(".toml") || lower.EndsWith(".ini")) &&
                !lower.Contains("desktop.ini"))
            {
                detectedTypes |= HZDInstallType.ConfigFile;
                fileRoutes[entry.FullName] = Path.GetFileName(path);
                signalConfidences.Add(0.80);
                continue;
            }

            // ── 8. .reg files (GPU utility registry patches) ─────────
            //    Extension-only — require anchor to avoid claiming unrelated
            //    archives that happen to ship .reg scripts.
            if (hasHZDAnchor && lower.EndsWith(".reg"))
            {
                detectedTypes |= HZDInstallType.GpuUtility;
                fileRoutes[entry.FullName] = Path.GetFileName(path);
                signalConfidences.Add(0.90);
                continue;
            }

            // ── 9. EXE replacement ──────────────────────────────────
            if (hasHZDAnchor && lower.EndsWith(".exe"))
            {
                detectedTypes |= HZDInstallType.BinaryReplacement;
                fileRoutes[entry.FullName] = Path.GetFileName(path);
                signalConfidences.Add(0.80);
                continue;
            }

            // ── Fallback ─────────────────────────────────────────────
            fileRoutes[entry.FullName] = path;
        }

        // ── Confidence calculation ───────────────────────────────────────
        double confidence;
        if (signalConfidences.Count == 0)
        {
            confidence = 0;
            detectedTypes = HZDInstallType.Unknown;
        }
        else
        {
            confidence = signalConfidences.Max();
            var distinctTypes = CountSetBits((int)detectedTypes);
            if (distinctTypes > 1)
                confidence = Math.Min(1.0, confidence + 0.02 * (distinctTypes - 1));
        }

        var typeNames = Enum.GetValues<HZDInstallType>()
            .Where(t => t != HZDInstallType.Unknown && detectedTypes.HasFlag(t))
            .Select(t => t.ToString())
            .ToList();

        var reason = typeNames.Count switch
        {
            0 => "No recognized Horizon Zero Dawn mod structure detected",
            1 => $"Detected as {typeNames[0]}",
            _ => $"Multi-path mod: {string.Join(" + ", typeNames)}"
        };

        return new HZDInstallLayout
        {
            Types = detectedTypes,
            Confidence = confidence,
            Reason = reason,
            FileRoutes = fileRoutes,
            StrippedPrefix = strippedPrefix
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool IsKnownProxyDll(string lowerFilename)
    {
        return lowerFilename is
            "winhttp.dll" or
            "winmm.dll" or
            "dxgi.dll" or
            "d3d11.dll" or
            "d3d12.dll" or
            "dinput8.dll" or
            "xinput1_3.dll" or
            "version.dll" or
            "nvngx.dll" or
            "vulkan-1.dll";
    }

    {
        foreach (var marker in markers)
        {
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return path[(idx + marker.Length)..];
        }
        return path;
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

        // Do NOT strip well-known HZD directories
        var wellKnown = new[]
        {
            "Packed_DX12", "packed_dx12",
            "ShaderFixes", "Saved Game", "fomod"
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
