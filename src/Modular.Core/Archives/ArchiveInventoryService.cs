using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;
using Modular.Core.Utilities;
using Modular.Sdk.Archives;

namespace Modular.Core.Archives;

/// <summary>
/// Persists archive inventories in the database for fast lookups.
/// On first scan, reads the archive and stores its entry list.
/// Subsequent queries use the cached inventory without re-reading the file.
/// </summary>
public class ArchiveInventoryService
{
    private readonly ModularDatabase _database;
    private readonly ArchiveReaderFactory _readerFactory;
    private readonly ILogger<ArchiveInventoryService>? _logger;

    public ArchiveInventoryService(
        ModularDatabase database,
        ArchiveReaderFactory? readerFactory = null,
        ILogger<ArchiveInventoryService>? logger = null)
    {
        _database = database;
        _readerFactory = readerFactory ?? new ArchiveReaderFactory();
        _logger = logger;
    }

    /// <summary>
    /// Gets the inventory for an archive, scanning it if not already cached.
    /// </summary>
    /// <param name="archivePath">Path to the archive file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of archive entries.</returns>
    public async Task<List<ArchiveEntryRecord>> GetInventoryAsync(string archivePath, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        // Check cache
        var cached = await GetCachedInventoryAsync(connection, archivePath, ct);
        if (cached != null)
            return cached;

        // Scan and cache
        return await ScanAndCacheAsync(connection, archivePath, ct);
    }

    /// <summary>
    /// Forces a rescan of the archive inventory.
    /// </summary>
    public async Task<List<ArchiveEntryRecord>> RescanAsync(string archivePath, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        // Delete existing cache
        await using var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM archive WHERE path = @path";
        deleteCmd.Parameters.AddWithValue("@path", archivePath);
        await deleteCmd.ExecuteNonQueryAsync(ct);

        return await ScanAndCacheAsync(connection, archivePath, ct);
    }

    private async Task<List<ArchiveEntryRecord>?> GetCachedInventoryAsync(
        SqliteConnection connection, string archivePath, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, mtime FROM archive WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", archivePath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var archiveId = reader.GetInt64(0);
        var cachedMtime = reader.IsDBNull(1) ? null : reader.GetString(1);

        // Validate cache freshness by checking mtime
        if (File.Exists(archivePath))
        {
            var currentMtime = File.GetLastWriteTimeUtc(archivePath).ToString("O");
            if (cachedMtime != currentMtime)
                return null; // Stale cache
        }

        // Read cached entries
        await using var entriesCmd = connection.CreateCommand();
        entriesCmd.CommandText = "SELECT inner_path, entry_type, size_bytes, compressed_bytes, crc32, sha256 FROM archive_entry WHERE archive_id = @id";
        entriesCmd.Parameters.AddWithValue("@id", archiveId);

        var entries = new List<ArchiveEntryRecord>();
        await using var entriesReader = await entriesCmd.ExecuteReaderAsync(ct);
        while (await entriesReader.ReadAsync(ct))
        {
            entries.Add(new ArchiveEntryRecord
            {
                InnerPath = entriesReader.GetString(0),
                EntryType = entriesReader.GetString(1),
                SizeBytes = entriesReader.GetInt64(2),
                CompressedBytes = entriesReader.GetInt64(3),
                Crc32 = entriesReader.IsDBNull(4) ? null : (uint)entriesReader.GetInt64(4),
                Sha256 = entriesReader.IsDBNull(5) ? null : entriesReader.GetString(5)
            });
        }

        _logger?.LogDebug("Cache hit for archive {Path}: {Count} entries", archivePath, entries.Count);
        return entries;
    }

    private async Task<List<ArchiveEntryRecord>> ScanAndCacheAsync(
        SqliteConnection connection, string archivePath, CancellationToken ct)
    {
        using var archive = _readerFactory.Open(archivePath)
            ?? throw new InvalidOperationException($"Unsupported archive format: {archivePath}");

        var fileInfo = new FileInfo(archivePath);
        var sha256 = await HashUtility.ComputeFileHashAsync(archivePath, ct: ct);
        var format = Path.GetExtension(archivePath).TrimStart('.');

        // Insert archive record
        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT OR REPLACE INTO archive (path, size_bytes, mtime, sha256, format, entry_count, scanned_at_utc)
            VALUES (@path, @size, @mtime, @sha256, @format, @count, @scanned)
            """;
        insertCmd.Parameters.AddWithValue("@path", archivePath);
        insertCmd.Parameters.AddWithValue("@size", fileInfo.Length);
        insertCmd.Parameters.AddWithValue("@mtime", fileInfo.LastWriteTimeUtc.ToString("O"));
        insertCmd.Parameters.AddWithValue("@sha256", sha256);
        insertCmd.Parameters.AddWithValue("@format", format);
        insertCmd.Parameters.AddWithValue("@count", archive.Entries.Count);
        insertCmd.Parameters.AddWithValue("@scanned", DateTime.UtcNow.ToString("O"));
        await insertCmd.ExecuteNonQueryAsync(ct);

        // Get the archive ID
        await using var idCmd = connection.CreateCommand();
        idCmd.CommandText = "SELECT id FROM archive WHERE path = @path";
        idCmd.Parameters.AddWithValue("@path", archivePath);
        var archiveId = (long)(await idCmd.ExecuteScalarAsync(ct))!;

        // Insert entries
        var entries = new List<ArchiveEntryRecord>();
        foreach (var entry in archive.Entries)
        {
            var record = new ArchiveEntryRecord
            {
                InnerPath = entry.FullName,
                EntryType = entry.IsDirectory ? "directory" : "file",
                SizeBytes = entry.Length,
                CompressedBytes = entry.CompressedLength,
                Crc32 = entry.Crc32
            };
            entries.Add(record);

            await using var entryCmd = connection.CreateCommand();
            entryCmd.CommandText = """
                INSERT INTO archive_entry (archive_id, inner_path, entry_type, size_bytes, compressed_bytes, crc32)
                VALUES (@archiveId, @path, @type, @size, @compressed, @crc)
                """;
            entryCmd.Parameters.AddWithValue("@archiveId", archiveId);
            entryCmd.Parameters.AddWithValue("@path", entry.FullName);
            entryCmd.Parameters.AddWithValue("@type", record.EntryType);
            entryCmd.Parameters.AddWithValue("@size", entry.Length);
            entryCmd.Parameters.AddWithValue("@compressed", entry.CompressedLength);
            entryCmd.Parameters.AddWithValue("@crc", entry.Crc32.HasValue ? (object)(long)entry.Crc32.Value : DBNull.Value);
            await entryCmd.ExecuteNonQueryAsync(ct);
        }

        _logger?.LogInformation("Scanned archive {Path}: {Count} entries", archivePath, entries.Count);
        return entries;
    }
}

/// <summary>
/// Represents a cached archive entry record.
/// </summary>
public class ArchiveEntryRecord
{
    public string InnerPath { get; set; } = string.Empty;
    public string EntryType { get; set; } = "file";
    public long SizeBytes { get; set; }
    public long CompressedBytes { get; set; }
    public uint? Crc32 { get; set; }
    public string? Sha256 { get; set; }
}
