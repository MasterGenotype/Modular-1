namespace Modular.Core.Utilities;

/// <summary>
/// File system utility methods.
/// </summary>
public static class FileUtils
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Expands ~ to the user's home directory.
    /// </summary>
    /// <param name="path">Path that may contain ~</param>
    /// <returns>Expanded path</returns>
    public static string ExpandPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return path;
    }

    /// <summary>
    /// Sanitizes a filename by replacing invalid characters with underscores.
    /// </summary>
    /// <param name="filename">Filename to sanitize</param>
    /// <returns>Sanitized filename</returns>
    public static string SanitizeFilename(string filename)
    {
        if (string.IsNullOrEmpty(filename))
            return filename;

        return string.Concat(filename.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c));
    }

    /// <summary>
    /// Sanitizes a directory name by replacing invalid characters with underscores.
    /// </summary>
    /// <param name="name">Directory name to sanitize</param>
    /// <returns>Sanitized directory name</returns>
    public static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Replace characters that are invalid in directory names
        var invalid = Path.GetInvalidPathChars().Concat([':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    /// <summary>
    /// Ensures a directory exists, creating it if necessary.
    /// </summary>
    /// <param name="path">Directory path</param>
    public static void EnsureDirectoryExists(string path)
    {
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Gets all subdirectories that look like mod IDs (numeric names).
    /// </summary>
    /// <param name="parentPath">Parent directory path</param>
    /// <returns>List of mod IDs found</returns>
    public static IEnumerable<int> GetModIdDirectories(string parentPath)
    {
        if (!Directory.Exists(parentPath))
            yield break;

        foreach (var dir in Directory.GetDirectories(parentPath))
        {
            var name = Path.GetFileName(dir);
            if (int.TryParse(name, out var modId))
                yield return modId;
        }
    }

    /// <summary>
    /// Gets all game domain directories (subdirectories of the mods root).
    /// </summary>
    /// <param name="modsRoot">Root mods directory</param>
    /// <returns>List of game domain names</returns>
    public static IEnumerable<string> GetGameDomains(string modsRoot)
    {
        if (!Directory.Exists(modsRoot))
            yield break;

        foreach (var dir in Directory.GetDirectories(modsRoot))
        {
            yield return Path.GetFileName(dir);
        }
    }

    /// <summary>
    /// Safely moves a directory, handling cross-device moves and merging.
    /// </summary>
    /// <param name="sourcePath">Source directory path</param>
    /// <param name="destPath">Destination directory path</param>
    /// <returns>True if the source was fully moved/merged and deleted, false otherwise</returns>
    public static bool MoveDirectory(string sourcePath, string destPath)
    {
        if (sourcePath == destPath)
            return true; // Nothing to do

        if (!Directory.Exists(sourcePath))
            return false; // Source doesn't exist

        // If destination exists, merge into it
        if (Directory.Exists(destPath))
        {
            // Move all files, overwriting if they exist
            foreach (var file in Directory.GetFiles(sourcePath))
            {
                var destFile = Path.Combine(destPath, Path.GetFileName(file));
                // Delete destination file if it exists (overwrite)
                if (File.Exists(destFile))
                    File.Delete(destFile);
                File.Move(file, destFile);
            }

            // Recursively move subdirectories
            foreach (var dir in Directory.GetDirectories(sourcePath))
            {
                var destDir = Path.Combine(destPath, Path.GetFileName(dir));
                MoveDirectory(dir, destDir);
            }

            // Remove source directory (should be empty now)
            if (!Directory.EnumerateFileSystemEntries(sourcePath).Any())
            {
                Directory.Delete(sourcePath);
                return true;
            }
            return false; // Source still has entries
        }
        else
        {
            // Ensure parent exists
            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            Directory.Move(sourcePath, destPath);
            return true;
        }
    }
}
