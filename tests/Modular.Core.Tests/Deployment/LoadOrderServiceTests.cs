using System.IO.Compression;
using FluentAssertions;
using Modular.Core.Database;
using Modular.Core.Deployment;
using Modular.Core.Installers;
using Modular.Core.Modules;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Deployment;

/// <summary>
/// <see cref="LoadOrderService"/> over each kind of load order: a game with one order, a
/// load order Modular keeps and renders, and one the game keeps in a file of its own.
/// </summary>
public sealed class LoadOrderServiceTests : IAsyncLifetime
{
    private readonly List<string> _tempDirs = new();
    private readonly ExternalTestModule _external = new();
    private ModularDatabase _database = null!;
    private ModInstallationService _service = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = new ModularDatabase(Path.Combine(NewDir("db"), "modular.db"));
        await _database.InitializeAsync();

        var registry = new GameModuleRegistry();
        registry.Register(new ManagedTestModule());
        registry.Register(_external);

        _service = new ModInstallationService(
            _database,
            moduleRegistry: registry,
            stagingBasePath: Path.Combine(NewDir("config"), "staging"));
    }

    // ── one order ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AGameWithOneOrder_ShowsTheInstallOrderAsItsLoadOrder()
    {
        var game = NewDir("generic");
        await InstallAsync(game, "mod-a", ("a.txt", "a"));
        await InstallAsync(game, "mod-b", ("b.txt", "b"));

        var orders = await _service.LoadOrder.GetAsync(await Resolve(game));

        orders.Kind.Should().Be(LoadOrderKind.None);
        orders.Install.Select(i => i.Key).Should().Equal("mod-a", "mod-b");
        orders.Load.Should().BeSameAs(orders.Install);
    }

    [Fact]
    public async Task AGameWithOneOrder_RefusesALoadOrderMove()
    {
        var game = NewDir("generic");
        await InstallAsync(game, "mod-a", ("a.txt", "a"));
        await InstallAsync(game, "mod-b", ("b.txt", "b"));

        var installation = await Resolve(game);
        var act = () => _service.LoadOrder.MoveAsync(
            installation, OrderListKind.Load, "mod-b", OrderPlacement.Top);

        (await act.Should().ThrowAsync<LoadOrderException>()).WithMessage("*one order, the install order*");
    }

    [Fact]
    public async Task AnInstallOrderMove_ChangesWhichModsFileWins()
    {
        var game = NewDir("generic");
        await InstallAsync(game, "mod-a", ("shared.txt", "from-a"));
        await InstallAsync(game, "mod-b", ("shared.txt", "from-b"));
        File.ReadAllText(Path.Combine(game, "shared.txt")).Should().Be("from-b");

        var result = await _service.LoadOrder.MoveAsync(
            await Resolve(game), OrderListKind.Install, "mod-a", OrderPlacement.After("mod-b"));

        result.Changed.Should().BeTrue();
        result.Order.Select(i => i.Key).Should().Equal("mod-b", "mod-a");
        result.Deployment!.Success.Should().BeTrue(result.Deployment.Error);
        File.ReadAllText(Path.Combine(game, "shared.txt")).Should().Be("from-a");
    }

    [Fact]
    public async Task AMoveToWhereItAlreadyIs_ChangesNothing_AndDoesNotDeploy()
    {
        var game = NewDir("generic");
        await InstallAsync(game, "mod-a", ("a.txt", "a"));
        await InstallAsync(game, "mod-b", ("b.txt", "b"));

        var result = await _service.LoadOrder.MoveAsync(
            await Resolve(game), OrderListKind.Install, "mod-b", OrderPlacement.Bottom);

        result.Changed.Should().BeFalse();
        result.Deployment.Should().BeNull();
    }

    // ── references ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_AcceptsAnId_AName_APosition_OrAnUnambiguousPart()
    {
        var items = new[]
        {
            new OrderItem("sbbf-4864", "SBBF Stylized Beautiful Body", "sbbf-4864"),
            new OrderItem("tails-3108", "Better Tails for Dragonborn", "tails-3108"),
            new OrderItem("3", "3", "3")
        };

        LoadOrderService.Resolve(items, "TAILS-3108").Key.Should().Be("tails-3108");
        LoadOrderService.Resolve(items, "better tails for dragonborn").Key.Should().Be("tails-3108");
        LoadOrderService.Resolve(items, "#1").Key.Should().Be("sbbf-4864");
        LoadOrderService.Resolve(items, "Tails").Key.Should().Be("tails-3108");

        // A mod named like a number is still that mod.
        LoadOrderService.Resolve(items, "3").Key.Should().Be("3");
    }

    [Fact]
    public void Resolve_SeveralEqualMatches_AreReportedRatherThanGuessed()
    {
        var items = new[]
        {
            new OrderItem("a", "Thicc Nude Tiefling", "a"),
            new OrderItem("b", "Thicc Nude Githyanki", "b")
        };

        var act = () => LoadOrderService.Resolve(items, "Thicc Nude");

        act.Should().Throw<LoadOrderException>()
            .WithMessage("*matches 2 entries*Thicc Nude Tiefling*Thicc Nude Githyanki*");
    }

    [Fact]
    public void Resolve_SeveralEntriesOfOneMod_CountAsOneMatch()
    {
        var items = new[]
        {
            new OrderItem("u1", "Armour Part 1", "armour"),
            new OrderItem("u2", "Armour Part 2", "armour")
        };

        LoadOrderService.Resolve(items, "Armour").ModId.Should().Be("armour");
    }

    [Fact]
    public void Resolve_NothingMatching_SaysSo()
    {
        var act = () => LoadOrderService.Resolve([new OrderItem("a", "Alpha", "a")], "zulu");

        act.Should().Throw<LoadOrderException>().WithMessage("*Nothing in the list matches 'zulu'*");
    }

    // ── a load order Modular keeps ──────────────────────────────────────────

    [Fact]
    public async Task AManagedLoadOrder_IsRenderedByTheModule_AndLeavesTheInstallOrderAlone()
    {
        var game = ManagedGame();
        await InstallAsync(game, "mod-a", ("paks/a.dat", "a"), ("shared.txt", "from-a"));
        await InstallAsync(game, "mod-b", ("paks/b.dat", "b"), ("shared.txt", "from-b"));
        await InstallAsync(game, "mod-c", ("paks/c.dat", "c"));

        var installation = await Resolve(game);
        var result = await _service.LoadOrder.MoveAsync(
            installation, OrderListKind.Load, "mod-c", OrderPlacement.Top);

        result.Deployment!.Success.Should().BeTrue(result.Deployment.Error);

        var orders = await _service.LoadOrder.GetAsync(installation);
        orders.Kind.Should().Be(LoadOrderKind.Managed);
        orders.Load.Select(i => i.Key).Should().Equal("mod-c", "mod-a", "mod-b");
        orders.Install.Select(i => i.Key).Should().Equal("mod-a", "mod-b", "mod-c");

        // The module's manifest and file names follow the load order…
        File.ReadAllLines(Path.Combine(game, ManagedTestModule.Manifest)).Should().Equal("mod-c", "mod-a", "mod-b");
        Directory.GetFiles(Path.Combine(game, "paks")).Select(Path.GetFileName)
            .Should().BeEquivalentTo("000_c.dat", "001_a.dat", "002_b.dat");

        // …while a file two mods share still goes by the install order.
        File.ReadAllText(Path.Combine(game, "shared.txt")).Should().Be("from-b");
    }

    [Fact]
    public async Task AModInstalledAfterALoadOrderMove_LoadsLast()
    {
        var game = ManagedGame();
        await InstallAsync(game, "mod-a", ("paks/a.dat", "a"));
        await InstallAsync(game, "mod-b", ("paks/b.dat", "b"));

        var installation = await Resolve(game);
        await _service.LoadOrder.MoveAsync(installation, OrderListKind.Load, "mod-b", OrderPlacement.Top);
        await InstallAsync(game, "mod-c", ("paks/c.dat", "c"));

        var orders = await _service.LoadOrder.GetAsync(installation);
        orders.Load.Select(i => i.Key).Should().Equal("mod-b", "mod-a", "mod-c");
    }

    [Fact]
    public async Task SetAsync_SetsAWholeOrder_InOneDeployment()
    {
        var game = ManagedGame();
        await InstallAsync(game, "mod-a", ("paks/a.dat", "a"));
        await InstallAsync(game, "mod-b", ("paks/b.dat", "b"));
        await InstallAsync(game, "mod-c", ("paks/c.dat", "c"));

        var installation = await Resolve(game);
        var deployment = await _service.LoadOrder.SetAsync(
            installation, OrderListKind.Load, ["mod-b", "mod-c", "mod-a"]);

        deployment!.Success.Should().BeTrue(deployment.Error);
        File.ReadAllLines(Path.Combine(game, ManagedTestModule.Manifest)).Should().Equal("mod-b", "mod-c", "mod-a");
    }

    // ── a load order the game keeps ─────────────────────────────────────────

    [Fact]
    public async Task AnExternalLoadOrder_ListsEveryEntry_AndMovesModularsAmongTheOthers()
    {
        var game = ExternalGame("base", "foreign");
        await InstallAsync(game, "mod-a", ("a.txt", "a"));
        await InstallAsync(game, "mod-b", ("b.txt", "b"));

        var installation = await Resolve(game);
        var before = await _service.LoadOrder.GetAsync(installation);

        before.Kind.Should().Be(LoadOrderKind.External);
        before.Load.Select(i => i.Key).Should().Equal("base", "foreign", "e-mod-a", "e-mod-b");
        before.Load.Single(i => i.Key == "foreign").Managed.Should().BeFalse();
        before.Load.Single(i => i.Key == "base").Locked.Should().BeTrue();

        await _service.LoadOrder.MoveAsync(
            installation, OrderListKind.Load, "mod-b", OrderPlacement.Before("foreign"));

        File.ReadAllLines(Path.Combine(game, ExternalTestModule.List))
            .Should().Equal("base", "e-mod-b", "foreign", "e-mod-a");

        // Modular's own record follows the game's list.
        var records = await _service.Deployment.Mods.ListAsync(installation.GameKey);
        records.OrderBy(r => r.LoadIndex).Select(r => r.ModId).Should().Equal("mod-b", "mod-a");
    }

    [Fact]
    public async Task AnExternalLoadOrder_KeepsALockedEntryFirst()
    {
        var game = ExternalGame("base", "foreign");
        await InstallAsync(game, "mod-a", ("a.txt", "a"));
        var installation = await Resolve(game);

        var moveLocked = () => _service.LoadOrder.MoveAsync(
            installation, OrderListKind.Load, "base", OrderPlacement.Bottom);
        (await moveLocked.Should().ThrowAsync<LoadOrderException>()).WithMessage("*has to stay where it is*");

        var beforeLocked = () => _service.LoadOrder.MoveAsync(
            installation, OrderListKind.Load, "mod-a", OrderPlacement.Before("base"));
        (await beforeLocked.Should().ThrowAsync<LoadOrderException>()).WithMessage("*Nothing can load before 'base'*");

        // Position 1 is the locked entry's, so asking for it lands just below.
        await _service.LoadOrder.MoveAsync(installation, OrderListKind.Load, "mod-a", OrderPlacement.At(1));
        File.ReadAllLines(Path.Combine(game, ExternalTestModule.List)).Should().Equal("base", "e-mod-a", "foreign");
    }

    [Fact]
    public async Task AnExternalLoadOrder_TheGameRefusing_IsReportedWithItsReason()
    {
        var game = ExternalGame("base");
        await InstallAsync(game, "mod-a", ("a.txt", "a"));
        await InstallAsync(game, "mod-b", ("b.txt", "b"));
        var installation = await Resolve(game);

        _external.Running = true;
        try
        {
            var act = () => _service.LoadOrder.MoveAsync(
                installation, OrderListKind.Load, "mod-b", OrderPlacement.At(2));

            (await act.Should().ThrowAsync<LoadOrderException>()).WithMessage("*running*");
            File.ReadAllLines(Path.Combine(game, ExternalTestModule.List)).Should().Equal("base", "e-mod-a", "e-mod-b");
        }
        finally
        {
            _external.Running = false;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string ManagedGame()
    {
        var game = NewDir("managed");
        File.WriteAllText(Path.Combine(game, ManagedTestModule.Marker), string.Empty);
        return game;
    }

    private string ExternalGame(params string[] entries)
    {
        var game = NewDir("external");
        File.WriteAllText(Path.Combine(game, ExternalTestModule.Marker), string.Empty);
        File.WriteAllLines(Path.Combine(game, ExternalTestModule.List), entries);
        return game;
    }

    private async Task<GameInstallation> Resolve(string game) =>
        (await _service.Deployment.ResolveInstallationAsync(game))!;

    private async Task InstallAsync(string game, string modId, params (string Path, string Content)[] files)
    {
        var archive = Path.Combine(NewDir("zip"), modId + ".zip");

        using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entryPath).Open());
                writer.Write(content);
            }
        }

        var result = await _service.InstallAsync(archive, game, new ModInstallationOptions
        {
            ModId = modId,
            AllowOverwrite = true,
            AutoSnapshot = false
        });

        result.Success.Should().BeTrue(result.Error);
    }

    private string NewDir(string label)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"modular-order-{label}-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// A game whose load order Modular keeps: paks are prefixed with their load position,
    /// as FF7 Remake does, and a manifest lists the order, as Cyberpunk does.
    /// </summary>
    private sealed class ManagedTestModule : GameModuleBase
    {
        public const string Marker = "managed.marker";
        public const string Manifest = "loadorder.txt";

        public override string ModuleId => "test-managed";
        public override string DisplayName => "Managed Test Game";
        public override IReadOnlyList<string> GameIds { get; } = ["test-managed"];
        public override IReadOnlyList<int> SteamAppIds { get; } = [];
        protected override IReadOnlyList<string> PrimaryAnchors { get; } = [Marker];
        public override IReadOnlyList<IModInstaller> GetInstallers() => [];
        public override LoadOrderKind LoadOrderKind => LoadOrderKind.Managed;

        public override DeployTarget MapDeployPath(DeployPathContext context) =>
            context.RelativePath.StartsWith("paks/", StringComparison.Ordinal)
                ? new DeployTarget(context.RootName, $"paks/{context.LoadPosition:000}_{Path.GetFileName(context.RelativePath)}")
                : new DeployTarget(context.RootName, context.RelativePath);

        public override Task WriteLoadOrderAsync(
            GameInstallation installation, IReadOnlyList<DeployedMod> mods, CancellationToken ct = default) =>
            File.WriteAllLinesAsync(
                Path.Combine(installation.InstallPath, Manifest),
                mods.OrderBy(m => m.LoadPosition).Select(m => m.ModId),
                ct);
    }

    /// <summary>
    /// A game that keeps its own load order in a file, which also lists entries Modular did
    /// not add, and whose first entry is locked, as Baldur's Gate 3 does with GustavX.
    /// </summary>
    private sealed class ExternalTestModule : GameModuleBase
    {
        public const string Marker = "external.marker";
        public const string List = "engine-order.txt";
        private const string Locked = "base";

        public bool Running { get; set; }

        public override string ModuleId => "test-external";
        public override string DisplayName => "External Test Game";
        public override IReadOnlyList<string> GameIds { get; } = ["test-external"];
        public override IReadOnlyList<int> SteamAppIds { get; } = [];
        protected override IReadOnlyList<string> PrimaryAnchors { get; } = [Marker];
        public override IReadOnlyList<IModInstaller> GetInstallers() => [];
        public override LoadOrderKind LoadOrderKind => LoadOrderKind.External;

        // Like modsettings.lsx: a new mod is appended, a removed one dropped, the rest kept.
        public override async Task WriteLoadOrderAsync(
            GameInstallation installation, IReadOnlyList<DeployedMod> mods, CancellationToken ct = default)
        {
            var path = Path.Combine(installation.InstallPath, List);
            var lines = File.ReadAllLines(path).ToList();
            var deployed = mods.Select(m => "e-" + m.ModId).ToList();

            lines.RemoveAll(l => l.StartsWith("e-", StringComparison.Ordinal) && !deployed.Contains(l));
            lines.AddRange(deployed.Where(d => !lines.Contains(d)));

            await File.WriteAllLinesAsync(path, lines, ct);
        }

        public override EngineOrder? ReadEngineOrder(GameInstallation installation, IReadOnlyList<DeployedMod> mods)
        {
            var ids = mods.Select(m => m.ModId).ToHashSet();

            return new EngineOrder(File.ReadAllLines(Path.Combine(installation.InstallPath, List))
                .Select(l => new EngineOrderEntry(
                    l, l,
                    l.StartsWith("e-", StringComparison.Ordinal) && ids.Contains(l[2..]) ? l[2..] : null,
                    Locked: l == Locked))
                .ToList());
        }

        public override Task SetEngineOrderAsync(
            GameInstallation installation, IReadOnlyList<string> entryIds, CancellationToken ct = default)
        {
            if (Running)
                throw new InvalidOperationException("The test game is running.");

            var path = Path.Combine(installation.InstallPath, List);
            var current = File.ReadAllLines(path);

            if (!current.Order().SequenceEqual(entryIds.Order()))
                throw new InvalidOperationException("not a reordering");

            return File.WriteAllLinesAsync(path, entryIds, ct);
        }
    }
}
