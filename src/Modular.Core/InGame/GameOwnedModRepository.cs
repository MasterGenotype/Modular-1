using System.Globalization;
using Microsoft.Data.Sqlite;
using Modular.Core.Database;

namespace Modular.Core.InGame;

/// <summary>Where a game-owned mod stands.</summary>
public enum GameOwnedModState
{
    /// <summary>Subscribed; the game downloads it on its next launch.</summary>
    PendingDownload,

    /// <summary>The game has downloaded it, and its file is in the game's mods folder.</summary>
    Installed,
}

/// <summary>
/// A mod whose file a game's own mod manager downloads, updates and deletes, which
/// Modular keeps track of but never deploys.
/// </summary>
public sealed record GameOwnedModRecord
{
    /// <summary>Modular's key for the game, such as <c>1086940</c>.</summary>
    public required string GameKey { get; init; }

    /// <summary>The hub the subscription is on, such as <c>modio</c>.</summary>
    public required string BackendId { get; init; }

    /// <summary>The mod's id on the hub.</summary>
    public required string RemoteModId { get; init; }

    /// <summary>The game's id on the hub, such as <c>6715</c>.</summary>
    public required string RemoteGameId { get; init; }

    /// <summary>Where the mod stands.</summary>
    public required GameOwnedModState State { get; init; }

    /// <summary>The mod's name on the hub.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The hub's id for the file the game installed, or will install.</summary>
    public string? RemoteFileId { get; init; }

    /// <summary>The file's name in the game's mods folder, once it is there.</summary>
    public string? FileName { get; init; }

    /// <summary>The module the file contains, lowercase.</summary>
    public string? ModuleUuid { get; init; }

    /// <summary>The module's version, packed as the game packs it.</summary>
    public long? Version64 { get; init; }

    /// <summary>The upload hash the module's metadata records.</summary>
    public string? UploadMd5 { get; init; }

    /// <summary>MD5 of the file itself, which changes whenever the game replaces it.</summary>
    public string? FileMd5 { get; init; }

    /// <summary>When Modular first recorded the mod, UTC, ISO 8601.</summary>
    public string? TrackedAtUtc { get; init; }

    /// <summary>When the record last changed, UTC, ISO 8601.</summary>
    public string? UpdatedAtUtc { get; init; }
}

/// <summary>
/// The mods each game's own mod manager owns. See the v8 schema in
/// <see cref="ModularDatabase"/>.
/// </summary>
public sealed class GameOwnedModRepository
{
    private const string Columns = """
        game_key, backend_id, remote_mod_id, remote_game_id, state, display_name,
        remote_file_id, file_name, module_uuid, version64, upload_md5, file_md5,
        tracked_at, updated_at
        """;

    private readonly ModularDatabase _database;

    /// <summary>Creates a repository.</summary>
    /// <param name="database">Modular database.</param>
    public GameOwnedModRepository(ModularDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Records a mod, or updates it. The first recorded time is kept.
    /// </summary>
    /// <param name="record">The mod.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when it is stored.</returns>
    public async Task UpsertAsync(GameOwnedModRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var connection = await _database.GetConnectionAsync();
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO game_owned_mod ({Columns})
            VALUES ($gameKey, $backendId, $remoteModId, $remoteGameId, $state, $displayName,
                    $remoteFileId, $fileName, $moduleUuid, $version64, $uploadMd5, $fileMd5,
                    $trackedAt, $updatedAt)
            ON CONFLICT(game_key, backend_id, remote_mod_id) DO UPDATE SET
                remote_game_id = excluded.remote_game_id,
                state          = excluded.state,
                display_name   = excluded.display_name,
                remote_file_id = excluded.remote_file_id,
                file_name      = excluded.file_name,
                module_uuid    = excluded.module_uuid,
                version64      = excluded.version64,
                upload_md5     = excluded.upload_md5,
                file_md5       = excluded.file_md5,
                updated_at     = excluded.updated_at;
            """;

        cmd.Parameters.AddWithValue("$gameKey", record.GameKey);
        cmd.Parameters.AddWithValue("$backendId", record.BackendId);
        cmd.Parameters.AddWithValue("$remoteModId", record.RemoteModId);
        cmd.Parameters.AddWithValue("$remoteGameId", record.RemoteGameId);
        cmd.Parameters.AddWithValue("$state", ToText(record.State));
        cmd.Parameters.AddWithValue("$displayName", (object?)record.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remoteFileId", (object?)record.RemoteFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fileName", (object?)record.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$moduleUuid", (object?)record.ModuleUuid?.ToLowerInvariant() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version64", (object?)record.Version64 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uploadMd5", (object?)record.UploadMd5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fileMd5", (object?)record.FileMd5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trackedAt", record.TrackedAtUtc ?? now);
        cmd.Parameters.AddWithValue("$updatedAt", now);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lists a game's game-owned mods.</summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mods, by name.</returns>
    public async Task<List<GameOwnedModRecord>> ListAsync(string gameKey, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM game_owned_mod
            WHERE game_key = $gameKey
            ORDER BY display_name COLLATE NOCASE, remote_mod_id
            """;
        cmd.Parameters.AddWithValue("$gameKey", gameKey);

        var results = new List<GameOwnedModRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Read(reader));
        return results;
    }

    /// <summary>Gets one game-owned mod.</summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="backendId">Hub the subscription is on.</param>
    /// <param name="remoteModId">The mod's id on the hub.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mod, or <c>null</c>.</returns>
    public async Task<GameOwnedModRecord?> GetAsync(
        string gameKey, string backendId, string remoteModId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM game_owned_mod
            WHERE game_key = $gameKey AND backend_id = $backendId AND remote_mod_id = $remoteModId
            """;
        cmd.Parameters.AddWithValue("$gameKey", gameKey);
        cmd.Parameters.AddWithValue("$backendId", backendId);
        cmd.Parameters.AddWithValue("$remoteModId", remoteModId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Forgets a game-owned mod. Its file, if any, is not touched.</summary>
    /// <param name="gameKey">Game key.</param>
    /// <param name="backendId">Hub the subscription is on.</param>
    /// <param name="remoteModId">The mod's id on the hub.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a record was removed.</returns>
    public async Task<bool> RemoveAsync(
        string gameKey, string backendId, string remoteModId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM game_owned_mod
            WHERE game_key = $gameKey AND backend_id = $backendId AND remote_mod_id = $remoteModId
            """;
        cmd.Parameters.AddWithValue("$gameKey", gameKey);
        cmd.Parameters.AddWithValue("$backendId", backendId);
        cmd.Parameters.AddWithValue("$remoteModId", remoteModId);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static GameOwnedModRecord Read(SqliteDataReader reader) => new()
    {
        GameKey = reader.GetString(0),
        BackendId = reader.GetString(1),
        RemoteModId = reader.GetString(2),
        RemoteGameId = reader.GetString(3),
        State = FromText(reader.GetString(4)),
        DisplayName = reader.IsDBNull(5) ? null : reader.GetString(5),
        RemoteFileId = reader.IsDBNull(6) ? null : reader.GetString(6),
        FileName = reader.IsDBNull(7) ? null : reader.GetString(7),
        ModuleUuid = reader.IsDBNull(8) ? null : reader.GetString(8),
        Version64 = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        UploadMd5 = reader.IsDBNull(10) ? null : reader.GetString(10),
        FileMd5 = reader.IsDBNull(11) ? null : reader.GetString(11),
        TrackedAtUtc = reader.GetString(12),
        UpdatedAtUtc = reader.GetString(13),
    };

    private static string ToText(GameOwnedModState state) => state switch
    {
        GameOwnedModState.PendingDownload => "pending_download",
        GameOwnedModState.Installed => "installed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static GameOwnedModState FromText(string text) => text switch
    {
        "pending_download" => GameOwnedModState.PendingDownload,
        "installed" => GameOwnedModState.Installed,
        _ => throw new InvalidDataException($"Unknown game-owned mod state '{text}'.")
    };
}
