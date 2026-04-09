using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Sdk.Installers;

namespace Modular.Core.Snapshots;

/// <summary>
/// Trigger type for snapshot creation.
/// </summary>
public enum SnapshotTrigger
{
    Manual,
    AutoInstall,
    AutoUninstall
}

/// <summary>
/// Represents a point-in-time snapshot of installed mods for a game.
/// </summary>
public class SnapshotRecord
{
    public string SnapshotId { get; set; } = string.Empty;
    public int GameAppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string GameInstallPath { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public SnapshotTrigger Trigger { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public int ModCount { get; set; }
}

/// <summary>
/// A single mod entry within a snapshot (denormalized from changeset at snapshot time).
/// </summary>
public class SnapshotEntryRecord
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ChangesetId { get; set; } = string.Empty;
    public string? ModId { get; set; }
    public string? ArchivePath { get; set; }
    public string? TargetDirectory { get; set; }
    public string? OperationsJson { get; set; }
    public string ChangesetCreatedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// Result of a snapshot restore operation.
/// </summary>
public class SnapshotRestoreResult
{
    public bool Success { get; set; }
    public string SnapshotId { get; set; } = string.Empty;
    public int ModsRemoved { get; set; }
    public int ModsReinstalled { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Manages creation, listing, and restoration of mod installation snapshots.
/// </summary>
public class SnapshotManager
{
    private readonly ModularDatabase _database;
    private readonly ChangesetManager _changesetManager;
    private readonly ILogger<SnapshotManager>? _logger;

    public SnapshotManager(
        ModularDatabase database,
        ChangesetManager changesetManager,
        ILogger<SnapshotManager>? logger = null)
    {
        _database = database;
        _changesetManager = changesetManager;
        _logger = logger;
    }

    /// <summary>
    /// Creates a snapshot of all committed changesets for a game's install path.
    /// </summary>
    public async Task<SnapshotRecord> CreateSnapshotAsync(
        int gameAppId,
        string gameName,
        string gameInstallPath,
        SnapshotTrigger trigger,
        string? name = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        var snapshotId = Guid.NewGuid().ToString("N")[..12];
        var createdAt = DateTime.UtcNow.ToString("o");
        var triggerStr = trigger switch
        {
            SnapshotTrigger.AutoInstall => "auto_install",
            SnapshotTrigger.AutoUninstall => "auto_uninstall",
            _ => "manual"
        };

        // Get all committed changesets for this game's install path
        var committed = await _changesetManager.ListByStateAsync(ChangesetState.Committed, ct);
        var normalizedPath = Path.GetFullPath(gameInstallPath);
        var gameChangesets = committed.Where(c =>
            c.TargetDirectory != null &&
            Path.GetFullPath(c.TargetDirectory).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            // Insert snapshot record
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    INSERT INTO snapshot (snapshot_id, game_appid, game_name, game_install_path, name, description, trigger, created_at_utc, mod_count)
                    VALUES (@id, @appid, @game_name, @path, @name, @desc, @trigger, @created, @count)
                    """;
                cmd.Parameters.AddWithValue("@id", snapshotId);
                cmd.Parameters.AddWithValue("@appid", gameAppId);
                cmd.Parameters.AddWithValue("@game_name", gameName);
                cmd.Parameters.AddWithValue("@path", gameInstallPath);
                cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@trigger", triggerStr);
                cmd.Parameters.AddWithValue("@created", createdAt);
                cmd.Parameters.AddWithValue("@count", gameChangesets.Count);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Insert snapshot entries (denormalized changeset data)
            foreach (var changeset in gameChangesets)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    INSERT INTO snapshot_entry (snapshot_id, changeset_id, mod_id, archive_path, target_directory, operations_json, changeset_created_at_utc)
                    VALUES (@sid, @cid, @mod_id, @archive, @target, @ops, @created)
                    """;
                cmd.Parameters.AddWithValue("@sid", snapshotId);
                cmd.Parameters.AddWithValue("@cid", changeset.ChangesetId);
                cmd.Parameters.AddWithValue("@mod_id", (object?)changeset.ModId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@archive", (object?)changeset.ArchivePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@target", (object?)changeset.TargetDirectory ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ops", (object?)changeset.OperationsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@created", changeset.CreatedAtUtc);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);

            _logger?.LogInformation("Created snapshot {Id} for {Game} with {Count} mods",
                snapshotId, gameName, gameChangesets.Count);

            return new SnapshotRecord
            {
                SnapshotId = snapshotId,
                GameAppId = gameAppId,
                GameName = gameName,
                GameInstallPath = gameInstallPath,
                Name = name,
                Description = description,
                Trigger = trigger,
                CreatedAtUtc = createdAt,
                ModCount = gameChangesets.Count
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Gets a single snapshot by ID.
    /// </summary>
    public async Task<SnapshotRecord?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id, game_appid, game_name, game_install_path, name, description, trigger, created_at_utc, mod_count
            FROM snapshot WHERE snapshot_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
            return ReadSnapshotRecord(reader);

        return null;
    }

    /// <summary>
    /// Gets all entries for a snapshot, ordered by install time.
    /// </summary>
    public async Task<List<SnapshotEntryRecord>> GetSnapshotEntriesAsync(string snapshotId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id, changeset_id, mod_id, archive_path, target_directory, operations_json, changeset_created_at_utc
            FROM snapshot_entry WHERE snapshot_id = @id
            ORDER BY changeset_created_at_utc ASC
            """;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var entries = new List<SnapshotEntryRecord>();
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new SnapshotEntryRecord
            {
                SnapshotId = reader.GetString(0),
                ChangesetId = reader.GetString(1),
                ModId = reader.IsDBNull(2) ? null : reader.GetString(2),
                ArchivePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                TargetDirectory = reader.IsDBNull(4) ? null : reader.GetString(4),
                OperationsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                ChangesetCreatedAtUtc = reader.GetString(6)
            });
        }

        return entries;
    }

    /// <summary>
    /// Lists snapshots for a game within a date range (for calendar day selection).
    /// </summary>
    public async Task<List<SnapshotRecord>> ListSnapshotsByDateRangeAsync(
        int gameAppId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id, game_appid, game_name, game_install_path, name, description, trigger, created_at_utc, mod_count
            FROM snapshot
            WHERE game_appid = @appid AND created_at_utc >= @start AND created_at_utc < @end
            ORDER BY created_at_utc DESC
            """;
        cmd.Parameters.AddWithValue("@appid", gameAppId);
        cmd.Parameters.AddWithValue("@start", startUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@end", endUtc.ToString("o"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var snapshots = new List<SnapshotRecord>();
        while (await reader.ReadAsync(ct))
        {
            snapshots.Add(ReadSnapshotRecord(reader));
        }

        return snapshots;
    }

    /// <summary>
    /// Returns the set of day-of-month numbers that have snapshots for a game in a given month.
    /// Powers the calendar dot indicators without loading full records.
    /// </summary>
    public async Task<HashSet<int>> GetSnapshotDatesAsync(int gameAppId, int year, int month, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();
        var startUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddMonths(1);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT CAST(substr(created_at_utc, 9, 2) AS INTEGER)
            FROM snapshot
            WHERE game_appid = @appid AND created_at_utc >= @start AND created_at_utc < @end
            """;
        cmd.Parameters.AddWithValue("@appid", gameAppId);
        cmd.Parameters.AddWithValue("@start", startUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@end", endUtc.ToString("o"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var days = new HashSet<int>();
        while (await reader.ReadAsync(ct))
        {
            days.Add(reader.GetInt32(0));
        }

        return days;
    }

    /// <summary>
    /// Deletes a snapshot and its entries (cascade).
    /// </summary>
    public async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        // Delete entries first (in case FK cascade isn't enabled)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM snapshot_entry WHERE snapshot_id = @id";
            cmd.Parameters.AddWithValue("@id", snapshotId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM snapshot WHERE snapshot_id = @id";
            cmd.Parameters.AddWithValue("@id", snapshotId);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
    }

    /// <summary>
    /// Restores a snapshot by diffing desired state against current state,
    /// removing extra mods and reinstalling missing ones.
    /// </summary>
    public async Task<SnapshotRestoreResult> RestoreSnapshotAsync(
        string snapshotId,
        ModInstallationService installService,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new SnapshotRestoreResult { SnapshotId = snapshotId };

        var snapshot = await GetSnapshotAsync(snapshotId, ct);
        if (snapshot == null)
        {
            result.Error = $"Snapshot '{snapshotId}' not found";
            return result;
        }

        // Load desired state (snapshot entries)
        var desiredEntries = await GetSnapshotEntriesAsync(snapshotId, ct);
        var desiredChangesetIds = desiredEntries.Select(e => e.ChangesetId).ToHashSet();

        // Load actual state (current committed changesets for this game)
        var normalizedPath = Path.GetFullPath(snapshot.GameInstallPath);
        var currentCommitted = await _changesetManager.ListByStateAsync(ChangesetState.Committed, ct);
        var actualChangesets = currentCommitted.Where(c =>
            c.TargetDirectory != null &&
            Path.GetFullPath(c.TargetDirectory).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var actualChangesetIds = actualChangesets.Select(c => c.ChangesetId).ToHashSet();

        // Compute diff
        var toRemove = actualChangesets.Where(c => !desiredChangesetIds.Contains(c.ChangesetId)).ToList();
        var toReinstall = desiredEntries.Where(e => !actualChangesetIds.Contains(e.ChangesetId)).ToList();

        _logger?.LogInformation(
            "Restore snapshot {Id}: removing {Remove} mod(s), reinstalling {Reinstall} mod(s), keeping {Keep} mod(s)",
            snapshotId, toRemove.Count, toReinstall.Count, desiredChangesetIds.Intersect(actualChangesetIds).Count());

        // Phase 1: Remove mods not in snapshot
        foreach (var changeset in toRemove)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress { CurrentOperation = $"Removing {changeset.ModId ?? changeset.ChangesetId}..." });

            try
            {
                var uninstallResult = await installService.UninstallAsync(changeset.ChangesetId, ct);
                if (uninstallResult.Success)
                {
                    result.ModsRemoved++;
                }
                else
                {
                    result.Errors.Add($"Failed to remove {changeset.ModId ?? changeset.ChangesetId}: {uninstallResult.Error}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error removing {changeset.ModId ?? changeset.ChangesetId}: {ex.Message}");
            }
        }

        // Phase 2: Reinstall mods from snapshot
        foreach (var entry in toReinstall)
        {
            ct.ThrowIfCancellationRequested();
            var modName = entry.ModId ?? entry.ChangesetId;
            progress?.Report(new InstallProgress { CurrentOperation = $"Reinstalling {modName}..." });

            if (string.IsNullOrEmpty(entry.ArchivePath) || !File.Exists(entry.ArchivePath))
            {
                result.Errors.Add($"Cannot reinstall {modName}: archive not found ({entry.ArchivePath ?? "no path"})");
                continue;
            }

            try
            {
                var targetDir = entry.TargetDirectory ?? snapshot.GameInstallPath;
                var options = new ModInstallationOptions
                {
                    ModId = entry.ModId,
                    AllowOverwrite = true,
                    CreateBackups = true,
                    AutoSnapshot = false // Prevent recursive auto-snapshot during restore
                };

                var installResult = await installService.InstallAsync(
                    entry.ArchivePath, targetDir, options, progress, ct);

                if (installResult.Success)
                {
                    result.ModsReinstalled++;
                }
                else
                {
                    result.Errors.Add($"Failed to reinstall {modName}: {installResult.Error}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error reinstalling {modName}: {ex.Message}");
            }
        }

        result.Success = result.Errors.Count == 0;

        _logger?.LogInformation(
            "Snapshot restore {Result}: {Removed} removed, {Reinstalled} reinstalled, {Errors} errors",
            result.Success ? "complete" : "partial",
            result.ModsRemoved, result.ModsReinstalled, result.Errors.Count);

        return result;
    }

    private static SnapshotRecord ReadSnapshotRecord(SqliteDataReader reader)
    {
        var triggerStr = reader.IsDBNull(6) ? "manual" : reader.GetString(6);
        var trigger = triggerStr switch
        {
            "auto_install" => SnapshotTrigger.AutoInstall,
            "auto_uninstall" => SnapshotTrigger.AutoUninstall,
            _ => SnapshotTrigger.Manual
        };

        return new SnapshotRecord
        {
            SnapshotId = reader.GetString(0),
            GameAppId = reader.GetInt32(1),
            GameName = reader.GetString(2),
            GameInstallPath = reader.GetString(3),
            Name = reader.IsDBNull(4) ? null : reader.GetString(4),
            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
            Trigger = trigger,
            CreatedAtUtc = reader.GetString(7),
            ModCount = reader.GetInt32(8)
        };
    }
}
