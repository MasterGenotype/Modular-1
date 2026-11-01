using Microsoft.Data.Sqlite;

namespace Modular.Core.Database;

/// <summary>
/// SQLite database manager for Modular.
/// Handles connection management, schema creation, and migrations.
/// </summary>
public sealed class ModularDatabase : IAsyncDisposable, IDisposable
{
    private const int CurrentSchemaVersion = 8;

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

            // Also create v2+ tables for fresh installs
            await CreateV2TablesAsync(connection, (SqliteTransaction)transaction);
            await CreateV4TablesAsync(connection, (SqliteTransaction)transaction);
            await CreateV5SchemaAsync(connection, (SqliteTransaction)transaction);
            await CreateV6SchemaAsync(connection, (SqliteTransaction)transaction);
            await CreateV7SchemaAsync(connection, (SqliteTransaction)transaction);
            await CreateV8SchemaAsync(connection, (SqliteTransaction)transaction);

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

            if (fromVersion < 3)
            {
                // Re-run v2 table creation to fix databases that were created fresh at v2
                // but missing v2 tables due to a bug in CreateSchemaAsync
                await CreateV2TablesAsync(connection, (SqliteTransaction)transaction);
            }

            if (fromVersion < 4)
            {
                await CreateV4TablesAsync(connection, (SqliteTransaction)transaction);
            }

            if (fromVersion < 5)
            {
                await CreateV5SchemaAsync(connection, (SqliteTransaction)transaction);
            }

            if (fromVersion < 6)
            {
                await CreateV6SchemaAsync(connection, (SqliteTransaction)transaction);
            }

            if (fromVersion < 7)
            {
                await CreateV7SchemaAsync(connection, (SqliteTransaction)transaction);
            }

            if (fromVersion < 8)
            {
                await CreateV8SchemaAsync(connection, (SqliteTransaction)transaction);
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
        await CreateV2TablesAsync(connection, transaction);
    }

    private static async Task CreateV2TablesAsync(SqliteConnection connection, SqliteTransaction transaction)
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

    private static async Task CreateV4TablesAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Snapshot table — point-in-time record of installed mods for a game
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS snapshot (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id TEXT NOT NULL UNIQUE,
                    game_appid INTEGER NOT NULL,
                    game_name TEXT NOT NULL,
                    game_install_path TEXT NOT NULL,
                    name TEXT,
                    description TEXT,
                    trigger TEXT NOT NULL DEFAULT 'manual',
                    created_at_utc TEXT NOT NULL,
                    mod_count INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_snapshot_game ON snapshot(game_appid);
                CREATE INDEX IF NOT EXISTS idx_snapshot_created ON snapshot(created_at_utc);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Snapshot entries — denormalized copy of each committed changeset at snapshot time
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS snapshot_entry (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id TEXT NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
                    changeset_id TEXT NOT NULL,
                    mod_id TEXT,
                    archive_path TEXT,
                    target_directory TEXT,
                    operations_json TEXT,
                    changeset_created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_snapshot_entry_snapshot ON snapshot_entry(snapshot_id);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Creates the v5 schema: the ownership columns that split staged installs from
    /// legacy direct installs.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <remarks>
    /// <para>
    /// <c>managed_by</c> decides how a changeset is uninstalled. <c>legacy</c> rows
    /// record real game-directory paths and are undone by deleting them;
    /// <c>deployment</c> rows record staged paths and are undone by removing the mod
    /// and redeploying. Existing rows stay <c>legacy</c>, which is what they are.
    /// </para>
    /// <para>
    /// <c>game_key</c> ties a changeset to the game whose deployment owns it.
    /// </para>
    /// </remarks>
    private static async Task CreateV5SchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        await AddColumnIfMissingAsync(
            connection, transaction,
            table: "changeset",
            column: "managed_by",
            definition: "TEXT NOT NULL DEFAULT 'legacy'");

        await AddColumnIfMissingAsync(
            connection, transaction,
            table: "changeset",
            column: "game_key",
            definition: "TEXT");

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_changeset_managed_by ON changeset(managed_by);
                CREATE INDEX IF NOT EXISTS idx_changeset_game_key ON changeset(game_key);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Creates the v6 schema: the ordered deployment tables.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <remarks>
    /// Separate from v5 so a database that already migrated to v5 — the changeset
    /// ownership split — still gets these tables. Folding them into v5 would have left
    /// such a database permanently without them.
    /// </remarks>
    private static async Task CreateV6SchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        // The deployed mod set for a game, in load order. "Later wins" is Modular's
        // single user-facing rule; each module translates that into its engine's own
        // convention when it writes a load-order manifest.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS game_mod (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_key TEXT NOT NULL,
                    mod_id TEXT NOT NULL,
                    module_id TEXT,
                    installer_id TEXT,
                    staging_path TEXT NOT NULL,
                    order_index INTEGER NOT NULL DEFAULT 0,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    installed_at TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'local',
                    source_file_id TEXT,
                    changeset_id TEXT,
                    display_name TEXT,
                    UNIQUE(game_key, mod_id)
                );
                CREATE INDEX IF NOT EXISTS idx_game_mod_order
                    ON game_mod(game_key, order_index);
                CREATE INDEX IF NOT EXISTS idx_game_mod_changeset
                    ON game_mod(changeset_id);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Per-game deployment method. Identity records are not comparable across
        // methods, so changing this forces a full purge and a fresh deploy.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS game_deploy_settings (
                    game_key TEXT PRIMARY KEY NOT NULL,
                    method TEXT NOT NULL DEFAULT 'hardlink',
                    fallback TEXT NOT NULL DEFAULT 'copy',
                    updated_at_utc TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Snapshot capture of the deployed mod set, so a restore is one transaction
        // followed by exactly one deploy.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS snapshot_game_mod (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_id TEXT NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
                    game_key TEXT NOT NULL,
                    mod_id TEXT NOT NULL,
                    module_id TEXT,
                    installer_id TEXT,
                    staging_path TEXT NOT NULL,
                    staging_hash TEXT,
                    order_index INTEGER NOT NULL DEFAULT 0,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    changeset_id TEXT,
                    source TEXT,
                    source_file_id TEXT,
                    display_name TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_snapshot_game_mod_snapshot
                    ON snapshot_game_mod(snapshot_id);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Creates the v7 schema: a load order kept apart from the install order.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <remarks>
    /// <para>
    /// <c>order_index</c> stays the install order, which decides a file two mods both
    /// deploy. <c>load_index</c> is the order the game applies mods in, for the modules
    /// that render one. Every existing row starts with the two equal, so no game's
    /// behaviour changes until the user moves something.
    /// </para>
    /// <para>
    /// Its own version rather than an addition to v6, which some databases have already
    /// recorded: extending it would leave them without the column for good.
    /// </para>
    /// </remarks>
    private static async Task CreateV7SchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "game_mod", "snapshot_game_mod" })
        {
            await AddColumnIfMissingAsync(
                connection, transaction, table: table, column: "load_index", definition: "INTEGER");

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"UPDATE {table} SET load_index = order_index WHERE load_index IS NULL;";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Creates the v8 schema: mods whose files a game's own mod manager owns.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    /// <remarks>
    /// <para>
    /// A game with an in-game mod manager (Baldur's Gate 3's, fed by mod.io) downloads,
    /// updates and deletes the files of the mods the user subscribes to. Modular tracks those
    /// mods here rather than in <c>game_mod</c>, which is the deployer's table: nothing the
    /// deployer does can then rename, hardlink or remove a file the game owns.
    /// </para>
    /// <para>
    /// <c>state</c> is <c>pending_download</c> from the subscription until the game has
    /// downloaded the file, then <c>installed</c>. The file's identity is what the game
    /// wrote: its name, and the module, version and upload hash its <c>meta.lsx</c> records.
    /// </para>
    /// </remarks>
    private static async Task CreateV8SchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS game_owned_mod (
                game_key        TEXT NOT NULL,
                backend_id      TEXT NOT NULL,
                remote_mod_id   TEXT NOT NULL,
                remote_game_id  TEXT NOT NULL,
                state           TEXT NOT NULL,
                display_name    TEXT,
                remote_file_id  TEXT,
                file_name       TEXT,
                module_uuid     TEXT,
                version64       INTEGER,
                upload_md5      TEXT,
                file_md5        TEXT,
                tracked_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                PRIMARY KEY (game_key, backend_id, remote_mod_id)
            );
            CREATE INDEX IF NOT EXISTS idx_game_owned_mod_module
                ON game_owned_mod(game_key, module_uuid);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Adds a column to a table when it is not already present.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <param name="table">Table to alter.</param>
    /// <param name="column">Column name.</param>
    /// <param name="definition">Column type and constraints.</param>
    /// <returns>A task that completes when the column exists.</returns>
    /// <remarks>
    /// SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, and a migration may run over a
    /// database that already has the column (a fresh v5 install, or a re-run).
    /// </remarks>
    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({table});";

            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(1));
        }

        if (existing.Count == 0 || existing.Contains(column))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync();
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
