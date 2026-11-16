using System.Security.Cryptography;
using Modular.Core.Dependencies.Conventions;
using Modular.Sdk.Archives;

namespace Modular.Core.Installers;

/// <summary>
/// Deletes the archive of a mod that is installed and staged, leaving a record in its place.
/// </summary>
/// <remarks>
/// <para>
/// A staged mod's files are its copy: a redeploy, reorder or enable never opens the archive
/// again (see <see cref="StagingManager"/>). Keeping the archive as well stores the mod twice.
/// </para>
/// <para>
/// The record, <c>&lt;archive&gt;.modular.json</c>, keeps everything Modular reads from an
/// archive after its install: the hub mod and file it came from, so it can be downloaded
/// again; its name, provides and requirements, for dependency checks; and its size and
/// SHA-256, to recognise the same file. Changesets keep the archive's path, which still
/// leads to the record.
/// </para>
/// </remarks>
public static class ArchiveRelease
{
    /// <summary>
    /// Writes the record for <paramref name="archivePath"/> and deletes the archive.
    /// </summary>
    /// <param name="archivePath">The archive.</param>
    /// <param name="archiveReaderFactory">Opens the archive to read what it declares.</param>
    /// <returns>Bytes freed.</returns>
    /// <exception cref="IOException">The record could not be written; the archive is untouched.</exception>
    public static long Release(
        string archivePath,
        IArchiveReaderFactory archiveReaderFactory)
    {
        ArgumentNullException.ThrowIfNull(archivePath);
        ArgumentNullException.ThrowIfNull(archiveReaderFactory);

        var info = new FileInfo(archivePath);
        if (!info.Exists)
            return 0;

        var declarations = DependencyConventions.Read(archivePath, archiveReaderFactory);
        var (sha256, md5) = HashFile(archivePath);
        var released = new ReleasedArchive
        {
            FileName = info.Name,
            Size = info.Length,
            Sha256 = sha256,
            Md5 = md5,
            ReleasedAtUtc = DateTimeOffset.UtcNow
        };

        DependencyConventions.WriteReleasedRecord(archivePath, declarations, released);

        File.Delete(archivePath);
        return info.Length;
    }

    /// <summary>
    /// Whether a path names an archive that was released.
    /// </summary>
    /// <param name="archivePath">Path the archive had.</param>
    /// <returns><c>true</c> when the archive is gone and its record says it was released.</returns>
    public static bool IsReleased(string? archivePath) =>
        !string.IsNullOrEmpty(archivePath) &&
        !File.Exists(archivePath) &&
        DependencyConventions.ReadReleased(archivePath) != null;

    // One read for both: SHA-256 to recognise the file, MD5 because that is what NexusMods
    // (md5_search) and mod.io (filehash) can look a file up by.
    private static (string Sha256, string Md5) HashFile(string path)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var stream = File.OpenRead(path);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
        }

        return (Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant());
    }
}
