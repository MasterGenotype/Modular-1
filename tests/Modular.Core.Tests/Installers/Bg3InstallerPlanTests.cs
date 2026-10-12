using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Installers.BaldursGate3;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// The Baldur's Gate 3 installer's plans: choices between versions of a pak, and files
/// that belong outside the game folder when a plan is installed without staging.
/// </summary>
public sealed class Bg3InstallerPlanTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task Analyze_AnArchiveOfferingTwoVersions_AsksAndInstallsTheDefaultMeanwhile()
    {
        var archive = Zip(("Option A/Mod.pak", "a"), ("Option B/Mod.pak", "b"));
        var installer = new BG3ModInstaller();

        var plan = await installer.AnalyzeAsync(archive, new InstallContext { GameDirectory = NewDir() });

        plan.RequiresUserInput.Should().BeTrue();
        plan.Choices.Should().ContainSingle().Which.Options.Select(o => o.Label)
            .Should().Equal("Option A", "Option B");
        plan.Operations.Should().ContainSingle().Which.SourcePath.Should().Be("Option A/Mod.pak");
    }

    [Fact]
    public async Task Analyze_WithTheAnswer_InstallsThatVersionAndAsksNothing()
    {
        var archive = Zip(("Option A/Mod.pak", "a"), ("Option B/Mod.pak", "b"));
        var context = new InstallContext { GameDirectory = NewDir() };
        context.Selections["Mod.pak"] = "Option B";

        var plan = await new BG3ModInstaller().AnalyzeAsync(archive, context);

        plan.RequiresUserInput.Should().BeFalse();
        plan.Operations.Should().ContainSingle().Which.SourcePath.Should().Be("Option B/Mod.pak");
    }

    [Fact]
    public async Task Install_WithoutStaging_PutsEachFileUnderTheRootItNames()
    {
        var game = NewDir();
        var userMods = NewDir();
        var archive = Zip(("Mod.pak", "pak-bytes"), ("Data/Public/Game/GUI/a.dds", "loose-bytes"));

        var installer = new BG3ModInstaller();
        var plan = await installer.AnalyzeAsync(archive, new InstallContext
        {
            GameDirectory = game,
            ModPaths = new GameModPaths(new Dictionary<string, string>
            {
                [ModRoots.Game] = game,
                [BG3ArchiveAnalyzer.ModsRoot] = userMods
            })
        });

        var result = await installer.InstallAsync(plan);

        result.Success.Should().BeTrue(result.Error);

        // The pak belongs in the user Mods folder, never in the game folder.
        File.ReadAllText(Path.Combine(userMods, "Mod.pak")).Should().Be("pak-bytes");
        File.Exists(Path.Combine(game, "Mod.pak")).Should().BeFalse();
        File.ReadAllText(Path.Combine(game, "Data", "Public", "Game", "GUI", "a.dds")).Should().Be("loose-bytes");

        // Recorded so uninstall can find both: relative under the game, absolute elsewhere.
        result.InstalledFiles.Should().BeEquivalentTo(
            "Data/Public/Game/GUI/a.dds", Path.Combine(userMods, "Mod.pak"));
    }

    [Fact]
    public async Task Install_WithoutStaging_AndNoPlaceForARoot_FailsRatherThanGuessing()
    {
        var game = NewDir();
        var archive = Zip(("Mod.pak", "pak-bytes"));

        var installer = new BG3ModInstaller();
        var plan = await installer.AnalyzeAsync(archive, new InstallContext
        {
            GameDirectory = game,
            ModPaths = new GameModPaths(new Dictionary<string, string> { [ModRoots.Game] = game })
        });

        var result = await installer.InstallAsync(plan);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(BG3ArchiveAnalyzer.ModsRoot);
        File.Exists(Path.Combine(game, "Mod.pak")).Should().BeFalse();
    }

    private string Zip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(NewDir(), "mod.zip");

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
        var dir = Path.Combine(Path.GetTempPath(), "modular-bg3plan-" + Guid.NewGuid().ToString("N")[..8]);
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
