namespace Modular.Sdk.Archives;

/// <summary>
/// Abstraction over archive formats (ZIP, 7z, RAR, TAR, etc.).
/// Provides unified read-only access to archive entries.
/// </summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>
    /// Gets all entries in the archive.
    /// </summary>
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    /// Extracts a single entry to the specified destination path.
    /// </summary>
    /// <param name="entry">The entry to extract.</param>
    /// <param name="destinationPath">Full path to write the extracted file.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExtractEntryAsync(ArchiveEntry entry, string destinationPath, bool overwrite = false, CancellationToken ct = default);

    /// <summary>
    /// Extracts all entries to the specified directory.
    /// </summary>
    /// <param name="destinationDirectory">Target directory for extraction.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExtractAllAsync(string destinationDirectory, bool overwrite = false, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for an entry without extracting to disk.
    /// </summary>
    /// <param name="entry">The entry to read.</param>
    /// <returns>A readable stream of the entry's contents.</returns>
    Stream OpenEntryStream(ArchiveEntry entry);
}

/// <summary>
/// Represents a single entry within an archive.
/// </summary>
public class ArchiveEntry
{
    /// <summary>
    /// Full path of the entry within the archive (e.g., "folder/file.txt").
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// File name only (e.g., "file.txt"). Empty for directory entries.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Uncompressed size in bytes.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Compressed size in bytes.
    /// </summary>
    public long CompressedLength { get; set; }

    /// <summary>
    /// Whether this entry represents a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Last write time of the entry (if available).
    /// </summary>
    public DateTimeOffset? LastWriteTime { get; set; }

    /// <summary>
    /// CRC-32 checksum (if available from the archive format).
    /// </summary>
    public uint? Crc32 { get; set; }
}

/// <summary>
/// Factory for creating IArchiveReader instances based on file extension or content.
/// </summary>
public interface IArchiveReaderFactory
{
    /// <summary>
    /// Creates an appropriate archive reader for the given file.
    /// </summary>
    /// <param name="archivePath">Path to the archive file.</param>
    /// <returns>An archive reader, or null if the format is not supported.</returns>
    IArchiveReader? Open(string archivePath);

    /// <summary>
    /// Checks if the given file is a supported archive format.
    /// </summary>
    bool IsSupported(string archivePath);
}
