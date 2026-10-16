namespace Modular.Core.Installers;

/// <summary>
/// Where Modular keeps the archives of mods installed from somewhere it does not manage,
/// such as the browser's Downloads folder.
/// </summary>
/// <remarks>
/// A changeset records the archive it was installed from, and a reinstall or snapshot
/// restore goes back to that file. An archive left in Downloads is soon cleaned away, so
/// the restore then fails. Adopting the archive into the store at install time keeps it
/// where Modular can find it.
/// </remarks>
public sealed class ArchiveStore
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IReadOnlyList<string> _managedRoots;

    /// <summary>
    /// Name of the store's folder inside the mods directory. The leading dot keeps it out
    /// of the game folders the mods directory is otherwise read as.
    /// </summary>
    public const string FolderName = ".archives";

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="storePath">Directory adopted archives are kept in.</param>
    /// <param name="managedRoots">
    /// Other directories Modular already keeps archives in, such as the mods directory
    /// downloads go to. An archive under one of them is left where it is.
    /// </param>
    public ArchiveStore(string storePath, IEnumerable<string?>? managedRoots = null)
    {
        StorePath = Path.GetFullPath(storePath);
        _managedRoots = (managedRoots ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r!))
            .Append(StorePath)
            .ToList();
    }

    /// <summary>Directory adopted archives are kept in.</summary>
    public string StorePath { get; }

    /// <summary>
    /// Whether an archive is already somewhere Modular keeps archives.
    /// </summary>
    /// <param name="archivePath">The archive.</param>
    /// <returns><c>true</c> when it needs no adopting.</returns>
    public bool IsManaged(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        return _managedRoots.Any(root => IsUnder(full, root));
    }

    /// <summary>
    /// Brings an archive into the store.
    /// </summary>
    /// <param name="archivePath">The archive to adopt.</param>
    /// <param name="gameKey">Game the archive was installed for; names its folder in the store.</param>
    /// <param name="copy">
    /// Copy instead of move, for a source that must stay where it is, such as a file in
    /// the game folder itself.
    /// </param>
    /// <returns>The archive's path in the store.</returns>
    /// <exception cref="IOException">The archive could not be moved or copied.</exception>
    public string Adopt(string archivePath, string gameKey, bool copy = false)
    {
        var source = Path.GetFullPath(archivePath);
        var directory = Path.Combine(StorePath, SanitizeSegment(gameKey));
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, Path.GetFileName(source));

        // The same archive installed again, or a second download of it, takes the copy
        // already kept; a different file of the same name gets a numbered one.
        var index = 1;
        while (File.Exists(destination))
        {
            if (SameContent(source, destination))
            {
                if (!copy)
                    File.Delete(source);
                return destination;
            }

            index++;
            destination = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(source)} ({index}){Path.GetExtension(source)}");
        }

        if (copy)
            File.Copy(source, destination);
        else
            File.Move(source, destination);

        return destination;
    }

    /// <summary>
    /// Whether a path is a directory or lies inside it.
    /// </summary>
    /// <param name="path">Full path to test.</param>
    /// <param name="root">Directory.</param>
    /// <returns><c>true</c> when <paramref name="path"/> is under <paramref name="root"/>.</returns>
    public static bool IsUnder(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(fullRoot, PathComparison) ||
               path.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;

        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(streamA)
            .AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(streamB));
    }

    private static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
