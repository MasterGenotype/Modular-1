using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using SharpCompress.Archives;

namespace Modular.Core.Archives;

/// <summary>
/// IArchiveReader implementation using SharpCompress for 7z, RAR, TAR, GZ, and other formats.
/// </summary>
public class SharpCompressArchiveReader : IArchiveReader
{
    private readonly IArchive _archive;
    private readonly List<ArchiveEntry> _entries;
    private readonly Dictionary<string, IArchiveEntry> _sharpEntryMap;

    public SharpCompressArchiveReader(string archivePath)
    {
        _archive = ArchiveFactory.Open(archivePath);
        _sharpEntryMap = new Dictionary<string, IArchiveEntry>();
        _entries = new List<ArchiveEntry>();

        foreach (var entry in _archive.Entries)
        {
            var key = entry.Key ?? string.Empty;
            var archiveEntry = new ArchiveEntry
            {
                FullName = key,
                Name = Path.GetFileName(key.TrimEnd('/', '\\')),
                Length = entry.Size,
                CompressedLength = entry.CompressedSize,
                IsDirectory = entry.IsDirectory,
                LastWriteTime = entry.LastModifiedTime.HasValue
                    ? new DateTimeOffset(entry.LastModifiedTime.Value)
                    : null,
                Crc32 = entry.Crc != 0 ? (uint)entry.Crc : null
            };

            _entries.Add(archiveEntry);
            if (!string.IsNullOrEmpty(key))
                _sharpEntryMap[key] = entry;
        }
    }

    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public async Task ExtractEntryAsync(ArchiveEntry entry, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_sharpEntryMap.TryGetValue(entry.FullName, out var sharpEntry))
            throw new FileNotFoundException($"Entry not found in archive: {entry.FullName}");

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(destinationPath);
            return;
        }

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(destinationPath) && !overwrite)
            throw new IOException($"File already exists: {destinationPath}");

        await using var sourceStream = sharpEntry.OpenEntryStream();
        await using var destStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destStream, ct);
    }

    public async Task ExtractAllAsync(string destinationDirectory, bool overwrite = false, CancellationToken ct = default)
    {
        foreach (var entry in _entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
                continue;

            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destPath = PathSanitizer.SanitizeEntryPath(entry.FullName, destinationDirectory);
            await ExtractEntryAsync(entry, destPath, overwrite, ct);
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        if (!_sharpEntryMap.TryGetValue(entry.FullName, out var sharpEntry))
            throw new FileNotFoundException($"Entry not found in archive: {entry.FullName}");

        return sharpEntry.OpenEntryStream();
    }

    public void Dispose()
    {
        _archive.Dispose();
    }
}
