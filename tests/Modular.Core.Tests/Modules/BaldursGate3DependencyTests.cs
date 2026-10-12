using System.Text;
using FluentAssertions;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Modules;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Modules;

/// <summary>
/// Baldur's Gate 3: reading a pak's <c>meta.lsx</c> and finding the modules it depends on
/// that nothing in the user <c>Mods</c> folder provides.
/// </summary>
public sealed class BaldursGate3DependencyTests : IDisposable
{
    private const string LibraryUuid = "396c5966-09b0-40a1-af3f-93a5e9ce71c0";
    private const string FrameworkUuid = "67fbbd53-7c7d-4cfa-9409-6d737b4d92a9";
    private const string GustavDevUuid = "28ac9ce2-2aba-8cda-b3b5-6e922f71b6b8";

    private readonly List<string> _tempDirs = new();

    [Theory]
    [InlineData(Bg3TestPak.Stored)]
    [InlineData(Bg3TestPak.Zlib)]
    [InlineData(Bg3TestPak.Lz4)]
    public void LspkReader_ReadFile_DecodesEveryMethodModsUse(int compression)
    {
        var pak = Path.Combine(NewTempDir(), "a.pak");
        var contents = Encoding.UTF8.GetBytes(new string('x', 400) + "meta");

        Bg3TestPak.Write(
            pak,
            ("Public/Thing/other.lsx", [1, 2, 3], Bg3TestPak.Stored),
            ("Mods/Thing/meta.lsx", contents, compression));

        LspkReader.ReadFile(pak, name => name == "Mods/Thing/meta.lsx")
            .Should().Equal(contents);
    }

    [Fact]
    public void LspkReader_ReadFile_NothingMatching_IsNull()
    {
        var pak = Path.Combine(NewTempDir(), "a.pak");
        Bg3TestPak.Write(pak, ("Public/Thing/other.lsx", [1, 2, 3], Bg3TestPak.Stored));

        LspkReader.ReadFile(pak, name => name.EndsWith("meta.lsx")).Should().BeNull();
    }

    [Fact]
    public void PakMeta_ReadsTheModuleAndItsDependencies()
    {
        var pak = Path.Combine(NewTempDir(), "Framework.pak");
        Bg3TestPak.WriteMod(
            pak, FrameworkUuid, "CompatibilityFramework",
            (LibraryUuid, "CommunityLibrary"), (GustavDevUuid, "GustavDev"));

        var meta = BG3PakMeta.Read(pak);

        meta.Should().NotBeNull();
        meta!.Module.Should().Be(
            new BG3ModuleRef(FrameworkUuid, "CompatibilityFramework", "CompatibilityFramework", 4321));
        meta.Module.Version64.Should().Be(BG3ModuleRef.DefaultVersion64);
        meta.Dependencies.Select(d => d.Uuid).Should().Equal(LibraryUuid, GustavDevUuid);
        meta.Dependencies[0].Name.Should().Be("CommunityLibrary");
    }

    [Fact]
    public void PakMeta_OlderAttributeTypesAndAByteOrderMark_StillParse()
    {
        // Older metas type the UUID as FixedString, omit PublishHandle, and may lead with a BOM.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
              <region id="Config"><node id="root"><children>
                <node id="Dependencies"><children>
                  <node id="ModuleShortDesc">
                    <attribute id="Folder" type="LSWString" value="CommunityLibrary" />
                    <attribute id="Name" type="FixedString" value="CommunityLibrary" />
                    <attribute id="UUID" type="FixedString" value="396C5966-09B0-40A1-AF3F-93A5E9CE71C0" />
                  </node>
                </children></node>
                <node id="ModuleInfo">
                  <attribute id="Folder" type="LSWString" value="Old" />
                  <attribute id="Name" type="FixedString" value="Old Mod" />
                  <attribute id="UUID" type="FixedString" value="b1f36451-c34a-4bd7-9776-3ba387576025" />
                  <attribute id="Version" type="int32" value="268435456" />
                </node>
              </children></node></region>
            </save>
            """;

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();

        var meta = BG3PakMeta.Parse(bytes);

        meta.Should().NotBeNull();
        meta!.Module.Name.Should().Be("Old Mod");
        meta.Module.PublishHandle.Should().Be(0);

        // 1.0.0.0 in the old 32-bit packing is 1.0.0.0 in the 64-bit one.
        meta.Module.Version64.Should().Be(36028797018963968);
        meta.Dependencies.Should().ContainSingle().Which.Uuid.Should().Be(LibraryUuid);
    }

    [Fact]
    public void PakMeta_APakWithNoMeta_IsNull()
    {
        var pak = Path.Combine(NewTempDir(), "override.pak");
        Bg3TestPak.Write(pak, ("Public/Game/GUI/a.dds", [1, 2, 3], Bg3TestPak.Stored));

        BG3PakMeta.Read(pak).Should().BeNull();
        BG3PakMeta.Parse(Encoding.UTF8.GetBytes("not xml")).Should().BeNull();
    }

    [Fact]
    public void FindUnmetDependencies_ReportsOnlyModulesNothingProvides()
    {
        var installation = Installation();
        var module = new BaldursGate3GameModule();
        var mods = ModsFolder(module, installation);

        // Needs a mod that is absent, a mod that is present, and a base-game module.
        Bg3TestPak.WriteMod(
            Path.Combine(mods, "30-001_Patch.pak"), "aaaaaaaa-0000-0000-0000-000000000001", "Patch",
            (LibraryUuid, "CommunityLibrary"),
            (FrameworkUuid, "CompatibilityFramework"),
            (GustavDevUuid, "GustavDev"));
        Bg3TestPak.WriteMod(Path.Combine(mods, "Framework.pak"), FrameworkUuid, "CompatibilityFramework");

        var unmet = module.FindUnmetDependencies(installation);

        unmet.Should().ContainSingle().Which.Should().Be(
            new UnmetDependency("Patch", "30-001_Patch.pak", "CommunityLibrary", LibraryUuid));
    }

    [Fact]
    public void FindUnmetDependencies_OnceTheModuleIsInstalled_IsEmpty()
    {
        var installation = Installation();
        var module = new BaldursGate3GameModule();
        var mods = ModsFolder(module, installation);

        Bg3TestPak.WriteMod(
            Path.Combine(mods, "Patch.pak"), "aaaaaaaa-0000-0000-0000-000000000001", "Patch",
            (LibraryUuid, "CommunityLibrary"));
        Bg3TestPak.WriteMod(Path.Combine(mods, "renamed by someone.pak"), LibraryUuid, "CommunityLibrary");

        module.FindUnmetDependencies(installation).Should().BeEmpty();
    }

    [Fact]
    public void FindUnmetDependencies_NoModsFolder_IsEmpty()
    {
        new BaldursGate3GameModule().FindUnmetDependencies(Installation()).Should().BeEmpty();
    }

    private static string ModsFolder(BaldursGate3GameModule module, GameInstallation installation)
    {
        var mods = module.ResolveModPaths(installation).GetRoot(BaldursGate3GameModule.ModsRoot)!;
        Directory.CreateDirectory(mods);
        return mods;
    }

    private GameInstallation Installation()
    {
        var gameDir = NewTempDir();
        Directory.CreateDirectory(Path.Combine(gameDir, "bin"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Data"));
        File.WriteAllText(Path.Combine(gameDir, "Data", "Gustav.pak"), string.Empty);

        return new GameInstallation
        {
            GameId = "baldursgate3",
            SteamAppId = 1086940,
            InstallPath = gameDir,
            ProtonPrefixPath = NewTempDir()
        };
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-bg3-" + Guid.NewGuid().ToString("N"));
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
