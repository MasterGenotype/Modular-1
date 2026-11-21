using FluentAssertions;
using Modular.Core.Steam;
using Xunit;

namespace Modular.Core.Tests.Steam;

public sealed class SteamShortcutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("modular-steam-").FullName;
    private const string Account = "864086787";

    private string ConfigDir => Path.Combine(_root, "userdata", Account, "config");
    private string ShortcutsPath => Path.Combine(ConfigDir, "shortcuts.vdf");
    private string GlobalConfig => Path.Combine(_root, "config", "config.vdf");

    // Shaped like Steam's own config.vdf, including a value with raw newlines that must survive.
    private const string ConfigVdf = "\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n"
        + "\t\t\t\t\"SDL_GamepadBind\"\t\t\"pad one,a:b0,\npad two,a:b1,\"\n"
        + "\t\t\t\t\"CompatToolMapping\"\n\t\t\t\t{\n"
        + "\t\t\t\t\t\"0\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"name\"\t\t\"proton-EM\"\n\t\t\t\t\t\t\"config\"\t\t\"\"\n\t\t\t\t\t\t\"priority\"\t\t\"75\"\n\t\t\t\t\t}\n"
        + "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    public SteamShortcutTests()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalConfig)!);
        File.WriteAllText(GlobalConfig, ConfigVdf);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SteamShortcutService Service(bool steamRunning = false) =>
        new(new SteamUserConfig(_root, Account), () => steamRunning);

    [Fact]
    public void BinaryVdf_RoundTripsTypesExactly()
    {
        var root = new BinaryVdfNode("shortcuts");
        var entry = new BinaryVdfNode("0");
        entry["appid"] = unchecked((int)3649725436u);
        entry["AppName"] = "Enshrouded";
        entry["IsHidden"] = 0;
        entry["tags"] = new BinaryVdfNode("tags");
        root["0"] = entry;

        var bytes = root.ToBytes();
        var back = BinaryVdfNode.Read(bytes);

        back.ToBytes().Should().Equal(bytes);
        back.GetNode("0")!.GetInt("appid").Should().Be(unchecked((int)3649725436u), "appid must stay an int32");
        back.GetNode("0")!.GetString("AppName").Should().Be("Enshrouded");
    }

    [Fact]
    public void Apply_CreatesShortcut_Proton_AndArt_ThenUpdatesInPlace()
    {
        var service = Service();
        var request = new SteamShortcutRequest("Bastion (pinned)", "/games/Bastion/Bastion.exe", "/games/Bastion",
            "SteamAppId=107100 %command%", "proton_experimental", SourceSteamAppId: 107100);
        var art = new Dictionary<SteamArtKind, SteamArtChoice>
        {
            [SteamArtKind.Grid] = new([1, 2, 3], ".jpg"),
            [SteamArtKind.Icon] = new([4, 5], "png"),
        };

        var created = service.Apply(request, art);

        created.Created.Should().BeTrue();
        var id = created.Shortcut.AppId;
        (id & 0x80000000).Should().NotBe(0u, "shortcut ids have the top bit set");
        var file = SteamShortcutsFile.Load(ShortcutsPath);
        file.Shortcuts.Should().ContainSingle(s => s.AppName == "Bastion (pinned)" && s.Exe == "/games/Bastion/Bastion.exe");
        File.ReadAllBytes(Path.Combine(ConfigDir, "grid", $"{id}p.jpg")).Should().Equal(1, 2, 3);
        file.Shortcuts.Single().Icon.Should().EndWith($"{id}_icon.png");
        new SteamUserConfig(_root, Account).GetCompatTool(id, fallBackToDefault: false).Should().Be("proton_experimental");
        File.ReadAllText(GlobalConfig).Should().Contain("\"pad one,a:b0,\npad two,a:b1,\"", "other values keep their raw newlines");

        // Same exe again: an update, same id; new art replaces the old format.
        var updated = service.Apply(request with { AppName = "Bastion (old build)" },
            new Dictionary<SteamArtKind, SteamArtChoice> { [SteamArtKind.Grid] = new([9], ".png") });

        updated.Created.Should().BeFalse();
        updated.Shortcut.AppId.Should().Be(id);
        SteamShortcutsFile.Load(ShortcutsPath).Shortcuts.Should().ContainSingle().Which.AppName.Should().Be("Bastion (old build)");
        File.Exists(Path.Combine(ConfigDir, "grid", $"{id}p.jpg")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(ConfigDir, "grid", $"{id}p.png")).Should().Equal(9);
    }

    [Fact]
    public void Apply_RefusesWhileSteamRuns()
    {
        FluentActions.Invoking(() => Service(steamRunning: true).Apply(
                new SteamShortcutRequest("X", "/x.exe", "/", "", null), new Dictionary<SteamArtKind, SteamArtChoice>()))
            .Should().Throw<InvalidOperationException>();
        File.Exists(ShortcutsPath).Should().BeFalse();
    }

    [Fact]
    public void TextVdfPatcher_AddThenRemove_GivesBackTheOriginal()
    {
        string[] path = ["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping"];
        var added = TextVdfPatcher.SetBlock(ConfigVdf, path, "2999999999", [("name", "GE-Proton11-7"), ("config", ""), ("priority", "250")]);

        added.Should().Contain("\t\t\t\t\t\"2999999999\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"name\"\t\t\"GE-Proton11-7\"");
        TextVdfPatcher.SetBlock(added, path, "2999999999", null).Should().Be(ConfigVdf);
    }

    [Theory]
    [InlineData("", "SteamAppId=107100 STEAM_COMPAT_DATA_PATH=\"/lib/steamapps/compatdata/107100\" %command%")]
    [InlineData("-windowed", "SteamAppId=107100 STEAM_COMPAT_DATA_PATH=\"/lib/steamapps/compatdata/107100\" %command% -windowed")]
    [InlineData("DXVK_HUD=1 %command% -x", "SteamAppId=107100 STEAM_COMPAT_DATA_PATH=\"/lib/steamapps/compatdata/107100\" DXVK_HUD=1 %command% -x")]
    public void LaunchOptions_ForASteamGame_KeepItsPrefixAndOwnOptions(string own, string expected)
    {
        SteamUserConfig.ShortcutLaunchOptionsForSteamGame(107100, "/lib", own).Should().Be(expected);
    }
}
