using FluentAssertions;
using Microsoft.Data.Sqlite;
using Modular.Core.Database;
using Modular.Core.Deployment;
using Xunit;

namespace Modular.Core.Tests;

/// <summary>
/// Schema migrations of the Modular database that change how a game's mods are stored.
/// </summary>
public sealed class DatabaseSchemaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modular-schema").FullName;

    [Fact]
    public async Task AFreshDatabase_StoresALoadOrderApartFromTheInstallOrder()
    {
        await using var database = new ModularDatabase(Path.Combine(_dir, "fresh.db"));
        await database.InitializeAsync();

        (await UserVersionAsync(database)).Should().Be(7);
        (await ColumnsAsync(database, "game_mod")).Should().Contain("load_index");
        (await ColumnsAsync(database, "snapshot_game_mod")).Should().Contain("load_index");
    }

    [Fact]
    public async Task AVersion6Database_GainsTheLoadOrder_StartingEqualToTheInstallOrder()
    {
        var path = Path.Combine(_dir, "v6.db");

        // A v6 database: v7's column removed again, with mods already in it.
        await using (var database = new ModularDatabase(path))
        {
            await database.InitializeAsync();
            await ExecAsync(database, """
                INSERT INTO game_mod (game_key, mod_id, staging_path, order_index, installed_at)
                VALUES ('g', 'first', '/s/first', 0, 'now'), ('g', 'second', '/s/second', 5, 'now');
                ALTER TABLE game_mod DROP COLUMN load_index;
                ALTER TABLE snapshot_game_mod DROP COLUMN load_index;
                PRAGMA user_version = 6;
                """);
        }

        SqliteConnection.ClearAllPools();

        await using (var database = new ModularDatabase(path))
        {
            await database.InitializeAsync();

            (await UserVersionAsync(database)).Should().Be(7);

            var mods = await new GameModRepository(database).ListAsync("g");
            mods.Select(m => (m.ModId, m.OrderIndex, m.LoadIndex))
                .Should().Equal(("first", 0, 0), ("second", 5, 5));
        }
    }

    private static async Task<long> UserVersionAsync(ModularDatabase database)
    {
        var connection = await database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ColumnsAsync(ModularDatabase database, string table)
    {
        var connection = await database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}');";

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecAsync(ModularDatabase database, string sql)
    {
        var connection = await database.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }
}
