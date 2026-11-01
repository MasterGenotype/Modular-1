using FluentAssertions;
using Microsoft.Data.Sqlite;
using Modular.Core.Database;
using Modular.Core.Deployment;
using Modular.Core.InGame;
using Modular.Core.Installers.BaldursGate3;
using Modular.Core.Modules;
using Modular.Core.Tests.Modules;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.InGame;

/// <summary>
/// Baldur's Gate 3 mod.io subscriptions handed to the game's own mod manager: one copy,
/// the game's, which Modular records but never places.
/// </summary>
public sealed class InGameModServiceTests : IAsyncLifetime
{
    private const string Alfira = "4345659";
    private const string AlfiraUuid = "3539eba9-6d77-c53d-1009-b3c77c9cd04c";
    private const string OtherUuid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const long V302 = 36028797018963968 + (2L << 31);
    private const long V303 = 36028797018963968 + (3L << 31);

    private readonly List<string> _dirs = [];
    private ModularDatabase _db = null!;
    private GameInstallation _installation = null!;
    private string _mods = null!;
    private bool _running;

    public async Task InitializeAsync()
    {
        _db = new ModularDatabase(Path.Combine(NewDir(), "modular.db"));
        await _db.InitializeAsync();

        var gameDir = NewDir();
        Directory.CreateDirectory(Path.Combine(gameDir, "bin"));
        Directory.CreateDirectory(Path.Combine(gameDir, "Data"));
        File.WriteAllText(Path.Combine(gameDir, "Data", "Gustav.pak"), string.Empty);
        _installation = new GameInstallation
        {
            GameId = "baldursgate3",
            SteamAppId = 1086940,
            InstallPath = gameDir,
            ProtonPrefixPath = NewDir()
        };

        _mods = new BaldursGate3GameModule().ResolveModPaths(_installation).GetRoot(BaldursGate3GameModule.ModsRoot)!;
        Directory.CreateDirectory(_mods);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FileNameFor_IsTheUploadsNameAsAPak()
    {
        new BG3InGameModManager().FileNameFor("alfirajoinstheparty_3539eba9-6-6gki.zip")
            .Should().Be("alfirajoinstheparty_3539eba9-6-6gki.pak");
    }

    [Fact]
    public async Task ASubscription_WaitsForTheGame_AndWritesNothing()
    {
        var service = Service();

        var record = await service.RecordSubscriptionAsync(_installation, Alfira, "Alfira", "8340694");

        record.State.Should().Be(GameOwnedModState.PendingDownload);
        Directory.EnumerateFileSystemEntries(_mods).Should().BeEmpty();
        File.Exists(BaldursGate3GameModule.ResolveModSettingsPath(_installation)).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_AdoptsTheFileTheGameDownloaded()
    {
        var service = Service();
        await service.RecordSubscriptionAsync(_installation, Alfira, "Alfira", null);
        GameDownloads("alfirajoinstheparty_3539eba9-6-6gki.pak", V303, "up-1");

        var report = await service.SyncAsync(_installation, Subscribed(Alfira));

        report.Items.Should().ContainSingle().Which.Kind.Should().Be(InGameSyncKind.Adopted);
        var record = await Owned().GetAsync("1086940", "modio", Alfira);
        record!.State.Should().Be(GameOwnedModState.Installed);
        record.FileName.Should().Be("alfirajoinstheparty_3539eba9-6-6gki.pak");
        record.ModuleUuid.Should().Be(AlfiraUuid);
        record.FileMd5.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sync_ReportsAnUpdateTheGameMade()
    {
        var service = Service();
        GameDownloads("alfirajoinstheparty_3539eba9-6-cxww.pak", V302, "up-1");
        await service.RecordSubscriptionAsync(_installation, Alfira, "Alfira", null);
        File.Delete(Path.Combine(_mods, "alfirajoinstheparty_3539eba9-6-cxww.pak"));
        GameDownloads("alfirajoinstheparty_3539eba9-6-6gki.pak", V303, "up-2");

        var report = await service.SyncAsync(_installation, Subscribed(Alfira));

        var item = report.Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be(InGameSyncKind.UpdatedByGame);
        item.Detail.Should().Contain("1.0.2.0 → 1.0.3.0").And.Contain("cxww");
        (await Owned().GetAsync("1086940", "modio", Alfira))!.FileName.Should().EndWith("-6gki.pak");
    }

    [Fact]
    public async Task Sync_PicksUpSubscriptionsMadeElsewhere()
    {
        var report = await Service().SyncAsync(_installation, Subscribed(Alfira), new Dictionary<string, string> { [Alfira] = "Alfira" });

        report.Items.Should().ContainSingle().Which.Kind.Should().Be(InGameSyncKind.AwaitingDownload);
        (await Owned().GetAsync("1086940", "modio", Alfira))!.State.Should().Be(GameOwnedModState.PendingDownload);
    }

    [Fact]
    public async Task Sync_ForgetsAModThatIsNeitherSubscribedNorPresent()
    {
        var service = Service();
        await service.RecordSubscriptionAsync(_installation, Alfira, "Alfira", null);

        var report = await service.SyncAsync(_installation, Subscribed());

        report.Items.Should().ContainSingle().Which.Kind.Should().Be(InGameSyncKind.Forgotten);
        (await Owned().GetAsync("1086940", "modio", Alfira)).Should().BeNull();
    }

    [Fact]
    public async Task Sync_FlagsAModularCopyOfASubscribedMod_AndNeverAdoptsIt()
    {
        var service = Service();
        Bg3TestPak.WriteModIoMod(Path.Combine(_mods, "50-900_AlfiraJoinsTheParty.pak"), AlfiraUuid, "Alfira", 4345659, V303);

        var report = await service.SyncAsync(_installation, Subscribed(Alfira));

        report.Items.Select(i => i.Kind).Should().BeEquivalentTo([InGameSyncKind.AwaitingDownload, InGameSyncKind.ModularCopy]);
        (await Owned().GetAsync("1086940", "modio", Alfira))!.FileName.Should().BeNull();
    }

    [Fact]
    public async Task Sync_FlagsTwoFilesHoldingOneModule()
    {
        GameDownloads("alfirajoinstheparty_3539eba9-6-cxww.pak", V302, "a");
        GameDownloads("alfirajoinstheparty_3539eba9-6-6gki.pak", V303, "b");

        var report = await Service().SyncAsync(_installation, Subscribed(Alfira));

        report.Items.Should().Contain(i => i.Kind == InGameSyncKind.Duplicate && i.Detail.Contains("cxww") && i.Detail.Contains("6gki"));
    }

    [Fact]
    public async Task Remove_DeletesTheGamesFileAndEntry_ButNotModularsFiles()
    {
        var service = Service();
        GameDownloads("alfirajoinstheparty_3539eba9-6-6gki.pak", V303, "b");
        Bg3TestPak.WriteMod(Path.Combine(_mods, "70-001_Other.pak"), OtherUuid, "Other");
        var settings = BaldursGate3GameModule.ResolveModSettingsPath(_installation)!;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, ModSettings(("GustavX", BG3ModSettings.GustavXUuid), ("Alfira", AlfiraUuid), ("Other", OtherUuid)));
        await service.RecordSubscriptionAsync(_installation, Alfira, "Alfira", null);

        var removed = await service.RemoveAsync(_installation, Alfira);

        removed.Should().Contain("alfirajoinstheparty_3539eba9-6-6gki.pak").And.Contain(r => r.Contains("Alfira"));
        Directory.EnumerateFiles(_mods).Select(Path.GetFileName).Should().Equal("70-001_Other.pak");
        BG3ModSettings.ReadEntries(settings).Select(e => e.Uuid).Should().Equal(BG3ModSettings.GustavXUuid, OtherUuid);
        (await Owned().GetAsync("1086940", "modio", Alfira)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_RefusesWhileTheGameRuns()
    {
        var service = Service();
        GameDownloads("alfirajoinstheparty_3539eba9-6-6gki.pak", V303, "b");
        _running = true;

        var act = () => service.RemoveAsync(_installation, Alfira);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(_mods, "alfirajoinstheparty_3539eba9-6-6gki.pak")).Should().BeTrue();
    }

    [Fact]
    public async Task FindModularCopies_FindsAModularModStagingTheSameModule()
    {
        var staging = NewDir();
        Bg3TestPak.WriteModIoMod(Path.Combine(staging, "AlfiraJoinsTheParty.pak"), AlfiraUuid, "Alfira", 4345659, V302);
        Bg3TestPak.WriteModIoMod(Path.Combine(_mods, "50-900_AlfiraJoinsTheParty.pak"), AlfiraUuid, "Alfira", 4345659, V302);
        await new GameModRepository(_db).UpsertAsync(new GameModRecord
        {
            GameKey = "1086940", ModId = "nexus-alfira", StagingPath = staging, DisplayName = "Alfira (Nexus)"
        });

        var copies = await Service().FindModularCopiesAsync(_installation, Alfira);

        copies.Should().ContainSingle().Which.ModId.Should().Be("nexus-alfira");
    }

    private InGameModService Service() =>
        new(Owned(), new GameModRepository(_db), new BG3InGameModManager(() => _running));

    private GameOwnedModRepository Owned() => new(_db);

    private void GameDownloads(string fileName, long version64, string md5) =>
        Bg3TestPak.WriteModIoMod(Path.Combine(_mods, fileName), AlfiraUuid, "AlfiraJoinsTheParty", 4345659, version64, md5);

    private static HashSet<string> Subscribed(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    private static string ModSettings(params (string Name, string Uuid)[] mods) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <save>
            <version major="4" minor="8" revision="0" build="700"/>
            <region id="ModuleSettings">
                <node id="root">
                    <children>
                        <node id="Mods">
                            <children>
        {string.Concat(mods.Select(m => $"""
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="{m.Name}"/>
                                    <attribute id="MD5" type="LSString" value=""/>
                                    <attribute id="Name" type="LSString" value="{m.Name}"/>
                                    <attribute id="PublishHandle" type="uint64" value="0"/>
                                    <attribute id="UUID" type="guid" value="{m.Uuid}"/>
                                    <attribute id="Version64" type="int64" value="36028797018963968"/>
                                </node>

            """))}
                            </children>
                        </node>
                    </children>
                </node>
            </region>
        </save>
        """;

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modular-ingame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }
}
