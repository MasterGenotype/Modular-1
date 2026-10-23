using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Archives;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Metadata;
using Modular.Core.Utilities;
using Xunit;

namespace Modular.Core.Tests.Dependencies;

/// <summary>
/// Reading the records other mod managers and hubs leave with a mod: where it came from
/// and what it depends on.
/// </summary>
public sealed class DependencyConventionsTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly ArchiveReaderFactory _factory = new();

    [Theory]
    [InlineData("Special - Savage Resilience-556-11-2-1733684094.zip", "556")]
    [InlineData("NSFW Loading Screens 2.0-13743-2-0-0-1731574831.rar", "13743")]
    // No extension, and a dot in the name that is not one.
    [InlineData("NSFW Loading Screens 2.0-13743-2-0-0-1731574831", "13743")]
    [InlineData("/downloads/Thing-0042-1-0-1700000000.7z", "42")]
    [InlineData("Thing-1-0.zip", null)]
    [InlineData("", null)]
    public void NexusFileName_ReadsTheModId(string name, string? expected)
    {
        NexusFileName.ParseModId(name).Should().Be(expected);
    }

    [Fact]
    public void ThunderstoreManifest_ListsItsDependenciesAndWhatItProvides()
    {
        var archive = Zip("mod.zip", ("manifest.json", """
            {
              "name": "BetterThing",
              "version_number": "1.2.0",
              "website_url": "",
              "description": "x",
              "dependencies": ["BepInEx-BepInExPack-5.4.2100", "Someone-Core_Lib-2.0.1", "malformed"]
            }
            """));

        var declared = DependencyConventions.Read(archive, _factory);

        declared.Name.Should().Be("BetterThing");
        declared.Provides.Should().Equal("BetterThing");
        declared.Requirements.Should().BeEquivalentTo(
        [
            new DeclaredRequirement(
                "BepInExPack by BepInEx", "BepInExPack", "thunderstore",
                Url: "https://thunderstore.io/package/BepInEx/BepInExPack/"),
            new DeclaredRequirement(
                "Core_Lib by Someone", "Core_Lib", "thunderstore",
                Url: "https://thunderstore.io/package/Someone/Core_Lib/")
        ]);
    }

    [Fact]
    public void AManifestThatIsNotThunderstores_IsIgnored()
    {
        // Plenty of things are called manifest.json; only one has a version_number.
        var archive = Zip("mod.zip", ("manifest.json", """{"name":"x","dependencies":["A-B-1.0.0"]}"""));

        DependencyConventions.Read(archive, _factory).Should().BeEquivalentTo(ArchiveDeclarations.None);
    }

    [Fact]
    public void Fomod_GivesTheNexusIdAndTheFilesItNeedsPresentOrAbsent()
    {
        var archive = Zip("mod.zip",
            ("MyMod/fomod/info.xml", """
                <fomod>
                  <Name>My Mod</Name>
                  <Id>4321</Id>
                  <Website>https://www.nexusmods.com/skyrimspecialedition/mods/4321</Website>
                </fomod>
                """),
            ("MyMod/fomod/ModuleConfig.xml", """
                <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <moduleName>My Mod</moduleName>
                  <moduleDependencies operator="And">
                    <fileDependency file="SkyUI_SE.esp" state="Active"/>
                    <fileDependency file="Old\Clash.esp" state="Missing"/>
                  </moduleDependencies>
                </config>
                """));

        var declared = DependencyConventions.Read(archive, _factory);

        declared.Name.Should().Be("My Mod");
        declared.Source.Should().Be(new DeclaredSource("nexusmods", "4321"));
        declared.Requirements.Should().BeEquivalentTo(
        [
            new DeclaredRequirement("SkyUI_SE.esp", "SkyUI_SE.esp", "fomod"),
            new DeclaredRequirement("Clash.esp", "Old/Clash.esp", "fomod", DependencyType.Incompatible)
        ]);
    }

    [Fact]
    public void Fomod_AnyOneOfSeveralFiles_IsNotReportedAsARequirement()
    {
        var archive = Zip("mod.zip", ("fomod/ModuleConfig.xml", """
            <config>
              <moduleDependencies operator="Or">
                <fileDependency file="A.esp" state="Active"/>
                <fileDependency file="B.esp" state="Active"/>
              </moduleDependencies>
            </config>
            """));

        DependencyConventions.Read(archive, _factory).Requirements.Should().BeEmpty();
    }

    [Fact]
    public void ModOrganizerMeta_BesideTheArchive_SaysWhichModItIs()
    {
        var archive = Zip("Renamed by hand.zip", ("readme.txt", "x"));
        File.WriteAllText(archive + ".meta", """
            [General]
            gameName=SkyrimSE
            modID=266
            fileID=432193
            repository=Nexus
            modName=Address Library
            """);

        var declared = DependencyConventions.Read(archive, _factory);

        declared.Source.Should().Be(new DeclaredSource("nexusmods", "266", FileId: "432193"));
        declared.Name.Should().Be("Address Library");
    }

    [Fact]
    public void ModOrganizerMeta_ForAManualDownload_SaysNothing()
    {
        var archive = Zip("mod.zip", ("readme.txt", "x"));
        File.WriteAllText(archive + ".meta", "[General]\nmodID=0\nfileID=0\nrepository=\n");

        DependencyConventions.Read(archive, _factory).Source.Should().BeNull();
    }

    [Fact]
    public void ModularsOwnDownloadRecord_RoundTrips_AndOutranksTheFileName()
    {
        // The name says NexusMods 556; the record says where it really came from.
        var archive = Zip("Thing-556-1-0-1700000000.zip", ("readme.txt", "x"));
        var source = new DeclaredSource("gamebanana", "598553", FileId: "77");

        DependencyConventions.WriteDownloadRecord(archive, source);

        DependencyConventions.Read(archive, _factory).Source.Should().Be(source);
    }

    [Fact]
    public void Bg3Info_ListsTheModulesAnArchiveProvides()
    {
        var archive = Zip("mod.zip", ("info.json", """
            {"Mods":[{"Author":"a","Name":"Dragonborn Unleashed","Folder":"DU",
                      "UUID":"B1F36451-C34A-4BD7-9776-3BA387576025","Version":"1"}],"MD5":""}
            """));

        var declared = DependencyConventions.Read(archive, _factory);

        declared.Name.Should().Be("Dragonborn Unleashed");
        declared.Provides.Should().Equal("b1f36451-c34a-4bd7-9776-3ba387576025");
    }

    [Fact]
    public void AnArchiveThatIsGone_StillYieldsWhatItsNameAndSidecarsSay()
    {
        var missing = Path.Combine(NewDir(), "Gone-9702-1-0-0-16-1774555674.zip");

        DependencyConventions.Read(missing, _factory).Source
            .Should().Be(new DeclaredSource("nexusmods", "9702"));
        DependencyConventions.Read(null, _factory).Should().Be(ArchiveDeclarations.None);
    }

    [Fact]
    public void MalformedRecords_AreSkippedNotThrown()
    {
        var archive = Zip("mod.zip",
            ("manifest.json", "{ not json"),
            ("fomod/info.xml", "<fomod><Name>"),
            ("info.json", "[1,2,3]"));
        File.WriteAllText(archive + DependencyConventions.DownloadRecordSuffix, "garbage");

        DependencyConventions.Read(archive, _factory).Should().BeEquivalentTo(ArchiveDeclarations.None);
    }

    [Fact]
    public void AnArchiveThatIsNotWhatItsExtensionSays_StillYieldsWhatItsNameSays()
    {
        // SharpCompress throws its own exception type for these, not an IOException.
        var archive = Path.Combine(NewDir(), "Broken-1234-1-0-1700000000.7z");
        File.WriteAllText(archive, "<html>not an archive</html>");

        DependencyConventions.Read(archive, _factory).Source
            .Should().Be(new DeclaredSource("nexusmods", "1234"));
    }

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(NewDir(), name);

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
            writer.Write(content);
        }

        return path;
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-conv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
