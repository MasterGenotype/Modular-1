using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Installers;
using Modular.Sdk.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// A mod's installer answers are saved with it, so an update or reinstall repeats them instead of
/// falling back to the installer's defaults; with nobody to ask, an unanswered question stops the
/// install.
/// </summary>
public sealed class SavedInstallChoicesTests : IAsyncLifetime
{
    private readonly List<string> _tempDirs = new();
    private ModularDatabase _database = null!;
    private ModInstallationService _service = null!;
    private string _modsDir = null!;

    // One question, two answers; the default is the first ("Skin Tone 01").
    private const string Config = """
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

    public async Task InitializeAsync()
    {
        _database = new ModularDatabase(Path.Combine(NewDir("db"), "modular.db"));
        await _database.InitializeAsync();
        _modsDir = NewDir("mods");
        _service = new ModInstallationService(_database, stagingBasePath: Path.Combine(NewDir("config"), "staging"),
            settings: new AppSettings { ModsDirectory = _modsDir, KeepInstalledArchives = true });
    }

    private string Version(string version) => Zip($"Butter-1-{version}.zip",
        ("fomod/ModuleConfig.xml", Config),
        ("mod_01/data/skin.txt", $"tone 01 v{version}"),
        ("mod_AIO/data/skin.txt", $"all in one v{version}"));

    private static Func<IReadOnlyList<InstallChoice>, CancellationToken, Task<IReadOnlyDictionary<string, string>?>> Pick(string label) =>
        (choices, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(choices.Where(c => !c.IsResolved)
            .ToDictionary(c => c.Id, c => c.Options.First(o => o.Label == label).Id));

    [Fact]
    public async Task AnUpdate_RepeatsTheSavedAnswer_InsteadOfTheDefault()
    {
        var game = NewDir("game");
        var first = await _service.InstallAsync(Version("1"), game,
            new ModInstallationOptions { ModId = "butter", AutoSnapshot = false, ChoiceResolver = Pick("All in One") });
        first.Success.Should().BeTrue(first.Error);
        File.ReadAllText(Path.Combine(game, "data/skin.txt")).Should().Be("all in one v1");
        (await _service.Deployment.Mods.GetAsync(first.GameKey!, "butter"))!.InstallChoices.Should().NotBeNull();

        // An unattended update: no one to ask, defaults not allowed.
        var update = await _service.InstallAsync(Version("2"), game,
            new ModInstallationOptions { ModId = "butter", AutoSnapshot = false, RequireAnsweredChoices = true });

        update.Success.Should().BeTrue(update.Error);
        File.ReadAllText(Path.Combine(game, "data/skin.txt")).Should().Be("all in one v2", "the saved answer is repeated, not the default");
    }

    [Fact]
    public async Task WithoutSavedAnswers_AnUnattendedInstallStops()
    {
        var game = NewDir("game");
        var result = await _service.InstallAsync(Version("1"), game,
            new ModInstallationOptions { ModId = "butter", AutoSnapshot = false, RequireAnsweredChoices = true });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("installer options");
        File.Exists(Path.Combine(game, "data/skin.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ChoosingAgain_IgnoresTheSavedAnswer()
    {
        var game = NewDir("game");
        (await _service.InstallAsync(Version("1"), game,
            new ModInstallationOptions { ModId = "butter", AutoSnapshot = false, ChoiceResolver = Pick("All in One") })).Success.Should().BeTrue();

        var again = await _service.InstallAsync(Version("1b"), game,
            new ModInstallationOptions { ModId = "butter", AutoSnapshot = false, ReuseSavedChoices = false, ChoiceResolver = Pick("Skin Tone 01") });

        again.Success.Should().BeTrue(again.Error);
        File.ReadAllText(Path.Combine(game, "data/skin.txt")).Should().Be("tone 01 v1b");
    }

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_modsDir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
            writer.Write(content);
        }
        return path;
    }

    private string NewDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"modular-choices-{label}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        foreach (var dir in _tempDirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }
}
