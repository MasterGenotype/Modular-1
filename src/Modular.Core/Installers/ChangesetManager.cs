using Microsoft.Extensions.Logging;
using Modular.Core.Database;

namespace Modular.Core.Installers;

/// <summary>
/// Manages installation changesets with a state machine for tracking provenance.
/// States: planned → staging → ready → committing → committed → failed → rolled_back
/// </summary>
/// <remarks>
/// A changeset also records who owns the files it describes. See
/// <see cref="ChangesetOwnership"/>: a staged install's operations are staging paths,
/// so deleting them would remove the staged copies and leave the deployed files behind.
/// </remarks>
public class ChangesetManager
{
    private readonly ModularDatabase _database;
    private readonly ILogger<ChangesetManager>? _logger;

    public ChangesetManager(ModularDatabase database, ILogger<ChangesetManager>? logger = null)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new changeset record.
    /// </summary>
    /// <param name="modId">Mod identifier, when known.</param>
    /// <param name="archivePath">Source archive.</param>
    /// <param name="targetDirectory">Game directory the install targets.</param>
    /// <param name="managedBy">
    /// Who owns the files this changeset records. <see cref="ChangesetOwnership.Legacy"/>
    /// means the operations are real game-directory paths that uninstall may delete;
    /// <see cref="ChangesetOwnership.Deployment"/> means they are staging paths and the
    /// deployment owns what is in the game folder.
    /// </param>
    /// <param name="gameKey">Key of the game whose deployment owns this changeset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new changeset id.</returns>
    public async Task<string> CreateChangesetAsync(
        string? modId = null,
        string? archivePath = null,
        string? targetDirectory = null,
        ChangesetOwnership managedBy = ChangesetOwnership.Legacy,
        string? gameKey = null,
        CancellationToken ct = default)
    {
        var changesetId = Guid.NewGuid().ToString("N")[..12];
        var connection = await _database.GetConnectionAsync();
        var now = DateTime.UtcNow.ToString("O");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO changeset (changeset_id, state, mod_id, archive_path, target_directory,
                                   managed_by, game_key, created_at_utc, updated_at_utc)
            VALUES (@id, 'planned', @modId, @archive, @target, @managedBy, @gameKey, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", changesetId);
        cmd.Parameters.AddWithValue("@modId", modId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@archive", archivePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@target", targetDirectory ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@managedBy", ToDbValue(managedBy));
        cmd.Parameters.AddWithValue("@gameKey", gameKey ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogInformation(
            "Created changeset {Id} (managed by {ManagedBy})", changesetId, managedBy);
        return changesetId;
    }

    /// <summary>
    /// Records who owns an existing changeset's files.
    /// </summary>
    /// <param name="changesetId">Changeset to update.</param>
    /// <param name="managedBy">New ownership.</param>
    /// <param name="gameKey">Game key to record, or <c>null</c> to leave it unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the row is updated.</returns>
    public async Task SetOwnershipAsync(
        string changesetId,
        ChangesetOwnership managedBy,
        string? gameKey = null,
        CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = gameKey == null
            ? """
              UPDATE changeset SET managed_by = @managedBy, updated_at_utc = @now
              WHERE changeset_id = @id
              """
            : """
              UPDATE changeset SET managed_by = @managedBy, game_key = @gameKey, updated_at_utc = @now
              WHERE changeset_id = @id
              """;

        cmd.Parameters.AddWithValue("@managedBy", ToDbValue(managedBy));
        if (gameKey != null)
            cmd.Parameters.AddWithValue("@gameKey", gameKey);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", changesetId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Transitions a changeset to a new state.
    /// </summary>
    public async Task UpdateStateAsync(
        string changesetId,
        ChangesetState newState,
        string? operationsJson = null,
        CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        var now = DateTime.UtcNow.ToString("O");

        await using var cmd = connection.CreateCommand();
        if (operationsJson != null)
        {
            cmd.CommandText = """
                UPDATE changeset SET state = @state, operations_json = @ops, updated_at_utc = @now
                WHERE changeset_id = @id
                """;
            cmd.Parameters.AddWithValue("@ops", operationsJson);
        }
        else
        {
            cmd.CommandText = """
                UPDATE changeset SET state = @state, updated_at_utc = @now
                WHERE changeset_id = @id
                """;
        }
        cmd.Parameters.AddWithValue("@state", newState.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@id", changesetId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug("Changeset {Id} → {State}", changesetId, newState);
    }

    /// <summary>
    /// Gets a changeset by ID.
    /// </summary>
    public async Task<ChangesetRecord?> GetChangesetAsync(string changesetId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT changeset_id, state, mod_id, archive_path, target_directory, operations_json,
                   created_at_utc, updated_at_utc, managed_by, game_key
            FROM changeset WHERE changeset_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", changesetId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadRecord(reader);
    }

    /// <summary>
    /// Lists changesets by state.
    /// </summary>
    public async Task<List<ChangesetRecord>> ListByStateAsync(
        ChangesetState state, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT changeset_id, state, mod_id, archive_path, target_directory, operations_json,
                   created_at_utc, updated_at_utc, managed_by, game_key
            FROM changeset WHERE state = @state ORDER BY created_at_utc DESC
            """;
        cmd.Parameters.AddWithValue("@state", state.ToString().ToLowerInvariant());

        var results = new List<ChangesetRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    private static ChangesetRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new()
        {
            ChangesetId = reader.GetString(0),
            State = Enum.Parse<ChangesetState>(reader.GetString(1), ignoreCase: true),
            ModId = reader.IsDBNull(2) ? null : reader.GetString(2),
            ArchivePath = reader.IsDBNull(3) ? null : reader.GetString(3),
            TargetDirectory = reader.IsDBNull(4) ? null : reader.GetString(4),
            OperationsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAtUtc = reader.GetString(6),
            UpdatedAtUtc = reader.GetString(7),
            ManagedBy = ParseOwnership(reader.IsDBNull(8) ? null : reader.GetString(8)),
            GameKey = reader.IsDBNull(9) ? null : reader.GetString(9)
        };

    private static string ToDbValue(ChangesetOwnership ownership) =>
        ownership.ToString().ToLowerInvariant();

    private static ChangesetOwnership ParseOwnership(string? value) =>
        Enum.TryParse<ChangesetOwnership>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ChangesetOwnership.Legacy;
}

/// <summary>
/// Who owns the files a changeset describes, and therefore how it is undone.
/// </summary>
public enum ChangesetOwnership
{
    /// <summary>
    /// The changeset's operations are real game-directory paths, written there
    /// directly. Uninstall deletes those paths and restores their backups.
    /// </summary>
    Legacy,

    /// <summary>
    /// The changeset's operations are staging paths. The deployment owns whatever is
    /// in the game folder, so uninstall removes the mod from the deployment and
    /// redeploys instead of deleting paths.
    /// </summary>
    Deployment
}

/// <summary>
/// Changeset state machine.
/// </summary>
public enum ChangesetState
{
    Planned,
    Staging,
    Ready,
    Committing,
    Committed,
    Failed,
    RolledBack,

    /// <summary>
    /// Replaced by a later install of the same mod. Its files and staging now belong to
    /// the changeset that replaced it, so it must never be undone on its own.
    /// </summary>
    Superseded
}

/// <summary>
/// Record of an installation changeset.
/// </summary>
public class ChangesetRecord
{
    public string ChangesetId { get; set; } = string.Empty;
    public ChangesetState State { get; set; }
    public string? ModId { get; set; }
    public string? ArchivePath { get; set; }
    public string? TargetDirectory { get; set; }
    public string? OperationsJson { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;

    /// <summary>Who owns the files this changeset describes.</summary>
    public ChangesetOwnership ManagedBy { get; set; } = ChangesetOwnership.Legacy;

    /// <summary>Key of the game whose deployment owns this changeset, when managed.</summary>
    public string? GameKey { get; set; }
}
