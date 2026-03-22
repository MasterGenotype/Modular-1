namespace Modular.Core.Utilities;

/// <summary>
/// Provides path sanitization and validation for archive extraction.
/// Defends against path traversal, absolute path injection, and symlink attacks.
/// </summary>
public static class PathSanitizer
{
    /// <summary>
    /// Validates and sanitizes an archive entry path, ensuring it stays within the target directory.
    /// </summary>
    /// <param name="entryPath">The relative path from the archive entry.</param>
    /// <param name="targetDirectory">The root target directory for extraction.</param>
    /// <returns>The validated, normalized full path.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the path is unsafe.</exception>
    public static string SanitizeEntryPath(string entryPath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            throw new InvalidOperationException("Archive entry path is empty.");

        // Reject absolute paths before normalization (NormalizeSeparators trims
        // leading separators, which would cause this check to pass incorrectly)
        if (Path.IsPathRooted(entryPath))
            throw new InvalidOperationException(
                $"Archive entry contains absolute path: '{entryPath}'");

        // Normalize separators to platform convention
        var normalized = NormalizeSeparators(entryPath);

        // Reject entries with parent directory traversal segments
        if (ContainsTraversalSegments(normalized))
            throw new InvalidOperationException(
                $"Archive entry contains path traversal: '{entryPath}'");

        // Reject entries starting with a drive letter (Windows)
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            throw new InvalidOperationException(
                $"Archive entry contains drive-rooted path: '{entryPath}'");

        // Reject null bytes
        if (entryPath.Contains('\0'))
            throw new InvalidOperationException(
                $"Archive entry contains null byte: '{entryPath}'");

        // Resolve the full destination path
        var fullTarget = Path.GetFullPath(targetDirectory);
        var fullDest = Path.GetFullPath(Path.Combine(fullTarget, normalized));

        // Final containment check: resolved path must be under target directory
        if (!fullDest.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(fullDest, fullTarget, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Archive entry escapes target directory: '{entryPath}' resolves to '{fullDest}'");
        }

        return fullDest;
    }

    /// <summary>
    /// Checks whether an entry path is safe without throwing.
    /// </summary>
    /// <param name="entryPath">The relative path from the archive entry.</param>
    /// <param name="targetDirectory">The root target directory for extraction.</param>
    /// <param name="resolvedPath">The resolved full path if safe; null otherwise.</param>
    /// <returns>True if the path is safe; false otherwise.</returns>
    public static bool TrySanitizeEntryPath(string entryPath, string targetDirectory, out string? resolvedPath)
    {
        resolvedPath = null;
        try
        {
            resolvedPath = SanitizeEntryPath(entryPath, targetDirectory);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes path separators to the current platform convention.
    /// Collapses redundant separators.
    /// </summary>
    public static string NormalizeSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Replace both forward and backslash with platform separator
        var normalized = path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        // Collapse repeated separators
        var sep = Path.DirectorySeparatorChar;
        var doubleSep = $"{sep}{sep}";
        while (normalized.Contains(doubleSep))
            normalized = normalized.Replace(doubleSep, sep.ToString());

        // Trim leading separator (we want relative paths)
        return normalized.TrimStart(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Checks if a path contains any parent directory traversal segments (.. or equivalent).
    /// </summary>
    public static bool ContainsTraversalSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var segments = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(s => s == "..");
    }

    /// <summary>
    /// Validates that a destination path for extraction does not already exist as a symlink
    /// pointing outside the target directory, which could be used as an attack vector.
    /// </summary>
    /// <param name="destPath">The resolved destination path.</param>
    /// <param name="targetDirectory">The root target directory.</param>
    /// <returns>True if safe; false if the path is a symlink escaping the target.</returns>
    public static bool IsSymlinkSafe(string destPath, string targetDirectory)
    {
        var fileInfo = new FileInfo(destPath);
        if (fileInfo.Exists && fileInfo.LinkTarget != null)
        {
            var linkTarget = Path.GetFullPath(fileInfo.LinkTarget, Path.GetDirectoryName(destPath)!);
            var fullTarget = Path.GetFullPath(targetDirectory);
            return linkTarget.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        var dirInfo = new DirectoryInfo(destPath);
        if (dirInfo.Exists && dirInfo.LinkTarget != null)
        {
            var linkTarget = Path.GetFullPath(dirInfo.LinkTarget, dirInfo.Parent!.FullName);
            var fullTarget = Path.GetFullPath(targetDirectory);
            return linkTarget.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        return true; // Not a symlink, safe
    }
}
