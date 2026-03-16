namespace Modular.Core.GameDetection;

/// <summary>
/// Interface for game engine detectors.
/// Follows the same Detect → Confidence → Evidence pattern as IModInstaller.
/// </summary>
public interface IEngineDetector
{
    /// <summary>
    /// Display name of this detector.
    /// </summary>
    string DetectorName { get; }

    /// <summary>
    /// Attempts to detect the game engine at the given install path.
    /// </summary>
    /// <param name="installPath">Path to the game's installation directory.</param>
    /// <returns>Detection result, or null if this engine is not detected.</returns>
    EngineDetectionResult? Detect(string installPath);
}

/// <summary>
/// Result of an engine detection attempt.
/// </summary>
public class EngineDetectionResult
{
    /// <summary>
    /// Detected engine family (e.g., "Unity", "Unreal", "Source", "Godot").
    /// </summary>
    public string EngineFamily { get; set; } = string.Empty;

    /// <summary>
    /// Confidence level (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Evidence supporting the detection (file paths found, etc.).
    /// </summary>
    public List<string> Evidence { get; set; } = new();

    /// <summary>
    /// Name of the detector that produced this result.
    /// </summary>
    public string DetectorName { get; set; } = string.Empty;
}

/// <summary>
/// Detects Unity engine by checking for UnityPlayer.dll, *_Data/ dirs, GameAssembly.dll.
/// </summary>
public class UnityEngineDetector : IEngineDetector
{
    public string DetectorName => "Unity";

    public EngineDetectionResult? Detect(string installPath)
    {
        if (!Directory.Exists(installPath))
            return null;

        var evidence = new List<string>();
        double confidence = 0;

        // UnityPlayer.dll / UnityPlayer.so
        var unityPlayer = Directory.GetFiles(installPath, "UnityPlayer.*", SearchOption.TopDirectoryOnly);
        if (unityPlayer.Length > 0)
        {
            evidence.Add($"Found: {Path.GetFileName(unityPlayer[0])}");
            confidence += 0.5;
        }

        // *_Data/ directories (e.g., GameName_Data/)
        var dataDirs = Directory.GetDirectories(installPath)
            .Where(d => Path.GetFileName(d).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (dataDirs.Length > 0)
        {
            evidence.Add($"Found data directory: {Path.GetFileName(dataDirs[0])}");
            confidence += 0.3;
        }

        // GameAssembly.dll (IL2CPP)
        if (File.Exists(Path.Combine(installPath, "GameAssembly.dll")))
        {
            evidence.Add("Found: GameAssembly.dll (IL2CPP)");
            confidence += 0.2;
        }

        // MonoBleedingEdge/ directory
        if (Directory.Exists(Path.Combine(installPath, "MonoBleedingEdge")))
        {
            evidence.Add("Found: MonoBleedingEdge/ (Mono runtime)");
            confidence += 0.1;
        }

        if (confidence <= 0)
            return null;

        return new EngineDetectionResult
        {
            EngineFamily = "Unity",
            Confidence = Math.Min(confidence, 1.0),
            Evidence = evidence,
            DetectorName = DetectorName
        };
    }
}

/// <summary>
/// Detects Unreal Engine by checking for *.pak files in Content/Paks/.
/// </summary>
public class UnrealEngineDetector : IEngineDetector
{
    public string DetectorName => "Unreal";

    public EngineDetectionResult? Detect(string installPath)
    {
        if (!Directory.Exists(installPath))
            return null;

        var evidence = new List<string>();
        double confidence = 0;

        // Check for Content/Paks/*.pak (top-level or one level deep)
        var paksDir = FindRecursive(installPath, "Paks", maxDepth: 3);
        if (paksDir != null)
        {
            var pakFiles = Directory.GetFiles(paksDir, "*.pak");
            if (pakFiles.Length > 0)
            {
                evidence.Add($"Found {pakFiles.Length} .pak files in {Path.GetRelativePath(installPath, paksDir)}");
                confidence += 0.7;
            }
        }

        // Engine/ directory
        if (Directory.Exists(Path.Combine(installPath, "Engine")))
        {
            evidence.Add("Found: Engine/ directory");
            confidence += 0.2;
        }

        // *.uproject file
        var uprojectFiles = Directory.GetFiles(installPath, "*.uproject", SearchOption.TopDirectoryOnly);
        if (uprojectFiles.Length > 0)
        {
            evidence.Add($"Found: {Path.GetFileName(uprojectFiles[0])}");
            confidence += 0.3;
        }

        if (confidence <= 0)
            return null;

        return new EngineDetectionResult
        {
            EngineFamily = "Unreal",
            Confidence = Math.Min(confidence, 1.0),
            Evidence = evidence,
            DetectorName = DetectorName
        };
    }

    private static string? FindRecursive(string root, string dirName, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir).Equals(dirName, StringComparison.OrdinalIgnoreCase))
                    return dir;

                var found = FindRecursive(dir, dirName, maxDepth - 1);
                if (found != null) return found;
            }
        }
        catch
        {
            // Permission issues
        }
        return null;
    }
}

/// <summary>
/// Detects Valve Source engine by checking for *.vpk files.
/// </summary>
public class SourceEngineDetector : IEngineDetector
{
    public string DetectorName => "Source";

    public EngineDetectionResult? Detect(string installPath)
    {
        if (!Directory.Exists(installPath))
            return null;

        var evidence = new List<string>();
        double confidence = 0;

        // *.vpk files
        var vpkFiles = Directory.GetFiles(installPath, "*.vpk", SearchOption.TopDirectoryOnly);
        if (vpkFiles.Length > 0)
        {
            evidence.Add($"Found {vpkFiles.Length} .vpk files");
            confidence += 0.7;
        }

        // gameinfo.txt (Source 1)
        var gameInfoFiles = Directory.GetFiles(installPath, "gameinfo.txt", SearchOption.AllDirectories)
            .Take(3).ToArray();
        if (gameInfoFiles.Length > 0)
        {
            evidence.Add("Found: gameinfo.txt");
            confidence += 0.2;
        }

        // bin/ directory with engine DLLs
        var binDir = Path.Combine(installPath, "bin");
        if (Directory.Exists(binDir))
        {
            if (File.Exists(Path.Combine(binDir, "engine.dll")) ||
                File.Exists(Path.Combine(binDir, "engine2.dll")) ||
                File.Exists(Path.Combine(binDir, "libengine2.so")))
            {
                evidence.Add("Found: Source engine binaries");
                confidence += 0.3;
            }
        }

        if (confidence <= 0)
            return null;

        return new EngineDetectionResult
        {
            EngineFamily = "Source",
            Confidence = Math.Min(confidence, 1.0),
            Evidence = evidence,
            DetectorName = DetectorName
        };
    }
}

/// <summary>
/// Detects Godot engine by checking for data.pck or *.pck files.
/// </summary>
public class GodotEngineDetector : IEngineDetector
{
    public string DetectorName => "Godot";

    public EngineDetectionResult? Detect(string installPath)
    {
        if (!Directory.Exists(installPath))
            return null;

        var evidence = new List<string>();
        double confidence = 0;

        // data.pck
        if (File.Exists(Path.Combine(installPath, "data.pck")))
        {
            evidence.Add("Found: data.pck");
            confidence += 0.8;
        }

        // *.pck files
        var pckFiles = Directory.GetFiles(installPath, "*.pck", SearchOption.TopDirectoryOnly);
        if (pckFiles.Length > 0 && confidence == 0) // don't double-count
        {
            evidence.Add($"Found {pckFiles.Length} .pck files");
            confidence += 0.6;
        }

        // .godot/ directory (Godot 4)
        if (Directory.Exists(Path.Combine(installPath, ".godot")))
        {
            evidence.Add("Found: .godot/ directory");
            confidence += 0.2;
        }

        if (confidence <= 0)
            return null;

        return new EngineDetectionResult
        {
            EngineFamily = "Godot",
            Confidence = Math.Min(confidence, 1.0),
            Evidence = evidence,
            DetectorName = DetectorName
        };
    }
}

/// <summary>
/// Aggregates multiple engine detectors and returns the highest-confidence result.
/// </summary>
public class CompositeEngineDetector
{
    private readonly List<IEngineDetector> _detectors;

    public CompositeEngineDetector(IEnumerable<IEngineDetector>? detectors = null)
    {
        _detectors = detectors?.ToList() ?? new List<IEngineDetector>
        {
            new UnityEngineDetector(),
            new UnrealEngineDetector(),
            new SourceEngineDetector(),
            new GodotEngineDetector()
        };
    }

    /// <summary>
    /// Runs all detectors and returns the highest-confidence result.
    /// </summary>
    /// <param name="installPath">Game installation directory.</param>
    /// <returns>Best detection result, or null if no engine detected.</returns>
    public EngineDetectionResult? Detect(string installPath)
    {
        EngineDetectionResult? best = null;

        foreach (var detector in _detectors)
        {
            try
            {
                var result = detector.Detect(installPath);
                if (result != null && (best == null || result.Confidence > best.Confidence))
                    best = result;
            }
            catch
            {
                // Skip failing detectors
            }
        }

        return best;
    }

    /// <summary>
    /// Runs all detectors and returns all results, sorted by confidence descending.
    /// </summary>
    public List<EngineDetectionResult> DetectAll(string installPath)
    {
        var results = new List<EngineDetectionResult>();

        foreach (var detector in _detectors)
        {
            try
            {
                var result = detector.Detect(installPath);
                if (result != null)
                    results.Add(result);
            }
            catch
            {
                // Skip failing detectors
            }
        }

        return results.OrderByDescending(r => r.Confidence).ToList();
    }
}
