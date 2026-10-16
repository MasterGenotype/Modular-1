using System.IO.Compression;

namespace Modular.Core.Installers;

/// <summary>
/// A mod that is a single file rather than an archive, such as a Baldur's Gate 3
/// <c>.pak</c>, wrapped in a temporary zip so the archive pipeline can install it.
/// </summary>
/// <remarks>
/// The zip holds the file at its root under its own name, which is how such a mod is
/// usually packed. It is stored, not compressed: paks are already compressed and can run
/// to gigabytes. The zip is deleted on dispose.
/// </remarks>
public sealed class LooseModFile : IDisposable
{
    /// <summary>Extensions installed as a bare file instead of read as an archive.</summary>
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pak" };

    private readonly string _directory;

    private LooseModFile(string directory, string archivePath)
    {
        _directory = directory;
        ArchivePath = archivePath;
    }

    /// <summary>The temporary zip to read in place of the bare file.</summary>
    public string ArchivePath { get; }

    /// <summary>
    /// Wraps a bare mod file, or returns <c>null</c> for anything that is read as an archive.
    /// </summary>
    /// <param name="path">The file given to install.</param>
    /// <returns>The wrapper, or <c>null</c>.</returns>
    public static LooseModFile? WrapIfNeeded(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path)))
            return null;

        var directory = Path.Combine(Path.GetTempPath(), "modular-loose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".zip");
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.NoCompression);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }

        return new LooseModFile(directory, zipPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover in the temp folder is harmless.
        }
    }
}
