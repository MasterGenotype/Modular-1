using FluentAssertions;
using Modular.Core.Database;
using Xunit;

namespace Modular.Core.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _testDbPath;

    public DatabaseTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"modular_test_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }

    [Fact]
    public void AddRecord_CanBeRetrieved()
    {
        var db = new DownloadDatabase(_testDbPath);
        var record = new DownloadRecord
        {
            GameDomain = "skyrim",
            ModId = 123,
            FileId = 456,
            Filename = "test.zip",
            Status = "success"
        };

        db.AddRecord(record);
        var found = db.FindRecord("skyrim", 123, 456);

        found.Should().NotBeNull();
        found!.Filename.Should().Be("test.zip");
    }

    [Fact]
    public void IsDownloaded_ReturnsTrueForSuccessStatus()
    {
        var db = new DownloadDatabase(_testDbPath);
        db.AddRecord(new DownloadRecord
        {
            GameDomain = "skyrim",
            ModId = 123,
            FileId = 456,
            Status = "success"
        });

        db.IsDownloaded("skyrim", 123, 456).Should().BeTrue();
    }

    [Fact]
    public void IsDownloaded_ReturnsFalseForFailedStatus()
    {
        var db = new DownloadDatabase(_testDbPath);
        db.AddRecord(new DownloadRecord
        {
            GameDomain = "skyrim",
            ModId = 123,
            FileId = 456,
            Status = "failed"
        });

        db.IsDownloaded("skyrim", 123, 456).Should().BeFalse();
    }

    [Fact]
    public async Task PersistsToDisk_AndReloads()
    {
        var db1 = new DownloadDatabase(_testDbPath);
        db1.AddRecord(new DownloadRecord
        {
            GameDomain = "skyrim",
            ModId = 1,
            FileId = 1,
            Status = "success"
        });
        await db1.SaveAsync();

        var db2 = new DownloadDatabase(_testDbPath);
        await db2.LoadAsync();

        db2.GetRecordCount().Should().Be(1);
    }

    [Fact]
    public void UpdateVerification_UpdatesRecord()
    {
        var db = new DownloadDatabase(_testDbPath);
        db.AddRecord(new DownloadRecord
        {
            GameDomain = "skyrim",
            ModId = 123,
            FileId = 456,
            Status = "success"
        });

        db.UpdateVerification("skyrim", 123, 456, "abc123", true);

        var record = db.FindRecord("skyrim", 123, 456);
        record!.Md5Actual.Should().Be("abc123");
        record.Status.Should().Be("verified");
    }
}
