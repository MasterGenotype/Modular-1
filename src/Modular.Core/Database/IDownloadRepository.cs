namespace Modular.Core.Database;

/// <summary>
/// Interface for download record persistence.
/// Abstracts the storage implementation (JSON, SQLite, etc.) from consumers.
/// </summary>
public interface IDownloadRepository
{
    /// <summary>
    /// Adds or updates a download record.
    /// </summary>
    /// <param name="record">The download record to add</param>
    void AddRecord(DownloadRecord record);

    /// <summary>
    /// Finds a download record by game domain, mod ID, and file ID.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <returns>Record if found, null otherwise</returns>
    DownloadRecord? FindRecord(string gameDomain, int modId, int fileId);

    /// <summary>
    /// Gets all download records for a specific game domain.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <returns>List of records for the domain</returns>
    IEnumerable<DownloadRecord> GetRecordsByDomain(string gameDomain);

    /// <summary>
    /// Gets all download records for a specific mod.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <returns>List of records for the mod</returns>
    IEnumerable<DownloadRecord> GetRecordsByMod(string gameDomain, int modId);

    /// <summary>
    /// Checks if a file has already been downloaded successfully.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <returns>True if file was downloaded and verified successfully</returns>
    bool IsDownloaded(string gameDomain, int modId, int fileId);

    /// <summary>
    /// Updates the MD5 hash and verification status of a record.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <param name="md5Actual">Actual MD5 hash</param>
    /// <param name="verified">Whether MD5 matches expected value</param>
    void UpdateVerification(string gameDomain, int modId, int fileId, string md5Actual, bool verified);

    /// <summary>
    /// Removes a record from the database.
    /// </summary>
    /// <param name="gameDomain">Game domain</param>
    /// <param name="modId">Mod ID</param>
    /// <param name="fileId">File ID</param>
    /// <returns>True if record was found and removed</returns>
    bool RemoveRecord(string gameDomain, int modId, int fileId);

    /// <summary>
    /// Gets total number of records in the repository.
    /// </summary>
    int GetRecordCount();

    /// <summary>
    /// Persists all changes to storage.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Loads data from storage.
    /// </summary>
    Task LoadAsync();
}
