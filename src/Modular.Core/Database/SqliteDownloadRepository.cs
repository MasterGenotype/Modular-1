using Microsoft.Data.Sqlite;

namespace Modular.Core.Database;

/// <summary>
/// SQLite-backed implementation of <see cref="IDownloadRepository"/>.
/// Provides ACID-compliant storage with indexed queries.
/// </summary>
public sealed class SqliteDownloadRepository : IDownloadRepository
{
    private readonly ModularDatabase _database;

    /// <summary>
    /// Creates a new SQLite download repository.
    /// </summary>
    /// <param name="database">The SQLite database manager.</param>
    public SqliteDownloadRepository(ModularDatabase database)
    {
        _database = database;
    }

    /// <inheritdoc />
    public void AddRecord(DownloadRecord record)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();
        AddRecordInternal(connection, record);
    }

    private static void AddRecordInternal(SqliteConnection connection, DownloadRecord record)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO downloads 
                (game_domain, mod_id, file_id, filename, file_path, downloaded_at, 
                 file_size, md5_expected, md5_actual, status, url, error_message)
            VALUES 
                (@game_domain, @mod_id, @file_id, @filename, @file_path, @downloaded_at,
                 @file_size, @md5_expected, @md5_actual, @status, @url, @error_message)
            """;

        cmd.Parameters.AddWithValue("@game_domain", record.GameDomain);
        cmd.Parameters.AddWithValue("@mod_id", record.ModId);
        cmd.Parameters.AddWithValue("@file_id", record.FileId);
        cmd.Parameters.AddWithValue("@filename", record.Filename);
        cmd.Parameters.AddWithValue("@file_path", record.Filepath);
        cmd.Parameters.AddWithValue("@downloaded_at", record.DownloadTime.ToString("O"));
        cmd.Parameters.AddWithValue("@file_size", record.FileSize);
        cmd.Parameters.AddWithValue("@md5_expected", record.Md5Expected);
        cmd.Parameters.AddWithValue("@md5_actual", record.Md5Actual);
        cmd.Parameters.AddWithValue("@status", (int)record.Status);
        cmd.Parameters.AddWithValue("@url", record.Url);
        cmd.Parameters.AddWithValue("@error_message", (object?)record.ErrorMessage ?? DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public DownloadRecord? FindRecord(string gameDomain, int modId, int fileId)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();
        return FindRecordInternal(connection, gameDomain, modId, fileId);
    }

    private static DownloadRecord? FindRecordInternal(SqliteConnection connection, string gameDomain, int modId, int fileId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_domain, mod_id, file_id, filename, file_path, downloaded_at,
                   file_size, md5_expected, md5_actual, status, url, error_message
            FROM downloads
            WHERE game_domain = @game_domain AND mod_id = @mod_id AND file_id = @file_id
            """;

        cmd.Parameters.AddWithValue("@game_domain", gameDomain);
        cmd.Parameters.AddWithValue("@mod_id", modId);
        cmd.Parameters.AddWithValue("@file_id", fileId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadRecord(reader);
        }

        return null;
    }

    /// <inheritdoc />
    public IEnumerable<DownloadRecord> GetRecordsByDomain(string gameDomain)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();
        var records = new List<DownloadRecord>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_domain, mod_id, file_id, filename, file_path, downloaded_at,
                   file_size, md5_expected, md5_actual, status, url, error_message
            FROM downloads
            WHERE game_domain = @game_domain
            """;

        cmd.Parameters.AddWithValue("@game_domain", gameDomain);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    /// <inheritdoc />
    public IEnumerable<DownloadRecord> GetRecordsByMod(string gameDomain, int modId)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();
        var records = new List<DownloadRecord>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_domain, mod_id, file_id, filename, file_path, downloaded_at,
                   file_size, md5_expected, md5_actual, status, url, error_message
            FROM downloads
            WHERE game_domain = @game_domain AND mod_id = @mod_id
            """;

        cmd.Parameters.AddWithValue("@game_domain", gameDomain);
        cmd.Parameters.AddWithValue("@mod_id", modId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    /// <inheritdoc />
    public bool IsDownloaded(string gameDomain, int modId, int fileId)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT status FROM downloads
            WHERE game_domain = @game_domain AND mod_id = @mod_id AND file_id = @file_id
            """;

        cmd.Parameters.AddWithValue("@game_domain", gameDomain);
        cmd.Parameters.AddWithValue("@mod_id", modId);
        cmd.Parameters.AddWithValue("@file_id", fileId);

        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            return false;

        var status = (DownloadStatus)Convert.ToInt32(result);
        return status == DownloadStatus.Success || status == DownloadStatus.Verified;
    }

    /// <inheritdoc />
    public void UpdateVerification(string gameDomain, int modId, int fileId, string md5Actual, bool verified)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE downloads
            SET md5_actual = @md5_actual, status = @status
            WHERE game_domain = @game_domain AND mod_id = @mod_id AND file_id = @file_id
            """;

        cmd.Parameters.AddWithValue("@md5_actual", md5Actual);
        cmd.Parameters.AddWithValue("@status", (int)(verified ? DownloadStatus.Verified : DownloadStatus.HashMismatch));
        cmd.Parameters.AddWithValue("@game_domain", gameDomain);
        cmd.Parameters.AddWithValue("@mod_id", modId);
        cmd.Parameters.AddWithValue("@file_id", fileId);

        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public bool RemoveRecord(string gameDomain, int modId, int fileId)
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM downloads
            WHERE game_domain = @game_domain AND mod_id = @mod_id AND file_id = @file_id
            """;

        cmd.Parameters.AddWithValue("@game_domain", gameDomain);
        cmd.Parameters.AddWithValue("@mod_id", modId);
        cmd.Parameters.AddWithValue("@file_id", fileId);

        return cmd.ExecuteNonQuery() > 0;
    }

    /// <inheritdoc />
    public int GetRecordCount()
    {
        var connection = _database.GetConnectionAsync().GetAwaiter().GetResult();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM downloads";

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Saves any pending changes.
    /// Note: SQLite writes are immediate, so this is a no-op for compatibility.
    /// </summary>
    public Task SaveAsync() => Task.CompletedTask;

    /// <summary>
    /// Loads the database.
    /// Note: SQLite connections are lazy, so this initializes the schema.
    /// </summary>
    public Task LoadAsync() => _database.InitializeAsync();

    private static DownloadRecord ReadRecord(SqliteDataReader reader)
    {
        return new DownloadRecord
        {
            GameDomain = reader.GetString(0),
            ModId = reader.GetInt32(1),
            FileId = reader.GetInt32(2),
            Filename = reader.GetString(3),
            Filepath = reader.GetString(4),
            DownloadTime = DateTime.Parse(reader.GetString(5)),
            FileSize = reader.GetInt64(6),
            Md5Expected = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            Md5Actual = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            Status = (DownloadStatus)reader.GetInt32(9),
            Url = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11)
        };
    }
}
