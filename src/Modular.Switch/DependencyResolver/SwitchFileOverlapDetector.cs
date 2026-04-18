using Modular.Switch.Installer;
using Modular.Switch.Models;
using SharpCompress.Archives;

namespace Modular.Switch.DependencyResolver;

/// <summary>
/// Detects file-level overlaps between Switch mods before installation.
/// Scans each mod's archive entries or extracted folder contents and reports
/// which normalized file paths are provided by more than one mod.
/// </summary>
public static class SwitchFileOverlapDetector
{
    /// <summary>
    /// Scans the file contents of each mod and returns a report of overlapping paths.
    /// </summary>
    public static async Task<SwitchOverlapReport> DetectAsync(
        IReadOnlyList<SwitchMod> mods,
        CancellationToken ct = default)
    {
        // Map normalized path (lowercased) -> list of ModKeys that provide it
        var fileProviders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();

            var paths = mod.IsExtracted
                ? GetFolderPaths(mod)
                : GetArchivePaths(mod);

            foreach (var path in paths)
            {
                if (!fileProviders.TryGetValue(path, out var providers))
                {
                    providers = [];
                    fileProviders[path] = providers;
                }

                if (!providers.Contains(mod.ModKey, StringComparer.OrdinalIgnoreCase))
                    providers.Add(mod.ModKey);
            }
        }

        var overlaps = fileProviders
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new SwitchFileOverlap
            {
                NormalizedPath = kv.Key,
                ModKeys = kv.Value
            })
            .OrderBy(o => o.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await Task.FromResult(new SwitchOverlapReport { Overlaps = overlaps });
    }

    private static IEnumerable<string> GetArchivePaths(SwitchMod mod)
    {
        if (!File.Exists(mod.SourcePath))
            yield break;

        using var archive = ArchiveFactory.Open(mod.SourcePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            var raw = entry.Key?.Replace('\\', '/') ?? string.Empty;

            if (SwitchModInstaller.IsBnpInternalPath(raw))
                continue;

            var normalized = SwitchModInstaller.NormaliseArchivePath(raw, mod.Category);
            if (normalized != null)
                yield return normalized.ToLowerInvariant();
        }
    }

    private static IEnumerable<string> GetFolderPaths(SwitchMod mod)
    {
        if (!Directory.Exists(mod.SourcePath))
            yield break;

        foreach (var file in Directory.EnumerateFiles(mod.SourcePath, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(mod.SourcePath, file).Replace('\\', '/');

            if (SwitchModInstaller.IsBnpInternalPath(relPath))
                continue;

            var normalized = SwitchModInstaller.NormaliseArchivePath(relPath, mod.Category);
            if (normalized != null)
                yield return normalized.ToLowerInvariant();
        }
    }
}

/// <summary>
/// A single file path provided by multiple mods.
/// </summary>
public sealed class SwitchFileOverlap
{
    /// <summary>Normalized file path (lowercased, category-stripped).</summary>
    public string NormalizedPath { get; init; } = string.Empty;

    /// <summary>ModKeys that provide this file, in install-order sequence.</summary>
    public List<string> ModKeys { get; init; } = [];
}

/// <summary>
/// Result of file overlap detection across a set of mods.
/// </summary>
public sealed class SwitchOverlapReport
{
    public List<SwitchFileOverlap> Overlaps { get; init; } = [];
    public bool HasOverlaps => Overlaps.Count > 0;
}
