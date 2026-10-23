using Modular.Core.Utilities;
using Modular.Sdk.Archives;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Modular.Core.Archives;

/// <summary>
/// IArchiveReader implementation using SharpCompress for 7z, RAR, TAR, GZ, and other formats.
/// </summary>
public class SharpCompressArchiveReader : IArchiveReader
{
    private readonly IArchive _archive;
    private readonly List<ArchiveEntry> _entries;
    private readonly Dictionary<string, IArchiveEntry> _sharpEntryMap;

    // Opening one entry of a solid archive decompresses everything packed before it, so
    // extracting entry by entry costs the whole archive again for every file. A forward
    // reader decompresses once; it is kept across calls and only restarted when a caller
    // asks for an entry it has already gone past.
    private readonly bool _sequential;
    private readonly HashSet<string> _passedKeys = new();
    private IReader? _forwardReader;

    public SharpCompressArchiveReader(string archivePath)
    {
        _archive = ArchiveFactory.OpenArchive(archivePath);
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
                Crc32 = CrcOf(entry)
            };

            _entries.Add(archiveEntry);
            if (!string.IsNullOrEmpty(key))
                _sharpEntryMap[key] = entry;
        }

        _sequential = _archive.IsSolid || _archive.Type == ArchiveType.SevenZip;
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

        await using var destStream = File.Create(destinationPath);

        if (_sequential && TryAdvanceTo(entry.FullName))
        {
            await using var forwardStream = _forwardReader!.OpenEntryStream();
            await forwardStream.CopyToAsync(destStream, ct);
            return;
        }

        await using var sourceStream = sharpEntry.OpenEntryStream();
        await sourceStream.CopyToAsync(destStream, ct);
    }

    /// <summary>
    /// Moves the forward reader onto the entry with the given key.
    /// </summary>
    /// <returns>False if the forward pass does not yield that entry.</returns>
    private bool TryAdvanceTo(string key)
    {
        if (_forwardReader == null || _passedKeys.Contains(key))
            ResetForwardReader();

        _forwardReader ??= _archive.ExtractAllEntries();

        while (_forwardReader.MoveToNextEntry())
        {
            var current = _forwardReader.Entry.Key ?? string.Empty;
            _passedKeys.Add(current);

            if (!_forwardReader.Entry.IsDirectory && current == key)
                return true;
        }

        ResetForwardReader();
        return false;
    }

    // RAR5 entries need not carry a CRC32 (directories, files hashed with BLAKE2), and
    // SharpCompress throws rather than returning 0 for them. The CRC is only a hint, so
    // a missing one is left null instead of failing the whole archive.
    private static uint? CrcOf(IArchiveEntry entry)
    {
        try
        {
            return entry.Crc != 0 ? (uint)entry.Crc : null;
        }
        catch (ArgumentNullException)
        {
            return null;
        }
    }

    private void ResetForwardReader()
    {
        _forwardReader?.Dispose();
        _forwardReader = null;
        _passedKeys.Clear();
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
        _forwardReader?.Dispose();
        _archive.Dispose();
    }
}
