using System.IO.Compression;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;

namespace Modular.Core.Archives;

/// <summary>
/// IArchiveReader implementation for ZIP archives using System.IO.Compression.
/// </summary>
public class ZipArchiveReader : IArchiveReader
{
    private readonly ZipArchive _archive;
    private readonly List<ArchiveEntry> _entries;

    public ZipArchiveReader(string archivePath)
    {
        _archive = ZipFile.OpenRead(archivePath);
        _entries = _archive.Entries.Select(e => new ArchiveEntry
        {
            FullName = e.FullName,
            Name = e.Name,
            Length = e.Length,
            CompressedLength = e.CompressedLength,
            IsDirectory = string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith('/'),
            LastWriteTime = e.LastWriteTime,
            Crc32 = (uint)e.Crc32
        }).ToList();
    }

    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public async Task ExtractEntryAsync(ArchiveEntry entry, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var zipEntry = _archive.GetEntry(entry.FullName)
            ?? throw new FileNotFoundException($"Entry not found in archive: {entry.FullName}");

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(destinationPath);
            return;
        }

        zipEntry.ExtractToFile(destinationPath, overwrite);
        await Task.CompletedTask;
    }

    public async Task ExtractAllAsync(string destinationDirectory, bool overwrite = false, CancellationToken ct = default)
    {
        foreach (var entry in _entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
                continue;

            var destPath = PathSanitizer.SanitizeEntryPath(entry.FullName, destinationDirectory);
            await ExtractEntryAsync(entry, destPath, overwrite, ct);
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var zipEntry = _archive.GetEntry(entry.FullName)
            ?? throw new FileNotFoundException($"Entry not found in archive: {entry.FullName}");

        return zipEntry.Open();
    }

    public void Dispose()
    {
        _archive.Dispose();
    }
}
