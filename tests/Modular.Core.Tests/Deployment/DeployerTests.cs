using FluentAssertions;
using Modular.Core.Deployment;
using Modular.Core.Modules;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Deployment;

/// <summary>
/// The deployer across all three placement methods: what it places, what it refuses to
/// destroy, and what a reorder or a game update changes.
/// </summary>
/// <remarks>
/// The purge, redeploy and reorder cases run once per method as a theory, because each
/// method recognises its own files differently and a bug in one identity check would
/// otherwise hide behind another.
/// </remarks>
public sealed class DeployerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public static TheoryData<DeploymentMethod> AllMethods => new()
    {
        DeploymentMethod.Hardlink,
        DeploymentMethod.Symlink,
        DeploymentMethod.Copy
    };

    // ── placement ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Deploy_PlacesStagedFilesIntoTheGameRoot(DeploymentMethod method)
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/archive/pc/mod/a.archive", "a-bytes"));

        var result = await fixture.DeployAsync(method, "mod-a");

        result.Success.Should().BeTrue(result.Error);
        result.Placed.Should().Contain("game/archive/pc/mod/a.archive");

        File.ReadAllText(fixture.GamePath("archive/pc/mod/a.archive")).Should().Be("a-bytes");
    }

    [Fact]
    public async Task Deploy_Hardlink_SharesTheInodeWithTheStagedFile()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "shared"));

        await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        // A hardlink is a second name for the same data, so writing through the staged
        // path is visible at the game path.
        File.WriteAllText(fixture.StagedPath("mod-a", "game/a.txt"), "changed");
        File.ReadAllText(fixture.GamePath("a.txt")).Should().Be("changed");
    }

    [Fact]
    public async Task Redeploy_AHardlinkWithAStaleRecordedInode_IsKeptNotPreserved()
    {
        // Seen on a real install: every recorded device:inode had gone stale while the links
        // were untouched, and a redeploy moved every deployed file into the overwrite store.
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");
        var actual = fixture.CorruptRecordedInodes();

        var result = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Success.Should().BeTrue(result.Error);
        result.Displaced.Should().BeEmpty();
        result.Unchanged.Should().Contain("game/a.txt");
        fixture.OverwriteHas("game/a.txt").Should().BeFalse();
        fixture.LoadManifest().Files.Single().Inode.Should().Be(actual.Single());
    }

    [Fact]
    public async Task Removing_AHardlinkWithAStaleRecordedInode_DeletesItNotPreserves()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        fixture.StageMod("mod-b", ("game/b.txt", "b"));
        await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a", "mod-b");
        fixture.CorruptRecordedInodes();

        var result = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Success.Should().BeTrue(result.Error);
        result.Displaced.Should().BeEmpty();
        File.Exists(fixture.GamePath("b.txt")).Should().BeFalse();
        fixture.OverwriteHas("game/b.txt").Should().BeFalse();
    }

    [Fact]
    public async Task Redeploy_AHardlinkReplacedByARealFile_IsStillPreserved()
    {
        // The stale-record allowance must not swallow a real edit: an atomic save breaks
        // the link, so the file no longer shares the staged inode.
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");
        fixture.CorruptRecordedInodes();

        var temp = fixture.GamePath("a.txt.tmp");
        File.WriteAllText(temp, "the user's own edit");
        File.Move(temp, fixture.GamePath("a.txt"), overwrite: true);

        var result = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Displaced.Should().Contain("game/a.txt");
        fixture.OverwriteHas("game/a.txt").Should().BeTrue();
    }

    [Fact]
    public async Task ForcedRedeploy_LaysEveryFileDownAgain()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"), ("game/sub/b.txt", "b"));
        var first = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");
        first.Placed.Should().HaveCount(2);

        var result = await fixture.ForceDeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Success.Should().BeTrue(result.Error);
        result.Unchanged.Should().BeEmpty();
        result.Placed.Should().BeEquivalentTo("game/a.txt", "game/sub/b.txt");
        result.Displaced.Should().BeEmpty();
        File.ReadAllText(fixture.GamePath("sub/b.txt")).Should().Be("b");

        var again = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");
        again.Unchanged.Should().BeEquivalentTo("game/a.txt", "game/sub/b.txt");
    }

    [Fact]
    public async Task ForcedRedeploy_OfModularsOwnLinks_PreservesNothing()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");
        fixture.CorruptRecordedInodes();

        var result = await fixture.ForceDeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Placed.Should().Contain("game/a.txt");
        result.Displaced.Should().BeEmpty();
        fixture.OverwriteHas("game/a.txt").Should().BeFalse();
    }

    [Fact]
    public async Task ForcedRedeploy_StillPreservesAFileTheUserEdited()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        var temp = fixture.GamePath("a.txt.tmp");
        File.WriteAllText(temp, "the user's own edit");
        File.Move(temp, fixture.GamePath("a.txt"), overwrite: true);

        var result = await fixture.ForceDeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Placed.Should().Contain("game/a.txt");
        result.Displaced.Should().Contain("game/a.txt");
        fixture.OverwriteHas("game/a.txt").Should().BeTrue();
        File.ReadAllText(fixture.GamePath("a.txt")).Should().Be("a");
    }

    [Fact]
    public async Task Deploy_Symlink_PointsAtTheAbsoluteStagedPath()
    {
        // Relative targets break as soon as two roots live in different trees, which is
        // the normal case once a Documents root is involved.
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "linked"));

        await fixture.DeployAsync(DeploymentMethod.Symlink, "mod-a");

        new FileInfo(fixture.GamePath("a.txt")).LinkTarget
            .Should().Be(fixture.StagedPath("mod-a", "game/a.txt"));
    }

    [Fact]
    public async Task Deploy_Copy_LeavesTheStagedFileIndependent()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "copied"));

        await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a");

        File.WriteAllText(fixture.StagedPath("mod-a", "game/a.txt"), "changed");
        File.ReadAllText(fixture.GamePath("a.txt")).Should().Be("copied");
    }

    // ── last writer wins ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Deploy_TwoModsSharingAPath_TheLaterOneWins(DeploymentMethod method)
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/shared.txt", "from-a"));
        fixture.StageMod("mod-b", ("game/shared.txt", "from-b"));

        var result = await fixture.DeployAsync(method, "mod-a", "mod-b");

        File.ReadAllText(fixture.GamePath("shared.txt")).Should().Be("from-b");

        result.Plan!.Overlaps.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new DeploymentOverlap("game", "shared.txt", "mod-a", "mod-b"));
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Redeploy_AfterAReorder_FlipsTheWinningFile(DeploymentMethod method)
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/shared.txt", "from-a"));
        fixture.StageMod("mod-b", ("game/shared.txt", "from-b"));

        await fixture.DeployAsync(method, "mod-a", "mod-b");
        File.ReadAllText(fixture.GamePath("shared.txt")).Should().Be("from-b");

        await fixture.DeployAsync(method, "mod-b", "mod-a");
        File.ReadAllText(fixture.GamePath("shared.txt")).Should().Be("from-a");
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Redeploy_WithNoChanges_ReportsNoConflictsAndRewritesNothing(
        DeploymentMethod method)
    {
        // Redeploying an unchanged set used to report every file as conflicting with
        // Modular's own previous output.
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"), ("game/b.txt", "b"));

        await fixture.DeployAsync(method, "mod-a");
        var second = await fixture.DeployAsync(method, "mod-a");

        second.Success.Should().BeTrue(second.Error);
        second.Plan!.Overlaps.Should().BeEmpty();
        second.ReplacedUnmanaged.Should().BeEmpty();
        second.Unchanged.Should().HaveCount(2);
        second.Placed.Should().BeEmpty();
    }

    // ── purge ───────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Purge_RemovesDeployedFilesAndRestoresTheOriginals(DeploymentMethod method)
    {
        var fixture = NewFixture();
        fixture.WriteGameFile("vanilla.txt", "vanilla-bytes");
        fixture.StageMod("mod-a", ("game/vanilla.txt", "modded"), ("game/new.txt", "new"));

        await fixture.DeployAsync(method, "mod-a");
        File.ReadAllText(fixture.GamePath("vanilla.txt")).Should().Be("modded");

        var purge = fixture.Purge();

        purge.Removed.Should().HaveCount(2);
        File.ReadAllText(fixture.GamePath("vanilla.txt")).Should().Be("vanilla-bytes");
        File.Exists(fixture.GamePath("new.txt")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Purge_LeavesTheStagedBytesIntact(DeploymentMethod method)
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "staged-bytes"));

        await fixture.DeployAsync(method, "mod-a");
        fixture.Purge();

        File.ReadAllText(fixture.StagedPath("mod-a", "game/a.txt")).Should().Be("staged-bytes");
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task Purge_AFileEditedSinceDeployment_IsPreservedNotDeleted(
        DeploymentMethod method)
    {
        // An in-place edit, or an editor's atomic save replacing our link with a real
        // file, is the user's work. Deleting it silently would be data loss.
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/edited.txt", "deployed"));

        await fixture.DeployAsync(method, "mod-a");

        // Simulate an atomic save: a temp file renamed over the deployed path. This
        // breaks a hardlink, replaces a symlink with a regular file, and changes a copy's
        // content — all three identity checks must notice.
        var temp = fixture.GamePath("edited.txt.tmp");
        File.WriteAllText(temp, "the user's own edit");
        File.Move(temp, fixture.GamePath("edited.txt"), overwrite: true);

        var purge = fixture.Purge();

        purge.Displaced.Should().Contain("game/edited.txt");
        File.Exists(fixture.GamePath("edited.txt")).Should().BeFalse();

        var preserved = Path.Combine(
            fixture.Layout.GetOverwriteDirectory(fixture.Installation.GameKey),
            "game", "edited.txt");
        File.ReadAllText(preserved).Should().Be("the user's own edit");
    }

    [Fact]
    public async Task Purge_GeneratedPaths_AreNeverTouched()
    {
        // r6/cache is rewritten by redscript at launch, and a CET mod's sqlite database is
        // the user's state. Neither is Modular's to remove.
        var fixture = NewFixture(new GeneratedPathsModule());
        fixture.StageMod("mod-a",
            ("game/r6/cache/final.redscripts", "compiled"),
            ("game/archive/pc/mod/a.archive", "a"));

        fixture.WriteGameFile("r6/cache/final.redscripts", "tool output");
        var result = await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a");

        // What the tool wrote is not replaced by the mod's copy...
        result.Seeded.Should().BeEmpty();
        File.ReadAllText(fixture.GamePath("r6/cache/final.redscripts")).Should().Be("tool output");

        // ...nor removed.
        var purge = fixture.Purge();

        purge.Removed.Should().NotContain("game/r6/cache/final.redscripts");
        File.ReadAllText(fixture.GamePath("r6/cache/final.redscripts")).Should().Be("tool output");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Deploy_AGeneratedFileAModShips_IsSeededWhereTheGameHasNone(string? existing)
    {
        // AMM ships its database filled; without it AMM fails with "no such table". An
        // empty file is what CET leaves behind when the mod opened a database that was
        // not there.
        var fixture = NewFixture(new GeneratedPathsModule());
        fixture.StageMod("mod-a", ("game/r6/cache/db.sqlite3", "filled"));
        if (existing != null)
            fixture.WriteGameFile("r6/cache/db.sqlite3", existing);

        var result = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Seeded.Should().Equal("game/r6/cache/db.sqlite3");
        File.ReadAllText(fixture.GamePath("r6/cache/db.sqlite3")).Should().Be("filled");

        // A copy, so the game's writes stay out of staging, and never purged.
        File.WriteAllText(fixture.GamePath("r6/cache/db.sqlite3"), "user state");
        File.ReadAllText(fixture.StagedPath("mod-a", "game/r6/cache/db.sqlite3")).Should().Be("filled");
        fixture.Purge();
        File.ReadAllText(fixture.GamePath("r6/cache/db.sqlite3")).Should().Be("user state");
    }

    [Fact]
    public async Task Purge_AfterAGameUpdate_DoesNotRestoreStaleBackups()
    {
        // Backups belong to the build they were taken from. Restoring one over an updated
        // game file would corrupt the install in a way that looks like a mod bug.
        var fixture = NewFixture();
        fixture.WriteGameFile("patched.txt", "build-100");
        fixture.StageMod("mod-a", ("game/patched.txt", "modded"));

        await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a");

        // The game updates, and Steam rewrites the file we had modded.
        fixture.SetBuildId("200");
        File.WriteAllText(fixture.GamePath("patched.txt"), "build-200");

        var purge = fixture.Purge();

        purge.StaleBackupsSkipped.Should().Contain("game/patched.txt");
        File.ReadAllText(fixture.GamePath("patched.txt")).Should().Be("build-200");
        purge.Warnings.Should().Contain(w => w.Contains("updated"));
    }

    // ── unmanaged files ─────────────────────────────────────────────────────

    [Fact]
    public async Task Deploy_OverAnUnmanagedFile_ReportsItAndBacksItUp()
    {
        var fixture = NewFixture();
        fixture.WriteGameFile("readme.txt", "shipped with the game");
        fixture.StageMod("mod-a", ("game/readme.txt", "from the mod"));

        var result = await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a");

        result.ReplacedUnmanaged.Should().Contain("game/readme.txt");
        result.VanillaBackedUp.Should().Contain("game/readme.txt");
        result.Plan!.UnmanagedReplacements.Should().ContainSingle();

        // Not reported as a mod conflict: no other mod is involved.
        result.Plan.Overlaps.Should().BeEmpty();
    }

    // ── fallback and probing ────────────────────────────────────────────────

    [Fact]
    public async Task Deploy_MethodUnavailable_FallsBackToCopy()
    {
        var fixture = NewFixture(strategies:
        [
            new CrossDeviceStrategy(DeploymentMethod.Hardlink),
            new SymlinkDeploymentStrategy(),
            new CopyDeploymentStrategy()
        ]);

        fixture.StageMod("mod-a", ("game/a.txt", "payload"));

        var result = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Success.Should().BeTrue(result.Error);
        result.FellBackToCopy.Should().Contain("game/a.txt");
        File.ReadAllText(fixture.GamePath("a.txt")).Should().Be("payload");

        var record = result.Manifest!.Find("game", "a.txt")!;
        record.Method.Should().Be("copy");
        record.Requested.Should().Be("hardlink");
    }

    [Fact]
    public async Task Deploy_MethodUnavailableWithFailFallback_TouchesNothing()
    {
        var fixture = NewFixture(strategies:
        [
            new CrossDeviceStrategy(DeploymentMethod.Hardlink),
            new SymlinkDeploymentStrategy(),
            new CopyDeploymentStrategy()
        ]);

        fixture.StageMod("mod-a", ("game/a.txt", "payload"));

        var result = await fixture.DeployAsync(
            DeploymentMethod.Hardlink, DeploymentFallback.Fail, "mod-a");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("fail");
        File.Exists(fixture.GamePath("a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Deploy_PreflightProbeFailure_IsReportedBeforeAnyFileIsPlaced()
    {
        var fixture = NewFixture(strategies:
        [
            new CrossDeviceStrategy(DeploymentMethod.Hardlink),
            new SymlinkDeploymentStrategy(),
            new CopyDeploymentStrategy()
        ]);

        fixture.StageMod("mod-a", ("game/a.txt", "payload"));

        var result = await fixture.DeployAsync(DeploymentMethod.Hardlink, "mod-a");

        result.Warnings.Should().Contain(w =>
            w.Contains("game") && w.Contains("will be copied"));
    }

    [Fact]
    public async Task Deploy_ModuleConstraint_ForcesCopyForMatchingFilesOnly()
    {
        var fixture = NewFixture(new ForceCopyForDllsModule());
        fixture.StageMod("mod-a", ("game/plugin.dll", "dll"), ("game/data.bin", "bin"));

        var result = await fixture.DeployAsync(DeploymentMethod.Symlink, "mod-a");

        result.ConstraintApplied.Should().ContainSingle()
            .Which.Should().Contain("plugin.dll");

        result.Manifest!.Find("game", "plugin.dll")!.Method.Should().Be("copy");
        result.Manifest.Find("game", "data.bin")!.Method.Should().Be("symlink");
    }

    // ── manifest ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deploy_WritesAManifestNamingEveryDeployedFile()
    {
        var fixture = NewFixture();
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        fixture.StageMod("mod-b", ("game/b.txt", "b"));

        var result = await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a", "mod-b");

        var manifestPath = DeploymentManifest.PathFor(
            fixture.Layout.GetGameStagingDirectory(fixture.Installation.GameKey));
        File.Exists(manifestPath).Should().BeTrue();

        var reloaded = DeploymentManifest.Load(
            fixture.Layout.GetGameStagingDirectory(fixture.Installation.GameKey),
            fixture.Installation.GameKey);

        reloaded.ModOrder.Should().Equal("mod-a", "mod-b");
        reloaded.Files.Should().HaveCount(2);
        reloaded.BuildId.Should().Be(result.Manifest!.BuildId);
        reloaded.Roots.Should().ContainKey("game");
    }

    // ── module hooks ────────────────────────────────────────────────────────

    [Fact]
    public async Task Deploy_MapDeployPath_CanRenameAndReroute()
    {
        var fixture = NewFixture(new PrefixingModule());
        fixture.StageMod("mod-a", ("game/paks/a_P.pak", "pak-a"));
        fixture.StageMod("mod-b", ("game/paks/b_P.pak", "pak-b"));

        var result = await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a", "mod-b");

        result.Placed.Should().BeEquivalentTo(
            ["game/paks/000_a_P.pak", "game/paks/001_b_P.pak"]);
    }

    [Fact]
    public async Task Deploy_PinnedLastFile_KeepsItsNameAndIsNotOrderPrefixed()
    {
        // A loader pak relies on its own name to sort last; prefixing it would drag it
        // into the ordered band and could break the loader.
        var fixture = NewFixture(new PrefixingModule());
        fixture.StageMod("mod-a", ("game/paks/a_P.pak", "pak-a"));
        fixture.StageMod("loader", ("game/paks/ZGameInstanceLoader.pak", "loader"));

        var result = await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a", "loader");

        result.Placed.Should().Contain("game/paks/ZGameInstanceLoader.pak");
        result.Placed.Should().NotContain(p => p.Contains("001_ZGameInstanceLoader"));

        // Pinned files come after every numbered one, so the engine's alphabetical mount
        // order matches the intent.
        result.Manifest!.Files.Last().Path.Should().Be("paks/ZGameInstanceLoader.pak");
    }

    [Fact]
    public async Task Deploy_LoadOrderManifest_IsWrittenAfterThePlacement()
    {
        var module = new LoadOrderWritingModule();
        var fixture = NewFixture(module);
        fixture.StageMod("mod-a", ("game/a.txt", "a"));
        fixture.StageMod("mod-b", ("game/b.txt", "b"));

        await fixture.DeployAsync(DeploymentMethod.Copy, "mod-a", "mod-b");

        module.WrittenOrder.Should().Equal("mod-a", "mod-b");
        module.FilesPresentWhenWritten.Should().BeTrue(
            "the manifest describes files that must already exist");
    }

    // ── fixture ─────────────────────────────────────────────────────────────

    private DeployFixture NewFixture(
        IGameModule? module = null,
        IEnumerable<IDeploymentStrategy>? strategies = null)
    {
        var root = NewDir("deploy");
        return new DeployFixture(root, module ?? new GenericGameModule(), strategies);
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

    /// <summary>
    /// A game directory, a staging root and a deployer, wired together.
    /// </summary>
    private sealed class DeployFixture
    {
        private readonly IGameModule _module;
        private readonly DeploymentPlanner _planner = new();
        private readonly Deployer _deployer;
        private string _buildId = "100";

        public DeployFixture(
            string root, IGameModule module, IEnumerable<IDeploymentStrategy>? strategies)
        {
            _module = module;

            GameDirectory = Path.Combine(root, "game");
            Directory.CreateDirectory(GameDirectory);

            Layout = new StagingLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "backups"));
            Directory.CreateDirectory(Layout.BasePath);

            _deployer = new Deployer(Layout, strategies);
        }

        public string GameDirectory { get; }

        public StagingLayout Layout { get; }

        public GameInstallation Installation => new()
        {
            GameId = "testgame",
            SteamAppId = 424242,
            InstallPath = GameDirectory,
            BuildId = _buildId,
            DisplayName = "Test Game",
            ModuleId = _module.ModuleId
        };

        public void SetBuildId(string buildId) => _buildId = buildId;

        public string GamePath(string relative) =>
            Path.Combine(GameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

        public string StagedPath(string modId, string rootRelative) =>
            Path.Combine(
                Layout.GetGameStagingDirectory(Installation.GameKey),
                modId,
                rootRelative.Replace('/', Path.DirectorySeparatorChar));

        public void WriteGameFile(string relative, string content)
        {
            var path = GamePath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void StageMod(string modId, params (string Path, string Content)[] files)
        {
            foreach (var (path, content) in files)
            {
                var full = StagedPath(modId, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
        }

        public Task<DeploymentResult> DeployAsync(DeploymentMethod method, params string[] modOrder) =>
            DeployAsync(method, DeploymentFallback.Copy, modOrder);

        public Task<DeploymentResult> DeployAsync(
            DeploymentMethod method, DeploymentFallback fallback, params string[] modOrder) =>
            DeployAsync(method, fallback, force: false, modOrder);

        public Task<DeploymentResult> ForceDeployAsync(DeploymentMethod method, params string[] modOrder) =>
            DeployAsync(method, DeploymentFallback.Copy, force: true, modOrder);

        private async Task<DeploymentResult> DeployAsync(
            DeploymentMethod method, DeploymentFallback fallback, bool force, string[] modOrder)
        {
            var installation = Installation;
            var modPaths = _module.ResolveModPaths(installation);

            var inputs = modOrder.Select((modId, index) =>
            {
                var stagingPath = Path.Combine(
                    Layout.GetGameStagingDirectory(installation.GameKey), modId);

                return new DeploymentModInput(
                    modId,
                    stagingPath,
                    index,
                    Installers.StagedFilesOf(stagingPath));
            }).ToList();

            var current = DeploymentManifest.Load(
                Layout.GetGameStagingDirectory(installation.GameKey), installation.GameKey);

            var plan = _planner.Plan(installation, _module, modPaths, inputs, current);

            DeploymentResult result;
            try
            {
                result = await _deployer.DeployAsync(
                    installation, _module, plan,
                    new DeploymentSettings(method, fallback, "test") { Force = force });
            }
            catch (DeploymentAbortedException ex)
            {
                return new DeploymentResult { Error = ex.Message };
            }

            result.Plan = plan;
            return result;
        }

        public DeploymentResult Purge() => _deployer.Purge(Installation);

        public DeploymentManifest LoadManifest() =>
            DeploymentManifest.Load(
                Layout.GetGameStagingDirectory(Installation.GameKey), Installation.GameKey);

        // Rewrites every recorded inode to one no file has, and returns the real ones.
        public List<string?> CorruptRecordedInodes()
        {
            var manifest = LoadManifest();
            var actual = manifest.Files.Select(f => f.Inode).ToList();

            foreach (var file in manifest.Files)
                file.Inode = "0:1";

            manifest.Save(Layout.GetGameStagingDirectory(Installation.GameKey));
            return actual;
        }

        public bool OverwriteHas(string rootRelative) =>
            File.Exists(Path.Combine(
                Layout.GetOverwriteDirectory(Installation.GameKey),
                rootRelative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Reads a staged tree the way the real pipeline does.</summary>
    private static class Installers
    {
        public static IReadOnlyList<StagedFile> StagedFilesOf(string stagingPath) =>
            Core.Installers.StagingManager.EnumerateStagedFiles(stagingPath);
    }

    // ── test modules and strategies ─────────────────────────────────────────

    /// <summary>
    /// Base for the test modules.
    /// </summary>
    /// <remarks>
    /// The optional hooks are declared here as virtual no-ops rather than left to the
    /// interface's default implementations: interface mapping is fixed at the class that
    /// declares it implements the interface, so a hook added only in a derived class would
    /// never be called.
    /// </remarks>
    private abstract class TestModule : IGameModule
    {
        public abstract string ModuleId { get; }
        public string DisplayName => ModuleId;
        public IReadOnlyList<string> GameIds { get; } = ["testgame"];
        public IReadOnlyList<int> SteamAppIds { get; } = [424242];
        public GameModuleMatch? Detect(string installPath) => null;
        public GameModPaths ResolveModPaths(GameInstallation installation) =>
            GameModPaths.ForInstallation(installation);
        public IReadOnlyList<IModInstaller> GetInstallers() => Array.Empty<IModInstaller>();

        public virtual IReadOnlyList<string> GetGeneratedPaths() => Array.Empty<string>();

        public virtual IReadOnlyList<DeployConstraint> GetDeployConstraints() =>
            Array.Empty<DeployConstraint>();

        public virtual DeployTarget MapDeployPath(DeployPathContext context) =>
            new(context.RootName, context.RelativePath);

        public virtual Task WriteLoadOrderAsync(
            GameInstallation installation,
            IReadOnlyList<DeployedMod> mods,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class GeneratedPathsModule : TestModule
    {
        public override string ModuleId => "generated-paths";

        public override IReadOnlyList<string> GetGeneratedPaths() => ["r6/cache/**"];
    }

    private sealed class ForceCopyForDllsModule : TestModule
    {
        public override string ModuleId => "force-copy";

        public override IReadOnlyList<DeployConstraint> GetDeployConstraints() =>
        [
            new("*.dll", ForceCopy: true, DisallowSymlink: true, "links break this loader")
        ];
    }

    private sealed class PrefixingModule : TestModule
    {
        public override string ModuleId => "prefixing";

        public override DeployTarget MapDeployPath(DeployPathContext context)
        {
            if (!context.RelativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                return new DeployTarget(context.RootName, context.RelativePath);

            var name = Path.GetFileName(context.RelativePath);
            var folder = Path.GetDirectoryName(context.RelativePath)?.Replace('\\', '/') ?? string.Empty;

            // A loader pak relies on its own name to sort last.
            if (name.StartsWith("ZGameInstanceLoader", StringComparison.OrdinalIgnoreCase))
                return new DeployTarget(context.RootName, context.RelativePath, PinnedLast: true);

            var prefixed = $"{context.OrderIndex:D3}_{name}";
            return new DeployTarget(
                context.RootName,
                folder.Length == 0 ? prefixed : $"{folder}/{prefixed}");
        }
    }

    private sealed class LoadOrderWritingModule : TestModule
    {
        public override string ModuleId => "load-order";

        public List<string> WrittenOrder { get; } = new();

        public bool FilesPresentWhenWritten { get; private set; }

        public override Task WriteLoadOrderAsync(
            GameInstallation installation,
            IReadOnlyList<DeployedMod> mods,
            CancellationToken ct = default)
        {
            WrittenOrder.AddRange(mods.Select(m => m.ModId));

            FilesPresentWhenWritten = mods
                .SelectMany(m => m.DeployedFiles)
                .All(f => File.Exists(Path.Combine(
                    installation.InstallPath,
                    f.RelativePath.Replace('/', Path.DirectorySeparatorChar))));

            return Task.CompletedTask;
        }
    }

    /// <summary>A strategy whose placement always reports a cross-device failure.</summary>
    private sealed class CrossDeviceStrategy(DeploymentMethod method) : IDeploymentStrategy
    {
        public DeploymentMethod Method => method;

        public FileIdentity Place(string stagedPath, string destinationPath) =>
            throw new DeploymentMethodUnavailableException(
                method, "staging and the target are on different devices");

        public FileIdentity? ReadIdentity(string destinationPath) => null;

        public bool Matches(FileIdentity recorded, FileIdentity current) => false;

        public bool ReflectsStaged(string stagedPath, FileIdentity recorded) => false;

        public void Remove(string destinationPath) => DeploymentFile.RemoveEntry(destinationPath);

        public string? Probe(string stagingRoot, string destinationRoot) =>
            "staging and the target are on different devices";
    }
}
