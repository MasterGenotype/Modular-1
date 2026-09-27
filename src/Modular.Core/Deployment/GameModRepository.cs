using System.Globalization;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;

namespace Modular.Core.Deployment;

/// <summary>
/// The deployed mod set for each game, and the deployment method each game uses.
/// </summary>
/// <remarks>
/// This is the table the deployer reads. Load order comes from, in priority order, a
/// profile's explicit list, then the stored <c>order_index</c>, then install sequence —
/// and "later wins" is the one rule the user has to learn.
/// </remarks>
public sealed class GameModRepository
{
    private readonly ModularDatabase _database;
    private readonly ILogger<GameModRepository>? _logger;

    /// <summary>
    /// Creates a repository.
    /// </summary>
    /// <param name="database">Modular database.</param>
    /// <param name="logger">Optional logger.</param>
    public GameModRepository(ModularDatabase database, ILogger<GameModRepository>? logger = null)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Records a mod as part of a game's deployment, or updates it if already present.
    /// </summary>
    /// <param name="record">The mod to record. A null order appends it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored record, with its resolved order index.</returns>
    public async Task<GameModRecord> UpsertAsync(
        GameModRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var connection = await _database.GetConnectionAsync();
        var existing = await GetAsync(record.GameKey, record.ModId, ct);

        // A reinstall keeps its place in the order; a new mod goes last, which is where
        // "later wins" means "wins".
        var orderIndex = record.OrderIndex
            ?? existing?.OrderIndex
            ?? await GetNextOrderIndexAsync(record.GameKey, ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_mod
                (game_key, mod_id, module_id, installer_id, staging_path, order_index,
                 enabled, installed_at, source, source_file_id, changeset_id, display_name)
            VALUES
                ($gameKey, $modId, $moduleId, $installerId, $stagingPath, $orderIndex,
                 $enabled, $installedAt, $source, $sourceFileId, $changesetId, $displayName)
            ON CONFLICT(game_key, mod_id) DO UPDATE SET
                module_id      = excluded.module_id,
                installer_id   = excluded.installer_id,
                staging_path   = excluded.staging_path,
                order_index    = excluded.order_index,
                enabled        = excluded.enabled,
                installed_at   = excluded.installed_at,
                source         = excluded.source,
                source_file_id = excluded.source_file_id,
                changeset_id   = excluded.changeset_id,
                display_name   = excluded.display_name;
            """;

        cmd.Parameters.AddWithValue("$gameKey", record.GameKey);
        cmd.Parameters.AddWithValue("$modId", record.ModId);
        cmd.Parameters.AddWithValue("$moduleId", (object?)record.ModuleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$installerId", (object?)record.InstallerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stagingPath", record.StagingPath);
        cmd.Parameters.AddWithValue("$orderIndex", orderIndex);
        cmd.Parameters.AddWithValue("$enabled", record.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "$installedAt", record.InstalledAtUtc ?? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$source", record.Source);
        cmd.Parameters.AddWithValue("$sourceFileId", (object?)record.SourceFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$changesetId", (object?)record.ChangesetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$displayName", (object?)record.DisplayName ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug(
            "Recorded mod {ModId} for {GameKey} at order {Order}",
            record.ModId, record.GameKey, orderIndex);

        return record with { OrderIndex = orderIndex };
    }

    /// <summary>
    /// Lists a game's mods in load order.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="enabledOnly">Whether to omit disabled mods.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mods, ordered so the last entry wins.</returns>
    public async Task<List<GameModRecord>> ListAsync(
        string gameKey, bool enabledOnly = false, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT game_key, mod_id, module_id, installer_id, staging_path, order_index,
                   enabled, installed_at, source, source_file_id, changeset_id, display_name
            FROM game_mod
            WHERE game_key = $gameKey {(enabledOnly ? "AND enabled = 1" : string.Empty)}
            ORDER BY order_index, mod_id
            """;
        cmd.Parameters.AddWithValue("$gameKey", gameKey);

        var results = new List<GameModRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            results.Add(Read(reader));

        return results;
    }

    /// <summary>
    /// Gets one mod.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="modId">Mod identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mod, or <c>null</c> when it is not part of the deployment.</returns>
    public async Task<GameModRecord?> GetAsync(
        string gameKey, string modId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_key, mod_id, module_id, installer_id, staging_path, order_index,
                   enabled, installed_at, source, source_file_id, changeset_id, display_name
            FROM game_mod WHERE game_key = $gameKey AND mod_id = $modId
            """;
        cmd.Parameters.AddWithValue("$gameKey", gameKey);
        cmd.Parameters.AddWithValue("$modId", modId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>
    /// Finds the mod a changeset installed.
    /// </summary>
    /// <param name="changesetId">Changeset identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mod, or <c>null</c> when no managed mod references it.</returns>
    public async Task<GameModRecord?> GetByChangesetAsync(
        string changesetId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_key, mod_id, module_id, installer_id, staging_path, order_index,
                   enabled, installed_at, source, source_file_id, changeset_id, display_name
            FROM game_mod WHERE changeset_id = $changesetId LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$changesetId", changesetId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>
    /// Removes a mod from a game's deployment.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="modId">Mod identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a row was removed.</returns>
    public async Task<bool> RemoveAsync(
        string gameKey, string modId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM game_mod WHERE game_key = $gameKey AND mod_id = $modId";
        cmd.Parameters.AddWithValue("$gameKey", gameKey);
        cmd.Parameters.AddWithValue("$modId", modId);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Rewrites a game's load order.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="modIdsInOrder">Mod ids, first to last; the last wins.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the order is stored.</returns>
    /// <remarks>
    /// Mods not named keep their relative order after the ones that were, so a partial
    /// reorder is a valid request rather than a way to lose mods.
    /// </remarks>
    public async Task SetOrderAsync(
        string gameKey, IReadOnlyList<string> modIdsInOrder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIdsInOrder);

        var existing = await ListAsync(gameKey, enabledOnly: false, ct);

        var ordered = modIdsInOrder
            .Where(id => existing.Any(m =>
                string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        ordered.AddRange(existing
            .Select(m => m.ModId)
            .Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));

        var connection = await _database.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                cmd.CommandText = """
                    UPDATE game_mod SET order_index = $order
                    WHERE game_key = $gameKey AND mod_id = $modId
                    """;
                cmd.Parameters.AddWithValue("$order", i);
                cmd.Parameters.AddWithValue("$gameKey", gameKey);
                cmd.Parameters.AddWithValue("$modId", ordered[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Enables or disables a mod.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="modId">Mod identifier.</param>
    /// <param name="enabled">Whether the mod should be deployed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a row was updated.</returns>
    public async Task<bool> SetEnabledAsync(
        string gameKey, string modId, bool enabled, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE game_mod SET enabled = $enabled
            WHERE game_key = $gameKey AND mod_id = $modId
            """;
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$gameKey", gameKey);
        cmd.Parameters.AddWithValue("$modId", modId);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Replaces a game's entire mod set in one transaction.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="records">The mod set to store, in load order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the set is stored.</returns>
    /// <remarks>
    /// Used by snapshot restore, which must reach the recorded state in one step and then
    /// deploy exactly once — not purge and redeploy per mod.
    /// </remarks>
    public async Task ReplaceAllAsync(
        string gameKey, IReadOnlyList<GameModRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var connection = await _database.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM game_mod WHERE game_key = $gameKey";
                delete.Parameters.AddWithValue("$gameKey", gameKey);
                await delete.ExecuteNonQueryAsync(ct);
            }

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];

                await using var insert = connection.CreateCommand();
                insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO game_mod
                        (game_key, mod_id, module_id, installer_id, staging_path, order_index,
                         enabled, installed_at, source, source_file_id, changeset_id, display_name)
                    VALUES
                        ($gameKey, $modId, $moduleId, $installerId, $stagingPath, $orderIndex,
                         $enabled, $installedAt, $source, $sourceFileId, $changesetId, $displayName)
                    """;
                insert.Parameters.AddWithValue("$gameKey", gameKey);
                insert.Parameters.AddWithValue("$modId", record.ModId);
                insert.Parameters.AddWithValue("$moduleId", (object?)record.ModuleId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$installerId", (object?)record.InstallerId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$stagingPath", record.StagingPath);
                insert.Parameters.AddWithValue("$orderIndex", record.OrderIndex ?? i);
                insert.Parameters.AddWithValue("$enabled", record.Enabled ? 1 : 0);
                insert.Parameters.AddWithValue(
                    "$installedAt",
                    record.InstalledAtUtc ?? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$source", record.Source);
                insert.Parameters.AddWithValue("$sourceFileId", (object?)record.SourceFileId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$changesetId", (object?)record.ChangesetId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$displayName", (object?)record.DisplayName ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Lists the game keys that have any managed mods.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The game keys.</returns>
    public async Task<List<string>> ListGameKeysAsync(CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT game_key FROM game_mod ORDER BY game_key";

        var keys = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));

        return keys;
    }

    /// <summary>
    /// Reads a game's stored deployment method.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The settings, or <c>null</c> when the game has none stored.</returns>
    public async Task<DeploymentSettings?> GetDeploySettingsAsync(
        string gameKey, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT method, fallback FROM game_deploy_settings WHERE game_key = $gameKey";
        cmd.Parameters.AddWithValue("$gameKey", gameKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var method = DeploymentMethods.ParseMethod(reader.GetString(0));
        var fallback = DeploymentMethods.ParseFallback(reader.GetString(1));

        if (method == null)
            return null;

        return new DeploymentSettings(
            method.Value,
            fallback ?? DeploymentFallback.Copy,
            "per-game setting");
    }

    /// <summary>
    /// Stores a game's deployment method.
    /// </summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="method">Method to use for this game.</param>
    /// <param name="fallback">What to do when the method is unavailable for a file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the setting is stored.</returns>
    public async Task SetDeploySettingsAsync(
        string gameKey,
        DeploymentMethod method,
        DeploymentFallback fallback,
        CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_deploy_settings (game_key, method, fallback, updated_at_utc)
            VALUES ($gameKey, $method, $fallback, $now)
            ON CONFLICT(game_key) DO UPDATE SET
                method         = excluded.method,
                fallback       = excluded.fallback,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$gameKey", gameKey);
        cmd.Parameters.AddWithValue("$method", method.ToName());
        cmd.Parameters.AddWithValue("$fallback", fallback.ToName());
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> GetNextOrderIndexAsync(string gameKey, CancellationToken ct)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(MAX(order_index), -1) + 1 FROM game_mod WHERE game_key = $gameKey";
        cmd.Parameters.AddWithValue("$gameKey", gameKey);

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is long next ? (int)next : 0;
    }

    private static GameModRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        GameKey = reader.GetString(0),
        ModId = reader.GetString(1),
        ModuleId = reader.IsDBNull(2) ? null : reader.GetString(2),
        InstallerId = reader.IsDBNull(3) ? null : reader.GetString(3),
        StagingPath = reader.GetString(4),
        OrderIndex = reader.GetInt32(5),
        Enabled = reader.GetInt32(6) != 0,
        InstalledAtUtc = reader.IsDBNull(7) ? null : reader.GetString(7),
        Source = reader.IsDBNull(8) ? "local" : reader.GetString(8),
        SourceFileId = reader.IsDBNull(9) ? null : reader.GetString(9),
        ChangesetId = reader.IsDBNull(10) ? null : reader.GetString(10),
        DisplayName = reader.IsDBNull(11) ? null : reader.GetString(11)
    };
}

/// <summary>
/// One mod in a game's deployment.
/// </summary>
public sealed record GameModRecord
{
    /// <summary>Key of the game this mod is deployed to.</summary>
    public required string GameKey { get; init; }

    /// <summary>Mod identifier, unique within the game.</summary>
    public required string ModId { get; init; }

    /// <summary>Module that owns the game.</summary>
    public string? ModuleId { get; init; }

    /// <summary>Installer that staged the mod.</summary>
    public string? InstallerId { get; init; }

    /// <summary>Absolute path of the mod's staging tree.</summary>
    public required string StagingPath { get; init; }

    /// <summary>
    /// Position in the load order, later winning. Null on insert means "append".
    /// </summary>
    public int? OrderIndex { get; init; }

    /// <summary>Whether the mod is deployed.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>When the mod was installed, ISO 8601 UTC.</summary>
    public string? InstalledAtUtc { get; init; }

    /// <summary>Where the mod came from: <c>nexus</c>, <c>gamebanana</c> or <c>local</c>.</summary>
    public string Source { get; init; } = "local";

    /// <summary>
    /// Backend file id, kept so a 1-Click or update flow can find the same file later.
    /// </summary>
    public string? SourceFileId { get; init; }

    /// <summary>Changeset that installed this mod.</summary>
    public string? ChangesetId { get; init; }

    /// <summary>Human-readable name, for order listings.</summary>
    public string? DisplayName { get; init; }
}
