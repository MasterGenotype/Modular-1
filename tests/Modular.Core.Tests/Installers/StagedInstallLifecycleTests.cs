using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Modular.Core.Database;
using Modular.Core.Installers;
using Xunit;

namespace Modular.Core.Tests.Installers;

/// <summary>
/// The staged install lifecycle end to end: what lands in the game folder, what the
/// changeset records, and what uninstall does with each kind of changeset.
/// </summary>
public sealed class StagedInstallLifecycleTests : IAsyncLifetime
{
    private readonly List<string> _tempDirs = new();
    private ModularDatabase _database = null!;
    private ModInstallationService _service = null!;
    private ChangesetManager _changesets = null!;
    private string _backupBase = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var dbPath = Path.Combine(NewDir("db"), "modular.db");
        _database = new ModularDatabase(dbPath);
        await _database.InitializeAsync();

        // The backup store sits beside the staging root, so a temp staging root keeps the
        // whole test off the user's real config directory.
        var configRoot = NewDir("config");
        _backupBase = Path.Combine(configRoot, "backups");

        _service = new ModInstallationService(
            _database, stagingBasePath: Path.Combine(configRoot, "staging"));
        _changesets = new ChangesetManager(_database);
    }

    // ── staged install ──────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_Staged_PlacesFilesAndKeepsTheStagedCopy()
    {
        var game = NewDir("game");
        var archive = CreateZip(("readme.txt", "hello"), ("data/thing.dat", "payload"));

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a",
            AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        result.ManagedBy.Should().Be(ChangesetOwnership.Deployment);
        result.StagingPath.Should().NotBeNull();
        result.StagedFiles.Should().Contain("game/data/thing.dat");

        // Deployed into the game folder …
        File.ReadAllText(Path.Combine(game, "data", "thing.dat")).Should().Be("payload");

        // … and still staged, so a redeploy needs no archive.
        File.ReadAllText(Path.Combine(result.StagingPath!, "game", "data", "thing.dat"))
            .Should().Be("payload");
    }

    [Fact]
    public async Task InstallAsync_Staged_RecordsStagedPathsNotGamePathsInTheChangeset()
    {
        // A managed changeset must never hand a game path to the legacy deletion flow.
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a",
            AutoSnapshot = false
        });

        var changeset = await _changesets.GetChangesetAsync(result.ChangesetId!);
        changeset!.ManagedBy.Should().Be(ChangesetOwnership.Deployment);
        changeset.GameKey.Should().NotBeNullOrEmpty();

        var ops = JsonSerializer.Deserialize<JsonElement>(changeset.OperationsJson!);

        ops.GetProperty("managedBy").GetString().Should().Be("deployment");
        ops.GetProperty("installedFiles").EnumerateArray().Should().BeEmpty();
        ops.GetProperty("stagedFiles").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("game/data/thing.dat");
        ops.GetProperty("deployedFiles").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task InstallAsync_Staged_BacksUpAPreExistingFileToTheBackupStore()
    {
        // The deployer owns vanilla backups, and keeps them outside the game folder and
        // stamped with the build they came from — not as a ".backup" sibling that a game
        // update would silently invalidate.
        var game = NewDir("game");
        File.WriteAllText(Path.Combine(game, "readme.txt"), "vanilla");

        var archive = CreateZip(("readme.txt", "modded"));

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a",
            AllowOverwrite = true,
            AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        result.Deployed.Should().BeTrue();
        File.ReadAllText(Path.Combine(game, "readme.txt")).Should().Be("modded");

        result.Deployment!.VanillaBackedUp.Should().Contain("game/readme.txt");
        File.Exists(Path.Combine(game, "readme.txt.backup")).Should().BeFalse();

        var backups = Directory
            .EnumerateFiles(_backupBase, "readme.txt", SearchOption.AllDirectories)
            .ToList();
        backups.Should().ContainSingle();
        File.ReadAllText(backups[0]).Should().Be("vanilla");
    }

    [Fact]
    public async Task InstallAsync_NoDeploy_RegistersTheModButChangesNothingInTheGame()
    {
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a",
            NoDeploy = true,
            AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        result.Deployed.Should().BeFalse();
        result.StagedFiles.Should().Contain("game/data/thing.dat");

        Directory.EnumerateFileSystemEntries(game).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallAsync_Staged_ReinstallingReportsNoConflicts()
    {
        // The pre-install conflict check used to fire on Modular's own output, so a
        // reinstall reported every file as conflicting with itself.
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var first = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });
        first.Success.Should().BeTrue(first.Error);

        var second = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });

        second.Success.Should().BeTrue(second.Error);
        second.Error.Should().BeNull();
    }

    [Fact]
    public async Task InstallAsync_DryRun_StagesNothing()
    {
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a",
            DryRun = true,
            AutoSnapshot = false
        });

        result.DryRun.Should().BeTrue();
        result.StagingPath.Should().BeNull();
        Directory.EnumerateFileSystemEntries(game).Should().BeEmpty();
    }

    // ── uninstall ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UninstallAsync_ManagedChangeset_RemovesDeployedFilesThenStaging()
    {
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var install = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });

        var uninstall = await _service.UninstallAsync(install.ChangesetId!);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        uninstall.FilesRemoved.Should().Be(1);
        uninstall.StagingRemoved.Should().BeTrue();

        File.Exists(Path.Combine(game, "data", "thing.dat")).Should().BeFalse();
        Directory.Exists(install.StagingPath!).Should().BeFalse();
    }

    [Fact]
    public async Task UninstallAsync_ManagedChangeset_RestoresTheVanillaFile()
    {
        var game = NewDir("game");
        File.WriteAllText(Path.Combine(game, "readme.txt"), "vanilla");
        var archive = CreateZip(("readme.txt", "modded"));

        var install = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AllowOverwrite = true, AutoSnapshot = false
        });

        await _service.UninstallAsync(install.ChangesetId!);

        File.ReadAllText(Path.Combine(game, "readme.txt")).Should().Be("vanilla");
    }

    [Fact]
    public async Task UninstallAsync_LegacyChangeset_RestoresBothBackupSuffixes()
    {
        // The game-specific installers write ".modular.bak"; uninstall restored only
        // ".backup", so a Cyberpunk, FF7R or HZD uninstall lost the vanilla file.
        var game = NewDir("game");

        var modularBak = Path.Combine(game, "a.txt");
        File.WriteAllText(modularBak, "modded-a");
        File.WriteAllText(modularBak + ".modular.bak", "vanilla-a");

        var dotBackup = Path.Combine(game, "b.txt");
        File.WriteAllText(dotBackup, "modded-b");
        File.WriteAllText(dotBackup + ".backup", "vanilla-b");

        var changesetId = await _changesets.CreateChangesetAsync(
            modId: "legacy-mod",
            archivePath: CreateZip(("a.txt", "modded-a")),
            targetDirectory: game,
            managedBy: ChangesetOwnership.Legacy);

        await _changesets.UpdateStateAsync(
            changesetId,
            ChangesetState.Committed,
            JsonSerializer.Serialize(new
            {
                installedFiles = new[] { "a.txt", "b.txt" },
                backedUpFiles = new[] { modularBak + ".modular.bak", dotBackup + ".backup" }
            }));

        var result = await _service.UninstallAsync(changesetId);

        result.Success.Should().BeTrue(result.Error);
        result.FilesRestored.Should().Be(2);
        File.ReadAllText(modularBak).Should().Be("vanilla-a");
        File.ReadAllText(dotBackup).Should().Be("vanilla-b");
    }

    // ── link safety ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallAsync_LegacyOverALinkedDeployment_LeavesTheStagedBytesIntact(
        bool symlink)
    {
        // A deployed file shares its bytes with the staged copy, so uninstall must delete
        // the link itself and never write through it or follow it. For a symlink,
        // following it on delete would destroy the staged copy outright.
        var (game, staging, changesetId) = await CreateLinkedDeploymentAsync(symlink);

        var result = await _service.UninstallAsync(changesetId);

        result.Success.Should().BeTrue(result.Error);
        File.Exists(Path.Combine(game, "linked.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(staging, "game", "linked.txt")).Should().Be("staged-bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallAsync_LegacyOverALinkedDeployment_RestoresTheVanillaFileByRename(
        bool symlink)
    {
        // Restoring must replace the link by rename. Copying over it would write straight
        // through into the staged bytes.
        var (game, staging, changesetId) = await CreateLinkedDeploymentAsync(
            symlink, withVanillaBackup: true);

        await _service.UninstallAsync(changesetId);

        File.ReadAllText(Path.Combine(game, "linked.txt")).Should().Be("vanilla-bytes");
        File.ReadAllText(Path.Combine(staging, "game", "linked.txt")).Should().Be("staged-bytes");
    }

    private async Task<(string Game, string Staging, string ChangesetId)> CreateLinkedDeploymentAsync(
        bool symlink,
        bool withVanillaBackup = false)
    {
        var game = NewDir("game");
        var staging = NewDir("staging-mod");

        var stagedDir = Path.Combine(staging, "game");
        Directory.CreateDirectory(stagedDir);
        var stagedFile = Path.Combine(stagedDir, "linked.txt");
        File.WriteAllText(stagedFile, "staged-bytes");

        var deployed = Path.Combine(game, "linked.txt");
        if (symlink)
            File.CreateSymbolicLink(deployed, stagedFile);
        else
            CreateHardLink(stagedFile, deployed);

        if (withVanillaBackup)
            File.WriteAllText(deployed + ".backup", "vanilla-bytes");

        var changesetId = await _changesets.CreateChangesetAsync(
            modId: "linked-mod",
            archivePath: CreateZip(("linked.txt", "staged-bytes")),
            targetDirectory: game,
            managedBy: ChangesetOwnership.Legacy);

        await _changesets.UpdateStateAsync(
            changesetId,
            ChangesetState.Committed,
            JsonSerializer.Serialize(new
            {
                installedFiles = new[] { "linked.txt" }
            }));

        return (game, staging, changesetId);
    }

    private static void CreateHardLink(string existing, string link)
    {
        // .NET 8 has no managed hardlink API. Shelling out keeps this test honest about
        // testing a real link rather than a copy.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ln",
            ArgumentList = { existing, link },
            RedirectStandardError = true
        })!;

        process.WaitForExit();
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

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
