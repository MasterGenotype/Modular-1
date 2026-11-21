using FluentAssertions;
using Modular.Core.ModLists;
using Xunit;

namespace Modular.Core.Tests;

public class ModListTests
{
    private static readonly RemoteModFile[] Files =
    [
        new("94720", "ACU - Character Customization-3850-2--3-2-1734138551.rar", 68608, "2.3.2", DateTimeOffset.FromUnixTimeSeconds(1734138551)),
        new("179199", "ACU - Character Customization 3850 4.1.8 2026-11-15T18-43Z Co8doeH8P.zip", 408576, "4.1.8", DateTimeOffset.Parse("2026-11-15T18:43:57Z")),
    ];

    [Fact]
    public void MatchFile_ByExactName() =>
        ModListExporter.MatchFile(Files, "ACU - Character Customization 3850 4.1.8 2026-11-15T18-43Z Co8doeH8P.zip", null)!.FileId.Should().Be("179199");

    [Fact]
    public void MatchFile_ByUnixUploadTimeInAnOldStyleName() =>
        ModListExporter.MatchFile(Files, "renamed-3850-2-3-2-1734138551.zip", null)!.FileId.Should().Be("94720");

    [Fact]
    public void MatchFile_ByVersionAndTimeInANewStyleName() =>
        ModListExporter.MatchFile(Files, "Other title 3850 4.1.8 2026-11-15T18-43Z zzz.zip", null)!.FileId.Should().Be("179199");

    [Fact]
    public void MatchFile_NoneWhenTheInstalledFileWasRemoved() =>
        ModListExporter.MatchFile(Files, "ACU - Character Customization-3850-3-2-1-1777741508.zip", 294681).Should().BeNull();

    [Fact]
    public void RoundTrip_KeepsEntries()
    {
        var list = new ModList
        {
            Game = new ModListGame("1091500", "Cyberpunk 2077", 1091500, "cyberpunk2077", "26161058"),
            Mods = [new ModListEntry { Order = 1, ModId = "3620", Name = "JB", Source = new ModListSource("nexusmods", "cyberpunk2077", "3620", "33775") }],
        };
        var path = Path.GetTempFileName();
        File.WriteAllText(path, list.ToJson());
        var back = ModList.Load(path);
        File.Delete(path);
        back.Mods.Single().Source!.FileId.Should().Be("33775");
        back.Game.NexusDomain.Should().Be("cyberpunk2077");
    }
}
