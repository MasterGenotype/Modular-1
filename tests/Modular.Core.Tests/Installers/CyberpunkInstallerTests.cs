using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Installers;
using Modular.Core.Installers.Cyberpunk;
using Modular.Sdk;
using Modular.Sdk.Archives;
using Modular.Sdk.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// Where the Cyberpunk 2077 installer puts each kind of mod, packed both with the game's
/// folder layout and the looser ways authors ship them.
/// </summary>
public sealed class CyberpunkInstallerTests : IDisposable
{
    private const string Cet = "bin/x64/plugins/cyber_engine_tweaks/mods/";

    private readonly List<string> _tempDirs = new();

    // ── Packed with the game's folder layout ────────────────────────────────

    [Theory]
    [InlineData("archive/pc/mod/Foo.archive", "archive/pc/mod/Foo.archive", CyberpunkInstallType.LegacyArchive)]
    [InlineData("archive/pc/mod/Foo.xl", "archive/pc/mod/Foo.xl", CyberpunkInstallType.LegacyArchive)]
    [InlineData("bin/x64/plugins/cyber_engine_tweaks/mods/Foo/init.lua", Cet + "Foo/init.lua", CyberpunkInstallType.CetMod)]
    [InlineData("r6/scripts/Foo/Foo.reds", "r6/scripts/Foo/Foo.reds", CyberpunkInstallType.RedscriptMod)]
    [InlineData("red4ext/plugins/Foo/Foo.dll", "red4ext/plugins/Foo/Foo.dll", CyberpunkInstallType.Red4ExtPlugin)]
    [InlineData("r6/tweaks/Foo/Foo.yaml", "r6/tweaks/Foo/Foo.yaml", CyberpunkInstallType.TweakMod)]
    [InlineData("r6/tweaks/Foo.tweak", "r6/tweaks/Foo.tweak", CyberpunkInstallType.TweakMod)]
    [InlineData("engine/config/platform/pc/Foo.ini", "engine/config/platform/pc/Foo.ini", CyberpunkInstallType.IniTweak)]
    public void GameLayout_DeploysAsPacked(string entry, string expected, CyberpunkInstallType type)
    {
        var layout = Analyze([entry], isCyberpunk: false);

        layout.Types.Should().Be(type);
        layout.FileRoutes[entry].Destination.Should().Be(expected);
    }

    [Fact]
    public void GameLayout_RedMod_DeploysAsPacked()
    {
        var layout = Analyze(["mods/Foo/info.json", "mods/Foo/archives/Foo.archive"], isCyberpunk: false);

        layout.Types.Should().Be(CyberpunkInstallType.RedMod);
        Destinations(layout).Should().Equal("mods/Foo/info.json", "mods/Foo/archives/Foo.archive");
    }

    [Fact]
    public void GameLayout_InsideAWrapperFolder_IsUnwrapped()
    {
        var layout = Analyze(["Foo v1.2/archive/pc/mod/Foo.archive", "Foo v1.2/r6/scripts/Foo.reds"], isCyberpunk: false);

        Destinations(layout).Should().Equal("archive/pc/mod/Foo.archive", "r6/scripts/Foo.reds");
    }

    // ── Packed loosely, installed into Cyberpunk ────────────────────────────

    [Fact]
    public void LooseArchives_GoDirectlyIntoArchivePcMod()
    {
        var layout = Analyze(["Foo/Foo.archive", "Foo/Foo.archive.xl", "Foo/Bar.xl"]);

        layout.Types.Should().Be(CyberpunkInstallType.LegacyArchive);
        Destinations(layout).Should().Equal(
            "archive/pc/mod/Foo.archive", "archive/pc/mod/Foo.archive.xl", "archive/pc/mod/Bar.xl");
    }

    [Fact]
    public void LooseRedMod_InItsOwnFolder_GoesUnderMods()
    {
        var layout = Analyze(["readme.txt", "Foo/info.json", "Foo/archives/Foo.archive", "Foo/customSounds/a.wav"]);

        layout.Types.Should().Be(CyberpunkInstallType.RedMod);
        Destinations(layout).Skip(1).Should().Equal(
            "mods/Foo/info.json", "mods/Foo/archives/Foo.archive", "mods/Foo/customSounds/a.wav");
    }

    [Fact]
    public void LooseRedMod_AtTheArchiveRoot_IsNamedAfterTheDownload()
    {
        var layout = Analyze(["info.json", "archives/Foo.archive"], modName: "Foo");

        Destinations(layout).Should().Equal("mods/Foo/info.json", "mods/Foo/archives/Foo.archive");
    }

    [Fact]
    public void LooseCetMod_KeepsItsFolderUnderCetMods()
    {
        var layout = Analyze(["MyCet/init.lua", "MyCet/modules/ui.lua", "MyCet/data/config.json"]);

        layout.Types.Should().Be(CyberpunkInstallType.CetMod);
        Destinations(layout).Should().Equal(
            Cet + "MyCet/init.lua", Cet + "MyCet/modules/ui.lua", Cet + "MyCet/data/config.json");
    }

    [Fact]
    public void LooseCetMod_NextToAReadme_KeepsItsFolderUnderCetMods()
    {
        var layout = Analyze(["readme.txt", "MyCet/init.lua"]);

        layout.FileRoutes["MyCet/init.lua"].Destination.Should().Be(Cet + "MyCet/init.lua");
    }

    [Fact]
    public void LooseRedscript_AtTheRoot_GetsAFolderInR6Scripts()
    {
        var layout = Analyze(["Foo.reds", "Sub/Bar.reds"], modName: "Foo Mod");

        layout.Types.Should().Be(CyberpunkInstallType.RedscriptMod);
        Destinations(layout).Should().Equal("r6/scripts/Foo Mod/Foo.reds", "r6/scripts/Sub/Bar.reds");
    }

    [Fact]
    public void LooseRed4ExtPlugin_GoesIntoAPluginFolder()
    {
        var wrapped = Analyze(["Foo/Foo.dll", "Foo/Foo.toml"]);
        var bare = Analyze(["Foo.dll"], modName: "Some Download");

        wrapped.Types.Should().Be(CyberpunkInstallType.Red4ExtPlugin);
        Destinations(wrapped).Should().Equal("red4ext/plugins/Foo/Foo.dll", "red4ext/plugins/Foo/Foo.toml");
        Destinations(bare).Should().Equal("red4ext/plugins/Foo/Foo.dll");
    }

    [Fact]
    public void AWindowsProgram_IsRefusedRatherThanInstalledAsAPlugin()
    {
        // BetterMinimap's "Installer" download: a setup program with a bundled runtime.
        var layout = Analyze(
            ["BetterMinimap-Installer.exe", "dotnet-runtime.exe", "lib/api-ms-win-core-file-l1-1-0.dll", "lib/tool.dll"]);

        layout.Rejection.Should().Contain("BetterMinimap-Installer.exe").And.Contain("not a mod");
        Destinations(layout).Should().NotContain(d => d.StartsWith("red4ext/"));
    }

    [Fact]
    public void RuntimeDlls_AreNotRed4ExtPlugins()
    {
        var layout = Analyze(["Foo/Foo.dll", "Runtime/vcruntime140.dll", "Runtime/api-ms-win-crt-heap-l1-1-0.dll"]);

        Destinations(layout).Should().Contain("red4ext/plugins/Foo/Foo.dll");
        Destinations(layout).Should().NotContain(d => d.StartsWith("red4ext/plugins/Runtime"));
    }

    [Fact]
    public async Task Analyze_AWindowsProgram_FailsWithTheReason()
    {
        var archive = Zip("Tool-1234-1-0-1700000000.zip", ("Tool.exe", "x"), ("lib/helper.dll", "y"));
        var installer = new CyberpunkModInstaller();

        var detection = await installer.DetectAsync(archive, GameIds.Cyberpunk2077);
        var analyze = () => installer.AnalyzeAsync(
            archive, new InstallContext { GameDirectory = NewDir(), GameId = GameIds.Cyberpunk2077 });

        detection.CanHandle.Should().BeTrue("claiming it keeps the loose-file installer from copying it in");
        await analyze.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Windows program (Tool.exe)*");
    }

    [Fact]
    public void LooseTweaks_GoIntoR6Tweaks()
    {
        var layout = Analyze(["Foo/a.yaml", "Foo/b.tweak"]);

        layout.Types.Should().Be(CyberpunkInstallType.TweakMod);
        Destinations(layout).Should().Equal("r6/tweaks/Foo/a.yaml", "r6/tweaks/Foo/b.tweak");
    }

    [Fact]
    public void LooseIni_GoesIntoTheEngineConfig()
    {
        var layout = Analyze(["Foo/user.ini"]);

        layout.Types.Should().Be(CyberpunkInstallType.IniTweak);
        Destinations(layout).Should().Equal("engine/config/platform/pc/user.ini");
    }

    [Fact]
    public void MultiTypeMod_RoutesEachPartToItsFolder()
    {
        var layout = Analyze(
            ["Foo/Foo.archive", "Foo/Foo.archive.xl", "Foo/Foo.reds", "Foo/Foo.yaml"]);

        layout.IsMultiPath.Should().BeTrue();
        Destinations(layout).Should().Equal(
            "archive/pc/mod/Foo.archive", "archive/pc/mod/Foo.archive.xl",
            "r6/scripts/Foo/Foo.reds", "r6/tweaks/Foo/Foo.yaml");
    }

    // ── Unknown game ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Foo/Foo.archive")]
    [InlineData("Foo/init.lua")]
    [InlineData("Foo/Foo.dll")]
    public void LooseLayout_ForAnUnknownGame_IsNotClaimed(string entry)
    {
        Analyze([entry], isCyberpunk: false).Types.Should().Be(CyberpunkInstallType.Unknown);
    }

    [Fact]
    public void LooseArchive_NextToACyberpunkPath_IsClaimedForAnUnknownGame()
    {
        var layout = Analyze(["Foo.archive", "r6/scripts/Foo.reds"], isCyberpunk: false);

        layout.FileRoutes["Foo.archive"].Destination.Should().Be("archive/pc/mod/Foo.archive");
    }

    // ── Paths already in a game folder stay put ─────────────────────────────

    [Fact]
    public void FilesInAGameFolder_AreNotMovedByExtension()
    {
        var layout = Analyze(
            ["r6/scripts/Foo.reds", "bin/x64/global.ini", "r6/config/inputUserMappings.xml", "red4ext/RED4ext.dll"],
            isCyberpunk: false);

        Destinations(layout).Should().Equal(
            "r6/scripts/Foo.reds", "bin/x64/global.ini", "r6/config/inputUserMappings.xml", "red4ext/RED4ext.dll");
    }

    [Fact]
    public void CetItself_LoadsThroughVersionDll()
    {
        var layout = Analyze(
            ["bin/x64/version.dll", "bin/x64/plugins/cyber_engine_tweaks.asi"], isCyberpunk: false);

        layout.Types.Should().HaveFlag(CyberpunkInstallType.FrameworkRoot);
        layout.FrameworkHints.Should().Contain(h => h.DestinationRelative == "bin/x64/version.dll" && h.IsCritical);
    }

    // ── Installer selection ────────────────────────────────────────────────

    [Fact]
    public async Task SelectInstaller_ForCyberpunk_PicksTheCyberpunkInstallerForALooseArchive()
    {
        var archive = Zip("Foo-1234-1-0-1700000000.zip", ("Foo/Foo.archive", "x"));
        var manager = new InstallerManager();

        var forCyberpunk = await manager.SelectInstallerAsync(archive, GameIds.Cyberpunk2077);
        var forNoGame = await manager.SelectInstallerAsync(archive);

        forCyberpunk!.Installer.InstallerId.Should().Be("cyberpunk2077");
        forNoGame!.Installer.InstallerId.Should().NotBe("cyberpunk2077");
    }

    [Fact]
    public async Task Analyze_ARootCetModFromNexus_NamesTheFolderAfterTheMod()
    {
        var archive = Zip("Better Lights-1234-1-0-1700000000.zip", ("init.lua", "x"), ("db.lua", "y"));

        var plan = await new CyberpunkModInstaller().AnalyzeAsync(
            archive, new InstallContext { GameDirectory = NewDir(), GameId = GameIds.Cyberpunk2077 });

        plan.Operations.Select(o => o.DestinationPath).Should().Equal(
            Cet + "Better Lights/init.lua", Cet + "Better Lights/db.lua");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CyberpunkInstallLayout Analyze(
        string[] paths, string? modName = null, bool isCyberpunk = true) =>
        CyberpunkArchiveAnalyzer.Analyze(
            paths.Select(p => new ArchiveEntry { FullName = p, Name = Path.GetFileName(p) }).ToList(),
            modName,
            isCyberpunk);

    private static List<string> Destinations(CyberpunkInstallLayout layout) =>
        layout.FileRoutes.Values.Select(r => r.Destination).ToList();

    private string Zip(string fileName, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(NewDir(), fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
            writer.Write(content);
        }
        return path;
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-cp77-" + Guid.NewGuid().ToString("N"));
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
