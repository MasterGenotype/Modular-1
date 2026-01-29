using System.Text.Json;
using Modular.Core.Exceptions;

namespace Modular.Core.Database;

/// <summary>
/// JSON-based database for tracking download history.
/// Thread-safe for concurrent access.
/// </summary>
public class DownloadDatabase
{
    private readonly string _dbPath;
    private readonly List<DownloadRecord> _records = [];
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Creates/opens a database at the specified path.
    /// </summary>
    /// <param name="dbPath">Path to database file (will be created if doesn't exist)</param>
    public DownloadDatabase(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// Adds a download record to the database.
    /// </summary>
    /// <param name="record">The download record to add</param>
    public void AddRecord(DownloadRecord record)
    {
        lock (_lock)
        {
            // Remove existing record with same key if exists
            _records.RemoveAll(r =>
                r.GameDomain == record.GameDomain &&
                r.ModId == record.ModId &&
                r.FileId == record.FileId);

            _records.Add(record);
        }
    }

    /// <summary>
    /// Finds a download record by game domain, mod ID, and file ID.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <returns>Record if found, null otherwise</returns>
    public DownloadRecord? FindRecord(string gameDomain, int modId, int fileId)
    {
        lock (_lock)
        {
            return _records.FirstOrDefault(r =>
                r.GameDomain == gameDomain &&
                r.ModId == modId &&
                r.FileId == fileId);
        }
    }

    /// <summary>
    /// Gets all download records for a specific game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>List of records for the domain</returns>
    public IEnumerable<DownloadRecord> GetRecordsByDomain(string gameDomain)
    {
        lock (_lock)
        {
            return _records.Where(r => r.GameDomain == gameDomain).ToList();
        }
    }

    /// <summary>
    /// Gets all download records for a specific mod.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <returns>List of records for the mod</returns>
    public IEnumerable<DownloadRecord> GetRecordsByMod(string gameDomain, int modId)
    {
        lock (_lock)
        {
            return _records.Where(r => r.GameDomain == gameDomain && r.ModId == modId).ToList();
        }
    }

    /// <summary>
    /// Checks if a file has already been downloaded successfully.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <returns>True if file was downloaded and verified successfully</returns>
    public bool IsDownloaded(string gameDomain, int modId, int fileId)
    {
        var record = FindRecord(gameDomain, modId, fileId);
        return record != null && (record.Status == "success" || record.Status == "verified");
    }

    /// <summary>
    /// Updates the MD5 hash and verification status of a record.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <param name="md5Actual">Actual MD5 hash</param>
    /// <param name="verified">Whether MD5 matches expected value</param>
    public void UpdateVerification(string gameDomain, int modId, int fileId, string md5Actual, bool verified)
    {
        lock (_lock)
        {
            var record = _records.FirstOrDefault(r =>
                r.GameDomain == gameDomain &&
                r.ModId == modId &&
                r.FileId == fileId);

            if (record != null)
            {
                record.Md5Actual = md5Actual;
                record.Status = verified ? "verified" : "hash_mismatch";
            }
        }
    }

    /// <summary>
    /// Removes a record from the database.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <returns>True if record was found and removed</returns>
    public bool RemoveRecord(string gameDomain, int modId, int fileId)
    {
        lock (_lock)
        {
            return _records.RemoveAll(r =>
                r.GameDomain == gameDomain &&
                r.ModId == modId &&
                r.FileId == fileId) > 0;
        }
    }

    /// <summary>
    /// Gets total number of records in the database.
    /// </summary>
    public int GetRecordCount()
    {
        lock (_lock)
        {
            return _records.Count;
        }
    }

    /// <summary>
    /// Saves the database to disk.
    /// </summary>
    public async Task SaveAsync()
    {
        List<DownloadRecord> snapshot;
        lock (_lock)
        {
            snapshot = [.. _records];
        }

        try
        {
            var directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(_dbPath, json);
        }
        catch (IOException ex)
        {
            throw new FileSystemException($"Failed to save database: {ex.Message}", ex)
            {
                FilePath = _dbPath
            };
        }
    }

    /// <summary>
    /// Loads the database from disk.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(_dbPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_dbPath);
            var records = JsonSerializer.Deserialize<List<DownloadRecord>>(json, JsonOptions);

            lock (_lock)
            {
                _records.Clear();
                if (records != null)
                    _records.AddRange(records);
            }
        }
        catch (JsonException ex)
        {
            throw new ParseException($"Failed to parse database: {ex.Message}", ex)
            {
                Context = _dbPath
            };
        }
        catch (IOException ex)
        {
            throw new FileSystemException($"Failed to load database: {ex.Message}", ex)
            {
                FilePath = _dbPath
            };
        }
    }
}
