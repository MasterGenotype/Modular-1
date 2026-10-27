using FluentAssertions;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Installers;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// Grouping the install list by the mod each archive is a file of, and learning from the
/// mod's page which archives are versions and which are add-ons.
/// </summary>
public sealed class ArchiveGrouperTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modular-group").FullName;

    [Fact]
    public void ArchivesInOneModFolder_AreOneMod()
    {
        var light = Write("gamebanana", "skyrim", "Maps", "Mod A", "Mod A - Light.zip");
        var dark = Write("gamebanana", "skyrim", "Maps", "Mod A", "Mod A - Dark.zip");
        var other = Write("gamebanana", "skyrim", "Maps", "Mod B", "b.zip");

        var groups = ArchiveGrouper.Identify([light, other, dark], _root);

        groups.Select(g => (g.Identity.Name, g.Archives.Count)).Should().Equal(("Mod A", 2), ("Mod B", 1));
        groups[0].Archives.Should().Equal(light, dark);
    }

    [Fact]
    public void LooseArchivesInAGameFolder_AreNotOneMod()
    {
        var a = Write("gamebanana", "skyrim", "a.zip");
        var b = Write("gamebanana", "skyrim", "b.zip");

        ArchiveGrouper.Identify([a, b], _root).Should().HaveCount(2);
    }

    [Fact]
    public void ANexusModsIdInTheFileName_JoinsArchivesFromDifferentFolders()
    {
        // The id is per game, and both are under nexusmods/baldursgate3.
        var main = Write("nexusmods", "baldursgate3", "PRESET-11456-1-4-0-0-1725753424.zip");
        var extra = Write("nexusmods", "baldursgate3", "Presets", "Extra-11456-1-2-0-0-1723262879.zip");
        var otherGame = Write("nexusmods", "cyberpunk2077", "Thing-11456-1-0-1700000000.zip");

        var groups = ArchiveGrouper.Identify([main, extra, otherGame], _root);

        groups.Select(g => g.Archives.Count).Should().Equal(2, 1);
        groups[0].Identity.Source.Should().Be(new DeclaredSource(BackendIds.NexusMods, "11456", "baldursgate3"));
    }

    [Fact]
    public void ADownloadRecord_SaysWhichModWhereverTheArchiveIs()
    {
        var a = Write("elsewhere", "renamed-1.zip");
        var b = Write("downloads", "renamed-2.zip");
        DependencyConventions.WriteDownloadRecord(a, new DeclaredSource(BackendIds.ModIo, "42", "6715", "100"));
        DependencyConventions.WriteDownloadRecord(b, new DeclaredSource(BackendIds.ModIo, "42", "6715", "200"));

        var groups = ArchiveGrouper.Identify([a, b], _root);

        groups.Should().ContainSingle().Which.Identity.Source!.ModId.Should().Be("42");
    }

    [Fact]
    public async Task TheModsPage_SaysWhichArchivesAreVersions()
    {
        var main = Write("nexusmods", "baldursgate3", "Mod A", "Mod A Main-500-2-0-1700000000.zip");
        var old = Write("nexusmods", "baldursgate3", "Mod A", "Mod A Main-500-1-0-1600000000.zip");
        var addOn = Write("nexusmods", "baldursgate3", "Mod A", "renamed-addon.zip");
        DependencyConventions.WriteDownloadRecord(addOn, new DeclaredSource(BackendIds.NexusMods, "500", "baldursgate3", "3"));

        var backend = new FilesBackend(BackendIds.NexusMods)
        {
            Files =
            {
                new BackendModFile { FileId = "1", FileName = "Mod A Main-500-2-0-1700000000.zip", Category = "main", DisplayName = "Mod A" },
                new BackendModFile { FileId = "2", FileName = "Mod A Main-500-1-0-1600000000.zip", Category = "old_version" },
                new BackendModFile { FileId = "3", FileName = "addon.zip", Category = "optional", DisplayName = "Extra armour" }
            }
        };

        var groups = await new ArchiveGrouper([backend], _root).GroupAsync([main, old, addOn]);

        var group = groups.Should().ContainSingle().Subject;
        group.KnowsFileTypes.Should().BeTrue();
        group.Archives.Select(a => (a.Category, a.IsVersion)).Should().Equal(
            ("main", true), ("old_version", true), ("optional", false));
        group.Archives[2].Label.Should().Be("Extra armour");
        backend.RequestedFilter!.IncludeArchived.Should().BeTrue();
    }

    [Fact]
    public async Task AModWithOneArchive_HasItsPageLeftUnread()
    {
        var only = Write("nexusmods", "baldursgate3", "Mod A", "Mod A-500-2-0-1700000000.zip");
        var backend = new FilesBackend(BackendIds.NexusMods);

        await new ArchiveGrouper([backend], _root).GroupAsync([only]);

        backend.Calls.Should().Be(0);
    }

    [Fact]
    public async Task APageThatCannotBeRead_LeavesTheFileTypesUnknown()
    {
        var a = Write("nexusmods", "baldursgate3", "Mod A", "A-500-2-0-1700000000.zip");
        var b = Write("nexusmods", "baldursgate3", "Mod A", "B-500-1-0-1600000000.zip");
        var backend = new FilesBackend(BackendIds.NexusMods) { Failure = new HttpRequestException("offline") };

        var groups = await new ArchiveGrouper([backend], _root).GroupAsync([a, b]);

        groups.Should().ContainSingle().Which.KnowsFileTypes.Should().BeFalse();
    }

    [Fact]
    public void FindClashes_PartsOfOneMod_ShareNoFiles()
    {
        // VTK Vanilla HD: the body and the head are both main files, and both needed.
        var body = Zip("body.zip", "archive/pc/mod/vtk_Body.archive", "archive/pc/mod/vtk_Body.xl", "readme.txt");
        var head = Zip("head.zip", "Head/archive/pc/mod/vtk_Head.archive", "Head/readme.txt");

        ArchiveGrouper.FindClashes([body, head], new Modular.Core.Archives.ArchiveReaderFactory())
            .Should().BeEmpty("readmes do not count, and the game files differ");
    }

    [Fact]
    public void FindClashes_VersionsOfOneFile_Clash_EvenInsideAWrapperFolder()
    {
        var v1 = Zip("v1.zip", "archive/pc/mod/thing.archive");
        var v2 = Zip("v2.zip", "Thing v2/archive/pc/mod/thing.archive");
        var other = Zip("other.zip", "archive/pc/mod/other.archive");

        ArchiveGrouper.FindClashes([v1, other, v2], new Modular.Core.Archives.ArchiveReaderFactory())
            .Should().Equal((0, 2));
    }

    [Fact]
    public void FindClashes_TwoFomodInstallers_AreAlternatives()
    {
        var a = Zip("25.zip", "fomod/ModuleConfig.xml", "mod_25/archive/pc/mod/sweaty25.archive");
        var b = Zip("50.zip", "fomod/ModuleConfig.xml", "mod_50/archive/pc/mod/sweaty50.archive");

        ArchiveGrouper.FindClashes([a, b], new Modular.Core.Archives.ArchiveReaderFactory())
            .Should().ContainSingle();
    }

    [Fact]
    public void PickCompatible_TicksEveryPart_AndTheFirstOfEachSetOfAlternatives()
    {
        // 0 body v2, 1 head, 2 body v1, 3 hands: the bodies clash, the rest go alongside.
        var clashes = new List<(int, int)> { (0, 2) };

        ArchiveGrouper.PickCompatible([0, 1, 2, 3], clashes).Should().Equal(0, 1, 3);
    }

    [Fact]
    public void PickCompatible_FollowsTheOrderGiven_AndClashesEitherWay()
    {
        var clashes = new List<(int, int)> { (0, 1), (1, 2) };

        ArchiveGrouper.PickCompatible([1, 0, 2], clashes).Should().Equal(1);
        ArchiveGrouper.PickCompatible([0, 1, 2], clashes).Should().Equal(0, 2);
    }

    [Fact]
    public void FindClashes_ThenPickCompatible_KeepsBodyVersionsApart_AndTheHead()
    {
        var bodyNew = Zip("body2.zip", "Body v2/archive/pc/mod/body.archive");
        var head = Zip("head.zip", "archive/pc/mod/head.archive");
        var bodyOld = Zip("body1.zip", "archive/pc/mod/body.archive");
        var archives = new List<string> { bodyNew, head, bodyOld };

        var clashes = ArchiveGrouper.FindClashes(archives, new Modular.Core.Archives.ArchiveReaderFactory());

        ArchiveGrouper.PickCompatible([0, 1, 2], clashes).Should().Equal(0, 1);
    }

    private string Zip(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write("x");
        }

        return path;
    }

    private string Write(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FilesBackend(string id) : IModBackend
    {
        public List<BackendModFile> Files { get; } = [];
        public FileFilter? RequestedFilter { get; private set; }
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }

        public string Id => id;
        public string DisplayName => id;
        public BackendCapabilities Capabilities => BackendCapabilities.None;

        public IReadOnlyList<string> ValidateConfiguration() => [];

        public Task<List<BackendMod>> GetUserModsAsync(string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult(new List<BackendMod>());

        public Task<List<BackendModFile>> GetModFilesAsync(
            string modId, string? gameDomain = null, FileFilter? filter = null, CancellationToken ct = default)
        {
            Calls++;
            RequestedFilter = filter;
            return Failure != null
                ? Task.FromException<List<BackendModFile>>(Failure)
                : Task.FromResult(Files.ToList());
        }

        public Task<string?> ResolveDownloadUrlAsync(
            string modId, string fileId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<BackendMod?> GetModInfoAsync(string modId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<BackendMod?>(null);

        public Task DownloadModsAsync(
            string outputDirectory,
            string? gameDomain = null,
            DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
