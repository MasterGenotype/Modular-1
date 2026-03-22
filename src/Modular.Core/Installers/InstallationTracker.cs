using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modular.Core.Database;

namespace Modular.Core.Installers;

/// <summary>
/// Tracks installed mods in the database for querying, verification, and uninstallation.
/// </summary>
public class InstallationTracker
{
    private readonly ModularDatabase _database;
    private readonly ILogger<InstallationTracker>? _logger;

    public InstallationTracker(ModularDatabase database, ILogger<InstallationTracker>? logger = null)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Records a successful mod installation.
    /// </summary>
    public async Task RecordInstallationAsync(InstalledModRecord record, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO installed_mods
                (mod_id, mod_name, version, game_domain, target_directory, archive_path,
                 installer_id, installed_files_json, backup_files_json, checksum,
                 installed_at_utc, updated_at_utc)
            VALUES
                (@modId, @name, @version, @domain, @target, @archive,
                 @installer, @files, @backups, @checksum,
                 @now, @now)
            """;

        cmd.Parameters.AddWithValue("@modId", record.ModId);
        cmd.Parameters.AddWithValue("@name", record.ModName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@version", record.Version ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@domain", record.GameDomain ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@target", record.TargetDirectory);
        cmd.Parameters.AddWithValue("@archive", record.ArchivePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@installer", record.InstallerId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@files", JsonSerializer.Serialize(record.InstalledFiles));
        cmd.Parameters.AddWithValue("@backups", JsonSerializer.Serialize(record.BackupFiles));
        cmd.Parameters.AddWithValue("@checksum", record.Checksum ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogInformation("Recorded installation of {ModId} ({Files} files)", record.ModId, record.InstalledFiles.Count);
    }

    /// <summary>
    /// Gets all installed mods.
    /// </summary>
    public async Task<List<InstalledModRecord>> GetInstalledModsAsync(
        string? gameDomain = null, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        if (gameDomain != null)
        {
            cmd.CommandText = """
                SELECT mod_id, mod_name, version, game_domain, target_directory, archive_path,
                       installer_id, installed_files_json, backup_files_json, checksum,
                       installed_at_utc, updated_at_utc
                FROM installed_mods WHERE game_domain = @domain
                ORDER BY installed_at_utc DESC
                """;
            cmd.Parameters.AddWithValue("@domain", gameDomain);
        }
        else
        {
            cmd.CommandText = """
                SELECT mod_id, mod_name, version, game_domain, target_directory, archive_path,
                       installer_id, installed_files_json, backup_files_json, checksum,
                       installed_at_utc, updated_at_utc
                FROM installed_mods ORDER BY installed_at_utc DESC
                """;
        }

        var results = new List<InstalledModRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    /// <summary>
    /// Gets a specific installed mod by ID.
    /// </summary>
    public async Task<InstalledModRecord?> GetInstalledModAsync(string modId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT mod_id, mod_name, version, game_domain, target_directory, archive_path,
                   installer_id, installed_files_json, backup_files_json, checksum,
                   installed_at_utc, updated_at_utc
            FROM installed_mods WHERE mod_id = @modId
            """;
        cmd.Parameters.AddWithValue("@modId", modId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadRecord(reader);
    }

    /// <summary>
    /// Removes the installation record for a mod.
    /// </summary>
    public async Task RemoveInstallationAsync(string modId, CancellationToken ct = default)
    {
        var connection = await _database.GetConnectionAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM installed_mods WHERE mod_id = @modId";
        cmd.Parameters.AddWithValue("@modId", modId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogInformation("Removed installation record for {ModId}", modId);
    }

    /// <summary>
    /// Verifies that all files for an installed mod still exist on disk.
    /// </summary>
    public async Task<VerificationResult> VerifyInstallationAsync(string modId, CancellationToken ct = default)
    {
        var record = await GetInstalledModAsync(modId, ct);
        if (record == null)
            return new VerificationResult { ModId = modId, Found = false };

        var result = new VerificationResult { ModId = modId, Found = true };

        foreach (var file in record.InstalledFiles)
        {
            if (File.Exists(file))
            {
                result.ValidFiles.Add(file);
            }
            else
            {
                result.MissingFiles.Add(file);
            }
        }

        result.IsValid = result.MissingFiles.Count == 0;
        return result;
    }

    private static InstalledModRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new InstalledModRecord
        {
            ModId = reader.GetString(0),
            ModName = reader.IsDBNull(1) ? null : reader.GetString(1),
            Version = reader.IsDBNull(2) ? null : reader.GetString(2),
            GameDomain = reader.IsDBNull(3) ? null : reader.GetString(3),
            TargetDirectory = reader.GetString(4),
            ArchivePath = reader.IsDBNull(5) ? null : reader.GetString(5),
            InstallerId = reader.IsDBNull(6) ? null : reader.GetString(6),
            InstalledFiles = JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? new(),
            BackupFiles = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8)) ?? new(),
            Checksum = reader.IsDBNull(9) ? null : reader.GetString(9),
            InstalledAtUtc = reader.GetString(10),
            UpdatedAtUtc = reader.GetString(11)
        };
    }
}

/// <summary>
/// Record of an installed mod.
/// </summary>
public class InstalledModRecord
{
    public string ModId { get; set; } = string.Empty;
    public string? ModName { get; set; }
    public string? Version { get; set; }
    public string? GameDomain { get; set; }
    public string TargetDirectory { get; set; } = string.Empty;
    public string? ArchivePath { get; set; }
    public string? InstallerId { get; set; }
    public List<string> InstalledFiles { get; set; } = new();
    /// <summary>Maps original file path to backup file path.</summary>
    public Dictionary<string, string> BackupFiles { get; set; } = new();
    public string? Checksum { get; set; }
    public string InstalledAtUtc { get; set; } = string.Empty;
    public string UpdatedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// Result of verifying an installed mod.
/// </summary>
public class VerificationResult
{
    public string ModId { get; set; } = string.Empty;
    public bool Found { get; set; }
    public bool IsValid { get; set; }
    public List<string> ValidFiles { get; set; } = new();
    public List<string> MissingFiles { get; set; } = new();
}
