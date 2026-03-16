using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;

namespace Modular.Core.Installers;

/// <summary>
/// Manages installation changesets with a state machine for tracking provenance.
/// States: planned → staging → ready → committing → committed → failed → rolled_back
/// </summary>
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
    public async Task<string> CreateChangesetAsync(
        string? modId = null,
        string? archivePath = null,
        string? targetDirectory = null,
        CancellationToken ct = default)
    {
        var changesetId = Guid.NewGuid().ToString("N")[..12];
        var connection = await _database.GetConnectionAsync();
        var now = DateTime.UtcNow.ToString("O");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO changeset (changeset_id, state, mod_id, archive_path, target_directory, created_at_utc, updated_at_utc)
            VALUES (@id, 'planned', @modId, @archive, @target, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", changesetId);
        cmd.Parameters.AddWithValue("@modId", modId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@archive", archivePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@target", targetDirectory ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogInformation("Created changeset {Id}", changesetId);
        return changesetId;
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
            SELECT changeset_id, state, mod_id, archive_path, target_directory, operations_json, created_at_utc, updated_at_utc
            FROM changeset WHERE changeset_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", changesetId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ChangesetRecord
        {
            ChangesetId = reader.GetString(0),
            State = Enum.Parse<ChangesetState>(reader.GetString(1), ignoreCase: true),
            ModId = reader.IsDBNull(2) ? null : reader.GetString(2),
            ArchivePath = reader.IsDBNull(3) ? null : reader.GetString(3),
            TargetDirectory = reader.IsDBNull(4) ? null : reader.GetString(4),
            OperationsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAtUtc = reader.GetString(6),
            UpdatedAtUtc = reader.GetString(7)
        };
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
            SELECT changeset_id, state, mod_id, archive_path, target_directory, operations_json, created_at_utc, updated_at_utc
            FROM changeset WHERE state = @state ORDER BY created_at_utc DESC
            """;
        cmd.Parameters.AddWithValue("@state", state.ToString().ToLowerInvariant());

        var results = new List<ChangesetRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ChangesetRecord
            {
                ChangesetId = reader.GetString(0),
                State = Enum.Parse<ChangesetState>(reader.GetString(1), ignoreCase: true),
                ModId = reader.IsDBNull(2) ? null : reader.GetString(2),
                ArchivePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                TargetDirectory = reader.IsDBNull(4) ? null : reader.GetString(4),
                OperationsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAtUtc = reader.GetString(6),
                UpdatedAtUtc = reader.GetString(7)
            });
        }

        return results;
    }
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
    RolledBack
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
}
