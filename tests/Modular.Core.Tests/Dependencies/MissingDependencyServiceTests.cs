using FluentAssertions;
using Modular.Core.Backends.NexusMods;
using Modular.Core.Dependencies;
using Modular.Core.Dependencies.Conventions;
using Modular.Core.Installers;
using Modular.Core.Modules;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Installers;
using Modular.Sdk.Modules;
using Xunit;

namespace Modular.Core.Tests.Dependencies;

/// <summary>
/// Working out which mods installed mods need and do not have, from what a backend lists
/// and what the mod files themselves declare.
/// </summary>
public sealed class MissingDependencyServiceTests
{
    private const string Domain = "baldursgate3";
    private const string GameDir = "/games/bg3";

    private static readonly ChangesetRecord Durge = Installed(
        "Durge", "Durge's mangled head - Dark Urge Dragonborn CC-9702-1-0-0-16-1774555674.zip",
        "70-000_Durges_mangled_head.pak");

    private static readonly ChangesetRecord Screens = Installed(
        "Screens", "NSFW Loading Screens 2.0-13743-2-0-0-1731574831.rar");

    // ── tracing an install back to a mod page ───────────────────────────────

    [Theory]
    [InlineData("Special - Savage Resilience-556-11-2-1733684094.zip", "556")]
    [InlineData("/dl/NSFW Loading Screens 2.0-13743-2-0-0-1731574831.rar", "13743")]
    [InlineData("Durge's mangled head - Dark Urge Dragonborn CC-9702-1-0-0-16-1774555674.zip", "9702")]
    [InlineData("just-a-mod.zip", null)]
    [InlineData("mod-v1-2.zip", null)]
    [InlineData(null, null)]
    public void ParseNexusModId_ReadsTheIdFromADownloadName(string? archive, string? expected)
    {
        MissingDependencyService.ParseNexusModId(archive).Should().Be(expected);
    }

    // ── what a backend lists ────────────────────────────────────────────────

    [Fact]
    public async Task ListedRequirements_AreSplitIntoDownloadableAndExternal()
    {
        var backend = new FakeBackend();
        backend.Requirements["9702"] =
        [
            new ModRequirement { ModId = "353", Name = "P4 Patches", Notes = "You need Patches_for_VFX_Heads file" },
            new ModRequirement { Name = "BG3 Mod Manager", IsExternal = true, SameGame = false, Url = "https://example.test/bg3mm" }
        ];

        var report = await Service(backend).FindAsync([Durge], [Durge]);

        report.ModsChecked.Should().Be(1);
        report.Items.Should().HaveCount(2);

        var patches = report.Items[0];
        patches.Kind.Should().Be(MissingDependencyKind.Downloadable);
        patches.ModId.Should().Be("353");
        patches.GameDomain.Should().Be(Domain);
        patches.BackendId.Should().Be("nexusmods");
        patches.RequiredBy.Should().Equal("Durge");
        patches.Notes.Should().Equal("You need Patches_for_VFX_Heads file");

        var manager = report.Items[1];
        manager.Kind.Should().Be(MissingDependencyKind.External);
        manager.ModId.Should().BeNull();
        manager.Url.Should().Be("https://example.test/bg3mm");
    }

    [Fact]
    public async Task ARequirementThatIsInstalled_IsNotMissing()
    {
        var backend = new FakeBackend();
        backend.Requirements["9702"] = [new ModRequirement { ModId = "13743", Name = "Screens" }];

        var report = await Service(backend).FindAsync([Durge], [Durge, Screens]);

        report.Items.Should().BeEmpty();
        backend.RequestedIds.Should().Equal("9702");
    }

    [Fact]
    public async Task ARequirementSharedByTwoMods_IsListedOnce()
    {
        var backend = new FakeBackend();
        backend.Requirements["9702"] = [new ModRequirement { ModId = "353", Name = "P4 Patches", Notes = "a" }];
        backend.Requirements["13743"] = [new ModRequirement { ModId = "353", Name = "P4 Patches", Notes = "b" }];

        var report = await Service(backend).FindAsync([Durge, Screens], [Durge, Screens]);

        var item = report.Items.Should().ContainSingle().Subject;
        item.RequiredBy.Should().BeEquivalentTo("Durge", "Screens");
        item.Notes.Should().BeEquivalentTo("a", "b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARequirementAlreadyDownloaded_NamesItsArchivesInsteadOfBeingFetchedAgain(bool backendLayout)
    {
        var mods = Directory.CreateTempSubdirectory("modular-deps").FullName;
        try
        {
            // nexusmods/<game>/…, or <game>/… in a mods directory not yet reorganized.
            var gameRoot = backendLayout ? Path.Combine(mods, "nexusmods", Domain) : Path.Combine(mods, Domain);

            // One archive known by its NexusMods name, one renamed and known by its record.
            var gameDir = Directory.CreateDirectory(Path.Combine(gameRoot, "Patches")).FullName;
            var named = Path.Combine(gameDir, "P4 Patches-353-4-0-1717339071.zip");
            var renamed = Path.Combine(gameDir, "renamed.zip");
            File.WriteAllText(named, string.Empty);
            File.WriteAllText(renamed, string.Empty);
            DependencyConventions.WriteDownloadRecord(renamed, new DeclaredSource("nexusmods", "353", Domain, "1"));
            File.WriteAllText(named + ".part", string.Empty);
            File.SetLastWriteTimeUtc(named, DateTime.UtcNow.AddDays(-1));

            var backend = new FakeBackend();
            backend.Requirements["9702"] =
            [
                new ModRequirement { ModId = "353", Name = "P4 Patches" },
                new ModRequirement { ModId = "777", Name = "Not Downloaded" }
            ];

            var installation = new GameInstallation { GameId = Domain, SteamAppId = 1086940, InstallPath = GameDir };
            var service = new MissingDependencyService(
                [backend],
                (_, _) => Task.FromResult<(GameInstallation, IGameModule)?>((installation, new FakeModule())),
                mods);

            var report = await service.FindAsync([Durge], [Durge]);

            report.Items.Single(i => i.ModId == "353").DownloadedArchives.Should().Equal(renamed, named);
            report.Items.Single(i => i.ModId == "777").DownloadedArchives.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }

    [Fact]
    public async Task ARequirementEveryListerCallsOptional_IsMarkedOptional()
    {
        var backend = new FakeBackend();
        backend.Requirements["9702"] =
        [
            new ModRequirement { ModId = "1", Name = "Compat Target", Notes = "(Optional) Adds custom icon" },
            new ModRequirement { ModId = "2", Name = "Shared", Notes = "Optional" },
            new ModRequirement { ModId = "3", Name = "Needed", Notes = "Required, optional files not needed" }
        ];
        backend.Requirements["13743"] = [new ModRequirement { ModId = "2", Name = "Shared" }];

        var report = await Service(backend).FindAsync([Durge, Screens], [Durge, Screens]);

        report.Items.Single(i => i.ModId == "1").Optional.Should().BeTrue();
        report.Items.Single(i => i.ModId == "2").Optional.Should().BeFalse("another mod needs it outright");
        report.Items.Single(i => i.ModId == "3").Optional.Should().BeFalse();
    }

    [Fact]
    public async Task ARequirementForAnotherGame_IsOnlyALink()
    {
        var backend = new FakeBackend();
        backend.Requirements["9702"] =
            [new ModRequirement { ModId = "12", Name = "Elsewhere", SameGame = false, Url = "https://example.test/other" }];

        var report = await Service(backend).FindAsync([Durge], [Durge]);

        report.Items.Should().ContainSingle().Which.Kind.Should().Be(MissingDependencyKind.External);
    }

    [Fact]
    public async Task AModWithNoKnownSource_IsReportedAsUntraceable()
    {
        var local = Installed("Handmade", "handmade.zip");
        var backend = new FakeBackend();

        var report = await Service(backend).FindAsync([local], [local]);

        report.Untraceable.Should().Equal("Handmade");
        report.Items.Should().BeEmpty();
        backend.RequestedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedLookup_IsAnErrorNotAnException()
    {
        var backend = new FakeBackend { Failure = new InvalidOperationException("rate limited") };

        var report = await Service(backend).FindAsync([Durge], [Durge]);

        report.Items.Should().BeEmpty();
        report.Errors.Should().ContainSingle().Which.Should().Contain("rate limited");
    }

    [Theory]
    [InlineData("/mods/nexusmods/skyrimspecialedition/Utilities/Thing/Thing-100-1-0-1700000000.zip")]
    [InlineData("/mods/skyrimspecialedition/Thing-100-1-0-1700000000.zip")]
    public async Task WithoutAGameModule_TheDownloadFolderNamesTheGame(string archivePath)
    {
        var record = new ChangesetRecord
        {
            ChangesetId = "c1",
            ModId = "Thing",
            ArchivePath = archivePath,
            TargetDirectory = "/games/unknown"
        };
        var backend = new FakeBackend();
        backend.Requirements["100"] = [new ModRequirement { ModId = "7", Name = "SKSE Thing" }];

        var service = new MissingDependencyService(
            [backend], (_, _) => Task.FromResult<(GameInstallation, IGameModule)?>(null), "/mods");

        var report = await service.FindAsync([record], [record]);

        report.Items.Should().ContainSingle().Which.GameDomain.Should().Be("skyrimspecialedition");
        backend.RequestedDomain.Should().Be("skyrimspecialedition");
    }

    [Fact]
    public async Task AnotherHubsDownloadFolder_DoesNotNameANexusModsGame()
    {
        // modio/<game> is named after the game, not a NexusMods domain; guessing one from
        // it would look the mod up on the wrong page.
        var record = new ChangesetRecord
        {
            ChangesetId = "c1",
            ModId = "Thing",
            ArchivePath = "/mods/modio/baldursgate3/Thing/Thing-100-1-0-1700000000.zip",
            TargetDirectory = "/games/unknown"
        };

        var service = new MissingDependencyService(
            [new FakeBackend()], (_, _) => Task.FromResult<(GameInstallation, IGameModule)?>(null), "/mods");

        var report = await service.FindAsync([record], [record]);

        report.Untraceable.Should().Equal("Thing");
    }

    // ── what the mod files declare ──────────────────────────────────────────

    [Fact]
    public async Task ADeclaredDependency_IsOfferedWithNameSearchCandidates()
    {
        var backend = new FakeBackend();
        backend.SearchResults.Add(new BackendMod { ModId = "42", Name = "Dragonborn Extra Tabs", BackendId = "nexusmods" });

        var module = new FakeModule
        {
            Unmet =
            [
                new UnmetDependency("Durge", "70-000_Durges_mangled_head.pak", "Dragonborn Extra Tabs - BT 3/4", "uuid-1")
            ]
        };

        var report = await Service(backend, module).FindAsync([Durge], [Durge]);

        var item = report.Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be(MissingDependencyKind.Unidentified);
        item.RequiredBy.Should().Equal("Durge");
        item.Candidates.Should().ContainSingle().Which.ModId.Should().Be("42");

        // Punctuation would be read as search syntax.
        backend.SearchTerms.Should().Equal("Dragonborn Extra Tabs BT 3 4");
    }

    [Fact]
    public async Task ADependencyDeclaredByAnotherModsFile_IsLeftOutOfASingleModCheck()
    {
        var module = new FakeModule
        {
            Unmet = [new UnmetDependency("Someone else's mod", "unmanaged.pak", "Library", "uuid-2")]
        };

        var single = await Service(new FakeBackend(), module).FindAsync([Durge], [Durge]);
        var everything = await Service(new FakeBackend(), module)
            .FindAsync([Durge], [Durge], includeUnmanaged: true);

        single.Items.Should().BeEmpty();
        everything.Items.Should().ContainSingle().Which.RequiredBy.Should().Equal("Someone else's mod");
    }

    // ── other hubs, other managers ──────────────────────────────────────────

    [Fact]
    public async Task AModFromAnotherHub_IsCheckedOnThatHub()
    {
        // No NexusMods id in the name; Modular's download record says where it came from.
        var dir = Directory.CreateTempSubdirectory("modular-deps-").FullName;
        try
        {
            var archive = Path.Combine(dir, "some mod.zip");
            DependencyConventions.WriteDownloadRecord(archive, new DeclaredSource("gamebanana", "598553"));

            var record = Installed("Banana", archive);
            var nexus = new FakeBackend();
            var banana = new FakeBackend("gamebanana");
            banana.Requirements["598553"] = [new ModRequirement { ModId = "111", Name = "Codename" }];

            var report = await Service([nexus, banana]).FindAsync([record], [record]);

            var item = report.Items.Should().ContainSingle().Subject;
            item.BackendId.Should().Be("gamebanana");
            item.ModId.Should().Be("111");
            nexus.RequestedIds.Should().BeEmpty();
            banana.RequestedDomain.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AModFromAHubThatIsNotEnabled_IsReportedNotIgnored()
    {
        var dir = Directory.CreateTempSubdirectory("modular-deps-").FullName;
        try
        {
            var archive = Path.Combine(dir, "some mod.zip");
            DependencyConventions.WriteDownloadRecord(archive, new DeclaredSource("gamebanana", "1"));
            var record = Installed("Banana", archive);

            var report = await Service(new FakeBackend()).FindAsync([record], [record]);

            report.Errors.Should().ContainSingle().Which.Should().Contain("gamebanana");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ARequirementInstalledByAnotherManager_IsTakenAsMet()
    {
        var backend = new FakeBackend();
        backend.Requirements["9702"] =
        [
            new ModRequirement { ModId = "353", Name = "Compatibility Framework" },
            new ModRequirement { ModId = "354", Name = "Something Else" }
        ];

        // In the game's folder under its own spelling, though Modular never installed it.
        var module = new FakeModule { Present = ["CompatibilityFramework"] };

        var report = await Service(backend, module).FindAsync([Durge], [Durge]);

        report.Items.Should().ContainSingle().Which.Name.Should().Be("Something Else");
        report.SatisfiedElsewhere.Should().Equal("Compatibility Framework");
    }

    [Fact]
    public async Task ModsThatDeclareAConflict_AreReported()
    {
        var module = new FakeModule
        {
            Conflicts =
            [
                new DeclaredConflict("Durge", "70-000_Durges_mangled_head.pak", "Other Heads", "uuid-9"),
                new DeclaredConflict("Unmanaged", "unmanaged.pak", "Thing", "uuid-8")
            ]
        };

        var single = await Service(new FakeBackend(), module).FindAsync([Durge], [Durge]);
        var everything = await Service(new FakeBackend(), module)
            .FindAsync([Durge], [Durge], includeUnmanaged: true);

        single.Conflicts.Should().ContainSingle()
            .Which.Should().Be(new InstalledConflict("Durge", "Other Heads", "baldursgate3"));
        everything.Conflicts.Should().HaveCount(2);
    }

    // ── choosing a file ─────────────────────────────────────────────────────

    [Fact]
    public void PickDefaultFile_ChoosesOnlyWhenThereIsNoDoubt()
    {
        var main = new BackendModFile { FileId = "1", Category = "main" };
        var otherMain = new BackendModFile { FileId = "2", Category = "main" };
        var optional = new BackendModFile { FileId = "3", Category = "optional" };

        MissingDependencyService.PickDefaultFile([main, optional]).Should().BeSameAs(main);
        MissingDependencyService.PickDefaultFile([optional]).Should().BeSameAs(optional);
        MissingDependencyService.PickDefaultFile([main, otherMain]).Should().BeNull();
        MissingDependencyService.PickDefaultFile([optional, optional]).Should().BeNull();
        MissingDependencyService.PickDefaultFile([]).Should().BeNull();
    }

    [Fact]
    public async Task GetFiles_ListsMainFilesFirst()
    {
        var backend = new FakeBackend();
        backend.Files.Add(new BackendModFile { FileId = "3", Category = "optional" });
        backend.Files.Add(new BackendModFile { FileId = "1", Category = "main" });

        var (mod, files) = await Service(backend).GetFilesAsync("nexusmods", "353", Domain);

        mod.ModId.Should().Be("353");
        files.Select(f => f.FileId).Should().Equal("1", "3");
    }

    // ── the NexusMods side ──────────────────────────────────────────────────

    [Fact]
    public void NexusResponse_MapsOnSiteAndOffSiteRequirements()
    {
        const string json = """
            {"data":{"legacyMods":{"nodes":[{"modId":9702,"modRequirements":{"nexusRequirements":{"nodes":[
              {"modId":"353","modName":"P4 Patches","gameId":"3474","url":"","notes":"Need the VFX file","externalRequirement":false},
              {"modId":"0","modName":"BG3 Mod Manager","gameId":"0","url":"https://example.test/bg3mm","notes":"","externalRequirement":true},
              {"modId":"12","modName":"Other game mod","gameId":"110","url":"","notes":null,"externalRequirement":false}
            ]}}}]}}}
            """;

        var node = NexusModsGraphQlClient.ParseResponse<NexusGraphQlData>(json)!.LegacyMods!.Nodes.Single();
        var mapped = node.ModRequirements!.NexusRequirements!.Nodes
            .Select(r => NexusModsBackend.MapRequirement(r, 3474, Domain))
            .ToList();

        node.ModId.Should().Be(9702);

        mapped[0].Should().BeEquivalentTo(new ModRequirement
        {
            ModId = "353",
            Name = "P4 Patches",
            SameGame = true,
            Url = "https://www.nexusmods.com/baldursgate3/mods/353",
            Notes = "Need the VFX file"
        });

        mapped[1].IsExternal.Should().BeTrue();
        mapped[1].ModId.Should().BeEmpty();
        mapped[1].Url.Should().Be("https://example.test/bg3mm");
        mapped[1].Notes.Should().BeNull();

        mapped[2].IsExternal.Should().BeFalse();
        mapped[2].SameGame.Should().BeFalse();
        mapped[2].Url.Should().BeNull();
    }

    [Fact]
    public void NexusResponse_ReportedErrors_Throw()
    {
        var act = () => NexusModsGraphQlClient.ParseResponse<NexusGraphQlData>(
            """{"errors":[{"message":"gameId is required when filtering by modId"}],"data":null}""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*gameId is required*");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static MissingDependencyService Service(FakeBackend backend, FakeModule? module = null) =>
        Service([backend], module);

    private static MissingDependencyService Service(FakeBackend[] backends, FakeModule? module = null)
    {
        module ??= new FakeModule();
        var installation = new GameInstallation
        {
            GameId = Domain,
            SteamAppId = 1086940,
            InstallPath = GameDir
        };

        return new MissingDependencyService(
            backends,
            (_, _) => Task.FromResult<(GameInstallation, IGameModule)?>((installation, module)));
    }

    private static ChangesetRecord Installed(string name, string archive, params string[] deployedFiles) => new()
    {
        ChangesetId = name,
        ModId = name,
        ArchivePath = archive,
        TargetDirectory = GameDir,
        GameKey = "1086940",
        ManagedBy = ChangesetOwnership.Deployment,
        OperationsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            deployedFiles = deployedFiles.Select(f => "/prefix/Mods/" + f).ToArray()
        })
    };

    private sealed class FakeModule : GameModuleBase
    {
        public IReadOnlyList<UnmetDependency> Unmet { get; init; } = [];
        public IReadOnlyList<DeclaredConflict> Conflicts { get; init; } = [];
        public IReadOnlyList<string> Present { get; init; } = [];

        public override string ModuleId => Domain;
        public override string DisplayName => "Baldur's Gate 3";
        public override IReadOnlyList<string> GameIds { get; } = ["1086940", Domain];
        public override IReadOnlyList<int> SteamAppIds { get; } = [1086940];
        protected override IReadOnlyList<string> PrimaryAnchors { get; } = ["bin"];

        public override IReadOnlyList<IModInstaller> GetInstallers() => [];

        public override IReadOnlyList<UnmetDependency> FindUnmetDependencies(
            GameInstallation installation) => Unmet;

        public override IReadOnlyList<DeclaredConflict> FindDeclaredConflicts(
            GameInstallation installation) => Conflicts;

        public override IReadOnlyList<string> ListPresentModNames(
            GameInstallation installation) => Present;
    }

    private sealed class FakeBackend(string id = "nexusmods") : IModBackend, IRequirementsBackend, ISearchableBackend
    {
        public Dictionary<string, IReadOnlyList<ModRequirement>> Requirements { get; } = new();
        public List<BackendMod> SearchResults { get; } = [];
        public List<BackendModFile> Files { get; } = [];
        public List<string> RequestedIds { get; } = [];
        public List<string> SearchTerms { get; } = [];
        public string? RequestedDomain { get; private set; }
        public Exception? Failure { get; init; }

        public string Id => id;
        public string DisplayName => "NexusMods";
        public BackendCapabilities Capabilities =>
            BackendCapabilities.Requirements | BackendCapabilities.Search;

        public IReadOnlyList<string> ValidateConfiguration() => [];

        public Task<IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>>> GetRequirementsAsync(
            IReadOnlyList<string> modIds, string? gameDomain = null, CancellationToken ct = default)
        {
            if (Failure != null)
                throw Failure;

            RequestedIds.AddRange(modIds);
            RequestedDomain = gameDomain;

            // A mod with nothing listed still comes back, with an empty list.
            IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>> result = modIds.ToDictionary(
                id => id,
                id => Requirements.TryGetValue(id, out var listed) ? listed : []);

            return Task.FromResult(result);
        }

        public Task<ModSearchResult> SearchModsAsync(ModSearchQuery query, CancellationToken ct = default)
        {
            SearchTerms.Add(query.Terms);
            return Task.FromResult(new ModSearchResult { Mods = SearchResults.ToList(), TotalCount = SearchResults.Count });
        }

        public Task<List<BackendModFile>> GetModFilesAsync(
            string modId, string? gameDomain = null, FileFilter? filter = null, CancellationToken ct = default) =>
            Task.FromResult(Files.ToList());

        public Task<BackendMod?> GetModInfoAsync(
            string modId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<BackendMod?>(new BackendMod { ModId = modId, Name = "Mod " + modId, BackendId = Id, GameDomain = gameDomain });

        public Task<List<BackendMod>> GetUserModsAsync(string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult(new List<BackendMod>());

        public Task<string?> ResolveDownloadUrlAsync(
            string modId, string fileId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task DownloadModsAsync(
            string outputDirectory, string? gameDomain = null, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
