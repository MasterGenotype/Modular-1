using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Modular.Core.Installers;
using Modular.Core.Installers.Fomod;
using Modular.Sdk.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// The FOMOD installer runs the archive's wizard: it asks what the wizard asks, installs
/// only what the picks select, and never copies the wizard's own files.
/// </summary>
public sealed class FomodInstallerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // Shaped like Sweaty 25: one folder per variant, each laid out from the game root.
    private const string VariantsConfig = """
        <config>
          <moduleName>Butter My V</moduleName>
          <installSteps order="Explicit">
            <installStep name="Options">
              <optionalFileGroups order="Explicit">
                <group name="Options" type="SelectExactlyOne">
                  <plugins order="Explicit">
                    <plugin name="Skin Tone 01">
                      <files><folder source="mod_01" destination="" priority="0" /></files>
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                    <plugin name="All in One">
                      <files><folder source="mod_AIO" destination="" priority="0" /></files>
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
          </installSteps>
        </config>
        """;

    // Shaped like Better Minimap: a style sets a flag, a page per style is shown only
    // for its flag, and source paths use backslashes.
    private const string BranchingConfig = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <installSteps order="Explicit">
            <installStep name="Style">
              <optionalFileGroups order="Explicit">
                <group name="Choose Minimap Style" type="SelectExactlyOne">
                  <plugins order="Explicit">
                    <plugin name="Compass Only">
                      <conditionFlags><flag name="custom">Inactive</flag></conditionFlags>
                      <files><folder source="Files\Compass Only" destination="" /></files>
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                    <plugin name="Custom">
                      <conditionFlags><flag name="custom">Active</flag></conditionFlags>
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
            <installStep name="Zoom">
              <visible><dependencies operator="And"><flagDependency flag="custom" value="Active"/></dependencies></visible>
              <optionalFileGroups order="Explicit">
                <group name="Choose Zoom Level" type="SelectExactlyOne">
                  <plugins order="Explicit">
                    <plugin name="Slight">
                      <files><folder source="Files\Custom\Slight" destination="" /></files>
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                    <plugin name="Medium">
                      <files><folder source="Files\Custom\Medium" destination="" /></files>
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
            <installStep name="DistanceFix">
              <optionalFileGroups order="Explicit">
                <group name=" Enable DistanceFix?" type="SelectExactlyOne">
                  <plugins order="Explicit">
                    <plugin name=" Yes">
                      <files><folder source="Files\DistanceFix" destination="" /></files>
                      <typeDescriptor><type name="Recommended"/></typeDescriptor>
                    </plugin>
                    <plugin name=" No">
                      <typeDescriptor><type name="Optional"/></typeDescriptor>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
          </installSteps>
        </config>
        """;

    [Fact]
    public async Task Analyze_Variants_AsksAndInstallsOnlyTheDefaultMeanwhile()
    {
        var archive = Zip(
            ("fomod/ModuleConfig.xml", VariantsConfig),
            ("fomod/info.xml", "<fomod />"),
            ("banner.png", "png"),
            ("mod_01/archive/pc/mod/sweaty_01.archive", "01"),
            ("mod_AIO/archive/pc/mod/sweaty_aio.archive", "aio"));

        var plan = await new FomodInstaller().AnalyzeAsync(archive, new InstallContext { GameDirectory = NewDir() });

        plan.RequiresUserInput.Should().BeTrue();
        var choice = plan.Choices.Should().ContainSingle().Subject;
        choice.Options.Select(o => o.Label).Should().Equal("Skin Tone 01", "All in One");
        choice.Options.Select(o => o.FileCount).Should().Equal(1, 1);
        Destinations(plan).Should().Equal("archive/pc/mod/sweaty_01.archive");
    }

    [Fact]
    public async Task Analyze_WithTheAnswer_InstallsThatVariantFromTheGameRoot()
    {
        var archive = Zip(
            ("fomod/ModuleConfig.xml", VariantsConfig),
            ("mod_01/archive/pc/mod/sweaty_01.archive", "01"),
            ("mod_AIO/archive/pc/mod/sweaty_aio.archive", "aio"));
        var context = new InstallContext { GameDirectory = NewDir() };
        context.Selections["1:Options/1:Options"] = "All in One";

        var plan = await new FomodInstaller().AnalyzeAsync(archive, context);

        plan.RequiresUserInput.Should().BeFalse();
        Destinations(plan).Should().Equal("archive/pc/mod/sweaty_aio.archive");
    }

    [Fact]
    public async Task Analyze_APageShownForAPick_IsAskedOnceThePickIsMade()
    {
        var archive = Zip(
            ("fomod/ModuleConfig.xml", BranchingConfig),
            ("Files/Compass Only/archive/pc/mod/compass.archive", "c"),
            ("Files/Custom/Slight/archive/pc/mod/slight.archive", "s"),
            ("Files/Custom/Medium/archive/pc/mod/medium.archive", "m"),
            ("Files/DistanceFix/engine/config/platform/pc/fix.ini", "ini"),
            ("Images/Header.jpg", "jpg"));
        var installer = new FomodInstaller();
        var context = new InstallContext { GameDirectory = NewDir() };

        var first = await installer.AnalyzeAsync(archive, context);
        first.Choices.Select(c => c.Title).Should().Equal(
            "Style: Choose Minimap Style", "DistanceFix: Enable DistanceFix?");

        var style = first.Choices[0];
        context.Selections[style.Id] = "Custom";
        var second = await installer.AnalyzeAsync(archive, context);

        var zoom = second.Choices.Should().Contain(c => c.Title == "Zoom: Choose Zoom Level").Subject;
        context.Selections[zoom.Id] = "Medium";
        var third = await installer.AnalyzeAsync(archive, context);

        // DistanceFix is recommended, so it is installed unless declined.
        Destinations(third).Should().Equal(
            "archive/pc/mod/medium.archive", "engine/config/platform/pc/fix.ini");
        third.Choices.Single(c => c.Title.StartsWith("DistanceFix")).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_SelectAny_AsksPerOptionAndHonoursRequiredAndRecommended()
    {
        const string config = """
            <config>
              <installSteps>
                <installStep name="Extras">
                  <optionalFileGroups>
                    <group name="Extras" type="SelectAny">
                      <plugins order="Explicit">
                        <plugin name="Core"><files><file source="core.txt" destination="core.txt" /></files>
                          <typeDescriptor><type name="Required"/></typeDescriptor></plugin>
                        <plugin name="Nice"><files><file source="nice.txt" destination="nice.txt" /></files>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor></plugin>
                        <plugin name="Odd"><files><file source="odd.txt" destination="odd.txt" /></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
                        <plugin name="Broken"><files><file source="broken.txt" destination="broken.txt" /></files>
                          <typeDescriptor><type name="NotUsable"/></typeDescriptor></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;
        var archive = Zip(
            ("fomod/ModuleConfig.xml", config),
            ("core.txt", "1"), ("nice.txt", "2"), ("odd.txt", "3"), ("broken.txt", "4"));

        var plan = await new FomodInstaller().AnalyzeAsync(archive, new InstallContext { GameDirectory = NewDir() });

        // Required is not asked; NotUsable is not offered; the rest are yes-or-no.
        plan.Choices.Select(c => c.Title).Should().Equal("Extras: install Nice?", "Extras: install Odd?");
        plan.Choices[0].Options.Select(o => o.Label).Should().Equal("Nice", "Skip Nice");
        Destinations(plan).Should().Equal("core.txt", "nice.txt");
    }

    [Fact]
    public async Task Analyze_RequiredAndConditionalFiles_FollowThePicksAndPriority()
    {
        const string config = """
            <config>
              <requiredInstallFiles>
                <file source="base\readme.txt" destination="docs\readme.txt" priority="0" />
                <file source="base\shared.ini" destination="shared.ini" priority="0" />
              </requiredInstallFiles>
              <installSteps>
                <installStep name="Look">
                  <optionalFileGroups>
                    <group name="Look" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="Dark"><conditionFlags><flag name="look">dark</flag></conditionFlags>
                          <typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
                        <plugin name="Light"><conditionFlags><flag name="look">light</flag></conditionFlags>
                          <typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
              <conditionalFileInstalls>
                <patterns>
                  <pattern>
                    <dependencies><flagDependency flag="look" value="light"/></dependencies>
                    <files><file source="light\shared.ini" destination="shared.ini" priority="5" /></files>
                  </pattern>
                </patterns>
              </conditionalFileInstalls>
            </config>
            """;
        var archive = Zip(
            ("fomod/ModuleConfig.xml", config),
            ("base/readme.txt", "r"), ("base/shared.ini", "base"), ("light/shared.ini", "light"));
        var context = new InstallContext { GameDirectory = NewDir() };
        context.Selections["1:Look/1:Look"] = "Light";

        var plan = await new FomodInstaller().AnalyzeAsync(archive, context);

        plan.Operations.Select(o => (o.SourcePath, o.DestinationPath)).Should().BeEquivalentTo(new[]
        {
            ("base/readme.txt", "docs/readme.txt"),
            ("light/shared.ini", "shared.ini")
        });
    }

    [Fact]
    public async Task Analyze_InsideAWrapperFolder_ReadsSourcesFromBesideTheFomodFolder()
    {
        var archive = Zip(
            ("Butter My V/fomod/ModuleConfig.xml", VariantsConfig),
            ("Butter My V/mod_01/archive/pc/mod/sweaty_01.archive", "01"),
            ("Butter My V/mod_AIO/archive/pc/mod/sweaty_aio.archive", "aio"));

        var plan = await new FomodInstaller().AnalyzeAsync(archive, new InstallContext { GameDirectory = NewDir() });

        plan.Operations.Should().ContainSingle().Which.SourcePath
            .Should().Be("Butter My V/mod_01/archive/pc/mod/sweaty_01.archive");
        Destinations(plan).Should().Equal("archive/pc/mod/sweaty_01.archive");
    }

    [Fact]
    public async Task Analyze_AUtf16ConfigDeclaringUtf8_IsRead()
    {
        // The FOMOD Creation Tool writes UTF-16; hand edits leave declarations that lie.
        var config = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + VariantsConfig;
        var archive = Zip(
            [("fomod/ModuleConfig.xml", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(config)).ToArray()),
             ("mod_01/archive/pc/mod/sweaty_01.archive", "01"u8.ToArray()),
             ("mod_AIO/archive/pc/mod/sweaty_aio.archive", "aio"u8.ToArray())]);

        var plan = await new FomodInstaller().AnalyzeAsync(archive, new InstallContext { GameDirectory = NewDir() });

        plan.Choices.Should().ContainSingle();
    }

    [Fact]
    public async Task Detect_AConfigThatInstallsNothing_IsLeftToTheGamesInstaller()
    {
        var archive = Zip(
            ("fomod/ModuleConfig.xml", "<config><moduleName>Info only</moduleName></config>"),
            ("archive/pc/mod/thing.archive", "a"));

        var detection = await new FomodInstaller().DetectAsync(archive);

        detection.CanHandle.Should().BeFalse();
    }

    [Fact]
    public async Task Install_WritesOnlyThePickedFiles()
    {
        var game = NewDir();
        var archive = Zip(
            ("fomod/ModuleConfig.xml", VariantsConfig),
            ("banner.png", "png"),
            ("mod_01/archive/pc/mod/sweaty_01.archive", "01"),
            ("mod_AIO/archive/pc/mod/sweaty_aio.archive", "aio"));
        var context = new InstallContext { GameDirectory = game };
        context.Selections["1:Options/1:Options"] = "All in One";

        var installer = new FomodInstaller();
        var result = await installer.InstallAsync(await installer.AnalyzeAsync(archive, context));

        result.Success.Should().BeTrue(result.Error);
        Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(game, f).Replace('\\', '/'))
            .Should().Equal("archive/pc/mod/sweaty_aio.archive");
    }

    [Fact]
    public void Planner_AtMostOne_OffersNoneAndDefaultsToIt()
    {
        var config = FomodModuleConfig.Parse(System.Xml.Linq.XDocument.Parse("""
            <config><installSteps><installStep name="Patch"><optionalFileGroups>
              <group name="Patch" type="SelectAtMostOne"><plugins>
                <plugin name="A"><files><file source="a" /></files><typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
                <plugin name="B"><files><file source="b" /></files><typeDescriptor><type name="Optional"/></typeDescriptor></plugin>
              </plugins></group>
            </optionalFileGroups></installStep></installSteps></config>
            """));

        var plan = FomodPlanner.Plan(config, null, _ => FomodFileState.Missing, files => files.Count());

        plan.Choices.Should().ContainSingle().Which.Options.Select(o => o.Label).Should().Equal("None", "A", "B");
        plan.Choices[0].SelectedOptionId.Should().Be(FomodPlanner.None);
        plan.Files.Should().BeEmpty();
    }

    private static IEnumerable<string> Destinations(InstallPlan plan) =>
        plan.Operations.Select(o => o.DestinationPath);

    private string Zip(params (string Path, string Content)[] entries) =>
        Zip(entries.Select(e => (e.Path, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    private string Zip((string Path, byte[] Content)[] entries)
    {
        var path = Path.Combine(NewDir(), "mod.zip");

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var stream = zip.CreateEntry(entryPath).Open();
            stream.Write(content);
        }

        return path;
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-fomod-" + Guid.NewGuid().ToString("N")[..8]);
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
