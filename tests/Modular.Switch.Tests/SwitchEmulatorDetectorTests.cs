using FluentAssertions;
using Modular.Switch.Scanner;
using Xunit;

namespace Modular.Switch.Tests;

public sealed class SwitchEmulatorDetectorTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("modular-emu-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private void Write(string relative, string content = "")
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Yuzu_GamesComeFromItsGameListCacheAndModFolders()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".local/share/yuzu/load/0100F2C0115B6000/TKMM"));
        Directory.CreateDirectory(Path.Combine(_home, ".local/share/yuzu/load/0100ABCDEF012000"));
        Write(".cache/yuzu/game_list/0100F2C0115B6000.pv.txt", "Update (1.2.1)\nSDMC (LayeredExeFS, LayeredFS)\n");
        Write(".config/yuzu/qt-config.ini", "Paths\\gamedirs\\1\\path=SDMC\nPaths\\gamedirs\\4\\path=/games/switch\n");

        var emulator = SwitchEmulatorDetector.Detect(_home).Should().ContainSingle().Subject;
        emulator.Name.Should().Be("yuzu");
        emulator.GameDirs().Should().Equal("/games/switch");

        var titles = SwitchEmulatorDetector.Titles(emulator);
        var totk = titles.Single(t => t.TitleId == "0100F2C0115B6000");
        totk.Name.Should().Be("The Legend of Zelda: Tears of the Kingdom");
        totk.Version.Should().Be("1.2.1");
        totk.AddOns.Should().Be("SDMC (LayeredExeFS, LayeredFS)");
        totk.ModFolderEntries.Should().Be(1);
        titles.Single(t => t.TitleId == "0100ABCDEF012000").Name.Should().Be("Switch game 0100ABCDEF012000", "unknown titles show their id");
    }

    [Fact]
    public void Ryujinx_NamesComeFromItsGameMetadata()
    {
        Write(".config/Ryujinx/games/0100000000010000/gui/metadata.json", """{"title":"Super Mario Odyssey"}""");
        Directory.CreateDirectory(Path.Combine(_home, ".config/Ryujinx/mods/contents/0100000000010000/SomeMod"));

        var emulator = SwitchEmulatorDetector.Detect(_home).Should().ContainSingle().Subject;
        emulator.Family.Should().Be(SwitchEmulatorFamily.Ryujinx);
        var title = SwitchEmulatorDetector.Titles(emulator).Should().ContainSingle().Subject;
        title.Name.Should().Be("Super Mario Odyssey");
        title.ModFolderEntries.Should().Be(1);
    }

    [Fact]
    public void NoEmulator_NothingFound() => SwitchEmulatorDetector.Detect(_home).Should().BeEmpty();
}
