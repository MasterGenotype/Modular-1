using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Configuration;
using Modular.Core.Database;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// Pruning a game's archives down to one copy of each installed mod: the installed mods'
/// archives give way to records, and with <c>removeUnused</c> every other archive of the
/// game goes.
/// </summary>
public sealed class ArchivePrunerTests : IAsyncLifetime
{
    private readonly List<string> _tempDirs = new();
    private ModularDatabase _database = null!;
    private string _modsDir = null!;
    private string _staging = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = new ModularDatabase(Path.Combine(NewDir("db"), "modular.db"));
        await _database.InitializeAsync();
        _modsDir = NewDir("mods");
        _staging = Path.Combine(NewDir("config"), "staging");
    }

    [Fact]
    public async Task Prune_ReleasesInstalledArchives_AndRemovesTheRest()
    {
        // Installed while archives were kept, as every install before store-once was.
        var keeping = new ModInstallationService(
            _database, stagingBasePath: _staging,
            settings: new AppSettings { ModsDirectory = _modsDir, KeepInstalledArchives = true });

        var modFolder = Path.Combine(_modsDir, "nexusmods", "game-domain", "Mod");
        var oldVersion = Zip(modFolder, "Mod-1-1-0-1700000000.zip", ("data/a.dat", "v1"));
        var current = Zip(modFolder, "Mod-1-2-0-1800000000.zip", ("data/a.dat", "v2"));
        DependencyConventions.WriteDownloadRecord(current, new DeclaredSource("nexusmods", "1", "game-domain", "20"));
        File.WriteAllText(Path.Combine(modFolder, "readme.txt"), "keep me");
        var neverInstalledFolder = Path.Combine(_modsDir, "nexusmods", "game-domain", "Other");
        var neverInstalled = Zip(neverInstalledFolder, "Other-2-1-0-1700000000.zip", ("data/b.dat", "b"));

        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod", AutoSnapshot = false };
        (await keeping.InstallAsync(oldVersion, game, options)).Success.Should().BeTrue();
        var installed = await keeping.InstallAsync(current, game, options);
        installed.Success.Should().BeTrue(installed.Error);

        var uninstalled = Zip(Path.Combine(_modsDir, ArchiveStore.FolderName, installed.GameKey!), "gone.zip", ("x", "x"));
        var pruner = new ArchivePruner(_database);

        // A dry run changes nothing.
        var plan = await pruner.PruneAsync(installed.GameKey!, _modsDir, null, removeUnused: true, apply: false);
        plan.Items.Should().BeEquivalentTo(new[]
        {
            new ArchivePruneItem(current, new FileInfo(current).Length, ArchivePruneAction.Release, "mod"),
            new ArchivePruneItem(oldVersion, new FileInfo(oldVersion).Length, ArchivePruneAction.Remove),
            new ArchivePruneItem(neverInstalled, new FileInfo(neverInstalled).Length, ArchivePruneAction.Remove),
            new ArchivePruneItem(uninstalled, new FileInfo(uninstalled).Length, ArchivePruneAction.Remove)
        });
        new[] { current, oldVersion, neverInstalled, uninstalled }.Should().OnlyContain(p => File.Exists(p));

        var result = await pruner.PruneAsync(installed.GameKey!, _modsDir, null, removeUnused: true, apply: true);

        result.Errors.Should().BeEmpty();
        result.BytesFreed.Should().Be(plan.BytesFreed);
        new[] { current, oldVersion, neverInstalled, uninstalled }.Should().OnlyContain(p => !File.Exists(p));
        ArchiveRelease.IsReleased(current).Should().BeTrue();
        DependencyConventions.ReadDownloadRecord(current).Source!.FileId.Should().Be("20");
        File.Exists(Path.Combine(modFolder, "readme.txt")).Should().BeTrue();
        Directory.Exists(neverInstalledFolder).Should().BeFalse("an emptied mod folder goes too");

        // The installed mod is untouched.
        File.ReadAllText(Path.Combine(game, "data", "a.dat")).Should().Be("v2");
        Directory.EnumerateFiles(installed.StagingPath!, "*", SearchOption.AllDirectories).Should().NotBeEmpty();

        // Nothing is left to do.
        var again = await pruner.PruneAsync(installed.GameKey!, _modsDir, null, removeUnused: true, apply: true);
        again.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Prune_WithoutRemoveUnused_OnlyReleases()
    {
        var keeping = new ModInstallationService(
            _database, stagingBasePath: _staging,
            settings: new AppSettings { ModsDirectory = _modsDir, KeepInstalledArchives = true });
        var folder = Path.Combine(_modsDir, "nexusmods", "game-domain", "Mod");
        var current = Zip(folder, "current.zip", ("a.dat", "a"));
        var other = Zip(folder, "other.zip", ("b.dat", "b"));

        var installed = await keeping.InstallAsync(current, NewDir("game"),
            new ModInstallationOptions { ModId = "mod", AutoSnapshot = false });
        installed.Success.Should().BeTrue(installed.Error);

        var result = await new ArchivePruner(_database).PruneAsync(
            installed.GameKey!, _modsDir, null, removeUnused: false, apply: true);

        result.Items.Should().ContainSingle().Which.Action.Should().Be(ArchivePruneAction.Release);
        File.Exists(current).Should().BeFalse();
        File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public async Task Prune_KeepsTheArchiveOfAModWhoseStagingIsGone()
    {
        // Without staged files the archive is the mod's only copy.
        var keeping = new ModInstallationService(
            _database, stagingBasePath: _staging,
            settings: new AppSettings { ModsDirectory = _modsDir, KeepInstalledArchives = true });
        var current = Zip(Path.Combine(_modsDir, "nexusmods", "game-domain", "Mod"), "current.zip", ("a.dat", "a"));
        var installed = await keeping.InstallAsync(current, NewDir("game"),
            new ModInstallationOptions { ModId = "mod", AutoSnapshot = false });
        Directory.Delete(installed.StagingPath!, recursive: true);

        var result = await new ArchivePruner(_database).PruneAsync(
            installed.GameKey!, _modsDir, null, removeUnused: true, apply: true);

        result.Unstaged.Should().Equal("mod");
        result.Items.Should().BeEmpty();
        File.Exists(current).Should().BeTrue();
    }

    private static string Zip(string folder, string name, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
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
        var dir = Path.Combine(Path.GetTempPath(), $"modular-prune-{label}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
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
