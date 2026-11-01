using Modular.Core.Dependencies.Conventions;

namespace Modular.Core.Downloads;

/// <summary>
/// The files in a mod folder of the mods directory: the downloads, and the records
/// Modular and other tools keep beside them.
/// </summary>
public static class ModLibraryFiles
{
    // Records kept beside a download, and downloads that have not finished.
    private static readonly string[] SidecarSuffixes =
    [
        DependencyConventions.DownloadRecordSuffix,
        DependencyConventions.ModOrganizerSuffix,
        ResumableDownloader.PartSuffix,
        ResumableDownloader.PartSuffix + ".json"
    ];

    // Files that are never a mod's download: readmes and pictures saved beside one.
    private static readonly HashSet<string> NotDownloads = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".html", ".htm", ".pdf", ".doc", ".docx",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".log", ".csv", ".db", ".sqlite", ".json"
    };

    /// <summary>Whether a file is a record kept beside a download, or an unfinished one.</summary>
    /// <param name="path">The file.</param>
    /// <returns>True for a sidecar.</returns>
    public static bool IsSidecar(string path) =>
        SidecarSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The downloads in a mod folder, for the installer.
    /// </summary>
    /// <remarks>
    /// Only the folder's own files: downloads land directly in their mod's folder. A file
    /// is a download unless it is a sidecar or a readme or picture, so a download saved
    /// without its extension (<c>Mod Configuration Menu 1.40.1</c>) still counts.
    /// </remarks>
    /// <param name="modFolder">The mod folder.</param>
    /// <returns>The downloads, by name; empty when the folder is gone.</returns>
    public static IReadOnlyList<string> Downloads(string modFolder)
    {
        if (!Directory.Exists(modFolder))
            return [];

        return Directory.EnumerateFiles(modFolder)
            .Where(f => !IsSidecar(f) && !NotDownloads.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
