using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;
using Modular.Core.Utilities;

namespace Modular.Core.Archives;

/// <summary>
/// Content-addressed blob store. Files are stored by their SHA-256 hash,
/// enabling deduplication when multiple mods share identical files.
/// </summary>
public class BlobStore
{
    private readonly ModularDatabase _database;
    private readonly string _blobDirectory;
    private readonly ILogger<BlobStore>? _logger;

    /// <summary>
    /// Creates a new blob store.
    /// </summary>
    /// <param name="database">Database for blob metadata.</param>
    /// <param name="blobDirectory">Directory for blob file storage.</param>
    /// <param name="logger">Optional logger.</param>
    public BlobStore(
        ModularDatabase database,
        string blobDirectory,
        ILogger<BlobStore>? logger = null)
    {
        _database = database;
        _blobDirectory = blobDirectory;
        _logger = logger;
        Directory.CreateDirectory(_blobDirectory);
    }

    /// <summary>
    /// Stores a file in the blob store, returning its SHA-256 hash.
    /// If the blob already exists, the file is not duplicated.
    /// </summary>
    /// <param name="filePath">Path to the file to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SHA-256 hash of the stored blob.</returns>
    public async Task<string> StoreAsync(string filePath, CancellationToken ct = default)
    {
        var sha256 = await HashUtility.ComputeFileHashAsync(filePath, ct: ct);
        var connection = await _database.GetConnectionAsync();

        // Check if blob already exists
        var existing = await GetBlobPathAsync(connection, sha256, ct);
        if (existing != null)
        {
            // Increment ref count
            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE blob SET ref_count = ref_count + 1 WHERE sha256 = @sha256";
            updateCmd.Parameters.AddWithValue("@sha256", sha256);
            await updateCmd.ExecuteNonQueryAsync(ct);

            _logger?.LogDebug("Blob {Hash} already exists, incremented ref count", sha256[..12]);
            return sha256;
        }

        // Store the blob
        var storagePath = GetStoragePath(sha256);
        var storageDir = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrEmpty(storageDir))
            Directory.CreateDirectory(storageDir);

        File.Copy(filePath, storagePath, overwrite: true);

        var fileInfo = new FileInfo(filePath);
        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO blob (sha256, storage_path, size_bytes, ref_count, created_at_utc)
            VALUES (@sha256, @path, @size, 1, @created)
            """;
        insertCmd.Parameters.AddWithValue("@sha256", sha256);
        insertCmd.Parameters.AddWithValue("@path", storagePath);
        insertCmd.Parameters.AddWithValue("@size", fileInfo.Length);
        insertCmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));
        await insertCmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug("Stored blob {Hash}: {Size} bytes", sha256[..12], fileInfo.Length);
        return sha256;
    }

    /// <summary>
    /// Retrieves a blob file path by its hash.
    /// </summary>
    /// <param name="sha256">The SHA-256 hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the blob file, or null if not found.</returns>
    public async Task<string?> GetAsync(string sha256, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        return await GetBlobPathAsync(connection, sha256, ct);
    }

    /// <summary>
    /// Links (copies) a blob to a destination path. Returns true if the blob existed.
    /// </summary>
    public async Task<bool> LinkToAsync(string sha256, string destinationPath, CancellationToken ct = default)
    {
        var blobPath = await GetAsync(sha256, ct);
        if (blobPath == null || !File.Exists(blobPath))
            return false;

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        File.Copy(blobPath, destinationPath, overwrite: true);
        return true;
    }

    /// <summary>
    /// Decrements the reference count and deletes the blob if it reaches zero.
    /// </summary>
    public async Task ReleaseAsync(string sha256, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE blob SET ref_count = ref_count - 1 WHERE sha256 = @sha256";
        cmd.Parameters.AddWithValue("@sha256", sha256);
        await cmd.ExecuteNonQueryAsync(ct);

        // Delete if ref_count dropped to 0
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT storage_path FROM blob WHERE sha256 = @sha256 AND ref_count <= 0";
        checkCmd.Parameters.AddWithValue("@sha256", sha256);
        var path = await checkCmd.ExecuteScalarAsync(ct) as string;

        if (path != null)
        {
            if (File.Exists(path))
                File.Delete(path);

            await using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM blob WHERE sha256 = @sha256";
            deleteCmd.Parameters.AddWithValue("@sha256", sha256);
            await deleteCmd.ExecuteNonQueryAsync(ct);

            _logger?.LogDebug("Deleted unreferenced blob {Hash}", sha256[..12]);
        }
    }

    private static async Task<string?> GetBlobPathAsync(SqliteConnection connection, string sha256, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT storage_path FROM blob WHERE sha256 = @sha256";
        cmd.Parameters.AddWithValue("@sha256", sha256);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Storage path uses first 2 chars of hash as subdirectory (Git-style).
    /// </summary>
    private string GetStoragePath(string sha256)
    {
        return Path.Combine(_blobDirectory, sha256[..2], sha256);
    }
}
