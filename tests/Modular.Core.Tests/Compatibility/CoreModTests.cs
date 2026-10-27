using FluentAssertions;
using Modular.Core.Compatibility;
using Modular.Core.Modules;
using Modular.Sdk.Backends;
using Modular.Sdk.Backends.Common;
using Modular.Sdk.Compatibility;
using Xunit;

namespace Modular.Core.Tests.Compatibility;

public class CoreModTests : IDisposable
{
    private const string Domain = "cyberpunk2077";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modular-coremods-" + Guid.NewGuid().ToString("N"));

    public CoreModTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static InstalledMod Mod(string id, string name, string[]? files = null, params string[] versions) => new()
    {
        Key = InstalledMod.HubKey("nexusmods", Domain, id),
        Name = name,
        BackendId = "nexusmods",
        GameDomain = Domain,
        ModId = id,
        Versions = versions,
        Files = files ?? []
    };

    private static CoreModContext Context(params InstalledMod[] mods) =>
        new("1091500", mods, new CyberpunkGameModule());

    // ── Reverse requirements ────────────────────────────────────────────────

    [Fact]
    public async Task RequiredBy_CountsInstalledDependents()
    {
        var backend = new FakeBackend();
        backend.Requirements["1"] = [Req("10")];
        backend.Requirements["2"] = [Req("10"), Req("99")];
        backend.Requirements["3"] = [new ModRequirement { Name = "Tool", IsExternal = true, Url = "https://x" }];

        var evidence = await new RequiredByInstalledSignal([backend]).EvaluateAsync(Context(
            Mod("1", "Alpha"), Mod("2", "Beta"), Mod("3", "Gamma"), Mod("10", "Codeware")));

        // 99 is required but not installed: a missing dependency, not a core mod.
        evidence.Should().ContainSingle();
        evidence[0].ModKey.Should().Be(InstalledMod.HubKey("nexusmods", Domain, "10"));
        evidence[0].Role.Should().Be(CoreModRole.Dependency);
        evidence[0].Dependents.Should().Equal("Alpha", "Beta");
        backend.RequestedDomain.Should().Be(Domain);
    }

    [Fact]
    public async Task RequiredBy_IgnoresAModListingItself()
    {
        var backend = new FakeBackend();
        backend.Requirements["1"] = [Req("1")];

        var evidence = await new RequiredByInstalledSignal([backend]).EvaluateAsync(Context(Mod("1", "Alpha")));

        evidence.Should().BeEmpty();
    }

    // ── Game file rules ─────────────────────────────────────────────────────

    [Fact]
    public async Task GameFiles_FindCyberpunkLoadersAndNativePlugins()
    {
        var evidence = await new GameFileRuleSignal().EvaluateAsync(Context(
            Mod("2380", "RED4ext", ["bin/x64/winmm.dll", "red4ext/RED4ext.dll"]),
            Mod("4197", "TweakXL", ["red4ext/plugins/TweakXL/TweakXL.dll", "r6/scripts/TweakXL/TweakXL.reds"]),
            Mod("5", "Plugin with odd case", ["RED4EXT\\Plugins\\Thing\\Thing.DLL"]),
            Mod("6", "Clothes", ["archive/pc/mod/clothes.archive"])));

        evidence.Where(e => e.ModKey.EndsWith(":2380")).Should().OnlyContain(e => e.Role == CoreModRole.Loader)
            .And.HaveCount(2);
        evidence.Should().Contain(e => e.ModKey.EndsWith(":4197") && e.Role == CoreModRole.NativeCode);
        evidence.Should().Contain(e => e.ModKey.EndsWith(":5") && e.Role == CoreModRole.NativeCode);
        evidence.Should().NotContain(e => e.ModKey.EndsWith(":6"));
    }

    [Fact]
    public async Task GameFiles_ApplyExtraRules()
    {
        var signal = new GameFileRuleSignal([new CoreModRule("r6/scripts/*/Framework.reds", CoreModRole.Declared, "Framework")]);

        var evidence = await signal.EvaluateAsync(new CoreModContext("x", [Mod("7", "F", ["r6/scripts/F/Framework.reds"])], null));

        evidence.Should().ContainSingle().Which.Reason.Should().StartWith("Framework");
    }

    // ── Declared list ───────────────────────────────────────────────────────

    [Fact]
    public async Task Declared_MatchesByIdAndByName()
    {
        var path = Path.Combine(_dir, "core-mods.json");
        await File.WriteAllTextAsync(path, """
            { "cyberpunk2077": [
                { "modId": "2987", "reason": "Atelier stores" },
                { "name": "virtual car dealer", "role": "Dependency" },
                { "modId": "404" } ] }
            """);

        var evidence = await new DeclaredCoreModsSignal(path).EvaluateAsync(Context(
            Mod("2987", "Virtual Atelier"), Mod("4454", "Virtual Car Dealer"), Mod("1", "Other")));

        evidence.Should().HaveCount(2);
        evidence.Should().Contain(e => e.ModKey.EndsWith(":2987") && e.Reason == "Atelier stores" && e.Role == CoreModRole.Declared);
        evidence.Should().Contain(e => e.ModKey.EndsWith(":4454") && e.Role == CoreModRole.Dependency);
    }

    [Fact]
    public async Task Declared_MissingFileIsEmpty()
    {
        var evidence = await new DeclaredCoreModsSignal(Path.Combine(_dir, "none.json")).EvaluateAsync(Context(Mod("1", "A")));

        evidence.Should().BeEmpty();
    }

    // ── Classifier ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Classifier_MergesEvidenceAndKeepsGoingWhenASignalFails()
    {
        var backend = new FakeBackend();
        backend.Requirements["1"] = [Req("4197")];
        backend.Requirements["2"] = [Req("4197"), Req("3518")];

        var classifier = new CoreModClassifier(
        [
            new RequiredByInstalledSignal([backend]),
            new GameFileRuleSignal(),
            new ThrowingSignal()
        ]);

        var report = await classifier.ClassifyAsync(Context(
            Mod("1", "A"), Mod("2", "B"),
            Mod("4197", "TweakXL", ["red4ext/plugins/TweakXL/TweakXL.dll"]),
            Mod("3518", "Native Settings UI"),
            Mod("9", "Not core", ["archive/pc/mod/x.archive"])));

        report.Entries.Select(e => e.Mod.Name).Should().Equal("TweakXL", "Native Settings UI");
        report.Entries[0].Roles.Should().Equal(CoreModRole.Dependency, CoreModRole.NativeCode);
        report.Entries[0].Dependents.Should().Equal("A", "B");
        report.Errors.Should().ContainSingle().Which.Should().StartWith("broken:");
        report.ModsChecked.Should().Be(5);
    }

    // ── Release check ───────────────────────────────────────────────────────

    private static readonly GameUpdateTarget Update = new(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), "2.5");

    [Fact]
    public async Task Release_NothingSinceTheUpdate()
    {
        var backend = new FakeBackend { Files = { ModFile("1.0", "2026-09-01") } };

        var status = await new BackendReleaseCheck([backend]).CheckAsync(Mod("1", "A", null, "1.0"), Update);

        status.Verdict.Should().Be(ReleaseVerdict.NoReleaseSinceTarget);
        status.LatestVersion.Should().Be("1.0");
    }

    [Fact]
    public async Task Release_InstalledVersionCameOutSinceTheUpdate()
    {
        var backend = new FakeBackend { Files = { ModFile("1.0", "2026-09-01"), ModFile("1.1", "2026-10-23", "Mod 1.1 for 2.5") } };

        var status = await new BackendReleaseCheck([backend]).CheckAsync(Mod("1", "A", null, "v1-1"), Update);

        status.Verdict.Should().Be(ReleaseVerdict.OnTargetRelease);
        status.MentionsTargetVersion.Should().BeTrue();
    }

    [Fact]
    public async Task Release_NewerReleaseNotInstalled()
    {
        // "2.51" and the mod's own version "2.5" do not name the game's 2.5.
        var backend = new FakeBackend { Files = { ModFile("2.5", "2026-10-25", "Mod for 2.51") } };

        var status = await new BackendReleaseCheck([backend]).CheckAsync(Mod("1", "A", null, "2.4"), Update);

        status.Verdict.Should().Be(ReleaseVerdict.UpdateAvailable);
        status.MentionsTargetVersion.Should().BeFalse();
        status.LatestUploadedAt.Should().Be(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Release_UntracedModIsUnknown()
    {
        var check = new BackendReleaseCheck([new FakeBackend()]);
        var mod = new InstalledMod { Key = "name:x", Name = "X" };

        check.CanCheck(mod).Should().BeFalse();
        (await check.CheckAsync(mod, Update)).Verdict.Should().Be(ReleaseVerdict.Unknown);
    }

    [Fact]
    public async Task Readiness_ChecksEveryCoreMod()
    {
        var backend = new FakeBackend { Files = { ModFile("1.0", "2026-10-23") } };
        var service = new GameUpdateReadinessService(
            new CoreModClassifier([new GameFileRuleSignal()]),
            [new BackendReleaseCheck([backend])]);

        var report = await service.CheckAsync(
            Context(Mod("2380", "RED4ext", ["red4ext/RED4ext.dll"], "1.0"), Mod("8", "Clothes")), Update);

        report.Items.Should().ContainSingle().Which.Status.Verdict.Should().Be(ReleaseVerdict.OnTargetRelease);
        report.Ready.Should().BeTrue();
    }

    [Fact]
    public async Task Readiness_TextureOnlyDependencyIsNotAffected()
    {
        var backend = new FakeBackend();
        backend.Requirements["1"] = [Req("20"), Req("21")];
        var service = new GameUpdateReadinessService(
            new CoreModClassifier([new RequiredByInstalledSignal([backend])]),
            [new BackendReleaseCheck([backend])]);

        var report = await service.CheckAsync(Context(
            Mod("1", "Preset", ["archive/pc/mod/preset.archive"]),
            Mod("20", "Body textures", ["archive/pc/mod/body.archive", "archive/pc/mod/body.xl"]),
            Mod("21", "Script framework", ["r6/scripts/F/F.reds"])), Update);

        report.Items.Single(i => i.Entry.Mod.Name == "Body textures").Status.Verdict
            .Should().Be(ReleaseVerdict.NotAffected);
        report.Items.Single(i => i.Entry.Mod.Name == "Script framework").Status.Verdict
            .Should().Be(ReleaseVerdict.Unknown, "the hub lists no files for it");
        report.Ready.Should().BeFalse();
    }

    [Fact]
    public async Task Readiness_AssetOnlyModWithANewReleaseStillShowsIt()
    {
        // Archives can hold quest data an update breaks; a release since the update says so.
        var backend = new FakeBackend { Files = { ModFile("3.3.3", "2026-10-27") } };
        backend.Requirements["1"] = [Req("20")];
        var service = new GameUpdateReadinessService(
            new CoreModClassifier([new RequiredByInstalledSignal([backend])]),
            [new BackendReleaseCheck([backend])]);

        var report = await service.CheckAsync(Context(
            Mod("1", "Quest"), Mod("20", "Quest Core", ["archive/pc/mod/core.archive"], "3.3.0")), Update);

        report.Items.Single().Status.Verdict.Should().Be(ReleaseVerdict.UpdateAvailable);
    }

    // ── Installed mod sources ───────────────────────────────────────────────

    [Fact]
    public async Task Inventory_ReadsModsAndMergesRepeats()
    {
        var path = Path.Combine(_dir, "inventory.json");
        await File.WriteAllTextAsync(path, """
            { "backend": "nexusmods", "gameDomain": "cyberpunk2077", "mods": [
                { "name": "Codeware", "modId": "7780", "version": "1.21.1", "files": ["red4ext\\plugins\\Codeware\\Codeware.dll"] },
                { "name": "Codeware", "modId": "7780", "versions": ["1.21.0"], "files": ["r6/scripts/Codeware/x.reds"] },
                { "name": "Hand placed", "files": ["archive/pc/mod/a.archive"] } ] }
            """);

        var mods = await new InventoryInstalledModSource(path).ListAsync();

        mods.Should().HaveCount(2);
        var codeware = mods.Single(m => m.ModId == "7780");
        codeware.Key.Should().Be("nexusmods:cyberpunk2077:7780");
        codeware.Versions.Should().BeEquivalentTo("1.21.1", "1.21.0");
        codeware.Files.Should().Contain("red4ext/plugins/Codeware/Codeware.dll");
        mods.Single(m => m.Name == "Hand placed").IsTraced.Should().BeFalse();
    }

    [Theory]
    [InlineData("RED4ext-2380-1-30-0-1773082858.zip", "1.30.0")]
    [InlineData("/x/Diemnocturnum Alpha 0.0.7.3.0-1604-0-0-7-3-0-alpha-1706725775.zip", "0.0.7.3.0.alpha")]
    [InlineData("red4ext-1.31.0.zip", null)]
    public void ParseNexusVersion_ReadsTheVersionFromADownloadName(string name, string? expected)
    {
        ChangesetInstalledModSource.ParseNexusVersion(name).Should().Be(expected);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ModRequirement Req(string id) => new() { ModId = id, Name = "Mod " + id };

    private static BackendModFile ModFile(string version, string uploaded, string? displayName = null) => new()
    {
        FileId = version,
        FileName = $"mod-{version}.zip",
        DisplayName = displayName,
        Version = version,
        UploadedAt = DateTime.SpecifyKind(DateTime.Parse(uploaded, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
    };

    private sealed class ThrowingSignal : ICoreModSignal
    {
        public string Id => "broken";

        public Task<IReadOnlyList<CoreModEvidence>> EvaluateAsync(CoreModContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("no network");
    }

    private sealed class FakeBackend : IModBackend, IRequirementsBackend
    {
        public Dictionary<string, IReadOnlyList<ModRequirement>> Requirements { get; } = new();
        public List<BackendModFile> Files { get; } = [];
        public string? RequestedDomain { get; private set; }

        public string Id => "nexusmods";
        public string DisplayName => "NexusMods";
        public BackendCapabilities Capabilities => BackendCapabilities.Requirements;

        public IReadOnlyList<string> ValidateConfiguration() => [];

        public Task<IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>>> GetRequirementsAsync(
            IReadOnlyList<string> modIds, string? gameDomain = null, CancellationToken ct = default)
        {
            RequestedDomain = gameDomain;
            IReadOnlyDictionary<string, IReadOnlyList<ModRequirement>> result = modIds.ToDictionary(
                id => id, id => Requirements.TryGetValue(id, out var listed) ? listed : []);
            return Task.FromResult(result);
        }

        public Task<List<BackendModFile>> GetModFilesAsync(
            string modId, string? gameDomain = null, FileFilter? filter = null, CancellationToken ct = default) =>
            Task.FromResult(Files.ToList());

        public Task<BackendMod?> GetModInfoAsync(string modId, string? gameDomain = null, CancellationToken ct = default) =>
            Task.FromResult<BackendMod?>(null);

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
