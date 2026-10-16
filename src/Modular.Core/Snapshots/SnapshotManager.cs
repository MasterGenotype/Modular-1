using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;
using Modular.Core.Deployment;
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
/// What a restore would do with one mod.
/// </summary>
public enum SnapshotRestoreAction
{
    /// <summary>Installed now and in the snapshot; kept as it is.</summary>
    Keep,

    /// <summary>In the snapshot but not installed now; brought back from its staged copy.</summary>
    Add,

    /// <summary>Its staged copy is gone; reinstalled from its archive.</summary>
    Reinstall,

    /// <summary>Its staged copy and its archive are both gone, so it cannot be restored.</summary>
    Unavailable,

    /// <summary>Installed now but not in the snapshot; removed.</summary>
    Remove
}

/// <summary>
/// One mod in a restore preview.
/// </summary>
public sealed class SnapshotRestorePlanItem
{
    /// <summary>Mod identifier.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Name to show; the mod id when none was recorded.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether the snapshot has the mod enabled; <c>null</c> when it is not in the snapshot.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Archive a reinstall would read, when one is recorded.</summary>
    public string? ArchivePath { get; init; }

    /// <summary>What the restore would do with it.</summary>
    public SnapshotRestoreAction Action { get; init; }
}

/// <summary>
/// What restoring a snapshot would change, worked out without changing anything.
/// </summary>
public sealed class SnapshotRestorePlan
{
    /// <summary>Every mod the restore touches or keeps; snapshot mods first, in load order.</summary>
    public List<SnapshotRestorePlanItem> Items { get; init; } = new();

    /// <summary>Mods in the snapshot.</summary>
    public int ModCount => Items.Count(i => i.Action != SnapshotRestoreAction.Remove);

    /// <summary>Mods in the snapshot that it has enabled.</summary>
    public int EnabledCount => Items.Count(i => i.Enabled == true);

    /// <summary>Mods the restore would install: brought back from staging or reinstalled.</summary>
    public int InstallCount => Count(SnapshotRestoreAction.Add) + Count(SnapshotRestoreAction.Reinstall);

    /// <summary>Number of mods with the given action.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The count.</returns>
    public int Count(SnapshotRestoreAction action) => Items.Count(i => i.Action == action);
}

/// <summary>
/// One deployed mod as a snapshot captured it.
/// </summary>
public sealed class SnapshotGameModRecord
{
    /// <summary>Key of the game the mod belongs to.</summary>
    public string GameKey { get; init; } = string.Empty;

    /// <summary>Mod identifier.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Module that owned the game.</summary>
    public string? ModuleId { get; init; }

    /// <summary>Installer that staged the mod.</summary>
    public string? InstallerId { get; init; }

    /// <summary>Absolute path of the mod's staging tree.</summary>
    public string StagingPath { get; init; } = string.Empty;

    /// <summary>
    /// Fingerprint of the staging tree at capture time, so a restore can tell that the
    /// tree is gone or has changed.
    /// </summary>
    public string? StagingHash { get; init; }

    /// <summary>Position in the load order; later wins.</summary>
    public int OrderIndex { get; init; }

    /// <summary>Whether the mod was enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Changeset that installed the mod, used to find its archive for re-staging.</summary>
    public string? ChangesetId { get; init; }

    /// <summary>Where the mod came from.</summary>
    public string Source { get; init; } = "local";

    /// <summary>Backend file id.</summary>
    public string? SourceFileId { get; init; }

    /// <summary>Human-readable name.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Manages creation, listing, and restoration of mod installation snapshots.
/// </summary>
public class SnapshotManager
{
    private readonly ModularDatabase _database;
    private readonly ChangesetManager _changesetManager;
    private readonly GameModRepository _gameMods;
    private readonly ILogger<SnapshotManager>? _logger;

    /// <summary>
    /// Creates the manager.
    /// </summary>
    /// <param name="database">Modular database.</param>
    /// <param name="changesetManager">Changeset manager, for legacy snapshots.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="gameMods">
    /// Deployed-mod repository; a default one over the same database is created when
    /// omitted.
    /// </param>
    public SnapshotManager(
        ModularDatabase database,
        ChangesetManager changesetManager,
        ILogger<SnapshotManager>? logger = null,
        GameModRepository? gameMods = null)
    {
        _database = database;
        _changesetManager = changesetManager;
        _gameMods = gameMods ?? new GameModRepository(database);
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
        CancellationToken ct = default,
        string? gameKey = null)
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

        // The deployed mod set is the state that actually matters for a managed game:
        // which mods, in what order, enabled or not. Restoring it is one transaction plus
        // one deploy, rather than a changeset diff replayed mod by mod.
        var managedMods = string.IsNullOrEmpty(gameKey)
            ? new List<GameModRecord>()
            : await _gameMods.ListAsync(gameKey, enabledOnly: false, ct);

        // A managed game is restored from its mod set, so that is what the snapshot holds.
        // Its committed changesets can name the same mod several times over.
        var modCount = managedMods.Count > 0 ? managedMods.Count : gameChangesets.Count;

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
                cmd.Parameters.AddWithValue("@count", modCount);
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

            foreach (var mod in managedMods)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = """
                    INSERT INTO snapshot_game_mod
                        (snapshot_id, game_key, mod_id, module_id, installer_id, staging_path,
                         staging_hash, order_index, enabled, changeset_id, source,
                         source_file_id, display_name)
                    VALUES
                        (@sid, @gameKey, @modId, @moduleId, @installerId, @stagingPath,
                         @stagingHash, @orderIndex, @enabled, @changesetId, @source,
                         @sourceFileId, @displayName)
                    """;
                cmd.Parameters.AddWithValue("@sid", snapshotId);
                cmd.Parameters.AddWithValue("@gameKey", mod.GameKey);
                cmd.Parameters.AddWithValue("@modId", mod.ModId);
                cmd.Parameters.AddWithValue("@moduleId", (object?)mod.ModuleId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@installerId", (object?)mod.InstallerId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@stagingPath", mod.StagingPath);
                cmd.Parameters.AddWithValue(
                    "@stagingHash", (object?)HashStagingTree(mod.StagingPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@orderIndex", mod.OrderIndex ?? 0);
                cmd.Parameters.AddWithValue("@enabled", mod.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@changesetId", (object?)mod.ChangesetId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@source", mod.Source);
                cmd.Parameters.AddWithValue("@sourceFileId", (object?)mod.SourceFileId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@displayName", (object?)mod.DisplayName ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);

            _logger?.LogInformation(
                "Created snapshot {Id} for {Game} with {Count} changesets and {Managed} managed mods",
                snapshotId, gameName, gameChangesets.Count, managedMods.Count);

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
                ModCount = modCount
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
        cmd.CommandText = $"""
            SELECT {SnapshotColumns}
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
        cmd.CommandText = $"""
            SELECT {SnapshotColumns}
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
    /// <param name="gameAppId">Game.</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="month">Calendar month.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="timeZone">
    /// Zone the calendar is shown in; the local zone when omitted. A snapshot belongs to the
    /// day its time falls on there, which is what the user reads beside it.
    /// </param>
    /// <returns>The days of the month with at least one snapshot.</returns>
    public async Task<HashSet<int>> GetSnapshotDatesAsync(
        int gameAppId, int year, int month, CancellationToken ct = default, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;

        var connection = await _database.GetConnectionAsync();
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(first, timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(first.AddMonths(1), timeZone);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT created_at_utc
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
            if (DateTime.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var created))
            {
                days.Add(TimeZoneInfo.ConvertTimeFromUtc(created.ToUniversalTime(), timeZone).Day);
            }
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

        // A snapshot of a managed game restores through the deployment: rewrite the mod
        // set in one transaction, re-stage anything missing without deploying, then deploy
        // exactly once. Replaying it changeset by changeset would purge and redeploy N
        // times and make every intermediate state visible on disk.
        var managedMods = await GetSnapshotGameModsAsync(snapshotId, ct);
        if (managedMods.Count > 0)
            return await RestoreManagedSnapshotAsync(snapshot, managedMods, installService, progress, ct);

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

    /// <summary>
    /// Works out what restoring a snapshot would do, without changing anything.
    /// </summary>
    /// <param name="snapshotId">Snapshot identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plan, or <c>null</c> when the snapshot does not exist.</returns>
    /// <remarks>
    /// Follows the same rules as <see cref="RestoreSnapshotAsync"/>, so the preview and
    /// the restore agree: a managed game is compared mod by mod against its current mod
    /// set, a legacy one changeset by changeset.
    /// </remarks>
    public async Task<SnapshotRestorePlan?> PlanRestoreAsync(string snapshotId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(snapshotId, ct);
        if (snapshot == null)
            return null;

        var plan = new SnapshotRestorePlan();

        var managedMods = await GetSnapshotGameModsAsync(snapshotId, ct);
        if (managedMods.Count > 0)
        {
            var gameKey = managedMods[0].GameKey;
            var current = await _gameMods.ListAsync(gameKey, enabledOnly: false, ct);
            var currentIds = current.Select(c => c.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desiredIds = managedMods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in managedMods)
            {
                var archivePath = mod.ChangesetId == null
                    ? null
                    : (await _changesetManager.GetChangesetAsync(mod.ChangesetId, ct))?.ArchivePath;

                var action = HasStagedFiles(mod.StagingPath)
                    ? currentIds.Contains(mod.ModId) ? SnapshotRestoreAction.Keep : SnapshotRestoreAction.Add
                    : archivePath != null && File.Exists(archivePath)
                        ? SnapshotRestoreAction.Reinstall
                        : SnapshotRestoreAction.Unavailable;

                plan.Items.Add(new SnapshotRestorePlanItem
                {
                    ModId = mod.ModId,
                    DisplayName = mod.DisplayName ?? mod.ModId,
                    Enabled = mod.Enabled,
                    ArchivePath = archivePath,
                    Action = action
                });
            }

            plan.Items.AddRange(current
                .Where(c => !desiredIds.Contains(c.ModId))
                .Select(c => new SnapshotRestorePlanItem
                {
                    ModId = c.ModId,
                    DisplayName = c.DisplayName ?? c.ModId,
                    Action = SnapshotRestoreAction.Remove
                }));

            return plan;
        }

        var entries = await GetSnapshotEntriesAsync(snapshotId, ct);
        var normalizedPath = Path.GetFullPath(snapshot.GameInstallPath);
        var actual = (await _changesetManager.ListByStateAsync(ChangesetState.Committed, ct))
            .Where(c =>
                c.TargetDirectory != null &&
                Path.GetFullPath(c.TargetDirectory).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var actualIds = actual.Select(c => c.ChangesetId).ToHashSet();
        var entryIds = entries.Select(e => e.ChangesetId).ToHashSet();

        foreach (var entry in entries)
        {
            var action = actualIds.Contains(entry.ChangesetId)
                ? SnapshotRestoreAction.Keep
                : !string.IsNullOrEmpty(entry.ArchivePath) && File.Exists(entry.ArchivePath)
                    ? SnapshotRestoreAction.Reinstall
                    : SnapshotRestoreAction.Unavailable;

            plan.Items.Add(new SnapshotRestorePlanItem
            {
                ModId = entry.ModId ?? entry.ChangesetId,
                DisplayName = entry.ModId ?? entry.ChangesetId,
                Enabled = true,
                ArchivePath = entry.ArchivePath,
                Action = action
            });
        }

        plan.Items.AddRange(actual
            .Where(c => !entryIds.Contains(c.ChangesetId))
            .Select(c => new SnapshotRestorePlanItem
            {
                ModId = c.ModId ?? c.ChangesetId,
                DisplayName = c.ModId ?? c.ChangesetId,
                ArchivePath = c.ArchivePath,
                Action = SnapshotRestoreAction.Remove
            }));

        return plan;
    }

    private static bool HasStagedFiles(string stagingPath) =>
        Directory.Exists(stagingPath) && Directory.EnumerateFileSystemEntries(stagingPath).Any();

    /// <summary>
    /// Reads the deployed mod set a snapshot captured.
    /// </summary>
    /// <param name="snapshotId">Snapshot identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The captured mods in load order; empty for a legacy snapshot.</returns>
    public async Task<List<SnapshotGameModRecord>> GetSnapshotGameModsAsync(
        string snapshotId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_key, mod_id, module_id, installer_id, staging_path, staging_hash,
                   order_index, enabled, changeset_id, source, source_file_id, display_name
            FROM snapshot_game_mod
            WHERE snapshot_id = @sid
            ORDER BY order_index, mod_id
            """;
        cmd.Parameters.AddWithValue("@sid", snapshotId);

        var results = new List<SnapshotGameModRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new SnapshotGameModRecord
            {
                GameKey = reader.GetString(0),
                ModId = reader.GetString(1),
                ModuleId = reader.IsDBNull(2) ? null : reader.GetString(2),
                InstallerId = reader.IsDBNull(3) ? null : reader.GetString(3),
                StagingPath = reader.GetString(4),
                StagingHash = reader.IsDBNull(5) ? null : reader.GetString(5),
                OrderIndex = reader.GetInt32(6),
                Enabled = reader.GetInt32(7) != 0,
                ChangesetId = reader.IsDBNull(8) ? null : reader.GetString(8),
                Source = reader.IsDBNull(9) ? "local" : reader.GetString(9),
                SourceFileId = reader.IsDBNull(10) ? null : reader.GetString(10),
                DisplayName = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }

        return results;
    }

    /// <summary>
    /// Restores a managed game's mod set and deploys it once.
    /// </summary>
    private async Task<SnapshotRestoreResult> RestoreManagedSnapshotAsync(
        SnapshotRecord snapshot,
        List<SnapshotGameModRecord> desired,
        ModInstallationService installService,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var result = new SnapshotRestoreResult { SnapshotId = snapshot.SnapshotId };
        var gameKey = desired[0].GameKey;

        var current = await _gameMods.ListAsync(gameKey, enabledOnly: false, ct);
        var desiredIds = desired.Select(d => d.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        result.ModsRemoved = current.Count(c => !desiredIds.Contains(c.ModId));

        // Re-stage anything whose staging tree has gone, without deploying: the single
        // deploy at the end is what puts files in the game folder.
        foreach (var mod in desired)
        {
            ct.ThrowIfCancellationRequested();

            if (HasStagedFiles(mod.StagingPath))
                continue;

            var archivePath = mod.ChangesetId == null
                ? null
                : (await _changesetManager.GetChangesetAsync(mod.ChangesetId, ct))?.ArchivePath;

            if (archivePath == null || !File.Exists(archivePath))
            {
                result.Errors.Add(
                    $"Cannot re-stage '{mod.ModId}': its archive is no longer available " +
                    $"({archivePath ?? "no path recorded"}).");
                continue;
            }

            progress?.Report(new InstallProgress
            {
                CurrentOperation = $"Re-staging {mod.DisplayName ?? mod.ModId}..."
            });

            var installResult = await installService.InstallAsync(
                archivePath,
                snapshot.GameInstallPath,
                new ModInstallationOptions
                {
                    ModId = mod.ModId,
                    AllowOverwrite = true,
                    AutoSnapshot = false,
                    NoDeploy = true,
                    Source = mod.Source,
                    SourceFileId = mod.SourceFileId
                },
                progress,
                ct);

            if (installResult.Success)
                result.ModsReinstalled++;
            else
                result.Errors.Add($"Failed to re-stage '{mod.ModId}': {installResult.Error}");
        }

        // One transaction to the recorded state.
        await _gameMods.ReplaceAllAsync(
            gameKey,
            desired.Select(d => new GameModRecord
            {
                GameKey = d.GameKey,
                ModId = d.ModId,
                ModuleId = d.ModuleId,
                InstallerId = d.InstallerId,
                StagingPath = d.StagingPath,
                OrderIndex = d.OrderIndex,
                Enabled = d.Enabled,
                Source = d.Source,
                SourceFileId = d.SourceFileId,
                ChangesetId = d.ChangesetId,
                DisplayName = d.DisplayName
            }).ToList(),
            ct);

        // …then exactly one deploy.
        progress?.Report(new InstallProgress { CurrentOperation = "Deploying restored mod set..." });

        var deployment = await installService.DeployGameAsync(snapshot.GameInstallPath, ct: ct);

        if (deployment is { Success: false })
            result.Errors.Add($"Deploy after restore failed: {deployment.Error}");

        result.Success = result.Errors.Count == 0;

        _logger?.LogInformation(
            "Restored managed snapshot {Id}: {Desired} mods, {Removed} removed, {Restaged} re-staged",
            snapshot.SnapshotId, desired.Count, result.ModsRemoved, result.ModsReinstalled);

        return result;
    }

    /// <summary>
    /// Fingerprints a staging tree so a restore can tell that its content changed.
    /// </summary>
    /// <param name="stagingPath">Root of the mod's staging tree.</param>
    /// <returns>The hash, or <c>null</c> when the tree is missing.</returns>
    /// <remarks>
    /// Paths, sizes and modification times rather than contents: this needs to be cheap
    /// enough to run for every mod on every snapshot, and it only has to notice that a
    /// tree is no longer what was captured.
    /// </remarks>
    private static string? HashStagingTree(string stagingPath)
    {
        if (!Directory.Exists(stagingPath))
            return null;

        var hash = new System.IO.Hashing.XxHash64();

        foreach (var file in Directory
                     .EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            var entry =
                $"{Path.GetRelativePath(stagingPath, file).Replace('\\', '/')}|" +
                $"{info.Length}|{info.LastWriteTimeUtc.Ticks}";

            hash.Append(System.Text.Encoding.UTF8.GetBytes(entry));
        }

        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    // Snapshots taken before mod_count counted the managed mod set recorded every
    // committed changeset instead; the mod set's size is read in its place.
    private const string SnapshotColumns = """
        snapshot_id, game_appid, game_name, game_install_path, name, description, trigger, created_at_utc,
        COALESCE(NULLIF((SELECT COUNT(*) FROM snapshot_game_mod g WHERE g.snapshot_id = snapshot.snapshot_id), 0), mod_count)
        """;

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
