using FluentAssertions;
using Modular.Core.Dependencies;
using Modular.Core.Installers;
using Modular.Core.Modules;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Cyberpunk 2077: working out from the files on disk which frameworks the installed mods
/// need, and which mods are present however they got there.
/// </summary>
public sealed class CyberpunkDependencyTests : IDisposable
{
    private const string CetMods = CyberpunkGameModule.CetModsFolder;

    private readonly List<string> _tempDirs = new();
    private readonly CyberpunkGameModule _module = new();

    // ── what the files need ─────────────────────────────────────────────────

    [Fact]
    public void Red4ExtPlugin_WithoutRed4Ext_IsUnmet()
    {
        var game = NewGameDir();
        WriteGameFile(game, "red4ext/plugins/InputLoader/input_loader.dll");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().ContainSingle().Which.Should().Be(
            new UnmetDependency("InputLoader", "input_loader.dll", "RED4ext", "red4ext"));
    }

    [Theory]
    [InlineData("red4ext/RED4ext.dll")]
    [InlineData("bin/x64/winmm.dll")]
    public void Red4ExtPlugin_WithRed4Ext_IsMet(string marker)
    {
        var game = NewGameDir();
        WriteGameFile(game, "red4ext/plugins/InputLoader/input_loader.dll");
        WriteGameFile(game, marker);

        _module.FindUnmetDependencies(Installation(game)).Should().BeEmpty();
    }

    [Fact]
    public void CetMod_WithoutCet_IsUnmet_AndNamedByAFileOtherThanInitLua()
    {
        // Every CET mod has an init.lua, so that name would claim every CET mod at once.
        var game = NewGameDir();
        WriteGameFile(game, CetMods + "SimpleMenu/init.lua");
        WriteGameFile(game, CetMods + "SimpleMenu/simple_menu.lua");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().ContainSingle().Which.Should().Be(
            new UnmetDependency("SimpleMenu", "simple_menu.lua", "Cyber Engine Tweaks", "cet"));
    }

    [Fact]
    public void CetFolder_WithoutInitLua_IsNotACetMod()
    {
        // CET skips such a folder, so it needs nothing; ValidateStaged warns about it instead.
        var game = NewGameDir();
        WriteGameFile(game, CetMods + "Leftovers/settings.json");

        _module.FindUnmetDependencies(Installation(game)).Should().BeEmpty();
    }

    [Fact]
    public void CetMod_WithCet_IsMet()
    {
        var game = NewGameDir();
        WriteGameFile(game, CetMods + "SimpleMenu/init.lua");
        WriteGameFile(game, "bin/x64/plugins/cyber_engine_tweaks.asi");

        _module.FindUnmetDependencies(Installation(game)).Should().BeEmpty();
    }

    [Fact]
    public void RedscriptSources_WithoutRedscript_AreUnmetOncePerMod()
    {
        var game = NewGameDir();
        WriteGameFile(game, "r6/scripts/BetterNetrunning/a.reds");
        WriteGameFile(game, "r6/scripts/BetterNetrunning/b.reds");
        WriteGameFile(game, "r6/scripts/loose.reds");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().BeEquivalentTo(
        [
            new UnmetDependency("BetterNetrunning", "a.reds", "redscript", "redscript"),
            new UnmetDependency("loose", "loose.reds", "redscript", "redscript")
        ]);
    }

    [Fact]
    public void RedscriptSources_WithRedscript_AreMet()
    {
        var game = NewGameDir();
        WriteGameFile(game, "r6/scripts/BetterNetrunning/a.reds");
        WriteGameFile(game, "engine/tools/scc.exe");

        _module.FindUnmetDependencies(Installation(game)).Should().BeEmpty();
    }

    [Fact]
    public void ArchiveXlFile_WithoutArchiveXl_IsUnmet()
    {
        var game = NewGameDir();
        WriteGameFile(game, "archive/pc/mod/CoolJacket.archive");
        WriteGameFile(game, "archive/pc/mod/CoolJacket.archive.xl");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().ContainSingle().Which.Should().Be(
            new UnmetDependency("CoolJacket", "CoolJacket.archive.xl", "ArchiveXL", "archivexl"));
    }

    [Fact]
    public void ArchiveXl_WithoutRed4Ext_IsItselfUnmet()
    {
        // ArchiveXL is a RED4ext plugin, so installing it alone only moves the gap down a level.
        var game = NewGameDir();
        WriteGameFile(game, "archive/pc/mod/CoolJacket.archive.xl");
        WriteGameFile(game, "red4ext/plugins/ArchiveXL/ArchiveXL.dll");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().ContainSingle().Which.Should().Be(
            new UnmetDependency("ArchiveXL", "ArchiveXL.dll", "RED4ext", "red4ext"));
    }

    [Theory]
    [InlineData("r6/tweaks/BiggerStash/stash.yaml")]
    [InlineData("r6/tweaks/BiggerStash/stash.yml")]
    [InlineData("r6/tweaks/BiggerStash/stash.tweak")]
    public void TweakFiles_WithoutTweakXl_AreUnmet(string path)
    {
        var game = NewGameDir();
        WriteGameFile(game, path);
        WriteGameFile(game, "red4ext/RED4ext.dll");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().ContainSingle().Which.Should().Be(
            new UnmetDependency("BiggerStash", Path.GetFileName(path), "TweakXL", "tweakxl"));
    }

    [Fact]
    public void AFullFrameworkStack_LeavesNothingUnmet()
    {
        var game = NewGameDir();
        WriteGameFile(game, "bin/x64/winmm.dll");
        WriteGameFile(game, "red4ext/RED4ext.dll");
        WriteGameFile(game, "red4ext/plugins/ArchiveXL/ArchiveXL.dll");
        WriteGameFile(game, "red4ext/plugins/TweakXL/TweakXL.dll");
        WriteGameFile(game, "bin/x64/plugins/cyber_engine_tweaks.asi");
        WriteGameFile(game, "engine/tools/scc.exe");
        WriteGameFile(game, "archive/pc/mod/CoolJacket.archive.xl");
        WriteGameFile(game, "r6/tweaks/BiggerStash/stash.yaml");
        WriteGameFile(game, "r6/scripts/BetterNetrunning/a.reds");
        WriteGameFile(game, CetMods + "SimpleMenu/init.lua");

        _module.FindUnmetDependencies(Installation(game)).Should().BeEmpty();
    }

    [Fact]
    public void FolderCase_DoesNotMatter()
    {
        // A mod packed as R6/ or Red4ext/ still loads under Proton, so it still counts.
        var game = NewGameDir();
        WriteGameFile(game, "R6/Scripts/BetterNetrunning/a.reds");
        WriteGameFile(game, "Engine/Tools/SCC.exe");
        WriteGameFile(game, "Red4ext/Plugins/InputLoader/input_loader.dll");

        var unmet = _module.FindUnmetDependencies(Installation(game));

        unmet.Should().ContainSingle().Which.Identifier.Should().Be("red4ext");
    }

    [Fact]
    public void AnEmptyOrMissingGameFolder_HasNothingUnmet()
    {
        _module.FindUnmetDependencies(Installation(NewGameDir())).Should().BeEmpty();
        _module.FindUnmetDependencies(Installation(Path.Combine(NewGameDir(), "gone")))
            .Should().BeEmpty();
    }

    // ── what is present ─────────────────────────────────────────────────────

    [Fact]
    public void PresentNames_IncludeFrameworksByTheirNexusNames_AndModFolders()
    {
        var game = NewGameDir();
        WriteGameFile(game, "red4ext/RED4ext.dll");
        WriteGameFile(game, "red4ext/plugins/Codeware/Codeware.dll");
        WriteGameFile(game, "red4ext/plugins/input_loader/input_loader.dll");
        WriteGameFile(game, "bin/x64/plugins/cyber_engine_tweaks.asi");
        WriteGameFile(game, CetMods + "SimpleMenu/init.lua");
        WriteGameFile(game, "r6/scripts/BetterNetrunning/a.reds");
        WriteGameFile(game, "r6/tweaks/BiggerStash/stash.yaml");
        WriteGameFile(game, "mods/CoolRedMod/info.json");
        WriteGameFile(game, "archive/pc/mod/CoolJacket.archive");

        var names = _module.ListPresentModNames(Installation(game));

        names.Should().Contain(
        [
            "RED4ext", "Codeware", "Cyber Engine Tweaks", "input_loader", "SimpleMenu",
            "BetterNetrunning", "BiggerStash", "CoolRedMod", "CoolJacket"
        ]);
        names.Should().NotContain(["redscript", "ArchiveXL", "TweakXL"]);
    }

    [Fact]
    public void PresentNames_ALoneFile_IsNamedWithoutItsExtension()
    {
        var game = NewGameDir();
        WriteGameFile(game, "r6/scripts/loose.reds");

        _module.ListPresentModNames(Installation(game)).Should().Equal("loose");
    }

    // ── through the dependency check ────────────────────────────────────────

    [Fact]
    public async Task DependencyCheck_ReportsAFrameworkTheInstalledModNeeds()
    {
        var game = NewGameDir();
        WriteGameFile(game, CetMods + "SimpleMenu/init.lua");
        WriteGameFile(game, CetMods + "SimpleMenu/simple_menu.lua");

        var simpleMenu = Installed(game, "Simple Menu", CetMods + "SimpleMenu/init.lua", CetMods + "SimpleMenu/simple_menu.lua");

        var report = await Service(game).FindAsync([simpleMenu], [simpleMenu]);

        var item = report.Items.Should().ContainSingle().Subject;
        item.Name.Should().Be("Cyber Engine Tweaks");
        item.Kind.Should().Be(MissingDependencyKind.Unidentified);
        item.GameDomain.Should().Be("cyberpunk2077");
        item.RequiredBy.Should().Equal("SimpleMenu");
    }

    [Fact]
    public async Task DependencyCheck_OfOneMod_LeavesOutWhatOtherModsNeed()
    {
        var game = NewGameDir();
        WriteGameFile(game, "r6/scripts/Unmanaged/x.reds");
        WriteGameFile(game, "archive/pc/mod/Mine.archive");

        var mine = Installed(game, "Mine", "archive/pc/mod/Mine.archive");

        var single = await Service(game).FindAsync([mine], [mine]);
        var everything = await Service(game).FindAsync([mine], [mine], includeUnmanaged: true);

        single.Items.Should().BeEmpty();
        everything.Items.Should().ContainSingle().Which.Name.Should().Be("redscript");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private MissingDependencyService Service(string game)
    {
        var installation = Installation(game);

        return new MissingDependencyService(
            [],
            (_, _) => Task.FromResult<(GameInstallation, IGameModule)?>((installation, _module)));
    }

    private static ChangesetRecord Installed(string game, string name, params string[] deployedFiles) => new()
    {
        ChangesetId = name,
        ModId = name,
        ArchivePath = name + ".zip",
        TargetDirectory = game,
        GameKey = "1091500",
        ManagedBy = ChangesetOwnership.Deployment,
        OperationsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            deployedFiles = deployedFiles.Select(f => Path.Combine(game, f)).ToArray()
        })
    };

    private static GameInstallation Installation(string gameDir) => new()
    {
        GameId = "cyberpunk2077",
        SteamAppId = 1091500,
        InstallPath = gameDir
    };

    private static void WriteGameFile(string gameDir, string relativePath)
    {
        var full = Path.Combine(gameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    private string NewGameDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "modular-cp77deps-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
