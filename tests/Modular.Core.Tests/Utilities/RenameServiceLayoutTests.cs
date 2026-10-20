using FluentAssertions;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.RateLimiting;
using Modular.Core.Services;
using Xunit;

namespace Modular.Core.Tests.Utilities;

/// <summary>
/// Renaming a NexusMods game folder into <c>nexusmods/&lt;game&gt;/&lt;mod&gt;/</c>: mod-id folders
/// named after the mod, no category level, and loose archives gathered into their mod.
/// </summary>
/// <remarks>
/// Every mod and the game's categories are in the metadata cache, so nothing is fetched.
/// </remarks>
public sealed class RenameServiceLayoutTests : IDisposable
{
    private const string Game = "cyberpunk2077";

    private readonly string _root = Directory.CreateTempSubdirectory("modular-rename").FullName;
    private readonly string _gameDir;
    private readonly RenameService _service;
    private readonly List<(string From, string To)> _moves = [];

    public RenameServiceLayoutTests()
    {
        _gameDir = Path.Combine(_root, "nexusmods", Game);
        Directory.CreateDirectory(_gameDir);

        var cache = new ModMetadataCache(Path.Combine(_root, ".cache.json"));
        cache.SetGameCategories(Game, new Dictionary<int, string> { [1] = "Visuals and Graphics", [2] = "Gameplay" });
        cache.SetModMetadata(Game, new ModMetadata { ModId = 107, Name = "Night City UI", CategoryId = 1 });
        cache.SetModMetadata(Game, new ModMetadata { ModId = 200, Name = "Better: Driving", CategoryId = 2 });
        cache.SetModMetadata(Game, new ModMetadata { ModId = 11456, Name = "Preset Collection", CategoryId = 2 });

        _service = new RenameService(
            new AppSettings { ModsDirectory = _root }, new NexusRateLimiter(), cache);
    }

    [Fact]
    public async Task ModIdFolders_AreRenamedStraightIntoTheGameFolder()
    {
        WriteFile("107", "ui.zip");

        await Reorganize();

        File.Exists(Path.Combine(_gameDir, "Night City UI", "ui.zip")).Should().BeTrue();
        Directory.Exists(Path.Combine(_gameDir, "Visuals and Graphics")).Should().BeFalse();
        _moves.Should().Contain((Path.Combine(_gameDir, "107"), Path.Combine(_gameDir, "Night City UI")));
    }

    [Fact]
    public async Task ModsInCategoryFolders_ComeBackUp_AndTheCategoryFoldersGo()
    {
        WriteFile("Visuals and Graphics", "Night City UI", "ui.zip");
        WriteFile("Gameplay", "Better_ Driving", "drive.zip");

        // Not in the cache, so only its category folder's name says it is one.
        WriteFile("Gameplay", "Unknown Mod", "unknown.zip");

        await Reorganize();

        File.Exists(Path.Combine(_gameDir, "Night City UI", "ui.zip")).Should().BeTrue();
        File.Exists(Path.Combine(_gameDir, "Better_ Driving", "drive.zip")).Should().BeTrue();
        File.Exists(Path.Combine(_gameDir, "Unknown Mod", "unknown.zip")).Should().BeTrue();
        Directory.Exists(Path.Combine(_gameDir, "Visuals and Graphics")).Should().BeFalse();
        Directory.Exists(Path.Combine(_gameDir, "Gameplay")).Should().BeFalse();
    }

    [Fact]
    public async Task LooseArchives_GoIntoTheirModsFolder_WithTheirDownloadRecord()
    {
        const string archive = "06 - PRESET COLLECTION-11456-1-2-0-0-1723262879.zip";
        WriteFile(archive);
        WriteFile(archive + ".modular.json");
        WriteFile("not-a-nexus-download.zip");

        await Reorganize();

        File.Exists(Path.Combine(_gameDir, "Preset Collection", archive)).Should().BeTrue();
        File.Exists(Path.Combine(_gameDir, "Preset Collection", archive + ".modular.json")).Should().BeTrue();
        File.Exists(Path.Combine(_gameDir, "not-a-nexus-download.zip")).Should().BeTrue();
        _moves.Should().Contain((Path.Combine(_gameDir, archive), Path.Combine(_gameDir, "11456", archive)));
    }

    [Fact]
    public async Task AModFolderNamedLikeACategory_IsNotTakenApart()
    {
        // A folder holding archives is a mod, whatever it is called.
        WriteFile("Gameplay", "gameplay.zip");

        await Reorganize();

        File.Exists(Path.Combine(_gameDir, "Gameplay", "gameplay.zip")).Should().BeTrue();
    }

    private Task<int> Reorganize() =>
        _service.ReorganizeAndRenameModsAsync(_gameDir, organizeByCategory: false, folderMoved: (from, to, _) =>
        {
            _moves.Add((from, to));
            return Task.CompletedTask;
        });

    private void WriteFile(params string[] parts)
    {
        var path = Path.Combine([_gameDir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
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
}
