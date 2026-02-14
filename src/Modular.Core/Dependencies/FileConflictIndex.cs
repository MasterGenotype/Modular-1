namespace Modular.Core.Dependencies;

/// <summary>
/// Tracks file-level conflicts between mods.
/// Maps game installation paths to the mods that provide them.
/// </summary>
public class FileConflictIndex
{
    // Maps normalized game path -> list of mods that provide this file
    private readonly Dictionary<string, List<FileProvider>> _fileProviders = new();
    private readonly object _lock = new();

    /// <summary>
    /// Registers a file provided by a mod.
    /// </summary>
    /// <param name="gamePath">Normalized path in game directory (e.g., "Data/Scripts/MyMod.dll")</param>
    /// <param name="modId">Canonical mod ID</param>
    /// <param name="fileName">Original filename</param>
    /// <param name="fileHash">Optional hash for content comparison</param>
    public void RegisterFile(string gamePath, string modId, string fileName, string? fileHash = null)
    {
        lock (_lock)
        {
            var normalizedPath = NormalizePath(gamePath);
            
            if (!_fileProviders.ContainsKey(normalizedPath))
                _fileProviders[normalizedPath] = new();

            var provider = new FileProvider
            {
                ModId = modId,
                FileName = fileName,
                FileHash = fileHash
            };

            // Avoid duplicates
            if (!_fileProviders[normalizedPath].Any(p => p.ModId == modId))
                _fileProviders[normalizedPath].Add(provider);
        }
    }

    /// <summary>
    /// Removes all files provided by a mod.
    /// </summary>
    public void UnregisterMod(string modId)
    {
        lock (_lock)
        {
            foreach (var path in _fileProviders.Keys.ToList())
            {
                _fileProviders[path].RemoveAll(p => p.ModId == modId);
                if (_fileProviders[path].Count == 0)
                    _fileProviders.Remove(path);
            }
        }
    }

    /// <summary>
    /// Detects all file conflicts in the index.
    /// </summary>
    public List<FileConflict> DetectConflicts()
    {
        lock (_lock)
        {
            var conflicts = new List<FileConflict>();

            foreach (var (path, providers) in _fileProviders)
            {
                if (providers.Count <= 1)
                    continue;

                var conflict = new FileConflict
                {
                    GamePath = path,
                    ConflictingMods = providers.Select(p => p.ModId).ToList(),
                    Type = DetermineConflictType(providers)
                };

                conflicts.Add(conflict);
            }

            return conflicts;
        }
    }

    /// <summary>
    /// Checks if a specific file has conflicts.
    /// </summary>
    public bool HasConflict(string gamePath)
    {
        lock (_lock)
        {
            var normalizedPath = NormalizePath(gamePath);
            return _fileProviders.TryGetValue(normalizedPath, out var providers) && providers.Count > 1;
        }
    }

    /// <summary>
    /// Gets all mods that provide a specific file.
    /// </summary>
    public List<string> GetProvidersForFile(string gamePath)
    {
        lock (_lock)
        {
            var normalizedPath = NormalizePath(gamePath);
            if (_fileProviders.TryGetValue(normalizedPath, out var providers))
                return providers.Select(p => p.ModId).ToList();
            return new List<string>();
        }
    }

    /// <summary>
    /// Gets all files provided by a mod.
    /// </summary>
    public List<string> GetFilesForMod(string modId)
    {
        lock (_lock)
        {
            return _fileProviders
                .Where(kvp => kvp.Value.Any(p => p.ModId == modId))
                .Select(kvp => kvp.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Generates a detailed conflict report.
    /// </summary>
    public FileConflictReport GenerateReport()
    {
        lock (_lock)
        {
            var conflicts = DetectConflicts();
            var report = new FileConflictReport
            {
                TotalConflicts = conflicts.Count,
                Conflicts = conflicts,
                ConflictsByType = conflicts.GroupBy(c => c.Type)
                    .ToDictionary(g => g.Key, g => g.Count())
            };

            return report;
        }
    }

    /// <summary>
    /// Clears all registered files.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _fileProviders.Clear();
        }
    }

    /// <summary>
    /// Normalizes a file path for comparison (lowercase, forward slashes).
    /// </summary>
    private string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant().Trim('/');
    }

    /// <summary>
    /// Determines the type of conflict based on file providers.
    /// </summary>
    private FileConflictType DetermineConflictType(List<FileProvider> providers)
    {
        // If all files have the same hash, they're identical (merge candidate)
        var hashes = providers.Where(p => p.FileHash != null).Select(p => p.FileHash).Distinct().ToList();
        if (hashes.Count == 1 && hashes[0] != null)
            return FileConflictType.IdenticalFiles;

        // Check if files are compatible formats that could be merged
        var fileName = providers[0].FileName.ToLowerInvariant();
        if (fileName.EndsWith(".ini") || fileName.EndsWith(".cfg") || fileName.EndsWith(".txt"))
            return FileConflictType.MergeCandidate;

        // Otherwise it's an overwrite conflict
        return FileConflictType.Overwrite;
    }
}

/// <summary>
/// Represents a mod that provides a file.
/// </summary>
public class FileProvider
{
    public string ModId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? FileHash { get; set; }
}

/// <summary>
/// Represents a file conflict between multiple mods.
/// </summary>
public class FileConflict
{
    /// <summary>
    /// Path in game directory where conflict occurs.
    /// </summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>
    /// List of mod IDs that conflict on this file.
    /// </summary>
    public List<string> ConflictingMods { get; set; } = new();

    /// <summary>
    /// Type of conflict.
    /// </summary>
    public FileConflictType Type { get; set; }

    public override string ToString()
    {
        return $"{GamePath}: {string.Join(", ", ConflictingMods)} [{Type}]";
    }
}

/// <summary>
/// Type of file conflict.
/// </summary>
public enum FileConflictType
{
    /// <summary>Multiple mods overwrite the same file with different content.</summary>
    Overwrite,

    /// <summary>Files are identical (same hash) - safe to use either.</summary>
    IdenticalFiles,

    /// <summary>Files could potentially be merged (e.g., config files).</summary>
    MergeCandidate
}

/// <summary>
/// Report of file conflicts across all mods.
/// </summary>
public class FileConflictReport
{
    public int TotalConflicts { get; set; }
    public List<FileConflict> Conflicts { get; set; } = new();
    public Dictionary<FileConflictType, int> ConflictsByType { get; set; } = new();

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"File Conflicts: {TotalConflicts} total"
        };

        foreach (var (type, count) in ConflictsByType.OrderByDescending(kvp => kvp.Value))
        {
            lines.Add($"  - {type}: {count}");
        }

        return string.Join("\n", lines);
    }
}
