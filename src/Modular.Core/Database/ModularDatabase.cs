using Microsoft.Data.Sqlite;

namespace Modular.Core.Database;

/// <summary>
/// SQLite database manager for Modular.
/// Handles connection management, schema creation, and migrations.
/// </summary>
public sealed class ModularDatabase : IAsyncDisposable, IDisposable
{
    private const int CurrentSchemaVersion = 2;

    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Creates a new SQLite database manager.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    public ModularDatabase(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
    }

    /// <summary>
    /// Gets an open connection to the database.
    /// </summary>
    public async Task<SqliteConnection> GetConnectionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { State: System.Data.ConnectionState.Open })
            return _connection;

        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        // Enable WAL mode for better concurrent access
        await using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        await walCmd.ExecuteNonQueryAsync();

        return _connection;
    }

    /// <summary>
    /// Initializes the database schema, creating tables and running migrations.
    /// </summary>
    public async Task InitializeAsync()
    {
        var connection = await GetConnectionAsync();
        var version = await GetSchemaVersionAsync(connection);

        if (version == 0)
        {
            await CreateSchemaAsync(connection);
        }
        else if (version < CurrentSchemaVersion)
        {
            await MigrateSchemaAsync(connection, version);
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task SetSchemaVersionAsync(SqliteConnection connection, int version)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Downloads table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS downloads (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        game_domain TEXT NOT NULL,
                        mod_id INTEGER NOT NULL,
                        file_id INTEGER NOT NULL,
                        filename TEXT NOT NULL,
                        file_path TEXT NOT NULL,
                        downloaded_at TEXT NOT NULL,
                        file_size INTEGER NOT NULL DEFAULT 0,
                        md5_expected TEXT NOT NULL DEFAULT '',
                        md5_actual TEXT NOT NULL DEFAULT '',
                        status INTEGER NOT NULL DEFAULT 0,
                        url TEXT NOT NULL DEFAULT '',
                        error_message TEXT,
                        UNIQUE(game_domain, mod_id, file_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_downloads_domain ON downloads(game_domain);
                    CREATE INDEX IF NOT EXISTS idx_downloads_mod ON downloads(game_domain, mod_id);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // Metadata cache table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS metadata_cache (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        game_domain TEXT NOT NULL,
                        mod_id INTEGER NOT NULL,
                        cached_at TEXT NOT NULL,
                        expires_at TEXT,
                        name TEXT,
                        summary TEXT,
                        description TEXT,
                        author TEXT,
                        version TEXT,
                        category_id INTEGER,
                        endorsements INTEGER,
                        downloads INTEGER,
                        created_at TEXT,
                        updated_at TEXT,
                        picture_url TEXT,
                        json_data TEXT,
                        UNIQUE(game_domain, mod_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_metadata_domain ON metadata_cache(game_domain);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // Rate limits table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS rate_limits (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        backend TEXT NOT NULL UNIQUE,
                        hourly_remaining INTEGER NOT NULL DEFAULT 0,
                        hourly_reset TEXT,
                        daily_remaining INTEGER NOT NULL DEFAULT 0,
                        daily_reset TEXT,
                        updated_at TEXT NOT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // Download history/statistics table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS download_history (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        game_domain TEXT NOT NULL,
                        mod_id INTEGER NOT NULL,
                        file_id INTEGER NOT NULL,
                        filename TEXT NOT NULL,
                        file_size INTEGER NOT NULL,
                        download_time_ms INTEGER,
                        success INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE INDEX IF NOT EXISTS idx_history_timestamp ON download_history(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_history_domain ON download_history(game_domain);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            await SetSchemaVersionAsync(connection, CurrentSchemaVersion);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task MigrateSchemaAsync(SqliteConnection connection, int fromVersion)
    {
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            if (fromVersion < 2)
            {
                await MigrateToV2Async(connection, (SqliteTransaction)transaction);
            }

            await SetSchemaVersionAsync(connection, CurrentSchemaVersion);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task MigrateToV2Async(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Game detection tables
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS detected_games (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_appid INTEGER NOT NULL,
                    display_name TEXT,
                    install_path TEXT NOT NULL,
                    library_root TEXT NOT NULL,
                    size_bytes INTEGER,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    UNIQUE(steam_appid, library_root)
                );
                CREATE INDEX IF NOT EXISTS idx_detected_games_appid ON detected_games(steam_appid);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS engine_detection (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_appid INTEGER NOT NULL,
                    library_root TEXT NOT NULL,
                    engine_family TEXT NOT NULL,
                    confidence REAL NOT NULL,
                    evidence_json TEXT,
                    detector_name TEXT NOT NULL,
                    detected_at_utc TEXT NOT NULL,
                    UNIQUE(steam_appid, library_root)
                );
                CREATE INDEX IF NOT EXISTS idx_engine_detection_appid ON engine_detection(steam_appid);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Archive management tables
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS archive (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    mtime TEXT,
                    sha256 TEXT,
                    format TEXT,
                    entry_count INTEGER NOT NULL DEFAULT 0,
                    scanned_at_utc TEXT NOT NULL,
                    UNIQUE(path)
                );
                CREATE TABLE IF NOT EXISTS archive_entry (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    archive_id INTEGER NOT NULL REFERENCES archive(id) ON DELETE CASCADE,
                    inner_path TEXT NOT NULL,
                    entry_type TEXT NOT NULL DEFAULT 'file',
                    size_bytes INTEGER NOT NULL DEFAULT 0,
                    compressed_bytes INTEGER NOT NULL DEFAULT 0,
                    crc32 INTEGER,
                    sha256 TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_archive_entry_archive ON archive_entry(archive_id);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Blob store table
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS blob (
                    sha256 TEXT PRIMARY KEY NOT NULL,
                    storage_path TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    ref_count INTEGER NOT NULL DEFAULT 1,
                    created_at_utc TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Changeset tracking table
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS changeset (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    changeset_id TEXT NOT NULL UNIQUE,
                    state TEXT NOT NULL DEFAULT 'planned',
                    mod_id TEXT,
                    archive_path TEXT,
                    target_directory TEXT,
                    operations_json TEXT,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_changeset_state ON changeset(state);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connection?.Dispose();
        _connection = null;
    }
}
