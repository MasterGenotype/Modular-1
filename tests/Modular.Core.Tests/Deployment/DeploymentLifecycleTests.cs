using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Database;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Core.Snapshots;
using Xunit;

namespace Modular.Core.Tests.Deployment;

/// <summary>
/// The ordered pipeline end to end: install registers a mod, reordering changes the winner,
/// uninstall removes it and redeploys, and a snapshot restore reaches its recorded state in
/// exactly one deployment.
/// </summary>
public sealed class DeploymentLifecycleTests : IAsyncLifetime
{
    private readonly List<string> _tempDirs = new();
    private ModularDatabase _database = null!;
    private ModInstallationService _service = null!;
    private ChangesetManager _changesets = null!;
    private SnapshotManager _snapshots = null!;
    private string _game = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var configRoot = NewDir("config");

        _database = new ModularDatabase(Path.Combine(NewDir("db"), "modular.db"));
        await _database.InitializeAsync();

        _changesets = new ChangesetManager(_database);
        _snapshots = new SnapshotManager(_database, _changesets);

        _service = new ModInstallationService(
            _database,
            snapshotManager: _snapshots,
            stagingBasePath: Path.Combine(configRoot, "staging"));

        _game = NewDir("game");
    }

    // ── registration and order ──────────────────────────────────────────────

    [Fact]
    public async Task Install_RegistersTheModInTheGamesDeployment()
    {
        var result = await InstallAsync("mod-a", ("data/a.txt", "a"));

        var mods = await _service.Deployment.Mods.ListAsync(result.GameKey!);

        mods.Should().ContainSingle();
        mods[0].ModId.Should().Be("mod-a");
        mods[0].OrderIndex.Should().Be(0);
        mods[0].ChangesetId.Should().Be(result.ChangesetId);
        mods[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Install_SecondMod_GoesLastInTheOrder()
    {
        // "Later wins" only means anything if a newly installed mod is later.
        await InstallAsync("mod-a", ("shared.txt", "from-a"));
        var second = await InstallAsync("mod-b", ("shared.txt", "from-b"));

        var mods = await _service.Deployment.Mods.ListAsync(second.GameKey!);

        mods.Select(m => m.ModId).Should().Equal("mod-a", "mod-b");
        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-b");
    }

    [Fact]
    public async Task Reinstall_KeepsItsPlaceInTheOrder()
    {
        await InstallAsync("mod-a", ("shared.txt", "from-a"));
        await InstallAsync("mod-b", ("shared.txt", "from-b"));
        var again = await InstallAsync("mod-a", ("shared.txt", "from-a-again"));

        var mods = await _service.Deployment.Mods.ListAsync(again.GameKey!);
        mods.Select(m => m.ModId).Should().Equal("mod-a", "mod-b");

        // mod-b is still later, so it still wins.
        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-b");
    }

    [Fact]
    public async Task SetOrder_ThenDeploy_FlipsTheWinner()
    {
        var first = await InstallAsync("mod-a", ("shared.txt", "from-a"));
        await InstallAsync("mod-b", ("shared.txt", "from-b"));

        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-b");

        await _service.Deployment.Mods.SetOrderAsync(first.GameKey!, ["mod-b", "mod-a"]);
        var deployment = await _service.DeployGameAsync(_game);

        deployment.Success.Should().BeTrue(deployment.Error);
        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-a");
    }

    [Fact]
    public async Task SetOrder_NamingOnlySomeMods_KeepsTheRestAfterThem()
    {
        var result = await InstallAsync("mod-a", ("a.txt", "a"));
        await InstallAsync("mod-b", ("b.txt", "b"));
        await InstallAsync("mod-c", ("c.txt", "c"));

        await _service.Deployment.Mods.SetOrderAsync(result.GameKey!, ["mod-c"]);

        var mods = await _service.Deployment.Mods.ListAsync(result.GameKey!);
        mods.Select(m => m.ModId).Should().Equal("mod-c", "mod-a", "mod-b");
    }

    [Fact]
    public async Task DisabledMod_IsNotDeployed()
    {
        var result = await InstallAsync("mod-a", ("a.txt", "a"));
        File.Exists(Path.Combine(_game, "a.txt")).Should().BeTrue();

        await _service.Deployment.Mods.SetEnabledAsync(result.GameKey!, "mod-a", enabled: false);
        var deployment = await _service.DeployGameAsync(_game);

        deployment.Success.Should().BeTrue(deployment.Error);
        File.Exists(Path.Combine(_game, "a.txt")).Should().BeFalse();
    }

    // ── uninstall ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Uninstall_ByChangeset_RemovesTheModAndRedeploysTheRest()
    {
        await InstallAsync("mod-a", ("shared.txt", "from-a"));
        var b = await InstallAsync("mod-b", ("shared.txt", "from-b"));

        var uninstall = await _service.UninstallAsync(b.ChangesetId!);

        uninstall.Success.Should().BeTrue(uninstall.Error);
        uninstall.StagingRemoved.Should().BeTrue();

        // mod-a wins again because it is the only mod left.
        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-a");

        var mods = await _service.Deployment.Mods.ListAsync(b.GameKey!);
        mods.Select(m => m.ModId).Should().Equal("mod-a");
    }

    [Fact]
    public async Task Uninstall_ByGameAndMod_TakesTheSamePath()
    {
        var a = await InstallAsync("mod-a", ("a.txt", "a"));

        var uninstall = await _service.UninstallModAsync(_game, "mod-a");

        uninstall.Success.Should().BeTrue(uninstall.Error);
        uninstall.ChangesetId.Should().Be(a.ChangesetId);
        File.Exists(Path.Combine(_game, "a.txt")).Should().BeFalse();

        (await _service.Deployment.Mods.ListAsync(a.GameKey!)).Should().BeEmpty();
    }

    [Fact]
    public async Task Uninstall_UnknownMod_IsReportedNotThrown()
    {
        await InstallAsync("mod-a", ("a.txt", "a"));

        var uninstall = await _service.UninstallModAsync(_game, "not-installed");

        uninstall.Success.Should().BeFalse();
        uninstall.Error.Should().Contain("not-installed");
    }

    [Fact]
    public async Task Uninstall_DeletesStagingOnlyAfterTheRedeploySucceeds()
    {
        var a = await InstallAsync("mod-a", ("a.txt", "a"));
        var stagingPath = a.StagingPath!;

        Directory.Exists(stagingPath).Should().BeTrue();

        await _service.UninstallAsync(a.ChangesetId!);

        Directory.Exists(stagingPath).Should().BeFalse();
    }

    // ── snapshots ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_CapturesTheDeployedModSet()
    {
        var result = await InstallAsync("mod-a", ("a.txt", "a"));
        await InstallAsync("mod-b", ("b.txt", "b"));

        var snapshot = await _snapshots.CreateSnapshotAsync(
            424242, "Test Game", _game, SnapshotTrigger.Manual,
            gameKey: result.GameKey);

        var captured = await _snapshots.GetSnapshotGameModsAsync(snapshot.SnapshotId);

        captured.Select(m => m.ModId).Should().Equal("mod-a", "mod-b");
        captured.Should().OnlyContain(m => m.StagingHash != null);
    }

    [Fact]
    public async Task RestoreSnapshot_ReachesTheRecordedStateInOneDeploy()
    {
        // Three mods captured, then a different two-mod state, then a restore. The restore
        // must not purge and redeploy once per mod.
        var a = await InstallAsync("mod-a", ("shared.txt", "from-a"));
        await InstallAsync("mod-b", ("shared.txt", "from-b"));
        await InstallAsync("mod-c", ("c.txt", "c"));

        var snapshot = await _snapshots.CreateSnapshotAsync(
            424242, "Test Game", _game, SnapshotTrigger.Manual, gameKey: a.GameKey);

        // Drift away from the snapshot: drop mod-c, add mod-d that also wins shared.txt.
        await _service.UninstallModAsync(_game, "mod-c");
        await InstallAsync("mod-d", ("shared.txt", "from-d"));

        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-d");
        File.Exists(Path.Combine(_game, "c.txt")).Should().BeFalse();

        var restore = await _snapshots.RestoreSnapshotAsync(snapshot.SnapshotId, _service);

        restore.Success.Should().BeTrue(string.Join("; ", restore.Errors));

        var mods = await _service.Deployment.Mods.ListAsync(a.GameKey!);
        mods.Select(m => m.ModId).Should().Equal("mod-a", "mod-b", "mod-c");

        // The snapshot's order decides the winner again, and mod-d is gone.
        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-b");
        File.ReadAllText(Path.Combine(_game, "c.txt")).Should().Be("c");
    }

    [Fact]
    public async Task RestoreSnapshot_ReStagesAModWhoseStagingIsGone()
    {
        var a = await InstallAsync("mod-a", ("a.txt", "a"));

        var snapshot = await _snapshots.CreateSnapshotAsync(
            424242, "Test Game", _game, SnapshotTrigger.Manual, gameKey: a.GameKey);

        // Simulate the staging folder being deleted between snapshot and restore.
        Directory.Delete(a.StagingPath!, recursive: true);
        File.Delete(Path.Combine(_game, "a.txt"));

        var restore = await _snapshots.RestoreSnapshotAsync(snapshot.SnapshotId, _service);

        restore.Success.Should().BeTrue(string.Join("; ", restore.Errors));
        restore.ModsReinstalled.Should().Be(1);
        File.ReadAllText(Path.Combine(_game, "a.txt")).Should().Be("a");
    }

    [Fact]
    public async Task InstallWithNoDeploy_TakesNoAutoSnapshot()
    {
        var before = (await _snapshots.ListSnapshotsByDateRangeAsync(
            424242, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))).Count;

        await InstallAsync("mod-a", noDeploy: true, files: [("a.txt", "a")]);

        var after = (await _snapshots.ListSnapshotsByDateRangeAsync(
            424242, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))).Count;

        after.Should().Be(before);
    }

    // ── deployment method ───────────────────────────────────────────────────

    [Fact]
    public async Task ResolveSettings_DefaultsToHardlinkFallingBackToCopy()
    {
        var settings = await _service.Deployment.ResolveSettingsAsync("424242");

        settings.Method.Should().Be(DeploymentMethod.Hardlink);
        settings.Fallback.Should().Be(DeploymentFallback.Copy);
        settings.Source.Should().Be("default");
    }

    [Fact]
    public async Task ResolveSettings_PerGameSettingBeatsTheDefault()
    {
        await _service.Deployment.Mods.SetDeploySettingsAsync(
            "424242", DeploymentMethod.Copy, DeploymentFallback.Copy);

        var settings = await _service.Deployment.ResolveSettingsAsync("424242");

        settings.Method.Should().Be(DeploymentMethod.Copy);
        settings.Source.Should().Be("per-game setting");
    }

    [Fact]
    public async Task ResolveSettings_FlagBeatsThePerGameSetting()
    {
        await _service.Deployment.Mods.SetDeploySettingsAsync(
            "424242", DeploymentMethod.Copy, DeploymentFallback.Copy);

        var settings = await _service.Deployment.ResolveSettingsAsync(
            "424242", DeploymentMethod.Symlink);

        settings.Method.Should().Be(DeploymentMethod.Symlink);
        settings.Source.Should().Be("--method");
    }

    [Fact]
    public async Task ChangingTheMethod_RebuildsTheDeployment()
    {
        // Identity records are not comparable across methods, so the change forces a purge
        // and a fresh deploy rather than an incremental one.
        var a = await InstallAsync("mod-a", ("a.txt", "a"));
        var installation = (await _service.Deployment.ResolveInstallationAsync(_game))!;

        var rebuilt = await _service.Deployment.SetDeploymentMethodAsync(
            installation, DeploymentMethod.Copy);

        rebuilt.Should().NotBeNull();
        rebuilt!.Success.Should().BeTrue(rebuilt.Error);

        var record = rebuilt.Manifest!.Find("game", "a.txt")!;
        record.Method.Should().Be("copy");

        File.ReadAllText(Path.Combine(_game, "a.txt")).Should().Be("a");
        a.GameKey.Should().Be(installation.GameKey);
    }

    [Fact]
    public void ProbeMethods_ReportsWhatWorksPerRoot()
    {
        var probe = _service.Deployment.ProbeMethods(new Sdk.Modules.GameInstallation
        {
            GameId = "testgame",
            SteamAppId = 424242,
            InstallPath = _game
        });

        probe.Should().ContainKey("game");

        // Copy works everywhere; that is the point of having it.
        probe["game"].Should().Contain(DeploymentMethod.Copy);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<ModInstallationResult> InstallAsync(
        string modId,
        params (string Path, string Content)[] files) =>
        await InstallAsync(modId, false, files);

    private async Task<ModInstallationResult> InstallAsync(
        string modId,
        bool noDeploy,
        params (string Path, string Content)[] files)
    {
        var archive = CreateZip(modId, files);

        var result = await _service.InstallAsync(archive, _game, new ModInstallationOptions
        {
            ModId = modId,
            AllowOverwrite = true,
            AutoSnapshot = false,
            NoDeploy = noDeploy
        });

        result.Success.Should().BeTrue(result.Error);
        return result;
    }

    private string CreateZip(string label, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(NewDir("zip"), label + ".zip");

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

/// <summary>
/// The guarantee that a snapshot restore reaches its recorded state in exactly one
/// deployment, rather than purging and redeploying once per mod.
/// </summary>
/// <remarks>
/// Kept separate because it needs a counting deployer wired in from the start, which the
/// rest of the lifecycle tests do not want.
/// </remarks>
public sealed class SnapshotRestoreDeployCountTests : IAsyncLifetime
{
    private readonly List<string> _tempDirs = new();
    private CountingDeployer _deployer = null!;
    private ModularDatabase _database = null!;
    private ModInstallationService _service = null!;
    private SnapshotManager _snapshots = null!;
    private string _game = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var configRoot = NewDir("config");

        _database = new ModularDatabase(Path.Combine(NewDir("db"), "modular.db"));
        await _database.InitializeAsync();

        var changesets = new ChangesetManager(_database);
        _snapshots = new SnapshotManager(_database, changesets);

        var stagingPath = Path.Combine(configRoot, "staging");
        var staging = new StagingManager(stagingPath);
        var registry = new Core.Modules.GameModuleRegistry();
        var resolver = new Core.GameDetection.GameInstallationResolver(registry: registry);

        _deployer = new CountingDeployer(new Deployer(new StagingLayout(stagingPath)));

        var deployment = new DeploymentService(
            _database, staging, resolver, deployer: _deployer);

        _service = new ModInstallationService(
            _database,
            snapshotManager: _snapshots,
            stagingBasePath: stagingPath,
            deployment: deployment);

        _game = NewDir("game");
    }

    [Fact]
    public async Task RestoreSnapshot_DeploysExactlyOnce()
    {
        var a = await InstallAsync("mod-a", ("shared.txt", "from-a"));
        await InstallAsync("mod-b", ("shared.txt", "from-b"));
        await InstallAsync("mod-c", ("c.txt", "c"));

        var snapshot = await _snapshots.CreateSnapshotAsync(
            424242, "Test Game", _game, SnapshotTrigger.Manual, gameKey: a.GameKey);

        await _service.UninstallModAsync(_game, "mod-c");
        await InstallAsync("mod-d", ("shared.txt", "from-d"));

        _deployer.Reset();

        var restore = await _snapshots.RestoreSnapshotAsync(snapshot.SnapshotId, _service);

        restore.Success.Should().BeTrue(string.Join("; ", restore.Errors));
        _deployer.DeployCount.Should().Be(1);
        _deployer.PurgeCount.Should().Be(0);

        File.ReadAllText(Path.Combine(_game, "shared.txt")).Should().Be("from-b");
    }

    [Fact]
    public async Task RestoreSnapshot_ReStagingDoesNotDeployPerMod()
    {
        // Re-staging happens with --no-deploy, so a restore that has to rebuild several
        // staging trees still deploys once at the end.
        var a = await InstallAsync("mod-a", ("a.txt", "a"));
        await InstallAsync("mod-b", ("b.txt", "b"));

        var snapshot = await _snapshots.CreateSnapshotAsync(
            424242, "Test Game", _game, SnapshotTrigger.Manual, gameKey: a.GameKey);

        foreach (var mod in await _snapshots.GetSnapshotGameModsAsync(snapshot.SnapshotId))
            Directory.Delete(mod.StagingPath, recursive: true);

        _deployer.Reset();

        var restore = await _snapshots.RestoreSnapshotAsync(snapshot.SnapshotId, _service);

        restore.Success.Should().BeTrue(string.Join("; ", restore.Errors));
        restore.ModsReinstalled.Should().Be(2);
        _deployer.DeployCount.Should().Be(1);
    }

    private async Task<ModInstallationResult> InstallAsync(
        string modId, params (string Path, string Content)[] files)
    {
        var archive = Path.Combine(NewDir("zip"), modId + ".zip");

        using (var zip = new System.IO.Compression.ZipArchive(
                   File.Create(archive), System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
                writer.Write(content);
            }
        }

        var result = await _service.InstallAsync(archive, _game, new ModInstallationOptions
        {
            ModId = modId,
            AllowOverwrite = true,
            AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
        return result;
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

    /// <summary>A real deployer that counts how often it is asked to work.</summary>
    private sealed class CountingDeployer(IDeployer inner) : IDeployer
    {
        public int DeployCount { get; private set; }

        public int PurgeCount { get; private set; }

        public void Reset()
        {
            DeployCount = 0;
            PurgeCount = 0;
        }

        public Task<DeploymentResult> DeployAsync(
            Sdk.Modules.GameInstallation installation,
            Sdk.Modules.IGameModule module,
            DeploymentPlan plan,
            DeploymentSettings settings,
            CancellationToken ct = default)
        {
            DeployCount++;
            return inner.DeployAsync(installation, module, plan, settings, ct);
        }

        public DeploymentResult Purge(Sdk.Modules.GameInstallation installation)
        {
            PurgeCount++;
            return inner.Purge(installation);
        }

        public Dictionary<string, List<DeploymentMethod>> ProbeAll(DeploymentPlan plan) =>
            inner.ProbeAll(plan);
    }
}
