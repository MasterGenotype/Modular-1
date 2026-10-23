using FluentAssertions;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Downloads;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Downloads;

/// <summary>
/// Library update checks: a mod folder is identified by its downloads, and only files
/// uploaded after them count as updates, the successors of the files it has ticked first.
/// </summary>
public sealed class LibraryUpdatesTests : IDisposable
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Identify_ReadsTheDownloadRecord_AndTheCategoryFolder()
    {
        var root = NewDir();
        var folder = ModFolder(root, "nexusmods/cyberpunk2077/Modders Resources/ArchiveXL");
        var archive = Path.Combine(folder, "ArchiveXL 4198 1.27.5.zip");
        File.WriteAllText(archive, "zip");
        DependencyConventions.WriteDownloadRecord(
            archive, new DeclaredSource(BackendIds.NexusMods, "4198", "cyberpunk2077", "167253"));

        var mods = LibraryUpdates.Identify(root, [folder], out var unknown);

        unknown.Should().BeEmpty();
        var mod = mods.Should().ContainSingle().Subject;
        mod.BackendId.Should().Be(BackendIds.NexusMods);
        mod.ModId.Should().Be("4198");
        mod.Game.Should().Be("cyberpunk2077");
        mod.Category.Should().Be("Modders Resources");
        mod.FileIds.Should().BeEquivalentTo("167253");
    }

    [Fact]
    public void Identify_WithoutARecord_FallsBackOnANexusFileNameAndItsUploadTime()
    {
        var root = NewDir();
        var folder = ModFolder(root, "nexusmods/cyberpunk2077/Some Mod");
        File.WriteAllText(Path.Combine(folder, "Some Mod-874-1-5-1617050815.7z"), "7z");

        var mod = LibraryUpdates.Identify(root, [folder], out _).Should().ContainSingle().Subject;

        mod.ModId.Should().Be("874");
        mod.Game.Should().Be("cyberpunk2077", "the game folder names it");
        mod.FileIds.Should().BeEmpty();
        mod.NewestDownload.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1617050815));
    }

    [Fact]
    public void Identify_AFolderWithNothingToSayWhereItCameFrom_IsReported()
    {
        var root = NewDir();
        var folder = ModFolder(root, "gamebanana/somegame/Thing");
        File.WriteAllText(Path.Combine(folder, "thing.zip"), "zip");

        LibraryUpdates.Identify(root, [folder], out var unknown).Should().BeEmpty();
        unknown.Should().Equal(folder);
    }

    [Fact]
    public void Compare_SuggestsTheSuccessorByNameAndCategory_AndOffersTheRest()
    {
        var mod = Mod(fileIds: ["1", "2"]);
        var files = new[]
        {
            Listed("1", "Variant A", "main", Old),
            Listed("2", "Patch", "optional", Old),
            Listed("3", "Variant A", "main", New),   // successor of 1
            Listed("4", "Variant B", "main", New),   // another variant: offered, not ticked
            Listed("5", "Patch", "optional", New)    // successor of 2
        };

        var (newer, suggested) = LibraryUpdates.Compare(files, mod);

        newer.Select(f => f.FileId).Should().BeEquivalentTo("3", "4", "5");
        suggested.Select(f => f.FileId).Should().BeEquivalentTo("3", "5");
    }

    [Fact]
    public void Compare_NothingUploadedSince_IsNoUpdate()
    {
        var mod = Mod(fileIds: ["2"]);
        var files = new[] { Listed("1", "Main", "main", Old), Listed("2", "Main", "main", New) };

        LibraryUpdates.Compare(files, mod).New.Should().BeEmpty();
    }

    [Fact]
    public void Compare_ADownloadNoLongerListed_SuggestsTheNewestMainFile()
    {
        // The downloaded file moved to old versions, so only its time is to go on.
        var mod = Mod(fileIds: ["1"], newestDownload: Old);
        var files = new[]
        {
            Listed("7", "Main", "main", New),
            Listed("8", "Extra", "optional", New.AddDays(1))
        };

        var (newer, suggested) = LibraryUpdates.Compare(files, mod);

        newer.Should().HaveCount(2);
        suggested.Should().ContainSingle().Which.FileId.Should().Be("7");
    }

    [Fact]
    public void Compare_WithoutFileIds_UsesTheDownloadTimeAsTheBar()
    {
        var mod = Mod(fileIds: [], newestDownload: new DateTimeOffset(New));
        var files = new[] { Listed("1", "Main", "main", Old), Listed("2", "Main", "main", New.AddDays(3)) };

        var (newer, suggested) = LibraryUpdates.Compare(files, mod);

        newer.Select(f => f.FileId).Should().Equal("2");
        suggested.Select(f => f.FileId).Should().Equal("2");
    }

    private static LibraryMod Mod(string[] fileIds, DateTimeOffset? newestDownload = null) => new()
    {
        Folder = "/mods/x",
        Name = "X",
        BackendId = BackendIds.NexusMods,
        ModId = "1",
        Game = "cyberpunk2077",
        FileIds = fileIds.ToHashSet(),
        NewestDownload = newestDownload
    };

    private static BackendModFile Listed(string id, string name, string category, DateTime uploaded) => new()
    {
        FileId = id,
        FileName = $"{name}-{id}.zip",
        DisplayName = name,
        Category = category,
        UploadedAt = uploaded
    };

    private static string ModFolder(string root, string relative)
    {
        var folder = Path.Combine(root, relative);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-libupd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
