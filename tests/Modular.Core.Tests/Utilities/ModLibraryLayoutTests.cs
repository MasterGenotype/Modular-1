using FluentAssertions;
using Modular.Core.Utilities;
using Xunit;

namespace Modular.Core.Tests.Utilities;

/// <summary>
/// The mods directory layout: <c>&lt;root&gt;/&lt;backend&gt;/&lt;game&gt;/&lt;mod&gt;</c>.
/// </summary>
public sealed class ModLibraryLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modular-layout").FullName;

    [Theory]
    [InlineData("nexusmods", "baldursgate3", "baldursgate3")]
    [InlineData("nexusmods", "cyberpunk2077", "cyberpunk2077")]
    [InlineData("gamebanana", "Baldur's Gate 3", "baldursgate3")]
    [InlineData("gamebanana", "The Legend of Zelda: Tears of the Kingdom", "thelegendofzeldatearsofthekingdom")]
    [InlineData("modio", "Baldur's Gate 3", "baldursgate3")]
    [InlineData("modio", "6715", "6715")]
    [InlineData("modio", null, "unknown")]
    [InlineData("gamebanana", "   ", "unknown")]
    public void GameFolderName_IsTheDomainForNexusMods_AndTheNameSlugForOthers(
        string backend, string? game, string expected)
    {
        ModLibraryLayout.GameFolderName(backend, game).Should().Be(expected);
    }

    [Fact]
    public void ModFolder_IsBackendThenGameThenMod()
    {
        ModLibraryLayout.ModFolder("/mods", "gamebanana", "Baldur's Gate 3", "Better: Hugs")
            .Should().Be(Path.Combine("/mods", "gamebanana", "baldursgate3", "Better_ Hugs"));

        ModLibraryLayout.ModFolder("/mods", "nexusmods", "cyberpunk2077", "107")
            .Should().Be(Path.Combine("/mods", "nexusmods", "cyberpunk2077", "107"));
    }

    [Theory]
    [InlineData("/mods/nexusmods/cyberpunk2077/Gameplay/Thing/a.zip", "nexusmods", "cyberpunk2077")]
    [InlineData("/mods/modio/baldursgate3/Hug/a.zip", "modio", "baldursgate3")]
    [InlineData("/mods/gamebanana/x/y", "gamebanana", "x")]
    public void Locate_NamesTheBackendAndGame(string path, string backend, string game)
    {
        ModLibraryLayout.Locate("/mods", path).Should().Be((backend, game));
    }

    [Theory]
    [InlineData("/mods/cyberpunk2077/Thing/a.zip")]  // old layout
    [InlineData("/mods/nexusmods/cyberpunk2077")]    // the game folder itself
    [InlineData("/elsewhere/nexusmods/a/b/c.zip")]   // outside the mods directory
    [InlineData("/mods/.archives/1086940/a.zip")]    // the archive store
    public void Locate_OutsideAGameFolder_IsNull(string path)
    {
        ModLibraryLayout.Locate("/mods", path).Should().BeNull();
    }

    [Fact]
    public void ListGames_ListsEachBackendsGameFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nexusmods", "cyberpunk2077", "Gameplay"));
        Directory.CreateDirectory(Path.Combine(_root, "modio", "baldursgate3", "Hug"));
        Directory.CreateDirectory(Path.Combine(_root, "gamebanana", ".hidden"));
        Directory.CreateDirectory(Path.Combine(_root, ".archives", "1086940"));
        Directory.CreateDirectory(Path.Combine(_root, "baldursgate3"));

        ModLibraryLayout.ListGames(_root).Select(g => (g.BackendId, g.Game)).Should().Equal(
            ("nexusmods", "cyberpunk2077"),
            ("modio", "baldursgate3"));
    }

    [Fact]
    public void ListGames_LeavesOutOldFlatModFolders_ButNotNexusGamesWithLooseArchives()
    {
        // gamebanana/<mod>/ holding the archive is the layout from before game folders.
        WriteFile("gamebanana", "Better Link House", "better_link_house.zip");
        Directory.CreateDirectory(Path.Combine(_root, "gamebanana", "thelegendofzeldatearsofthekingdom", "Gate Pass"));

        // A NexusMods game folder may hold loose archives, and is still a game.
        WriteFile("nexusmods", "baldursgate3", "PRESET COLLECTION-11456-1-4-0-0-1725753424.zip");

        ModLibraryLayout.ListGames(_root).Select(g => (g.BackendId, g.Game)).Should().Equal(
            ("nexusmods", "baldursgate3"),
            ("gamebanana", "thelegendofzeldatearsofthekingdom"));
    }

    private void WriteFile(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
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
