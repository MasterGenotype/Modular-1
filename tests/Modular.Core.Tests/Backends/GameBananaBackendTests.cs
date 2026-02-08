using System.Text.Json;
using Modular.Core.Backends;
using Modular.Core.Backends.GameBanana;
using Modular.Core.Configuration;
using Xunit;

namespace Modular.Core.Tests.Backends;

public class GameBananaBackendTests
{
    [Fact]
    public void Id_ReturnsGamebanana()
    {
        var backend = CreateBackend();

        Assert.Equal("gamebanana", backend.Id);
    }

    [Fact]
    public void DisplayName_ReturnsGameBanana()
    {
        var backend = CreateBackend();

        Assert.Equal("GameBanana", backend.DisplayName);
    }

    [Fact]
    public void Capabilities_IsNone()
    {
        var backend = CreateBackend();

        // GameBanana has no special capabilities
        Assert.Equal(BackendCapabilities.None, backend.Capabilities);
        Assert.False(backend.Capabilities.HasFlag(BackendCapabilities.GameDomains));
        Assert.False(backend.Capabilities.HasFlag(BackendCapabilities.FileCategories));
        Assert.False(backend.Capabilities.HasFlag(BackendCapabilities.Md5Verification));
        Assert.False(backend.Capabilities.HasFlag(BackendCapabilities.RateLimited));
        Assert.False(backend.Capabilities.HasFlag(BackendCapabilities.Authentication));
        Assert.False(backend.Capabilities.HasFlag(BackendCapabilities.ModCategories));
    }

    [Fact]
    public void ValidateConfiguration_ReturnsError_WhenUserIdMissing()
    {
        var settings = new AppSettings { GameBananaUserId = "" };
        var backend = CreateBackend(settings);

        var errors = backend.ValidateConfiguration();

        Assert.Single(errors);
        Assert.Contains("user ID", errors[0]);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsEmpty_WhenUserIdPresent()
    {
        var settings = new AppSettings { GameBananaUserId = "12345" };
        var backend = CreateBackend(settings);

        var errors = backend.ValidateConfiguration();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ResolveDownloadUrlAsync_ReturnsNull()
    {
        // GameBanana provides URLs inline in GetModFilesAsync,
        // so ResolveDownloadUrlAsync always returns null
        var backend = CreateBackend();

        var result = await backend.ResolveDownloadUrlAsync("12345", "67890");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModFilesAsync_DoesNotRequireGameDomain()
    {
        // GameBanana doesn't require game domain - should not throw
        var backend = CreateBackend();

        // This will fail because we can't actually call the API,
        // but it should NOT throw ArgumentException for missing domain
        var files = await backend.GetModFilesAsync("12345", gameDomain: null);

        // Returns empty list due to API failure (no real API call), but no exception
        Assert.NotNull(files);
    }

    [Fact]
    public async Task GetUserModsAsync_DoesNotRequireGameDomain()
    {
        // GameBanana doesn't use game domains at all
        var backend = CreateBackend();

        var mods = await backend.GetUserModsAsync(gameDomain: null);

        // Returns empty list due to API failure, but no exception
        Assert.NotNull(mods);
    }

    [Fact]
    public void GameBananaGameIds_DefaultsToEmptyList()
    {
        var settings = new AppSettings();

        Assert.NotNull(settings.GameBananaGameIds);
        Assert.Empty(settings.GameBananaGameIds);
    }

    [Fact]
    public void GameBananaDownloadDir_DefaultsToGamebanana()
    {
        var settings = new AppSettings();

        Assert.Equal("gamebanana", settings.GameBananaDownloadDir);
    }

    private static GameBananaBackend CreateBackend(AppSettings? settings = null)
    {
        settings ??= new AppSettings { GameBananaUserId = "test-user-id" };
        return new GameBananaBackend(settings);
    }
}

/// <summary>
/// Tests for GameBanana API response model deserialization.
/// </summary>
public class GameBananaModelsTests
{
    [Fact]
    public void GameBananaRecord_DeserializesCorrectly()
    {
        var json = """
            {
                "_idRow": 12345,
                "_sName": "Test Mod",
                "_sModelName": "Mod",
                "_tsDateAdded": 1700000000,
                "_tsDateModified": 1700100000,
                "_nSubscriptionCount": 42,
                "_aSubmitter": {
                    "_idRow": 789,
                    "_sName": "TestAuthor"
                },
                "_aGame": {
                    "_idRow": 8694,
                    "_sName": "Stardew Valley"
                }
            }
            """;

        var record = JsonSerializer.Deserialize<GameBananaRecord>(json);

        Assert.NotNull(record);
        Assert.Equal(12345, record.Id);
        Assert.Equal("Test Mod", record.Name);
        Assert.Equal("Mod", record.ModelName);
        Assert.Equal(1700000000, record.DateAddedTimestamp);
        Assert.Equal(1700100000, record.DateModifiedTimestamp);
        Assert.Equal(42, record.SubscriptionCount);
        Assert.NotNull(record.Submitter);
        Assert.Equal(789, record.Submitter.Id);
        Assert.Equal("TestAuthor", record.Submitter.Name);
        Assert.NotNull(record.Game);
        Assert.Equal(8694, record.Game.Id);
        Assert.Equal("Stardew Valley", record.Game.Name);
    }

    [Fact]
    public void GameBananaFileEntry_DeserializesCorrectly()
    {
        var json = """
            {
                "_idRow": 999,
                "_sFile": "my_mod_v1.2.zip",
                "_nFilesize": 1048576,
                "_sDownloadUrl": "https://files.gamebanana.com/mods/my_mod_v1.2.zip",
                "_sMd5Checksum": "abc123",
                "_sDescription": "Main file",
                "_tsDateAdded": 1700000000,
                "_nDownloadCount": 150
            }
            """;

        var file = JsonSerializer.Deserialize<GameBananaFileEntry>(json);

        Assert.NotNull(file);
        Assert.Equal(999, file.Id);
        Assert.Equal("my_mod_v1.2.zip", file.FileName);
        Assert.Equal(1048576, file.FileSize);
        Assert.Equal("https://files.gamebanana.com/mods/my_mod_v1.2.zip", file.DownloadUrl);
        Assert.Equal("abc123", file.Md5Checksum);
        Assert.Equal("Main file", file.Description);
        Assert.Equal(1700000000, file.DateAddedTimestamp);
        Assert.Equal(150, file.DownloadCount);
    }

    [Fact]
    public void GameBananaRecordResponse_DeserializesCorrectly()
    {
        var json = """
            {
                "_aRecords": [
                    { "_idRow": 1, "_sName": "Mod 1" },
                    { "_idRow": 2, "_sName": "Mod 2" }
                ],
                "_nRecordCount": 2
            }
            """;

        var response = JsonSerializer.Deserialize<GameBananaRecordResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(2, response.RecordCount);
        Assert.Equal(2, response.Records.Count);
        Assert.Equal(1, response.Records[0].Id);
        Assert.Equal("Mod 1", response.Records[0].Name);
    }

    [Fact]
    public void GameBananaFilesResponse_DeserializesCorrectly()
    {
        var json = """
            {
                "_aFiles": [
                    { "_idRow": 1, "_sFile": "file1.zip", "_sDownloadUrl": "https://example.com/1", "_nFilesize": 100, "_tsDateAdded": 0, "_nDownloadCount": 0 },
                    { "_idRow": 2, "_sFile": "file2.zip", "_sDownloadUrl": "https://example.com/2", "_nFilesize": 200, "_tsDateAdded": 0, "_nDownloadCount": 0 }
                ]
            }
            """;

        var response = JsonSerializer.Deserialize<GameBananaFilesResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(2, response.Files.Count);
        Assert.Equal("file1.zip", response.Files[0].FileName);
        Assert.Equal("file2.zip", response.Files[1].FileName);
    }

    [Fact]
    public void TimestampConversion_ProducesCorrectDateTime()
    {
        // Unix timestamp for 2023-11-14 22:13:20 UTC
        long timestamp = 1700000000;

        var dateTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;

        Assert.Equal(2023, dateTime.Year);
        Assert.Equal(11, dateTime.Month);
        Assert.Equal(14, dateTime.Day);
    }

    [Fact]
    public void GameBananaRecord_HandlesNullOptionalFields()
    {
        var json = """
            {
                "_idRow": 12345,
                "_sName": "Test Mod"
            }
            """;

        var record = JsonSerializer.Deserialize<GameBananaRecord>(json);

        Assert.NotNull(record);
        Assert.Equal(12345, record.Id);
        Assert.Null(record.Submitter);
        Assert.Null(record.Game);
        Assert.Null(record.DateModifiedTimestamp);
    }
}
