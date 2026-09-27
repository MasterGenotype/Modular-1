using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Installers;
using Modular.Sdk.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// Every built-in installer must run happily against an empty staging target and leave
/// no backup files behind.
/// </summary>
/// <remarks>
/// The installers each back up a destination file before overwriting it — some with
/// <c>.modular.bak</c>, some with <c>.backup</c>. Under staging the destination
/// directory is always empty, so that code is intentionally inert: it must not fire,
/// and it must not throw. Vanilla backups are the deployer's job, and it owns the only
/// copy that matters.
/// </remarks>
public sealed class StagedInstallerBackupTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public static TheoryData<string> InstallerIds => new()
    {
        "loose-file",
        "steam-mod",
        "fomod",
        "bepinex",
        "unreal-pak",
        "cyberpunk2077",
        "ff7remake",
        "horizon-zero-dawn"
    };

    [Theory]
    [MemberData(nameof(InstallerIds))]
    public async Task StagedInstall_WritesNoBackupFiles(string installerId)
    {
        var manager = new InstallerManager();
        var installer = manager.GetInstaller(installerId);
        installer.Should().NotBeNull($"installer '{installerId}' should be registered");

        var game = NewDir("game");
        var staging = new StagingManager(NewDir("staging"));
        var archive = CreateArchiveFor(installerId);

        var plan = await installer!.AnalyzeAsync(archive, new InstallContext
        {
            GameDirectory = game,
            ModId = "mod-" + installerId,
            AllowOverwrite = true,
            CreateBackups = true
        });

        using var session = staging.CreateSession("cs", "test-game", "mod-" + installerId);
        staging.RedirectPlan(plan, game, session.StagingDirectory);

        var result = await manager.ExecuteInstallAsync(plan);

        result.Success.Should().BeTrue(result.Error);

        var backups = Directory
            .EnumerateFiles(session.StagingDirectory, "*", SearchOption.AllDirectories)
            .Where(f =>
                f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".modular.bak", StringComparison.OrdinalIgnoreCase))
            .ToList();

        backups.Should().BeEmpty("the staging target is empty, so nothing can be backed up");
        result.BackedUpFiles.Should().BeEmpty();

        // And nothing leaked into the game folder.
        Directory.EnumerateFileSystemEntries(game).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InstallerIds))]
    public async Task StagedInstall_ProducesStagedFilesUnderANamedRoot(string installerId)
    {
        var manager = new InstallerManager();
        var installer = manager.GetInstaller(installerId)!;

        var game = NewDir("game");
        var staging = new StagingManager(NewDir("staging"));
        var archive = CreateArchiveFor(installerId);

        var plan = await installer.AnalyzeAsync(archive, new InstallContext
        {
            GameDirectory = game,
            ModId = "mod",
            AllowOverwrite = true
        });

        using var session = staging.CreateSession("cs", "test-game", "mod");
        staging.RedirectPlan(plan, game, session.StagingDirectory);
        await manager.ExecuteInstallAsync(plan);

        var staged = StagingManager.EnumerateStagedFiles(session.StagingDirectory);

        staged.Should().NotBeEmpty();
        staged.Should().OnlyContain(f => f.RootName.Length > 0);
        staged.Should().OnlyContain(f => !f.RelativePath.StartsWith('/'));
    }

    private string CreateArchiveFor(string installerId) => installerId switch
    {
        "fomod" => CreateZip(
            ("fomod/ModuleConfig.xml", FomodConfig),
            ("fomod/info.xml", "<fomod><Name>Test</Name></fomod>"),
            ("data/textures/thing.dds", "dds")),

        "bepinex" => CreateZip(
            ("BepInEx/plugins/Thing/Thing.dll", "dll"),
            ("BepInEx/config/Thing.cfg", "cfg"),
            ("winhttp.dll", "loader")),

        "unreal-pak" => CreateZip(("CoolMod_P.pak", "pak")),

        "cyberpunk2077" => CreateZip(
            ("archive/pc/mod/cool.archive", "archive"),
            ("r6/scripts/cool/cool.reds", "reds")),

        "ff7remake" => CreateZip(
            ("End/Content/Paks/~mods/CoolFF7Mod_P.pak", "pak"),
            ("End/Binaries/Win64/dxgi.dll", "hook")),

        "horizon-zero-dawn" => CreateZip(
            ("Packed_DX12/Patch_Cool.bin", "bin")),

        _ => CreateZip(
            ("readme.txt", "hello"),
            ("data/thing.dat", "data"))
    };

    private const string FomodConfig = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <moduleName>Test Mod</moduleName>
          <requiredInstallFiles>
            <folder source="data" destination="" />
          </requiredInstallFiles>
        </config>
        """;

    private string CreateZip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(NewDir("zip"), "mod.zip");

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
            writer.Write(content);
        }

        return path;
    }

    private string NewDir(string label)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"modular-{label}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <inheritdoc />
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
                // Test cleanup only.
            }
        }
    }
}
