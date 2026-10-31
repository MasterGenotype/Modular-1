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
    private string _archiveBase = null!;
    private string _stagingBase = null!;

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
        _archiveBase = Path.Combine(configRoot, "archives");
        _stagingBase = Path.Combine(configRoot, "staging");

        _service = new ModInstallationService(
            _database, stagingBasePath: _stagingBase);
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

        // The first install moved the archive into the store; a reinstall reads it there.
        var second = await _service.InstallAsync(first.ArchivePath, game, new ModInstallationOptions
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

    // ── bare files ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_BarePak_InstallsItAndRecordsTheRealPath()
    {
        var game = NewDir("game");
        var pak = Path.Combine(NewDir("paks"), "Library.pak");
        File.WriteAllText(pak, "LSPK-bytes");

        var result = await _service.InstallAsync(pak, game, new ModInstallationOptions
        {
            ModId = "library",
            AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        result.StagedFiles.Should().ContainSingle().Which.Should().EndWith("Library.pak");

        // The changeset points at the .pak, not at the temporary zip it was read through.
        var changeset = await _changesets.GetChangesetAsync(result.ChangesetId!);
        changeset!.ArchivePath.Should().Be(result.ArchivePath);
        Path.GetFileName(changeset.ArchivePath).Should().Be("Library.pak");
        File.ReadAllText(changeset.ArchivePath!).Should().Be("LSPK-bytes");
    }

    // ── archive store ───────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_ArchiveFromElsewhere_IsMovedIntoTheStore()
    {
        // An archive left in Downloads is gone by the time a snapshot restore wants it.
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        File.Exists(archive).Should().BeFalse();
        result.ArchivePath.Should().StartWith(_archiveBase + Path.DirectorySeparatorChar);
        File.Exists(result.ArchivePath).Should().BeTrue();

        var changeset = await _changesets.GetChangesetAsync(result.ChangesetId!);
        changeset!.ArchivePath.Should().Be(result.ArchivePath);
    }

    [Fact]
    public async Task InstallAsync_ArchiveInTheModsDirectory_StaysWhereItIs()
    {
        var modsDir = NewDir("mods");
        var service = new ModInstallationService(
            _database,
            stagingBasePath: Path.Combine(Path.GetDirectoryName(_archiveBase)!, "staging"),
            settings: new Modular.Core.Configuration.AppSettings { ModsDirectory = modsDir });

        var archive = Path.Combine(modsDir, "game-domain", "mod.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Move(CreateZip(("data/thing.dat", "payload")), archive);

        var result = await service.InstallAsync(archive, NewDir("game"), new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        result.ArchivePath.Should().Be(archive);
        File.Exists(archive).Should().BeTrue();
    }

    [Fact]
    public async Task InstallAsync_WithAModsDirectory_KeepsTheArchiveInsideIt()
    {
        var modsDir = NewDir("mods");
        var service = new ModInstallationService(
            _database,
            stagingBasePath: Path.Combine(Path.GetDirectoryName(_archiveBase)!, "staging"),
            settings: new Modular.Core.Configuration.AppSettings { ModsDirectory = modsDir });

        var archive = CreateZip(("data/thing.dat", "payload"));

        var result = await service.InstallAsync(archive, NewDir("game"), new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        File.Exists(archive).Should().BeFalse();
        result.ArchivePath.Should().StartWith(
            Path.Combine(modsDir, ArchiveStore.FolderName) + Path.DirectorySeparatorChar);
        File.Exists(result.ArchivePath).Should().BeTrue();

        // The store is not mistaken for a game's download folder.
        Modular.Core.Utilities.FileUtils.GetGameDomains(modsDir).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallAsync_ArchiveInsideTheGameFolder_IsCopiedNotMoved()
    {
        // A pak taken from an earlier deployment must not vanish from under the game.
        var game = NewDir("game");
        var archive = Path.Combine(game, "old-mod.zip");
        File.Move(CreateZip(("data/thing.dat", "payload")), archive);

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        File.Exists(archive).Should().BeTrue();
        result.ArchivePath.Should().StartWith(_archiveBase + Path.DirectorySeparatorChar);
        File.Exists(result.ArchivePath).Should().BeTrue();
    }

    [Fact]
    public async Task InstallAsync_SameNameDifferentArchive_KeepsBoth()
    {
        var game = NewDir("game");

        var first = await _service.InstallAsync(CreateZip(("a.dat", "one")), game,
            new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false });
        var second = await _service.InstallAsync(CreateZip(("b.dat", "two")), game,
            new ModInstallationOptions { ModId = "mod-b", AutoSnapshot = false });

        first.Success.Should().BeTrue(first.Error);
        second.Success.Should().BeTrue(second.Error);
        second.ArchivePath.Should().NotBe(first.ArchivePath);
        File.Exists(first.ArchivePath).Should().BeTrue();
        File.Exists(second.ArchivePath).Should().BeTrue();
    }

    [Fact]
    public async Task InstallAsync_AdoptingAnArchive_RepointsEarlierSnapshotsAtIt()
    {
        // A snapshot keeps its own copy of each archive path, and a restore reads that copy.
        var game = NewDir("game");
        var archive = CreateZip(("data/thing.dat", "payload"));

        var first = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false, AdoptArchive = false
        });
        first.Success.Should().BeTrue(first.Error);

        var snapshots = new Modular.Core.Snapshots.SnapshotManager(_database, _changesets);
        var snapshot = await snapshots.CreateSnapshotAsync(
            424242, "Test Game", game, Modular.Core.Snapshots.SnapshotTrigger.Manual,
            gameKey: first.GameKey);

        var second = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false
        });
        second.Success.Should().BeTrue(second.Error);

        var entries = await snapshots.GetSnapshotEntriesAsync(snapshot.SnapshotId);
        entries.Should().ContainSingle().Which.ArchivePath.Should().Be(second.ArchivePath);
    }

    [Fact]
    public async Task InstallAsync_AdoptArchiveOff_LeavesTheArchive()
    {
        var archive = CreateZip(("data/thing.dat", "payload"));

        var result = await _service.InstallAsync(archive, NewDir("game"), new ModInstallationOptions
        {
            ModId = "mod-a", AutoSnapshot = false, AdoptArchive = false
        });

        result.Success.Should().BeTrue(result.Error);
        result.ArchivePath.Should().Be(archive);
        File.Exists(archive).Should().BeTrue();
    }

    // ── reinstall ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_Reinstall_ListsTheModOnceAndSupersedesTheOldChangeset()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };

        var first = await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "v2")), game, options);

        first.Success.Should().BeTrue(first.Error);
        second.Success.Should().BeTrue(second.Error);

        var installed = await _service.ListInstalledAsync();
        installed.Should().ContainSingle().Which.ChangesetId.Should().Be(second.ChangesetId);

        (await _changesets.GetChangesetAsync(first.ChangesetId!))!.State
            .Should().Be(ChangesetState.Superseded);
    }

    [Fact]
    public async Task InstallAsync_ANewVersionInAnArchiveWithANewName_ReplacesTheModDeployingTheSameFiles()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { AutoSnapshot = false };

        var first = await _service.InstallAsync(Named(CreateZip(("data/thing.dat", "v1")), "Mod-1.0.zip"), game, options);
        var second = await _service.InstallAsync(Named(CreateZip(("data/thing.dat", "v2")), "Mod-1.1.zip"), game, options);

        first.Success.Should().BeTrue(first.Error);
        second.Success.Should().BeTrue(second.Error);
        second.ReplacedModId.Should().Be("Mod-1.0");

        var installed = await _service.ListInstalledAsync();
        installed.Should().ContainSingle().Which.ChangesetId.Should().Be(second.ChangesetId);
        (await _changesets.GetChangesetAsync(first.ChangesetId!))!.State
            .Should().Be(ChangesetState.Superseded);
        File.ReadAllText(Path.Combine(game, "data", "thing.dat")).Should().Be("v2");
    }

    [Fact]
    public async Task InstallAsync_AnIdThatIsOnlyTheArchiveName_StillReplacesTheModDeployingTheSameFiles()
    {
        // How the CLI and GUI call it: the id is filled in from the file name.
        var game = NewDir("game");

        await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game,
            new ModInstallationOptions { ModId = "Mod-1.0", MatchInstalledByFiles = true, AutoSnapshot = false });
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "v2")), game,
            new ModInstallationOptions { ModId = "Mod-1.1", MatchInstalledByFiles = true, AutoSnapshot = false });

        second.Success.Should().BeTrue(second.Error);
        second.ReplacedModId.Should().Be("Mod-1.0");
        (await _service.ListInstalledAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task InstallAsync_AnArchiveSharingOnlySomeFiles_IsANewMod()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { AutoSnapshot = false };

        await _service.InstallAsync(Named(CreateZip(("data/thing.dat", "base")), "Base.zip"), game, options);
        var patch = await _service.InstallAsync(
            Named(CreateZip(("data/thing.dat", "patched"), ("data/extra.dat", "more")), "Patch.zip"), game, options);

        patch.Success.Should().BeTrue(patch.Error);
        patch.ReplacedModId.Should().BeNull();
        (await _service.ListInstalledAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task InstallAsync_AGivenModId_IsKept_EvenWhenAnotherModDeploysTheSameFiles()
    {
        var game = NewDir("game");

        await _service.InstallAsync(CreateZip(("data/thing.dat", "a")), game,
            new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false });
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "b")), game,
            new ModInstallationOptions { ModId = "mod-b", AutoSnapshot = false });

        second.Success.Should().BeTrue(second.Error);
        second.ReplacedModId.Should().BeNull();
        (await _service.ListInstalledAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task InstallAsync_Reinstall_DeploysTheNewVersion()
    {
        // The rebuilt staging tree must reach the game, even though the old deployed file
        // still matches what was recorded for it.
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };

        await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "v2")), game, options);

        second.Success.Should().BeTrue(second.Error);
        File.ReadAllText(Path.Combine(game, "data", "thing.dat")).Should().Be("v2");
    }

    [Fact]
    public async Task ListInstalledAsync_RepairsAReinstallRecordedBeforeSuperseding()
    {
        // Databases written before this fix hold both changesets as committed.
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };

        var first = await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "v2")), game, options);
        await _changesets.UpdateStateAsync(first.ChangesetId!, ChangesetState.Committed);

        var installed = await _service.ListInstalledAsync();

        installed.Should().ContainSingle().Which.ChangesetId.Should().Be(second.ChangesetId);
        (await _changesets.GetChangesetAsync(first.ChangesetId!))!.State
            .Should().Be(ChangesetState.Superseded);
    }

    [Fact]
    public async Task UninstallAsync_ReplacedChangeset_LeavesTheReinstalledModAlone()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };

        var first = await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "v2")), game, options);
        await _changesets.UpdateStateAsync(first.ChangesetId!, ChangesetState.Committed);

        var result = await _service.UninstallAsync(first.ChangesetId!);

        result.Success.Should().BeFalse();
        File.ReadAllText(Path.Combine(game, "data", "thing.dat")).Should().Be("v2");
        Directory.Exists(second.StagingPath).Should().BeTrue();
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

    // ── failed and interrupted installs ─────────────────────────────────────

    [Fact]
    public async Task InstallAsync_AReinstallThatFails_LeavesTheInstalledVersionAlone()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };
        var first = await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);
        first.Success.Should().BeTrue(first.Error);

        // Stop the reinstall once its new staging exists: that used to be the installed
        // mod's own folder, already emptied.
        using var cts = new CancellationTokenSource();
        var stopAtCommit = new CancelOn(cts, "Installing files...");
        var second = await _service.InstallAsync(
            CreateZip(("data/thing.dat", "v2")), game, options, stopAtCommit, cts.Token);

        second.Success.Should().BeFalse();
        stopAtCommit.Fired.Should().BeTrue();
        File.ReadAllText(Path.Combine(first.StagingPath!, "game", "data", "thing.dat")).Should().Be("v1");
        File.ReadAllText(Path.Combine(game, "data", "thing.dat")).Should().Be("v1");
        Directory.GetDirectories(Path.GetDirectoryName(first.StagingPath!)!, "*.incoming-*").Should().BeEmpty();

        (await _service.ListInstalledAsync()).Should().ContainSingle()
            .Which.ChangesetId.Should().Be(first.ChangesetId);
    }

    [Fact]
    public async Task InstallAsync_AReinstall_LeavesNoSetAsideStaging()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };
        await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);
        var second = await _service.InstallAsync(CreateZip(("data/thing.dat", "v2")), game, options);

        second.Success.Should().BeTrue(second.Error);
        File.ReadAllText(Path.Combine(second.StagingPath!, "game", "data", "thing.dat")).Should().Be("v2");
        Directory.GetDirectories(Path.GetDirectoryName(second.StagingPath!)!)
            .Where(d => d.Contains(".incoming-") || d.Contains(".previous-"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task UninstallAsync_AnInstallKilledMidCommit_ClearsItsStagingAndRollsItBack()
    {
        var game = NewDir("game");
        var probe = await _service.InstallAsync(CreateZip(("x.dat", "x")), game,
            new ModInstallationOptions { ModId = "probe", AutoSnapshot = false, NoDeploy = true });
        var gameKey = probe.GameKey!;

        // What a process killed while committing leaves: no operations, staged files.
        var changesetId = await _changesets.CreateChangesetAsync(
            modId: "killed", archivePath: CreateZip(("a.dat", "a")), targetDirectory: game,
            managedBy: ChangesetOwnership.Deployment, gameKey: gameKey);
        await _changesets.UpdateStateAsync(changesetId, ChangesetState.Committing);
        var staging = Path.Combine(_stagingBase, gameKey, "killed");
        Directory.CreateDirectory(Path.Combine(staging, "game"));
        File.WriteAllText(Path.Combine(staging, "game", "a.dat"), "a");

        (await _service.ListInterruptedAsync(game)).Should().ContainSingle()
            .Which.ChangesetId.Should().Be(changesetId);

        var uninstall = await _service.UninstallAsync(changesetId);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        uninstall.StagingRemoved.Should().BeTrue();
        Directory.Exists(staging).Should().BeFalse();
        (await _changesets.GetChangesetAsync(changesetId))!.State.Should().Be(ChangesetState.RolledBack);
        (await _service.ListInterruptedAsync(game)).Should().BeEmpty();
    }

    [Fact]
    public async Task UninstallAsync_AReinstallKilledAfterSwappingIn_PutsTheInstalledVersionBack()
    {
        var game = NewDir("game");
        var options = new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false };
        var first = await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game, options);

        var changesetId = await _changesets.CreateChangesetAsync(
            modId: "mod-a", archivePath: CreateZip(("data/thing.dat", "v2")), targetDirectory: game,
            managedBy: ChangesetOwnership.Deployment, gameKey: first.GameKey);
        await _changesets.UpdateStateAsync(changesetId, ChangesetState.Committing);

        // Swapped in, not yet registered: the new tree holds the folder, the old waits aside.
        var current = first.StagingPath!;
        Directory.Move(current, current + ".previous-" + changesetId);
        Directory.CreateDirectory(Path.Combine(current, "game", "data"));
        File.WriteAllText(Path.Combine(current, "game", "data", "thing.dat"), "v2");

        var uninstall = await _service.UninstallAsync(changesetId);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        File.ReadAllText(Path.Combine(current, "game", "data", "thing.dat")).Should().Be("v1");
        Directory.Exists(current + ".previous-" + changesetId).Should().BeFalse();
        (await _service.ListInstalledAsync()).Should().ContainSingle()
            .Which.ChangesetId.Should().Be(first.ChangesetId);
    }

    [Fact]
    public async Task UninstallAsync_AnInterruptedInstallOfAnInstalledMod_KeepsThatModsStaging()
    {
        var game = NewDir("game");
        var first = await _service.InstallAsync(CreateZip(("data/thing.dat", "v1")), game,
            new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false });

        var changesetId = await _changesets.CreateChangesetAsync(
            modId: "mod-a", targetDirectory: game,
            managedBy: ChangesetOwnership.Deployment, gameKey: first.GameKey);
        await _changesets.UpdateStateAsync(changesetId, ChangesetState.Staging);

        var uninstall = await _service.UninstallAsync(changesetId);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        File.ReadAllText(Path.Combine(first.StagingPath!, "game", "data", "thing.dat")).Should().Be("v1");
        File.ReadAllText(Path.Combine(game, "data", "thing.dat")).Should().Be("v1");
    }

    [Fact]
    public async Task UninstallAsync_CountsOnlyTheRemovedModsFiles()
    {
        var game = NewDir("game");
        await _service.InstallAsync(CreateZip(("a/one.dat", "1"), ("a/two.dat", "2")), game,
            new ModInstallationOptions { ModId = "mod-a", AutoSnapshot = false });
        var b = await _service.InstallAsync(CreateZip(("b/three.dat", "3")), game,
            new ModInstallationOptions { ModId = "mod-b", AutoSnapshot = false });

        var uninstall = await _service.UninstallAsync(b.ChangesetId!);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        uninstall.FilesRemoved.Should().Be(1);
        File.Exists(Path.Combine(game, "a", "one.dat")).Should().BeTrue();
    }

    /// <summary>Cancels the install the moment it reports a given step.</summary>
    private sealed class CancelOn(CancellationTokenSource cts, string operation)
        : IProgress<Modular.Sdk.Installers.InstallProgress>
    {
        public bool Fired { get; private set; }

        public void Report(Modular.Sdk.Installers.InstallProgress value)
        {
            if (value.CurrentOperation != operation)
                return;
            Fired = true;
            cts.Cancel();
        }
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

    private string Named(string archive, string fileName)
    {
        var path = Path.Combine(NewDir("zip"), fileName);
        File.Move(archive, path);
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
